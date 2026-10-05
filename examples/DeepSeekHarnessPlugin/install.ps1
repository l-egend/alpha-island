param(
    [string]$HarnessHome = 'E:\Deepseek Harness\.dsh',
    [string]$Profile = 'web',
    [ValidateRange(1024, 65535)][int]$Port = 8765,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
if ($Profile -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Invalid profile name' }
$profileDir = Join-Path $HarnessHome "profiles\$Profile"
$patchPath = Join-Path $profileDir 'cordis.patch.yml'
if (-not (Test-Path -LiteralPath $patchPath)) { throw "Profile patch not found: $patchPath" }
$pluginPath = (Join-Path $PSScriptRoot 'index.mjs').Replace('\', '/')
$text = [System.IO.File]::ReadAllText($patchPath)
$start = '# BEGIN ALPHA ISLAND AGENT'
$end = '# END ALPHA ISLAND AGENT'
if (($text.Contains($start)) -ne ($text.Contains($end))) { throw 'Incomplete Alpha Island marker; inspect the patch manually' }
$clean = [regex]::Replace($text, '(?ms)^# BEGIN ALPHA ISLAND AGENT\r?\n.*?^# END ALPHA ISLAND AGENT\r?\n?', '')
if (-not $Remove) {
    $backup = Join-Path $profileDir 'cordis.patch.before-alpha-island.yml'
    if (-not (Test-Path -LiteralPath $backup)) { [System.IO.File]::Copy($patchPath, $backup) }
    $clean = $clean.TrimEnd("`r", "`n") + "`n" + @"
# BEGIN ALPHA ISLAND AGENT
- insert:
    - id: alpha-island-agent
      name: '$pluginPath'
      config:
        endpoint: 'http://127.0.0.1:$Port/'
        timeoutSec: 120
# END ALPHA ISLAND AGENT
"@ + "`n"
}
[System.IO.File]::WriteAllText($patchPath, $clean, [System.Text.UTF8Encoding]::new($false))
Write-Output $(if ($Remove) { 'Alpha Island agent plugin removed.' } else { 'Alpha Island agent plugin configured. Live profiles reload automatically; otherwise restart Harness.' })
