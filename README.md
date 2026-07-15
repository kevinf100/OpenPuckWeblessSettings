# OpenPuck Native Configuration

A cross-platform native desktop application for discovering, configuring, diagnosing, backing up, and updating OpenPuck microcontroller targets.

The application is written in C# with Avalonia and communicates directly over USB through libusb. It does not require a browser and does not embed HTML, a WebView, or a browser engine.

> [!IMPORTANT]
> This is an independent native client for OpenPuck hardware. The upstream firmware and original browser configuration interface are maintained by the [OpenPuck project](https://github.com/safijari/openpuck/tree/main). This repository does not replace or represent the upstream project.

> [!IMPORTANT]
> Linux and MacOS is currently untested!

## Features

- Native Windows, Linux, and macOS UI.
- Automatic discovery of OpenPuck puck and ReversePuck targets.
- One actively claimed target at a time with target switching and reconnect support.
- Live status updates approximately every 600 ms.
- Overview of firmware, protocol, USB mode, RF links, controller slots, battery, signal, rates, failures, timing, reset state, and IMU data.
- Staged controller settings with **Apply** and **Revert**.
- USB modes, back-button chords, mouse, rumble, Switch motion, and per-emulated-controller mappings.
- Protocol-v16 desktop/lizard-map editor.
- Web-interface-compatible settings and four-bond JSON backup/restore.
- Capture, flight-recorder, wedge, reset, timing, and maintenance diagnostics.
- Local UF2 and GitHub-release firmware updates with UF2 validation, CRC32, on-device verification, transfer resynchronization, and automatic post-flash reconnect.
- Session-only Advanced mode with an explicit warning for prereleases, engineering diagnostics, factory erase, and full-board wipe.

## Project status

The attached `28DE:1304` OpenPuck target has been verified on Windows with an A5 protocol-v17 handshake. Automated tests cover protocol parsing, USB cleanup, reconnect behavior, settings fields, backup compatibility, lizard maps, UF2 extraction, and CRC32.

Hardware acceptance is still required for:

- ReversePuck-specific operations.
- Linux and macOS USB access and packaging.
- Firmware flashing and destructive maintenance operations on expendable hardware.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) for development builds.
- A compatible OpenPuck microcontroller target exposing its vendor-class bulk USB interface.
- Native libusb 1.0 support:
  - **Windows x64/ARM64:** the official libusb 1.0.30 DLL is bundled. The OpenPuck vendor interface must use WinUSB.
  - **Linux:** install the distribution's libusb 1.0 runtime and apply [`packaging/linux/50-openpuck.rules`](packaging/linux/50-openpuck.rules).
  - **macOS:** install or package `libusb-1.0.0.dylib` for the target architecture. Release app bundles must include and sign the library.

Close Steam, browsers, USB sniffers, and other applications that might already own the OpenPuck USB interface before connecting.

## Build and run

```powershell
dotnet restore OpenPuckWeblessSettings.slnx
dotnet run --project OpenPuckWeblessSettings.csproj
```

The app scans automatically. Select an OpenPuck target and choose **Connect**.

For a terminal-only connection check:

```powershell
dotnet run --project OpenPuckWeblessSettings.csproj -- --probe
```

## Tests

```powershell
dotnet test OpenPuckWeblessSettings.slnx
```

## Publishing

Self-contained builds do not require a separately installed .NET runtime:

```powershell
dotnet publish OpenPuckWeblessSettings.csproj -c Release -r win-x64 --self-contained true
dotnet publish OpenPuckWeblessSettings.csproj -c Release -r win-arm64 --self-contained true
dotnet publish OpenPuckWeblessSettings.csproj -c Release -r linux-x64 --self-contained true
dotnet publish OpenPuckWeblessSettings.csproj -c Release -r osx-x64 --self-contained true
dotnet publish OpenPuckWeblessSettings.csproj -c Release -r osx-arm64 --self-contained true
```

Linux and macOS distributions must package a compatible native libusb library. Platform packages should be tested and signed according to the target operating system's requirements.

## Advanced mode and safety

Advanced mode exposes operations that can erase settings, controller bonds, or the firmware itself. Firmware updates, factory-reset images, DFU commands, factory erase, and full-board wipe can disconnect the target or require manual UF2 recovery.

- Back up settings and bonds before maintenance.
- Do not unplug a target while an update is being verified or applied.
- Test firmware updates and destructive commands on hardware whose configuration can be lost.
- Full-board wipe intentionally removes the application firmware and requires a valid UF2 image for recovery.

The software is provided without any guarantee that it cannot corrupt configuration, interrupt input, or leave a target requiring manual recovery.

## Upstream credit

OpenPuck firmware, protocol behavior, hardware support, and the original web configuration interface come from the [safijari/openpuck project](https://github.com/safijari/openpuck/tree/main). The protocol implementation in this application was derived from the upstream project's current `index.html` and supporting documentation. Please report firmware-specific issues and contribute firmware improvements upstream where appropriate.

## AI usage disclosure

This application was developed with substantial assistance from OpenAI Codex, including code generation, refactoring, documentation, and test creation. Automated tests and an attached-hardware connection probe have been run, but AI-generated code can contain incorrect assumptions, incomplete edge-case handling, or security and reliability defects.

Users and contributors should review changes carefully and independently validate firmware updates, USB behavior, backup restoration, and destructive operations before relying on them. AI assistance does not imply endorsement, warranty, or support from OpenAI or the upstream OpenPuck project.

## Third-party software

- [Avalonia UI](https://avaloniaui.net/) — MIT licensed.
- [LibUsbDotNet](https://github.com/LibUsbDotNet/LibUsbDotNet) — LGPL-3.0-or-later.
- [libusb](https://libusb.info/) — LGPL-2.1-or-later.

See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for bundled native-library attribution. Preserve all applicable license notices when redistributing binaries.
