param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pluginSource = Join-Path $repoRoot 'examples\OppoPodsPlugin'
if (-not $SkipBuild) {
    & dotnet build (Join-Path $pluginSource 'OppoPodsPlugin.csproj') -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Earbuds plugin build failed.' }
}
$pluginOutput = Join-Path $pluginSource ('bin\' + $Configuration + '\net8.0-windows10.0.19041.0')
$version = (Get-Content -LiteralPath (Join-Path $pluginSource 'plugin.json') -Raw | ConvertFrom-Json).version
$packageDirectory = Join-Path $repoRoot 'bin\packages'
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
$staging = Join-Path ([IO.Path]::GetTempPath()) ('oppo-pods-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    foreach ($name in @('AlphaIsland.Plugins.OppoPods.dll', 'AlphaIsland.Plugins.OppoPods.deps.json', 'plugin.json', 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $pluginOutput $name) -Destination $staging
    }
    $sourceDestination = Join-Path $staging 'source\examples\OppoPodsPlugin'
    foreach ($file in Get-ChildItem -LiteralPath $pluginSource -Recurse -File) {
        $relative = $file.FullName.Substring($pluginSource.Length + 1)
        if ($relative -match '^(bin|obj)[\\/]' -or ($file.Extension -notin @('.cs', '.csproj', '.json', '.md') -and $file.Name -ne 'LICENSE')) { continue }
        $destination = Join-Path $sourceDestination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    $sdkDestination = Join-Path $staging 'source\PluginSdk'
    New-Item -ItemType Directory -Path $sdkDestination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'PluginSdk') -File | Where-Object { $_.Extension -in @('.cs', '.csproj') } |
        Copy-Item -Destination $sdkDestination
    @'
对应源代码位于 source/，包含插件、协议与型号数据、SDK 接口、许可和修改说明。
需要 .NET SDK 8 与 Windows 10 2004 或以上。
构建：dotnet build source/examples/OppoPodsPlugin/OppoPodsPlugin.csproj -c Release -p:DeployPlugin=false
将生成的 AlphaIsland.Plugins.OppoPods.dll、deps.json 和 plugin.json 放在岛插件同一目录。
宿主提供 SDK 及 Windows SDK 运行库，发布插件时无需附带另一份。
'@ | Set-Content -LiteralPath (Join-Path $staging 'SOURCE-BUILD.txt') -Encoding utf8
    $archive = Join-Path $packageDirectory ('OppoPodsPlugin-' + $version + '.zip')
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -Force
    Get-Item -LiteralPath $archive | Select-Object FullName, Length
}
finally {
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedStaging.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedStaging).StartsWith('oppo-pods-package-')) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
