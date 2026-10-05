param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pluginSource = Join-Path $repoRoot 'examples\VivoOfficePlugin'
if (-not $SkipBuild) {
    & dotnet build (Join-Path $pluginSource 'VivoOfficePlugin.csproj') -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Vivo plugin build failed.' }
}
$pluginOutput = Join-Path $pluginSource ('bin\' + $Configuration + '\net8.0-windows')
$version = (Get-Content -LiteralPath (Join-Path $pluginSource 'plugin.json') -Raw | ConvertFrom-Json).version
$packageDirectory = Join-Path $repoRoot 'bin\packages'
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
$staging = Join-Path ([IO.Path]::GetTempPath()) ('vivo-office-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    foreach ($name in @('AlphaIsland.Plugins.VivoOffice.dll', 'AlphaIsland.Plugins.VivoOffice.deps.json', 'plugin.json', 'README.md')) {
        Copy-Item -LiteralPath (Join-Path $pluginOutput $name) -Destination $staging
    }
    $sourceDestination = Join-Path $staging 'source\examples\VivoOfficePlugin'
    New-Item -ItemType Directory -Path $sourceDestination -Force | Out-Null
    Get-ChildItem -LiteralPath $pluginSource -File | Where-Object { $_.Extension -in @('.cs', '.csproj', '.json', '.md') } |
        Copy-Item -Destination $sourceDestination
    $sdkDestination = Join-Path $staging 'source\PluginSdk'
    New-Item -ItemType Directory -Path $sdkDestination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'PluginSdk') -File | Where-Object { $_.Extension -in @('.cs', '.csproj') } |
        Copy-Item -Destination $sdkDestination
    $archive = Join-Path $packageDirectory ('VivoOfficePlugin-' + $version + '.zip')
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -Force
    Get-Item -LiteralPath $archive | Select-Object FullName, Length
}
finally {
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedStaging.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedStaging).StartsWith('vivo-office-package-')) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
