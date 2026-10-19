// TempFold ESP32-C3 sensor firmware. Build and serial setup: https://github.com/smallketchup82/TempFold/wiki
#include <WiFi.h>
#include <esp_wifi.h>
#include <HTTPClient.h>
#include <Wire.h>
#include <Adafruit_SHT31.h>
#include <ArduinoJson.h>
#include <Preferences.h>
#include <atomic>
#include <ctype.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>

#if !defined(CONFIG_IDF_TARGET_ESP32C3)
#error "This firmware targets ESP32-C3 only."
#endif

#ifndef TFIRM_VERSION
#define TFIRM_VERSION dev
#endif
// Stringify the build token to avoid platform-specific compiler flag quoting.
#define TFIRM_STRINGIFY_IMPL(value) #value
#define TFIRM_STRINGIFY(value) TFIRM_STRINGIFY_IMPL(value)
constexpr char FIRMWARE_VERSION[] = TFIRM_STRINGIFY(TFIRM_VERSION);
constexpr size_t MAX_CONFIG_BYTES = 1536;
constexpr unsigned long CONNECT_TIMEOUT_MS = 20000;
constexpr unsigned long RETRY_DELAY_MS = 30000;
constexpr unsigned long API_CONNECT_TIMEOUT_MS = 5000;
constexpr unsigned long API_RESPONSE_TIMEOUT_MS = 5000;

struct DeviceConfig {
    String boardName = "TempFold sensor";
    String wifiSsid;
    String wifiPassword;
    String apiEndpoint; // Blank disables the radio and HTTP.
    int sdaPin = 8;
    int sclPin = 9;
    uint8_t sensorAddress = 0x44;
    uint32_t sensorIntervalMs = 5000;
};
DeviceConfig config;
DeviceConfig pendingConfig;
Adafruit_SHT31 sensor;
QueueHandle_t sensorQueue = nullptr;
bool sensorReady = false;
bool restartRequired = false;
std::atomic<int> lastHttpStatus{0};
// Event handlers and the worker update diagnostics; only the main loop writes
// serial output. Retain boot milestones so USB reconnect cannot hide them.
std::atomic<uint32_t> wifiAttempts{0};
std::atomic<uint32_t> wifiLastAttemptMs{0};
std::atomic<uint32_t> wifiFirstIpMs{0};
std::atomic<uint32_t> wifiFirstAssociationMs{0};
std::atomic<uint32_t> httpFirstSuccessMs{0};
std::atomic<int> wifiDisconnectReason{0};
std::atomic<uint32_t> wifiFailureRevision{0};
std::atomic<uint32_t> wifiLastFailureMs{0};
std::atomic<int> wifiTxPowerQuarterDbm{-1};
std::atomic<bool> wifiPowerVerified{false};
std::atomic<uint32_t> networkRevision{0};
unsigned long lastSensorAttempt = 0;
bool firstSensorRead = true;
char commandBuffer[MAX_CONFIG_BYTES + 1];
size_t commandLength = 0;
bool commandOverflow = false;
unsigned long lastCommandByte = 0;

// Configuration is immutable after startup. Only the worker owns Wi-Fi/HTTP;
// only the main loop writes serial output, so responses cannot interleave.
bool apiEnabled() { return !config.apiEndpoint.isEmpty(); }

bool validPin(int pin) {
    // Exclude flash and USB pins. Defaults 8/9 are strapping pins: wiring must
    // preserve the board boot levels (see the wiki Hardware Assembly guide).
    return pin == 0 || pin == 1 || (pin >= 3 && pin <= 7) || pin == 10 ||
           pin == 20 || pin == 21 || pin == 8 || pin == 9;
}

bool validText(const char* value, size_t maximum) {
    if (!value || strlen(value) > maximum) return false;
    for (const unsigned char* p = reinterpret_cast<const unsigned char*>(value); *p; ++p)
        if (*p < 32 || *p == 127) return false;
    return true;
}

bool decodeConfig(JsonVariantConst root, DeviceConfig& result) {
    if (!root.is<JsonObjectConst>()) return false;
    for (JsonPairConst item : root.as<JsonObjectConst>()) {
        String key = item.key().c_str();
        if (key != "boardName" && key != "wifiSsid" && key != "wifiPassword" && key != "apiEndpoint" &&
            key != "sdaPin" && key != "sclPin" && key != "sensorAddress" &&
            key != "sensorIntervalMs") return false;
    }
    const char* names[] = {"wifiSsid", "wifiPassword", "apiEndpoint", "boardName"};
    const size_t limits[] = {32, 64, 256, 64};
    String* targets[] = {&result.wifiSsid, &result.wifiPassword, &result.apiEndpoint, &result.boardName};
    for (int i = 0; i < 4; ++i) {
        if (root[names[i]].isUnbound()) continue;
        if (!root[names[i]].is<const char*>() ||
            !validText(root[names[i]].as<const char*>(), limits[i]) ||
            root[names[i]].as<JsonString>().size() != strlen(root[names[i]].as<const char*>())) return false;
        *targets[i] = root[names[i]].as<const char*>();
    }
    if (result.boardName.isEmpty()) return false;
    if (!root["sdaPin"].isUnbound()) {
        if (!root["sdaPin"].is<int>()) return false;
        result.sdaPin = root["sdaPin"].as<int>();
    }
    if (!root["sclPin"].isUnbound()) {
        if (!root["sclPin"].is<int>()) return false;
        result.sclPin = root["sclPin"].as<int>();
    }
    if (!validPin(result.sdaPin) || !validPin(result.sclPin) || result.sdaPin == result.sclPin)
        return false;
    if (!root["sensorAddress"].isUnbound()) {
        if (!root["sensorAddress"].is<int>()) return false;
        int address = root["sensorAddress"].as<int>();
        if (address != 0x44 && address != 0x45) return false;
        result.sensorAddress = address;
    }
    if (!root["sensorIntervalMs"].isUnbound()) {
        if (!root["sensorIntervalMs"].is<uint32_t>()) return false;
        result.sensorIntervalMs = root["sensorIntervalMs"].as<uint32_t>();
    }
    if (result.sensorIntervalMs < 1000 || result.sensorIntervalMs > 3600000) return false;
    if (!result.apiEndpoint.isEmpty()) {
        // Base URL only, no credentials, query, fragment or path. HTTPS requires
        // a trust/provisioning design; do not silently disable certificate checks.
        if (!result.apiEndpoint.startsWith("http://") || result.wifiSsid.isEmpty()) return false;
        String authority = result.apiEndpoint.substring(7);
        if (authority.isEmpty() || authority.indexOf('/') >= 0 || authority.indexOf('@') >= 0 ||
            authority.indexOf('?') >= 0 || authority.indexOf('#') >= 0 || authority.indexOf(' ') >= 0 ||
            authority.indexOf('\\') >= 0) return false;
        int colon = authority.indexOf(':');
        String host = colon < 0 ? authority : authority.substring(0, colon);
        if (host.isEmpty() || host[0] == '.' || host.endsWith(".")) return false;
        for (size_t i = 0; i < host.length(); ++i)
            if (!isalnum(static_cast<unsigned char>(host[i])) && host[i] != '.' && host[i] != '-') return false;
        if (colon >= 0) {
            String port = authority.substring(colon + 1);
            if (port.isEmpty() || port.length() > 5) return false;
            for (size_t i = 0; i < port.length(); ++i) if (!isdigit(port[i])) return false;
            if (port.toInt() < 1 || port.toInt() > 65535) return false;
        }
    }
    size_t passwordLength = result.wifiPassword.length();
    if (passwordLength != 0 && (passwordLength < 8 || passwordLength > 64)) return false;
    if (passwordLength == 64)
        for (size_t i = 0; i < passwordLength; ++i)
            if (!isxdigit(static_cast<unsigned char>(result.wifiPassword[i]))) return false;
    return true;
}

void encodeConfig(JsonDocument& doc, const DeviceConfig& value, bool includePassword) {
    doc["boardName"] = value.boardName;
    doc["wifiSsid"] = value.wifiSsid;
    if (includePassword) doc["wifiPassword"] = value.wifiPassword;
    else doc["wifiPasswordSet"] = !value.wifiPassword.isEmpty();
    doc["apiEndpoint"] = value.apiEndpoint;
    doc["sdaPin"] = value.sdaPin;
    doc["sclPin"] = value.sclPin;
    doc["sensorAddress"] = value.sensorAddress;
    doc["sensorIntervalMs"] = value.sensorIntervalMs;
}

bool saveConfig(const DeviceConfig& value) {
    JsonDocument doc;
    encodeConfig(doc, value, true);
    String serialized;
    if (doc.overflowed() || serializeJson(doc, serialized) == 0) return false;
    Preferences prefs;
    if (!prefs.begin("tempfold", false)) return false;
    // One NVS value avoids partially updated credentials after power loss.
    bool saved = prefs.putString("config-v1", serialized) == serialized.length();
    prefs.end();
    return saved;
}

void loadConfig() {
    Preferences prefs;
    if (!prefs.begin("tempfold", true)) return; // New device uses defaults.
    String stored = prefs.getString("config-v1", "");
    prefs.end();
    if (stored.isEmpty()) return;
    JsonDocument doc;
    DeviceConfig loaded;
    if (stored.length() > MAX_CONFIG_BYTES || deserializeJson(doc, stored) ||
        !decodeConfig(doc.as<JsonVariantConst>(), loaded)) {
        Serial.println("ERROR=STORED_CONFIG_INVALID");
        return;
    }
    config = loaded;
}

void reply(const char* command, bool ok, const char* error = nullptr) {
    JsonDocument doc;
    doc["command"] = command;
    doc["ok"] = ok;
    if (error) doc["error"] = error;
    doc["restartRequired"] = restartRequired;
    Serial.print("TF=");
    serializeJson(doc, Serial);
    Serial.println();
}

void handleCommand(const char* line) {
    if (strcmp(line, "TF GET") == 0) {
        JsonDocument doc;
        encodeConfig(doc, config, false);
        doc["command"] = "GET";
        doc["ok"] = true;
        doc["protocolVersion"] = 1;
        doc["configPatch"] = true;
        doc["firmwareVersion"] = FIRMWARE_VERSION;
        doc["chip"] = "ESP32-C3";
        doc["restartRequired"] = restartRequired;
        doc["sensorReady"] = sensorReady;
        doc["lastHttpStatus"] = lastHttpStatus.load();
        doc["wifiConnected"] = apiEnabled() && WiFi.status() == WL_CONNECTED;
        doc["uptimeMs"] = millis();
        doc["wifiConnectionAttempts"] = wifiAttempts.load();
        doc["wifiLastAttemptMs"] = wifiLastAttemptMs.load();
        doc["wifiFirstIpMs"] = wifiFirstIpMs.load();
        doc["wifiFirstAssociationMs"] = wifiFirstAssociationMs.load();
        doc["wifiModemSleepEnabled"] = WiFi.getSleep() != WIFI_PS_NONE;
        doc["httpFirstSuccessMs"] = httpFirstSuccessMs.load();
        int reason = wifiDisconnectReason.load();
        doc["wifiLastDisconnectReason"] = reason;
        doc["wifiLastDisconnectReasonText"] = reason ? WiFi.STA.disconnectReasonName(static_cast<wifi_err_reason_t>(reason)) : "";
        doc["wifiRssiDbm"] = WiFi.status() == WL_CONNECTED ? WiFi.RSSI() : 0;
        doc["wifiTxPowerQuarterDbm"] = wifiTxPowerQuarterDbm.load();
        doc["wifiPowerVerified"] = wifiPowerVerified.load();
        Serial.print("TF="); serializeJson(doc, Serial); Serial.println();
    } else if (strncmp(line, "TF SET ", 7) == 0) {
        JsonDocument doc;
        DeviceConfig next = pendingConfig; // Patch; preserve omitted fields, including secrets.
        if (deserializeJson(doc, line + 7, DeserializationOption::NestingLimit(2)) ||
            !decodeConfig(doc.as<JsonVariantConst>(), next)) {
            reply("SET", false, "INVALID_CONFIG"); return;
        }
        if (!saveConfig(next)) { reply("SET", false, "STORAGE_FAILED"); return; }
        pendingConfig = next;
        restartRequired = true;
        reply("SET", true);
    } else if (strcmp(line, "TF RESET") == 0) {
        if (!saveConfig(DeviceConfig{})) { reply("RESET", false, "STORAGE_FAILED"); return; }
        pendingConfig = DeviceConfig{};
        restartRequired = true;
        reply("RESET", true);
    } else if (strcmp(line, "TF RESTART") == 0) {
        reply("RESTART", true);
        Serial.flush();
        delay(100);
        ESP.restart();
    } else {
        reply("UNKNOWN", false, "UNKNOWN_COMMAND");
    }
}

void serviceSerial() {
    // Bound work per iteration so continuous input cannot starve sampling.
    for (int budget = 0; budget < 256 && Serial.available(); ++budget) {
        char c = Serial.read();
        lastCommandByte = millis();
        if (c == '\n') {
            if (commandOverflow) reply("UNKNOWN", false, "LINE_TOO_LONG");
            else if (commandLength) {
                commandBuffer[commandLength] = '\0';
                handleCommand(commandBuffer);
            }
            memset(commandBuffer, 0, sizeof(commandBuffer));
            commandLength = 0; commandOverflow = false;
        } else if (c != '\r') {
            if (c == '\0' || commandLength >= MAX_CONFIG_BYTES) commandOverflow = true;
            else if (!commandOverflow) commandBuffer[commandLength++] = c;
        }
    }
    if ((commandLength || commandOverflow) && millis() - lastCommandByte > 5000) {
        memset(commandBuffer, 0, sizeof(commandBuffer));
        commandLength = 0; commandOverflow = false;
        reply("UNKNOWN", false, "LINE_TIMEOUT");
    }
}

void serviceSensor() {
    unsigned long now = millis();
    if (!firstSensorRead && now - lastSensorAttempt < config.sensorIntervalMs) return;
    firstSensorRead = false;
    lastSensorAttempt = now;
    if (!sensorReady) sensorReady = sensor.begin(config.sensorAddress);
    if (!sensorReady) { Serial.println("ERROR=SHT30_NOT_FOUND"); return; }
    float temperature = sensor.readTemperature();
    if (!isfinite(temperature)) {
        sensorReady = false;
        Serial.println("ERROR=READ_FAILED"); return;
    }
    Serial.printf("TEMP=%.2f\n", temperature);
    if (sensorQueue) xQueueOverwrite(sensorQueue, &temperature);
}

bool prepareWifiRadio() {
    // mode(WIFI_STA) alone does not wait for the asynchronous STA-start event.
    // begin(false) waits for that event without initiating a connection.
    if (!WiFi.STA.begin(false)) {
        wifiPowerVerified.store(false);
        networkRevision.fetch_add(1);
        return false;
    }
    bool applied = WiFi.setTxPower(WIFI_POWER_8_5dBm);
    int actual = static_cast<int>(WiFi.getTxPower());
    wifiTxPowerQuarterDbm.store(actual);
    bool verified = applied && actual == static_cast<int>(WIFI_POWER_8_5dBm);
    wifiPowerVerified.store(verified);
    networkRevision.fetch_add(1);
    return verified;
}

unsigned long wifiRetryDelay(uint32_t attempts) {
    // A transient failure during boot should not impose a 50-second silence.
    // Repeated failures still back off, avoiding a tight loop on an absent AP.
    return attempts < 3 ? 1000 : RETRY_DELAY_MS;
}

void apiWorker(void*) {
    WiFi.persistent(false);
    // Own retry timing in this worker. The core's mandatory initial retry is
    // allowed a brief grace period before a failed session is cancelled.
    WiFi.setAutoReconnect(false);
    WiFi.onEvent([](arduino_event_id_t event, arduino_event_info_t info) {
        if (event == ARDUINO_EVENT_WIFI_STA_DISCONNECTED) {
            // Preserve the actual failure rather than overwriting it with our
            // deliberate disconnect at the connection deadline.
            if (info.wifi_sta_disconnected.reason != WIFI_REASON_ASSOC_LEAVE) {
                wifiDisconnectReason.store(info.wifi_sta_disconnected.reason);
                wifiLastFailureMs.store(millis());
                wifiFailureRevision.fetch_add(1);
            }
            networkRevision.fetch_add(1);
        } else if (event == ARDUINO_EVENT_WIFI_STA_CONNECTED) {
            uint32_t unset = 0;
            wifiFirstAssociationMs.compare_exchange_strong(unset, millis());
            networkRevision.fetch_add(1);
        } else if (event == ARDUINO_EVENT_WIFI_STA_GOT_IP) {
            uint32_t unset = 0;
            wifiFirstIpMs.compare_exchange_strong(unset, millis());
            networkRevision.fetch_add(1);
        }
    });
    String url = config.apiEndpoint + "/sensor";
    bool attempting = false;
    bool firstAttempt = true;
    unsigned long attemptStarted = 0;
    unsigned long retryStarted = 0;
    unsigned long retryDelay = RETRY_DELAY_MS;
    uint32_t failuresAtAttempt = 0;
    float temperature;
    for (;;) {
        unsigned long now = millis();
        if (WiFi.status() != WL_CONNECTED) {
            uint32_t failureRevision = wifiFailureRevision.load();
            unsigned long failureTime = wifiLastFailureMs.load();
            // Read time after callback timestamps to avoid unsigned underflow.
            now = millis();
            bool failed = failureRevision != failuresAtAttempt &&
                now - failureTime >= 1000 && !WiFi.STA.connected();
            if (attempting && (failed || now - attemptStarted >= CONNECT_TIMEOUT_MS)) {
                // Arduino disconnect() skips the driver call when association
                // hasn't finished. Cancel the pending handshake explicitly.
                esp_wifi_disconnect();
                attempting = false;
                retryStarted = millis();
                retryDelay = wifiRetryDelay(wifiAttempts.load());
                networkRevision.fetch_add(1);
                now = millis();
            }
            if (!attempting && (firstAttempt || now - retryStarted >= retryDelay)) {
                if (!prepareWifiRadio()) {
                    // Never attempt association at an unverified power setting.
                    vTaskDelay(pdMS_TO_TICKS(1000));
                    continue;
                }
                firstAttempt = false;
                attemptStarted = millis();
                wifiLastAttemptMs.store(attemptStarted);
                wifiAttempts.fetch_add(1);
                failuresAtAttempt = wifiFailureRevision.load();
                networkRevision.fetch_add(1);
                WiFi.begin(config.wifiSsid.c_str(), config.wifiPassword.c_str());
                attempting = true;
            }
            vTaskDelay(pdMS_TO_TICKS(100));
            continue;
        }
        if (attempting) { attempting = false; retryStarted = now - RETRY_DELAY_MS; }
        if (xQueueReceive(sensorQueue, &temperature, pdMS_TO_TICKS(250)) != pdTRUE) continue;
        char payload[64];
        int length = snprintf(payload, sizeof(payload), "{\"temperature\":%.2f}", temperature);
        if (length <= 0 || static_cast<size_t>(length) >= sizeof(payload)) continue;
        WiFiClient client;
        HTTPClient http;
        http.useHTTP10(true);
        http.setReuse(false);
        http.setConnectTimeout(API_CONNECT_TIMEOUT_MS);
        http.setTimeout(API_RESPONSE_TIMEOUT_MS);
        if (http.begin(client, url)) {
            http.addHeader("Content-Type", "application/json");
            lastHttpStatus.store(http.POST(reinterpret_cast<uint8_t*>(payload), length));
        }
        else lastHttpStatus.store(-1);
        http.end();
        if (lastHttpStatus.load() >= 200 && lastHttpStatus.load() < 300) {
            uint32_t unset = 0;
            if (httpFirstSuccessMs.compare_exchange_strong(unset, millis()))
                networkRevision.fetch_add(1);
        }
    }
}

void serviceNetworkDiagnostics() {
    static uint32_t previousRevision = 0;
    uint32_t revision = networkRevision.load();
    if (revision == previousRevision) return;
    previousRevision = revision;
    Serial.printf("NET=uptimeMs:%lu attempts:%lu powerVerified:%d txPowerQuarterDbm:%d connected:%d disconnectReason:%d firstIpMs:%lu firstHttpSuccessMs:%lu httpStatus:%d\n",
        static_cast<unsigned long>(millis()), static_cast<unsigned long>(wifiAttempts.load()),
        wifiPowerVerified.load(), wifiTxPowerQuarterDbm.load(), WiFi.status() == WL_CONNECTED,
        wifiDisconnectReason.load(), static_cast<unsigned long>(wifiFirstIpMs.load()),
        static_cast<unsigned long>(httpFirstSuccessMs.load()), lastHttpStatus.load());
}

void setup() {
    Serial.begin(115200);
    // Never wait for a serial terminal: sensors must run unattended.
    loadConfig();
    pendingConfig = config;
    Wire.begin(config.sdaPin, config.sclPin);
    Wire.setTimeOut(100);
    if (apiEnabled()) {
        sensorQueue = xQueueCreate(1, sizeof(float));
        if (!sensorQueue) Serial.println("ERROR=API_QUEUE_CREATE_FAILED");
        else if (xTaskCreate(apiWorker, "api-worker", 8192, nullptr, 1, nullptr) != pdPASS) {
            vQueueDelete(sensorQueue);
            sensorQueue = nullptr;
            Serial.println("ERROR=API_TASK_CREATE_FAILED");
        }
    }
    Serial.print("READY=TempFold/");
    Serial.println(FIRMWARE_VERSION);
}

void loop() {
    serviceSerial();
    serviceNetworkDiagnostics();
    serviceSensor();
    delay(10);
}
