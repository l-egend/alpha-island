**[简体中文](README.md) | English**

# Earbud Controls

Show per-part battery readings and controls for supported earbuds, with options such as noise control, EQ presets and game mode when available.

- Version: **0.1.6**
- Plugin ID: `oppo.pods`
- Minimum host plugin API: **6**

## Requirements

Usually requires pairing and an audio connection in Windows first. Controls depend on the brand, model, firmware and backend.

## Features

- Read battery levels for the left earbud, right earbud and case when reported.
- Expose supported noise control, built-in EQ, game mode, spatial audio or dual-device options.
- Configure automatic connection, reconnection, a preferred device and model overrides.

## Install and use

Follow the [common installation steps](../../README.en.md#install) first, then:

1. Pair the earbuds in Windows Bluetooth settings. Close other earbud managers that may occupy the private control channel.
2. Install and enable the plugin, open the Earbuds card, refresh, select the device and connect.
3. Use the controls actually available for that device. Connecting and reading status do not automatically change its sound settings.

## Notes and limits

- Supports some OPPO / OnePlus / realme, vivo / iQOO and Huawei devices. Xiaomi / Redmi, Edifier and AirPods support depends on the backend; some are read-only.
- Working Windows audio does not guarantee an available private control channel. AirPods currently rely on decodable unencrypted BLE advertisements; encrypted advertisements and a Windows control channel are not supported.
- Custom EQ editing, firmware updates and gesture configuration are not implemented. A model entry does not mean every feature has been tested on hardware.

License and upstream modifications: [LICENSE](LICENSE) · [third-party notices](THIRD-PARTY-NOTICES.md).

Reference project: [OppoPodsManager](https://github.com/Zhaoyi-ya/OppoPodsManager)

## Build from source

Run from the root of the plugin source checkout:

```powershell
dotnet build examples/OppoPodsPlugin/OppoPodsPlugin.csproj -c Release
```

Requires the .NET 8 SDK on Windows. A normal build only generates files; install the complete plugin folder manually. Workstation packaging commands are maintained outside this repository.

[Back to the plugin catalog](../../README.en.md#earbuds)
