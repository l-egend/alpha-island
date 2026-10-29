**[简体中文](README.md) | English**

# TrafficMonitor Native Bridge

This x64 native component is installed in **TrafficMonitor**. It exposes network speeds and available daily totals to Alpha Island. It has no Alpha Island `plugin.json` and does not replace the Alpha Island-side plugin.

## Installation

1. Obtain `AlphaIsland.TrafficMonitorBridge.dll` and its license files from the separate native bridge package.
2. Exit TrafficMonitor, place the bridge DLL in the `plugins` folder beside its executable, then restart TrafficMonitor. Use a compatible x64 version.
3. Separately install and enable the [TrafficMonitor Network Data plugin](../../examples/TrafficMonitorPlugin/README.en.md) in Alpha Island, then select TrafficMonitor as its network source.
4. Run both applications as the same Windows user. If the source is waiting for the bridge, first check that TrafficMonitor loaded the DLL, then check the selected source in Alpha Island.

## Building

Open `TrafficMonitorBridge.vcxproj` with Visual Studio’s Desktop development with C++ workload and build for x64. The project uses C++17 and the v143 toolset; maintainers can also use an adapted x64 MinGW toolchain. Building a C# plugin does not automatically build or install this bridge.

Upstream interfaces and licenses are retained in [upstream/](upstream/). Preserve the attribution, licenses and corresponding source. This bridge and the Alpha Island-side plugin belong in two different applications’ plugin folders.
