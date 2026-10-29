**[简体中文](README.md) | English**

# Clock Example

Show the time and date with a custom title, seconds, 12/24-hour format and font size; also serves as a plugin development example.

- Version: **1.0.1**
- Plugin ID: `example.clock`
- Minimum host plugin API: **2**

## Requirements

No account, network access or extra application is required. It uses the current Windows local time.

## Features

- Customize the card title and choose whether to display seconds.
- Switch between 12/24-hour time and adjust the time font from 28 to 44 DIP.
- Pause the UI timer when the card is inactive or hidden.

## Install and use

Follow the [common installation steps](../../README.en.md#install) first, then:

1. Install and enable the Clock Example plugin.
2. Open its card from the component menu.
3. Adjust the title, time format and font size in Plugin settings.

## Notes and limits

- Time and date come from the system; displayed language follows the main application.
- The example demonstrates cards, settings, services and scheduled task lifetimes. A normal build does not install it into the main app.

<a id="development"></a>

## Development example

See [ClockPlugin.cs](ClockPlugin.cs) for card, setting, service and scheduler registration, and the [SDK contracts](../../PluginSdk/PluginContracts.cs) for interfaces. Keep a unique ID and a manifest whose assembly and entry type match your plugin.

## Build from source

Run from the root of the plugin source checkout:

```powershell
dotnet build examples/ClockPlugin/ClockPlugin.csproj -c Release
```

Requires the .NET 8 SDK on Windows. A normal build only generates files; install the complete plugin folder manually. Workstation packaging commands are maintained outside this repository.

[Back to the plugin catalog](../../README.en.md#clock)
