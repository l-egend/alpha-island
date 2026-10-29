**[简体中文](README.md) | English**

# vivo Phone Connection

Read existing vivo Office Suite connection records to show the device, connection method, time and local network address, with new-connection notices.

- Version: **0.1.3**
- Plugin ID: `vivo.office`
- Minimum host plugin API: **2**

## Requirements

vivo Office Suite must be installed and have written connection records. Current online status requires the suite to be running with matching device information.

## Features

- Show the current device or the most recent device after disconnection.
- Distinguish current and historical local addresses without mixing in another device or the PC’s address.
- Notify about new connections without replaying old notices at startup or when first opening the card.

## Install and use

Follow the [common installation steps](../../README.en.md#install) first, then:

1. Connect the phone through vivo Office Suite at least once.
2. Install and enable the plugin, then open the vivo phone card.
3. After the suite closes or the phone disconnects, treat entries marked as the recent device as historical records.

## Notes and limits

- Only the required connection fields are read, not notes, calendars, accounts, transfer databases or phone battery levels.
- This is a connection-status card, not remote control, file transfer or notification mirroring.
- Missing records or fields are shown as unavailable rather than inferred as live status.

## Build from source

Run from the root of the plugin source checkout:

```powershell
dotnet build examples/VivoOfficePlugin/VivoOfficePlugin.csproj -c Release
```

Requires the .NET 8 SDK on Windows. A normal build only generates files; install the complete plugin folder manually. Workstation packaging commands are maintained outside this repository.

[Back to the plugin catalog](../../README.en.md#vivo-phone)
