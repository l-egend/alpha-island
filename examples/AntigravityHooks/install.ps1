param(
    [string]$NodePath = '',
    [string]$HookPath = (Join-Path $env:USERPROFILE '.gemini\config\hooks.json'),
    [string]$ConfigDirectory = (Join-Path $env:LOCALAPPDATA 'alpha-island\config'),
    [string]$IntegrationDirectory = (Join-Path $env:LOCALAPPDATA 'alpha-island\integrations\antigravity'),
    [string]$RuntimeDirectory = (Join-Path $env:LOCALAPPDATA 'alpha-island\runtime\antigravity'),
    [ValidateRange(1024,65535)][int]$Port = 8765,
    [ValidateRange(5,120)][int]$TimeoutSec = 120,
    [ValidateSet('auto','confirm')][string]$ApprovalMode = 'auto',
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$hookName = 'alpha-island-agent-bridge'
$configPath = Join-Path $ConfigDirectory 'antigravity-hooks.json'
$adapter = Join-Path $IntegrationDirectory 'hook.mjs'
$config = if (Test-Path -LiteralPath $HookPath) {
    $parsed = Get-Content -LiteralPath $HookPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($parsed -isnot [pscustomobject]) { throw 'Antigravity hooks must be a JSON object; original file was not changed.' }
    $parsed
} else { [pscustomobject]@{} }
$owned = $config.PSObject.Properties[$hookName]
if ($owned) {
    $ownershipText = $owned.Value | ConvertTo-Json -Depth 30
    foreach ($match in [regex]::Matches($ownershipText, '-EncodedCommand ([A-Za-z0-9+/=]+)')) {
        try { $ownershipText += [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($match.Groups[1].Value)) } catch {}
    }
    if ($ownershipText -notmatch 'antigravity-hooks\.json') { throw 'The Alpha Island hook name belongs to an unknown handler; original file was not changed.' }
}
if (!$Remove) {
    if (!$NodePath) { $NodePath = (Get-Command node -ErrorAction Stop).Source }
    $node = (Resolve-Path -LiteralPath $NodePath).Path
    foreach ($taskPath in @($node,$adapter,$configPath)) {
        if ($taskPath -match '["\r\n%]') { throw 'Unsupported character in command path' }
    }
}
New-Item -ItemType Directory -Path $ConfigDirectory -Force | Out-Null
if (Test-Path -LiteralPath $HookPath) {
    $backups = Join-Path $ConfigDirectory 'backups'
    New-Item -ItemType Directory -Path $backups -Force | Out-Null
    Copy-Item -LiteralPath $HookPath -Destination (Join-Path $backups ('antigravity-hooks-before-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '.json'))
}
$config.PSObject.Properties.Remove($hookName)
if (!$Remove) {
    New-Item -ItemType Directory -Path $IntegrationDirectory -Force | Out-Null
    $source = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'hook.mjs')).Path
    if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($adapter)) { Copy-Item -LiteralPath $source -Destination $adapter -Force }
    $settings = @{endpoint="http://127.0.0.1:$Port/";runtimeDir=$RuntimeDirectory;timeoutSec=$TimeoutSec;approvalMode=$ApprovalMode;policySchemaVersion=1;hookPath=[IO.Path]::GetFullPath($HookPath);nativeConfigPath=(Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($HookPath))) 'config.json')}
    [IO.File]::WriteAllText($configPath, ($settings | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $definition = [pscustomobject]@{enabled=$true}
    foreach ($event in @('PreInvocation','PostInvocation','PreToolUse','PostToolUse','Stop')) {
        # Antigravity's Go launcher escapes embedded quotes before cmd /c. An encoded
        # PowerShell command has no shell metacharacters and safely handles arbitrary paths.
        $script = "& '" + $node.Replace("'", "''") + "' '" + $adapter.Replace("'", "''") + "' --config '" + $configPath.Replace("'", "''") + "' --event " + $event
        $command = 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
        $handler = @{type='command';command=$command;timeout=$(if($event -eq 'PreToolUse'){$TimeoutSec+10}else{5})}
        $handlers = if($event -in @('PreToolUse','PostToolUse')) { @(@{matcher='*';hooks=@($handler)}) } else { @($handler) }
        $definition | Add-Member -MemberType NoteProperty -Name $event -Value @($handlers)
    }
    $config | Add-Member NoteProperty $hookName $definition
}
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($HookPath)) -Force | Out-Null
[IO.File]::WriteAllText($HookPath, ($config | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
Write-Output $(if($Remove){'Removed Alpha Island Antigravity hooks; other hooks preserved.'}else{'Installed Alpha Island Antigravity hooks. Restart Antigravity if hooks are not reloaded. Grants continue through native permissions.'})
