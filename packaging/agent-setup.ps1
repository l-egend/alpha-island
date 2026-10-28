param(
    [ValidateSet('Detect','Auto','Install','Remove')][string]$Mode = 'Detect',
    [ValidateSet('all','codex','antigravity','deepseek-harness','claude-code')][string]$Provider = 'all',
    [string]$InstallDirectory = (Split-Path $PSScriptRoot -Parent),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'alpha-island'),
    [string]$UserProfileDirectory = $env:USERPROFILE,
    [string]$LocalDataDirectory = $env:LOCALAPPDATA,
    [string]$CodexHome = '', [string]$HarnessHome = '', [string]$ClaudeHome = '', [string]$ProjectRoot = '',
    [switch]$AllProjects,
    [ValidateRange(1024,65535)][int]$Port = 8765,
    [switch]$Isolated, [switch]$FunctionsOnly
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Read-SetupJson([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) { return $null }
    $value = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($value -isnot [pscustomobject]) { throw 'Configuration must be a JSON object.' }
    return $value
}
function Get-SetupHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','') }
    finally { $hash.Dispose(); $stream.Dispose() }
}
function Get-SetupDevice {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes([Environment]::MachineName + '|' + $UserProfileDirectory))).Replace('-','') }
    finally { $hash.Dispose() }
}
function Write-SetupJson([string]$Path, $Value) {
    $directory = [IO.Path]::GetDirectoryName($Path)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, $Path + '.bak') }
        else { [IO.File]::Move($temporary, $Path) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
function Assert-SetupPath([string]$Path) {
    if (!$Path -or ![IO.Path]::IsPathRooted($Path)) { throw 'An absolute local path is required.' }
    $full = [IO.Path]::GetFullPath($Path)
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked configuration paths are not supported.' }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}
function Get-SetupHome([string]$Name, [string]$Override, [string]$Fallback) {
    if ($Override) { return [IO.Path]::GetFullPath($Override) }
    if (!$Isolated) {
        foreach ($scope in @('Process','User','Machine')) {
            $candidate = [Environment]::GetEnvironmentVariable($Name,$scope)
            if ($candidate -and [IO.Path]::IsPathRooted($candidate) -and (Test-Path -LiteralPath $candidate -PathType Container)) { return [IO.Path]::GetFullPath($candidate) }
        }
    }
    return [IO.Path]::GetFullPath($Fallback)
}
function Get-SetupNode {
    $bundled = Join-Path $InstallDirectory 'agent-integrations\runtime\node.exe'
    if (Test-Path -LiteralPath $bundled -PathType Leaf) { return $bundled }
    $private = Join-Path $DataRoot 'integrations\runtime\node.exe'
    if (Test-Path -LiteralPath $private -PathType Leaf) { return $private }
    if (!$Isolated) {
        $command = Get-Command node.exe -ErrorAction SilentlyContinue
        if ($command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) { return $command.Source }
    }
    return ''
}
function Test-CodexApplication {
    if ($Isolated) { return $false }
    if ((Get-Command codex.exe -ErrorAction SilentlyContinue) -or (Get-Command codex.cmd -ErrorAction SilentlyContinue)) { return $true }
    if (@(Get-Process -Name Codex -ErrorAction SilentlyContinue).Count) { return $true }
    if (Test-Path -LiteralPath (Join-Path $LocalDataDirectory 'OpenAI\Codex')) { return $true }
    if (Get-Command Get-AppxPackage -ErrorAction SilentlyContinue) {
        try { return @(Get-AppxPackage -Name 'OpenAI.Codex*' -ErrorAction Stop).Count -gt 0 } catch { }
    }
    return $false
}
function Get-AgentTargets {
    $codex = Get-SetupHome 'CODEX_HOME' $CodexHome (Join-Path $UserProfileDirectory '.codex')
    $harness = Get-SetupHome 'DSH_HOME' $HarnessHome (Join-Path $UserProfileDirectory '.dsh')
    $claude = Get-SetupHome 'CLAUDE_CONFIG_DIR' $ClaudeHome (Join-Path $UserProfileDirectory '.claude')
    $claudeVersion = ''
    if (!$Isolated -and (Test-Path -LiteralPath $claude -PathType Container)) {
        $claudeCommand = Get-Command claude.exe -ErrorAction SilentlyContinue
        $claudeExecutable = if ($claudeCommand) { $claudeCommand.Source } else { Join-Path $UserProfileDirectory '.local\bin\claude.exe' }
        if (Test-Path -LiteralPath $claudeExecutable -PathType Leaf) {
            try { $versionText = & $claudeExecutable --version 2>$null; if ("$versionText" -match '(\d+\.\d+\.\d+)') { $claudeVersion=$Matches[1] } } catch { }
        }
    }
    $agHook = Join-Path $UserProfileDirectory '.gemini\config\hooks.json'
    $agRegistration = Read-SetupJson (Join-Path $DataRoot 'config\antigravity-hooks.json')
    # Migrate the old hard-coded confirm default; retain choices made with the new policy schema.
    $agApprovalMode = 'auto'
    if ($agRegistration -and $agRegistration.policySchemaVersion -eq 1 -and $agRegistration.approvalMode -in @('auto','confirm')) { $agApprovalMode = $agRegistration.approvalMode }
    $profile = 'web'
    $registration = Read-SetupJson (Join-Path $DataRoot 'config\deepseek-harness.json')
    if ($registration -and $registration.home -eq $harness -and $registration.profile -match '^[a-zA-Z0-9_-]+$') { $profile = $registration.profile }
    if (!(Test-Path -LiteralPath (Join-Path $harness "profiles\$profile\cordis.patch.yml"))) {
        $profiles = Join-Path $harness 'profiles'
        if (Test-Path -LiteralPath $profiles) {
            $found = Get-ChildItem -LiteralPath $profiles -Directory | Where-Object { $_.Name -match '^[a-zA-Z0-9_-]+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'cordis.patch.yml')) } | Select-Object -First 1
            if ($found) { $profile = $found.Name }
        }
    }
    $scope = $ProjectRoot
    $codexRegistration = Read-SetupJson (Join-Path $DataRoot 'config\codex-hooks.json')
    if ($AllProjects -and $ProjectRoot) { throw 'Choose either all projects or a project directory.' }
    # First installation is portable across drives; existing explicit scopes remain unchanged.
    $globalScope = [bool]$AllProjects -or (!$ProjectRoot -and (!$codexRegistration -or $codexRegistration.allProjects -eq $true))
    $approvalMode = 'native'
    if ($codexRegistration -and $codexRegistration.approvalMode -in @('native','island')) { $approvalMode = $codexRegistration.approvalMode }
    if (!$globalScope -and !$scope -and $codexRegistration -and $codexRegistration.projectRoot -and (Test-Path -LiteralPath $codexRegistration.projectRoot -PathType Container)) { $scope = $codexRegistration.projectRoot }
    $codexHooks = Join-Path $codex 'hooks.json'
    $dshPatch = Join-Path $harness "profiles\$profile\cordis.patch.yml"
    @(
        [pscustomobject]@{id='codex';name='Codex';detected=((Test-Path -LiteralPath $codex -PathType Container) -or (Test-CodexApplication));profileMissing=(!(Test-Path -LiteralPath $codex -PathType Container));target=$codexHooks;home=$codex;profile='';scope=$scope;allProjects=$globalScope;adapter='CodexHooks';file='hook.mjs';config='codex-hooks.json';owned=$false;approvalMode=$approvalMode},
        [pscustomobject]@{id='antigravity';name='Antigravity';detected=((Test-Path -LiteralPath (Split-Path $agHook -Parent)) -or (Test-Path -LiteralPath (Join-Path $LocalDataDirectory 'Programs\antigravity\Antigravity.exe')));target=$agHook;home='';profile='';scope='';adapter='AntigravityHooks';file='hook.mjs';config='antigravity-hooks.json';owned=$false;approvalMode=$agApprovalMode},
        [pscustomobject]@{id='deepseek-harness';name='DeepSeek Harness';detected=(Test-Path -LiteralPath $dshPatch -PathType Leaf);target=$dshPatch;home=$harness;profile=$profile;scope='';adapter='DeepSeekHarnessPlugin';file='index.mjs';config='deepseek-harness.json';owned=$false},
        [pscustomobject]@{id='claude-code';name='Claude Code';detected=(Test-Path -LiteralPath $claude -PathType Container);target=(Join-Path $claude 'settings.json');home=$claude;profile='';scope='';allProjects=$true;adapter='ClaudeCodeHooks';file='hook.mjs';config='claude-code-hooks.json';owned=$false;nativeVersion=$claudeVersion}
    ) | ForEach-Object {
        if (Test-Path -LiteralPath $_.target -PathType Leaf) {
            $text = [IO.File]::ReadAllText($_.target)
            $_.owned = switch ($_.id) {
                'codex' { $text.Contains('Alpha Island / ') }
                'antigravity' { $text.Contains('alpha-island-agent-bridge') }
                'claude-code' { $text.Contains('Alpha Island / Claude Code / ') }
                default { $text.Contains('# BEGIN ALPHA ISLAND AGENT') }
            }
            if ($_.id -eq 'antigravity' -and $_.owned) {
                try {
                    $agConfig = $text | ConvertFrom-Json
                    $_ | Add-Member NoteProperty nativeDisabled ($agConfig.'alpha-island-agent-bridge'.enabled -eq $false)
                } catch { }
            }
            if ($_.id -eq 'claude-code') {
                try { $cc = $text | ConvertFrom-Json; $_ | Add-Member NoteProperty nativeDisabled ($cc.disableAllHooks -eq $true -or $cc.allowManagedHooksOnly -eq $true) } catch { }
            }
        }
        $_
    }
}
function Invoke-AgentSetup {
    $configDirectory = Join-Path $DataRoot 'config'
    $ledgerPath = Join-Path $configDirectory 'integrations.json'
    $ledger = Read-SetupJson $ledgerPath
    if (!$ledger) { $ledger = [pscustomobject]@{version=1;providers=[pscustomobject]@{}} }
    if (!$ledger.providers -or $ledger.providers -isnot [pscustomobject]) { throw 'Invalid integration preferences; original file was preserved.' }
    $device = Get-SetupDevice
    $imported = $ledger.device -and $ledger.device -ne $device
    $targets = @(Get-AgentTargets)
    $node = Get-SetupNode
    $needsRuntime = @($targets | Where-Object { $_.detected -and $_.id -ne 'deepseek-harness' }).Count -gt 0
    if ($needsRuntime -and $node -and $Mode -in @('Auto','Install') -and $node -eq (Join-Path $InstallDirectory 'agent-integrations\runtime\node.exe')) {
        $runtimeDirectory = Join-Path $DataRoot 'integrations\runtime'
        $null = Assert-SetupPath $runtimeDirectory
        New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
        $runtime = Join-Path $runtimeDirectory 'node.exe'
        if (!(Test-Path -LiteralPath $runtime) -or (Get-SetupHash $runtime) -ne (Get-SetupHash $node)) {
            Copy-Item -LiteralPath $node -Destination $runtime -Force
            foreach ($file in @('LICENSE','SHA256.txt')) { Copy-Item -LiteralPath (Join-Path (Split-Path $node -Parent) $file) -Destination $runtimeDirectory -Force }
        }
        $node = $runtime
    }
    $adapterRoot = Join-Path $InstallDirectory 'agent-integrations'
    if (!(Test-Path -LiteralPath (Join-Path $adapterRoot 'CodexHooks'))) { $adapterRoot = Join-Path $InstallDirectory 'examples' }
    $results = @()
    $changed = $false
    foreach ($target in $targets) {
        $preference = $ledger.providers.PSObject.Properties[$target.id]
        $disabled = ($preference -and $preference.Value.enabled -eq $false) -or $target.nativeDisabled -eq $true
        $status = if ($disabled) { 'disabled' } elseif ($target.owned) { 'configured' } elseif ($preference) { 'removed' } elseif ($target.detected) { 'available' } else { 'not-installed' }
        $detail = ''
        $selected = $Provider -eq 'all' -or $Provider -eq $target.id
        $mutate = $selected -and ($Mode -eq 'Install' -or $Mode -eq 'Remove' -or ($Mode -eq 'Auto' -and !$disabled -and $target.detected))
        $targetMoved = $preference -and $preference.Value.target -and $preference.Value.target -ne $target.target
        if ($Mode -eq 'Auto' -and $preference -and !$target.owned -and !$imported -and !$targetMoved) { $mutate = $false; $status = if ($disabled) {'disabled'} else {'removed'} }
        if ($target.id -eq 'codex' -and $target.detected -and $target.profileMissing -and $Mode -ne 'Remove') { $status='profile-required';$mutate=$false }
        if ($Mode -ne 'Remove' -and $target.id -ne 'deepseek-harness' -and !$node -and $target.detected) { $status='runtime-missing';$mutate=$false }
        if ($Mode -ne 'Remove' -and $target.id -eq 'codex' -and !$target.allProjects -and !$target.scope -and $target.detected) { $status='scope-required';$mutate=$false }
        if ($mutate) {
            try {
                if ($Mode -ne 'Remove' -and !$target.detected) { throw 'Application configuration was not found. Start the application once, then detect again.' }
                $null = Assert-SetupPath $DataRoot
                $null = Assert-SetupPath $target.target
                $installer = Join-Path $adapterRoot ($target.adapter + '\install.ps1')
                $adapterFile = Join-Path $adapterRoot ($target.adapter + '\' + $target.file)
                $arguments = @{ConfigDirectory=$configDirectory;Port=$Port;Remove=($Mode -eq 'Remove')}
                $integrationDirectory = Join-Path $DataRoot ('integrations\' + $target.id)
                switch ($target.id) {
                    'codex' { $arguments.NodePath=$node;$arguments.CodexDirectory=$target.home;$arguments.ProjectRoot=$target.scope;$arguments.AllProjects=[bool]$target.allProjects;$arguments.ApprovalMode=$target.approvalMode }
                    'antigravity' { $arguments.NodePath=$node;$arguments.HookPath=$target.target;$arguments.IntegrationDirectory=$integrationDirectory;$arguments.RuntimeDirectory=(Join-Path $DataRoot 'runtime\antigravity');$arguments.ApprovalMode=$target.approvalMode }
                    'deepseek-harness' { $arguments.HarnessHome=$target.home;$arguments.Profile=$target.profile;$arguments.IntegrationDirectory=$integrationDirectory }
                    'claude-code' { $arguments.NodePath=$node;$arguments.ClaudeDirectory=$target.home;$arguments.IntegrationDirectory=$integrationDirectory;$arguments.NativeVersion=$target.nativeVersion }
                }
                # Fingerprints only contain runtime paths, port and adapter hashes, never API credentials.
                $stamp = "$node|$Port|$($target.target)|$($target.scope)|$($target.allProjects)|$($target.approvalMode)|$($target.nativeVersion)|$(Get-SetupHash $adapterFile)|$(Get-SetupHash $installer)"
                if ($Mode -ne 'Auto' -or !$preference -or $preference.Value.stamp -ne $stamp) { $null = & $installer @arguments }
                $enabled = $Mode -ne 'Remove'
                $ledger.providers | Add-Member -Force NoteProperty $target.id ([pscustomobject]@{enabled=$enabled;stamp=$stamp;target=$target.target})
                $changed=$true;$status=if($enabled){'configured'}else{'disabled'}
                if ($enabled -and $target.id -eq 'claude-code' -and $target.nativeDisabled) { $status='disabled';$detail='Claude Code has disabled user hooks; enable them in Claude settings.' }
            } catch { $status='error';$detail=$_.Exception.Message }
        }
        $results += [pscustomobject]@{id=$target.id;name=$target.name;detected=[bool]$target.detected;status=$status;detail=$detail;scope=$target.scope;profile=$target.profile;allProjects=($target.allProjects -eq $true)}
    }
    if ($changed) { $null = Assert-SetupPath $DataRoot; $ledger | Add-Member -Force NoteProperty device $device; Write-SetupJson $ledgerPath $ledger }
    [pscustomobject]@{runtimeAvailable=[bool]$node;providers=$results}
}
if (!$FunctionsOnly) {
    try { Invoke-AgentSetup | ConvertTo-Json -Depth 8 -Compress }
    catch { [pscustomobject]@{runtimeAvailable=$false;providers=@();error=$_.Exception.Message} | ConvertTo-Json -Compress; exit 1 }
}
