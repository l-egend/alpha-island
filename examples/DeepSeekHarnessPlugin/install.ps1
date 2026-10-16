param(
    [string]$HarnessHome = $(if ($env:DSH_HOME) { $env:DSH_HOME } else { Join-Path $env:USERPROFILE '.dsh' }),
    [string]$Profile = 'web',
    [ValidateRange(1024, 65535)][int]$Port = 8765,
    [string]$IntegrationDirectory = (Join-Path $env:LOCALAPPDATA 'alpha-island\integrations\deepseek-harness'),
    [string]$ConfigDirectory = (Join-Path $env:LOCALAPPDATA 'alpha-island\config'),
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
if ($Profile -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Invalid profile name' }
$profileDir = Join-Path $HarnessHome "profiles\$Profile"
$patchPath = Join-Path $profileDir 'cordis.patch.yml'
if (-not (Test-Path -LiteralPath $patchPath)) { throw "Profile patch not found: $patchPath" }
$pluginPath = (Join-Path $IntegrationDirectory 'index.mjs').Replace('\', '/')
if ($pluginPath -match "[\r\n]") { throw 'Unsupported character in plugin path' }
$pluginLiteral = $pluginPath.Replace("'", "''")
$text = [System.IO.File]::ReadAllText($patchPath)
$start = '# BEGIN ALPHA ISLAND AGENT'
$end = '# END ALPHA ISLAND AGENT'
if (($text.Contains($start)) -ne ($text.Contains($end))) { throw 'Incomplete Alpha Island marker; inspect the patch manually' }
$clean = [regex]::Replace($text, '(?ms)^# BEGIN ALPHA ISLAND AGENT\r?\n.*?^# END ALPHA ISLAND AGENT\r?\n?', '')
if (-not $Remove) {
    $backup = Join-Path $profileDir 'cordis.patch.before-alpha-island.yml'
    if (-not (Test-Path -LiteralPath $backup)) { [System.IO.File]::Copy($patchPath, $backup) }
    New-Item -ItemType Directory -Path $IntegrationDirectory,$ConfigDirectory -Force | Out-Null
    $source = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'index.mjs')).Path
    if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($pluginPath)) { Copy-Item -LiteralPath $source -Destination $pluginPath -Force }
    [IO.File]::WriteAllText((Join-Path $ConfigDirectory 'deepseek-harness.json'), (@{home=$HarnessHome;profile=$Profile;endpoint="http://127.0.0.1:$Port/"} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $clean = $clean.TrimEnd("`r", "`n") + "`n" + @"
# BEGIN ALPHA ISLAND AGENT
- insert:
    - id: alpha-island-agent
      name: '$pluginLiteral'
      config:
        endpoint: 'http://127.0.0.1:$Port/'
        timeoutSec: 120
# END ALPHA ISLAND AGENT
"@ + "`n"
}
[System.IO.File]::WriteAllText($patchPath, $clean, [System.Text.UTF8Encoding]::new($false))
Write-Output $(if ($Remove) { 'Alpha Island agent plugin removed.' } else { 'Alpha Island agent plugin configured. Live profiles reload automatically; otherwise restart Harness.' })
