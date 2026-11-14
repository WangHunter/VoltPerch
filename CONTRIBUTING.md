# Contributing to VoltPerch

Thank you for helping improve peripheral battery monitoring on Windows. Focused bug fixes, device compatibility evidence and small, reviewable additions are welcome.

## Before making a change

Search existing issues first. For a new device protocol or substantial behavior change, describe the device, connection mode, evidence and intended scope in an issue before implementing it.

Use the [bug report form](https://github.com/WangHunter/VoltPerch/issues/new?template=bug_report.yml) for a defect and the [device support form](https://github.com/WangHunter/VoltPerch/issues/new?template=device_support.yml) for compatibility work. Remove personal device names, MAC addresses, serial numbers, local usernames and identifying paths from attachments. USB vendor/product IDs and model names are useful and do not identify an individual unit.

## Development setup

Use Windows 10 version 2004 or later, or Windows 11, with the .NET 10 SDK. The main project is `src/BatteryDesk/BatteryDesk.csproj`.

```powershell
dotnet build src/BatteryDesk/BatteryDesk.csproj -c Release
dotnet publish src/BatteryDesk/BatteryDesk.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

Do not publish over a running installation. Keep application data, device captures and local SDK/package caches out of commits.

## Code and localization

- Write source comments, developer documentation and user-facing source strings in English.
- Route user-facing text through the localization helpers. Use stable English format keys with arguments; do not interpolate a sentence before translating it.
- Use `Loc.*` dynamic resources for static XAML strings and update both language resources when adding text.
- Keep stored device IDs, protocol values and setting keys independent of language.
- Use the shared device-name helper at display boundaries.
- Check English and Simplified Chinese, light and dark themes, and smaller window sizes when a UI change affects layout.

Keep changes scoped to the problem. Prefer existing helpers and patterns over introducing a new framework or unrelated refactor.

## Device and battery semantics

Battery correctness matters more than filling every row with a number:

- Never invent a percentage for an unavailable reading.
- Keep exact percentages, system estimates and coarse levels distinct. A capacity ratio is not proof of measurement precision.
- Do not treat a connected receiver as proof that the peripheral is online.
- Preserve the latest valid reading after a failed read or disconnection, with its not-live status.
- Do not present cached values as current after startup.
- Do not infer charging or full charge solely from a USB connection.
- Match observations to a device using defensible identity evidence; do not merge devices solely because they share a model name.

Hardware integrations must use only the queries needed to read connection and battery status. Sending a query packet may require a HID write or feature report, but it must not modify firmware, lighting, macros, bindings or other device settings. Do not add speculative or undocumented mutation commands. Document protocol sources and licensing in `THIRD_PARTY_NOTICES.txt` when applicable.

## Validation

Run checks relevant to the change and report which ones passed:

```powershell
dotnet run --project tools/CoreChecks/CoreChecks.csproj -c Release
dotnet run --project tools/WakeChecks/WakeChecks.csproj -c Release
dotnet run --project tools/XInputChecks/XInputChecks.csproj -c Release
dotnet run --project tools/WgiChecks/WgiChecks.csproj -c Release
dotnet run --project tools/BluetoothChecks/BluetoothChecks.csproj -c Release
dotnet run --project tools/LocalizationChecks/LocalizationChecks.csproj -c Release
```

Add focused regression checks for protocol parsing or state behavior. Include malformed responses, unavailable values and relevant connection transitions. Avoid tests that simply repeat implementation details.

Keep automated checks separate from hardware validation in the report. For a device integration, record the model, firmware if available, connection mode and operations exercised. If unplug/reconnect, sleep/wake or charging behavior was not checked, say so. Do not imply support for another connection mode or model based on a single successful sample.

Use the app's controlled capture path or an existing verified workflow for visual checks. Helper tools must catch top-level exceptions and exit with a clear error instead of opening unhandled-exception dialogs. Do not silently trigger device-changing operations as part of tests.

## Pull requests

Describe the concrete problem, resulting behavior and validation. Include redacted screenshots for UI changes and source evidence for new device protocols. Call out unverified hardware behavior and keep unrelated changes out of the pull request.

Contributions are made under the project's [MIT License](LICENSE). Preserve third-party attribution and license notices.
