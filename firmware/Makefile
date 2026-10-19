SHELL := /bin/sh
.DEFAULT_GOAL := help
ROOT := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))
ARDUINO_CLI ?= arduino-cli
PYTHON ?= python3
CORE_VERSION ?= 3.3.12
FQBN ?= esp32:esp32:esp32c3:CDCOnBoot=cdc,FlashSize=4M,PartitionScheme=default
export PATCH
PORT ?=
ESP32_INDEX := https://espressif.github.io/arduino-esp32/package_esp32_index.json
SKETCH := $(ROOT)/TFirm
BUILD := $(ROOT)/build

.PHONY: help deps build compile upload flash run monitor boards release publish check-tool check-port clean test
help:
	@printf '%s\n' 'make -C firmware deps                 Install pinned ESP32 core and libraries' 'make -C firmware build                Compile ESP32-C3 firmware' 'make -C firmware test                 Test Wi-Fi startup and retry policy' 'make -C firmware boards               List connected devices' 'make -C firmware run PORT=/dev/ttyACM0 Build, upload, then monitor' 'make -C firmware upload PORT=...      Build and upload (board starts running)' 'make -C firmware monitor PORT=...     Monitor existing firmware at 115200 baud' 'make -C firmware release PATCH=0      Build YYYY.MM.PATCH release archive' 'make -C firmware clean                Remove generated build/release files'

test:
	"$(PYTHON)" "$(ROOT)/tests/wifi_startup_test.py"

check-tool:
	@command -v "$(ARDUINO_CLI)" >/dev/null || { printf '%s\n' 'Install Arduino CLI and add it to PATH first.' >&2; exit 1; }
check-port:
	@test -n "$(PORT)" || { printf '%s\n' 'Specify PORT, e.g. PORT=/dev/ttyACM0 (use boards to list devices).' >&2; exit 1; }

deps: check-tool
	"$(ARDUINO_CLI)" core update-index --additional-urls "$(ESP32_INDEX)"
	"$(ARDUINO_CLI)" core install "esp32:esp32@$(CORE_VERSION)" --additional-urls "$(ESP32_INDEX)"
	"$(ARDUINO_CLI)" lib update-index
	"$(ARDUINO_CLI)" lib install "ArduinoJson@7.4.3" "Adafruit SHT31 Library@2.2.2" "Adafruit BusIO@1.17.4"

build: check-tool
	"$(ARDUINO_CLI)" compile --fqbn "$(FQBN)" --warnings all --build-path "$(BUILD)/work" --output-dir "$(BUILD)/output" "$(SKETCH)"
compile: build

upload: check-port build
	"$(ARDUINO_CLI)" upload --fqbn "$(FQBN)" --port "$(PORT)" --input-dir "$(BUILD)/output" "$(SKETCH)"
flash: upload
run: upload
	"$(ARDUINO_CLI)" monitor --port "$(PORT)" --config baudrate=115200
monitor: check-tool check-port
	"$(ARDUINO_CLI)" monitor --port "$(PORT)" --config baudrate=115200
boards: check-tool
	"$(ARDUINO_CLI)" board list

# Release output is independent of development builds and contains no local JSON.
release: check-tool
	@set -eu; \
	version=$$("$(PYTHON)" "$(ROOT)/release_version.py"); \
	printf 'Building TFirm %s\n' "$$version"; \
	"$(ARDUINO_CLI)" compile --clean --fqbn "$(FQBN)" --warnings all --build-property "compiler.cpp.extra_flags=-DTFIRM_VERSION=$$version" --build-path "$(BUILD)/release-work" --output-dir "$(BUILD)/release-output" "$(SKETCH)"; \
	"$(PYTHON)" "$(ROOT)/package_release.py" "$(FQBN)" "$(CORE_VERSION)" "$$version"
publish: release

clean:
	rm -rf "$(ROOT)/build" "$(ROOT)/dist"
