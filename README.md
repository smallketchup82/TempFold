# TempFold

TempFold uses Folding@home to help maintain a comfortable room temperature with your computer's waste heat. An ESP32-C3 sensor device sends temperature readings over USB or Wi-Fi, and TempFold pauses or resumes your Folding@home v8 client around a target temperature. It adds heat when needed; it cannot actively cool the room.

## Getting started

[Assemble the sensor](https://github.com/smallketchup82/TempFold/wiki/Hardware-Assembly), [install the CLI](https://github.com/smallketchup82/TempFold/wiki/CLI-Installation), and [set up the firmware](https://github.com/smallketchup82/TempFold/wiki/Firmware-Setup). With a configured FAH client and a USB sensor on Linux:

```sh
tempfold --target 22 --range 1 --port /dev/ttyACM0 --allowed-transmission-sources serial
```

This starts folding at or below 21 °C and pauses it at or above 23 °C, retaining the previous decision between thresholds. See the [wiki](https://github.com/smallketchup82/TempFold/wiki) for usage, configuration, and development guides.

## Features

- USB serial and HTTP temperature input.
- Weekly schedules, temporary target/range overrides, and manual or Linux GameMode pausing.
- Live temperature, trends, and folding status, plus headless text or JSON output.
- Optional rotating structured diagnostic logs and configurable connection/sensor deadlines.
- Direct `fah start`, `fah stop`, and `fah status` commands.
- Interactive ESP32-C3 firmware installation and persistent configuration.

The CLI uses .NET 10 NativeAOT: published builds need no installed .NET runtime and target a specific OS/CPU. It talks directly to the FAH v8 WebSocket API without `fahctl` or Python. Firmware tooling is handled separately through Arduino CLI and Espressif tools.

## License

TempFold is available under the [MIT License](LICENSE), provided as-is without warranty. See the license for the full terms.

## AI disclosure

AI tools, including ChatGPT Codex, have assisted with this project's code and documentation. All AI-generated code is reviewed, tested, and edited by humans to ensure quality and accuracy.
