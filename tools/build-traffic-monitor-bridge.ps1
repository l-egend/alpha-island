param([string]$Compiler = 'g++')
$ErrorActionPreference = 'Stop'
$taskProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskOutput = Join-Path $taskProject 'bin\TrafficMonitorBridge\x64'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCompiler = Get-Command $Compiler -ErrorAction Stop
$taskTarget = & $taskCompiler.Source -dumpmachine
if ($LASTEXITCODE -ne 0 -or $taskTarget -notmatch '^x86_64-.*mingw') { throw '需要 Windows x64 MinGW g++；MSVC 请单独构建 TrafficMonitorBridge.vcxproj。' }
$taskSource = Join-Path $taskProject 'plugins\TrafficMonitorBridge\TrafficMonitorBridge.cpp'
$taskDll = Join-Path $taskOutput 'AlphaIsland.TrafficMonitorBridge.dll'
& $taskCompiler.Source '-std=c++17' '-shared' '-O2' '-s' '-static' '-Wall' '-Wno-unused-parameter' '-Werror' '-Wl,--no-insert-timestamp' $taskSource '-ladvapi32' '-o' $taskDll
if ($LASTEXITCODE -ne 0) { throw 'TrafficMonitor 桥接 DLL 构建失败。' }
Copy-Item -LiteralPath (Join-Path $taskProject 'plugins\TrafficMonitorBridge\upstream\LICENSE') -Destination (Join-Path $taskOutput 'TrafficMonitor-LICENSE.txt') -Force
Get-Item -LiteralPath $taskDll | Select-Object FullName, Length
