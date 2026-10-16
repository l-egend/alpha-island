param(
    [string]$NodePath = '',
    [string]$ClaudeDirectory = $(if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }),
    [string]$ConfigDirectory = (Join-Path $env:LOCALAPPDATA 'alpha-island\config'),
    [string]$IntegrationDirectory = '',
    [string]$NativeVersion = '',
    [ValidateRange(1024,65535)][int]$Port = 8765,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
if (!$IntegrationDirectory) { $IntegrationDirectory = Join-Path (Split-Path $ConfigDirectory -Parent) 'integrations\claude-code' }
$adapter = Join-Path $IntegrationDirectory 'hook.mjs'
$configPath = Join-Path $ConfigDirectory 'claude-code-hooks.json'
$hookPath = Join-Path $ClaudeDirectory 'settings.json'
foreach ($path in @($hookPath,$configPath,$adapter)) {
    if (![IO.Path]::IsPathRooted($path)) { throw 'An absolute local configuration path is required.' }
    for ($cursor = [IO.Path]::GetFullPath($path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked configuration paths are not supported.' }
    }
}
$config = if (Test-Path -LiteralPath $hookPath) { Get-Content -LiteralPath $hookPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($config -isnot [pscustomobject]) { throw 'Claude settings must be a JSON object; original file was preserved.' }
if (!$config.hooks) { $config | Add-Member -Force NoteProperty hooks ([pscustomobject]@{}) }
if ($config.hooks -isnot [pscustomobject]) { throw 'Claude hooks must be a JSON object; original file was preserved.' }
$original = if (Test-Path -LiteralPath $hookPath) { [IO.File]::ReadAllText($hookPath) } else { '' }
foreach ($property in @($config.hooks.PSObject.Properties)) {
    $groups = @()
    foreach ($group in @($property.Value)) {
        $handlers = @($group.hooks | Where-Object { $_.statusMessage -notlike 'Alpha Island / Claude Code / *' })
        if ($handlers.Count) { $group.hooks = $handlers; $groups += $group }
    }
    $config.hooks.PSObject.Properties.Remove($property.Name)
    if ($groups.Count) { $config.hooks | Add-Member NoteProperty $property.Name $groups }
}
if (!$Remove) {
    if (!$NodePath) { $NodePath = (Get-Command node.exe -ErrorAction Stop).Source }
    $node = (Resolve-Path -LiteralPath $NodePath).Path
    foreach ($commandPath in @($node,$adapter,$configPath)) { if ($commandPath -match '["\r\n]') { throw 'Unsupported command path.' } }
    New-Item -ItemType Directory -Path $ConfigDirectory,$IntegrationDirectory -Force | Out-Null
    $source = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'hook.mjs')).Path
    if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($adapter)) { Copy-Item -LiteralPath $source -Destination $adapter -Force }
    $settings = @{ endpoint="http://127.0.0.1:$Port/";hookPath=[IO.Path]::GetFullPath($hookPath);approvalMode='native';allProjects=$true }
    [IO.File]::WriteAllText($configPath, ($settings | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $script = "& '" + $node.Replace("'","''") + "' '" + $adapter.Replace("'","''") + "' --config '" + $configPath.Replace("'","''") + "'"
    $command = 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $events = @('SessionStart','UserPromptSubmit','PreToolUse','PostToolUse','PostToolUseFailure','PermissionRequest','Notification','PreCompact','Stop','SessionEnd')
    # Unknown/older CLIs get the established lifecycle events; never put an unknown event in their settings.
    $parsedVersion = $null
    if ([Version]::TryParse($NativeVersion,[ref]$parsedVersion) -and $parsedVersion -ge [Version]'2.1.139') {
        $events += @('PostCompact','StopFailure','Elicitation','ElicitationResult')
    }
    foreach ($event in $events) {
        $group = [pscustomobject]@{ hooks=@([pscustomobject]@{type='command';command=$command;timeout=3;statusMessage="Alpha Island / Claude Code / $event"}) }
        if ($event -eq 'Notification') { $group | Add-Member NoteProperty matcher 'permission_prompt|idle_prompt|elicitation_dialog|elicitation_url_dialog|agent_needs_input' }
        $groups = @(@($config.hooks.$event) | Where-Object { $null -ne $_ })
        $config.hooks | Add-Member -Force NoteProperty $event (@($groups) + @($group))
    }
}
$text = $config | ConvertTo-Json -Depth 50
New-Item -ItemType Directory -Path $ClaudeDirectory -Force | Out-Null
if ($original -and $original -ne $text) {
    $backups = Join-Path $ConfigDirectory 'backups'; New-Item -ItemType Directory -Path $backups -Force | Out-Null
    Copy-Item -LiteralPath $hookPath -Destination (Join-Path $backups ('claude-settings.before-alpha-island-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '.json'))
}
$temporary = $hookPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$replacementBackup = $temporary + '.backup'
try {
    [IO.File]::WriteAllText($temporary,$text,[Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $hookPath) { [IO.File]::Replace($temporary,$hookPath,$replacementBackup) } else { [IO.File]::Move($temporary,$hookPath) }
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    if (Test-Path -LiteralPath $replacementBackup) { Remove-Item -LiteralPath $replacementBackup -Force }
}
Write-Output $(if ($Remove) { 'Removed Alpha Island Claude hooks; other settings preserved.' } else { 'Configured Claude Code for all projects. Restart existing sessions to load hooks.' })
