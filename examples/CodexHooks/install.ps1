param(
    [string]$NodePath = '',
    [string]$CodexDirectory = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }),
    [string]$ConfigDirectory = (Join-Path $env:LOCALAPPDATA 'alpha-island\config'),
    [string]$ProjectRoot = '',
    [switch]$AllProjects,
    [string]$IntegrationDirectory = '',
    [string]$RuntimeDirectory = '',
    [int]$Port = 8765,
    [ValidateSet('native', 'island')][string]$ApprovalMode = 'native',
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
if ($Port -lt 1 -or $Port -gt 65535) { throw 'Port must be in 1..65535' }
if (!$Remove -and !$AllProjects -and !$ProjectRoot) { throw 'Choose all projects or supply -ProjectRoot.' }
if ($AllProjects -and $ProjectRoot) { throw 'Choose either all projects or a project directory.' }
if (!$IntegrationDirectory) { $IntegrationDirectory = Join-Path (Split-Path $ConfigDirectory -Parent) 'integrations\codex' }
if (!$RuntimeDirectory) { $RuntimeDirectory = Join-Path (Split-Path $ConfigDirectory -Parent) 'runtime\codex' }
$adapter = Join-Path $IntegrationDirectory 'hook.mjs'
$configPath = Join-Path $ConfigDirectory 'codex-hooks.json'
$hookPath = Join-Path $CodexDirectory 'hooks.json'
$config = if (Test-Path -LiteralPath $hookPath) { Get-Content -LiteralPath $hookPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{ hooks = [pscustomobject]@{} } }
if (!$config.hooks) { $config | Add-Member -Force NoteProperty hooks ([pscustomobject]@{}) }
if (Test-Path -LiteralPath $hookPath) {
    $backupDirectory = Join-Path $ConfigDirectory 'backups'
    $backup = Join-Path $backupDirectory ('hooks.before-alpha-island-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '.json')
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    Copy-Item -LiteralPath $hookPath -Destination $backup
}
# Only handlers belonging to this adapter are removed or replaced; other hooks are retained.
foreach ($property in @($config.hooks.PSObject.Properties)) {
    $groups = @()
    foreach ($group in @($property.Value)) {
        $handlers = @($group.hooks | Where-Object { $_.statusMessage -notlike 'Alpha Island / *' })
        if ($handlers.Count) { $group.hooks = $handlers; $groups += $group }
    }
    $config.hooks.PSObject.Properties.Remove($property.Name)
    if ($groups.Count) { $config.hooks | Add-Member NoteProperty $property.Name $groups }
}
if (!$Remove) {
    if (!$NodePath) { $NodePath = (Get-Command node -ErrorAction Stop).Source }
    $node = (Resolve-Path -LiteralPath $NodePath).Path
    foreach ($commandPath in @($node, $adapter, $configPath)) {
        if ($commandPath -match '["\r\n%]') { throw 'Unsupported character in command path' }
    }
    New-Item -ItemType Directory -Path $ConfigDirectory,$IntegrationDirectory -Force | Out-Null
    $source = (Resolve-Path (Join-Path $PSScriptRoot 'hook.mjs')).Path
    if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($adapter)) { Copy-Item -LiteralPath $source -Destination $adapter -Force }
    $settings = @{ endpoint = "http://127.0.0.1:$Port/"; projectRoot = $(if ($AllProjects) { '' } else { (Resolve-Path -LiteralPath $ProjectRoot).Path }); allProjects = [bool]$AllProjects;
        runtimeDir = $RuntimeDirectory; timeoutSec = 120; approvalMode = $ApprovalMode; hookPath = [IO.Path]::GetFullPath($hookPath) }
    [System.IO.File]::WriteAllText($configPath, ($settings | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
    # Explicit PowerShell invocation works under both cmd and PowerShell hook launchers.
    $nodeLiteral = "'" + $node.Replace("'", "''") + "'"
    $adapterLiteral = "'" + $adapter.Replace("'", "''") + "'"
    $configLiteral = "'" + $configPath.Replace("'", "''") + "'"
    $command = 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "& ' + $nodeLiteral + ' ' + $adapterLiteral + ' --config ' + $configLiteral + '"'
    foreach ($event in @('SessionStart','UserPromptSubmit','PreToolUse','PostToolUse','PermissionRequest','PreCompact','PostCompact','Stop','Interrupt','SessionEnd')) {
        $handler = @{ type = 'command'; command = $command; timeout = $(if ($event -eq 'PermissionRequest' -and $ApprovalMode -eq 'island') { 130 } elseif ($event -in @('Interrupt','SessionEnd')) { 3 } else { 5 }); statusMessage = "Alpha Island / $event" }
        $groups = @(@($config.hooks.$event) | Where-Object { $null -ne $_ })
        $newGroup = [pscustomobject]@{ hooks = @($handler) }
        $config.hooks | Add-Member -Force NoteProperty $event (@($groups) + @($newGroup))
    }
}
New-Item -ItemType Directory -Path $CodexDirectory -Force | Out-Null
[System.IO.File]::WriteAllText($hookPath, ($config | ConvertTo-Json -Depth 30), (New-Object System.Text.UTF8Encoding($false)))
Write-Output $(if ($Remove) { 'Removed Alpha Island Codex Hooks; other hooks preserved.' } else { 'Configured Alpha Island Codex Hooks. Review and trust them with Codex /hooks.' })
