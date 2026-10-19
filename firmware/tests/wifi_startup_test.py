"""Exercise the actual radio preparation function with a fake ESP32 Wi-Fi API."""
import pathlib
import subprocess
import tempfile

source = (pathlib.Path(__file__).resolve().parents[1] / 'TFirm' / 'TFirm.ino').read_text()
start = source.index('bool prepareWifiRadio() {')
end = source.index('\nvoid apiWorker', start)
function = source[start:end]
harness = r'''
#include <atomic>
#include <cassert>
constexpr int WIFI_POWER_8_5dBm = 34;
constexpr unsigned long RETRY_DELAY_MS = 30000;
std::atomic<bool> wifiPowerVerified{false};
std::atomic<int> wifiTxPowerQuarterDbm{-1};
std::atomic<unsigned> networkRevision{0};
struct Station {
    bool ready = true;
    bool started = false;
    bool begin(bool connect) {
        assert(!connect); // Starting the radio must not start association.
        started = ready;
        return ready;
    }
};
struct Radio {
    Station STA;
    bool applySucceeds = true;
    int readback = 34;
    int powerCalls = 0;
    bool setTxPower(int power) {
        assert(STA.started); // Power must be set only after station startup.
        assert(power == WIFI_POWER_8_5dBm);
        ++powerCalls;
        return applySucceeds;
    }
    int getTxPower() { assert(STA.started); return readback; }
} WiFi;
'''
scenarios = r'''
int main() {
    assert(wifiRetryDelay(1) == 1000);
    assert(wifiRetryDelay(2) == 1000);
    assert(wifiRetryDelay(3) == 30000);
    assert(wifiRetryDelay(100) == 30000);
    // Successful startup verifies the actual requested power.
    assert(prepareWifiRadio());
    assert(wifiPowerVerified.load());
    assert(wifiTxPowerQuarterDbm.load() == 34);
    // Failed station startup must never attempt a power change.
    WiFi.STA.ready = false;
    int previousCalls = WiFi.powerCalls;
    assert(!prepareWifiRadio());
    assert(!wifiPowerVerified.load());
    assert(WiFi.powerCalls == previousCalls);
    // A failed setter is a failure even if readback happens to match.
    WiFi.STA.ready = true;
    WiFi.applySucceeds = false;
    assert(!prepareWifiRadio());
    assert(!wifiPowerVerified.load());
    // A successful setter with wrong readback is also a failure.
    WiFi.applySucceeds = true;
    WiFi.readback = 78;
    assert(!prepareWifiRadio());
    assert(!wifiPowerVerified.load());
    // A later successful preparation can recover without booting again.
    WiFi.readback = 34;
    assert(prepareWifiRadio());
    assert(wifiPowerVerified.load());
}
'''
with tempfile.TemporaryDirectory(prefix='tempfold-wifi-test-') as workspace:
    path = pathlib.Path(workspace)
    (path / 'test.cpp').write_text(harness + function + scenarios)
    subprocess.run(['g++', '-std=c++17', '-Wall', '-Wextra', '-Werror', str(path / 'test.cpp'), '-o', str(path / 'test')], check=True)
    subprocess.run([str(path / 'test')], check=True)
print('Wi-Fi startup scenarios passed: ordering, startup failure, setter failure, wrong readback, recovery, retry backoff.')
