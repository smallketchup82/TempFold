# Contributing to TempFold

Bug reports, documentation corrections, and focused improvements are welcome. User setup and development instructions live in the [wiki](https://github.com/smallketchup82/TempFold/wiki); this file describes how to prepare a useful contribution.

## Before making changes

For a substantial feature, dependency, protocol change, or redesign, open an issue first to explain the problem and proposed behavior. Small fixes can go straight to a pull request. Check existing issues and changes so related work stays together.

Keep each contribution focused. Describe the user-visible problem, the expected result, and any compatibility constraints before choosing an implementation.

## Code and design

Prefer the simplest implementation that satisfies the current requirements. Preserve the existing architecture and coding style, and follow `.editorconfig`.

- Keep related functionality together. Separate classes by concrete responsibility, not speculative architectural layers.
- Do not introduce interfaces, services, managers, repositories, factories, or extra files without a demonstrated need. Explain the concrete problem before adding an abstraction.
- Prefer direct, readable code over clever abstractions or small performance optimizations.
- Avoid unrelated refactoring, dependency upgrades, generated-file changes, and formatting churn.
- Keep defaults and user-facing contracts explicit. When changing a setting, update parsing, validation, runtime handling, tests, and documentation together.

The [CLI development guide](https://github.com/smallketchup82/TempFold/wiki/Development-BuildTest-CLI) explains the current code layout, control/concurrency behavior, and NativeAOT constraints. The [firmware development guide](https://github.com/smallketchup82/TempFold/wiki/Development-BuildTest-Firmware) covers the pinned toolchain, flashing, and packaging.

## Tests and validation

Run checks appropriate to the change and report their results. Add focused regression coverage for behavior changes; keep scenarios readable and test observable outcomes rather than mirroring implementation details or relying on arbitrary sleeps. Documentation-only edits generally need link/example review, not a full application test run.

For CLI changes, from the repository root:

```sh
dotnet build TempFold.sln -c Release
dotnet test TempFold.sln -c Release
```

Use `--filter 'Category!=Integration'` for a fast unit-test pass, but include relevant integration coverage before submitting runtime changes. The suite uses simulated HTTP/WebSocket clients and does not require a physical sensor or running FAH client.

Changes to serialization, startup, logging, dependencies, or other AOT-sensitive paths should also pass a native publish on a suitable host, for example Linux x64:

```sh
dotnet publish TempFold/TempFold.csproj -c Release -r linux-x64
```

Reflection-based JSON is disabled. Register new serialized types in the relevant existing source-generation context, and fix trimming/native warnings rather than disabling the checks. Smoke-test changed behavior with the published executable when relevant.

For firmware changes:

```sh
make -C firmware build
make -C firmware test
```

Run `make -C firmware deps` first when the pinned dependencies are missing. When hardware verification is needed, use the repository's Make upload workflow and preserve saved settings unless erasure is explicitly part of the test. Distinguish compilation, simulation, USB readings, configuration readback, power-cycle retention, Wi-Fi association, and accepted HTTP delivery. Report which were actually checked and which remain untested.

Do not claim another OS or board is verified because a Linux build or simulated test passed. If your environment prevents a required check, state the limitation and the checks you did complete.

## Firmware and configuration compatibility

Preserve the serial/API contracts unless the change explicitly requires a migration. Document changes to provisioning capabilities, defaults, constraints, stored settings, or image/partition layouts, and consider existing devices and older clients.

A merged factory image and `firmware setup` overwrite saved settings. Normal compatible Make uploads and configuration patches serve different purposes. Do not add an automatic erase to resolve an ordinary configuration or connection error.

Keep pinned toolchain/library versions synchronized where used. Do not compile personal Wi-Fi credentials or server addresses into reusable firmware.

## Documentation

Update the relevant wiki guide when commands, defaults, configuration fields, protocols, or workflows change. Keep the root README a short introduction and entry point; keep this file focused on contribution practices. Avoid duplicating setup manuals in the source repository.

The GitHub wiki is a separate repository. Coordinate documentation changes with the code change, and link the corresponding wiki commit or proposed text in the pull request. Use terms from the [glossary](https://github.com/smallketchup82/TempFold/wiki/Development-Concepts-Glossary), especially the distinction between the sensor device, TempFold controller, and FAH client.

## Bug reports

Include the TempFold/TFirm version, OS/architecture, relevant board and sensor, steps to reproduce, expected behavior, actual behavior, and a minimal configuration or log excerpt. For network problems, distinguish USB sampling, Wi-Fi association, and HTTP results.

Remove secrets and unnecessary personal details. Never commit `TFirm.local.json`, passwords, credential-bearing serial commands, flash dumps, or private network profiles. Ignoring a file now does not remove secrets already committed to history.

## Pull requests

Explain the problem and resulting behavior, link related issues, and list meaningful validation with any remaining limitations. Include a short terminal transcript or screenshot when it helps review an interactive change, after removing sensitive data. Keep unrelated work in separate pull requests.

AI-assisted contributions are welcome, but you remain responsible for understanding the result, checking its accuracy, and performing the reported validation. Mention substantial AI assistance and review generated code, documentation, and tests as carefully as other contributions.

Contributions are made under the project's [MIT License](LICENSE).
