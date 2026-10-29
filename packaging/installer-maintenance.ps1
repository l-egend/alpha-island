param(
    [ValidateSet('Install', 'Uninstall', 'ValidateRemoval')][string]$Mode = 'Install',
    [Parameter(Mandatory)][string]$InstallDirectory,
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'alpha-island'),
    [string]$RunValue = 'AlphaIsland',
    [string]$HookDirectory = '',
    [switch]$PurgeData,
    [switch]$FunctionsOnly
)
$ErrorActionPreference = 'Stop'

function Assert-OrdinaryTree([string]$Root) {
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ([IO.Path]::GetFileName($full) -ne 'alpha-island') { throw 'Refusing to remove an unexpected data directory.' }
    for ($item = [IO.DirectoryInfo]::new($full); $null -ne $item; $item = $item.Parent) {
        if ($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Directory link is not allowed: $($item.FullName)" }
    }
    if (Test-Path -LiteralPath $full) {
        foreach ($item in Get-ChildItem -LiteralPath $full -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked data cannot be removed automatically: $($item.FullName)" }
            if ($item.PSIsContainer) { Assert-ChildTree $item.FullName $full }
        }
    }
    return $full
}
function Assert-ChildTree([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Data path escaped its root.' }
    foreach ($item in Get-ChildItem -LiteralPath $full -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked data cannot be removed automatically: $($item.FullName)" }
        if ($item.PSIsContainer) { Assert-ChildTree $item.FullName $Root }
    }
}
function Write-AtomicJson([string]$Path, $Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $temporary = $Path + '.install-' + [Guid]::NewGuid().ToString('N')
    $replaceBackup = $Path + '.install-backup-' + [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, $replaceBackup) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        if (Test-Path -LiteralPath $replaceBackup) { Remove-Item -LiteralPath $replaceBackup -Force }
    }
}
function Get-InstallerHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
function Remove-IslandHooks([string]$Path, [switch]$ValidateOnly) {
    if (!(Test-Path -LiteralPath $Path)) { return }
    if ((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Hook configuration cannot be a file link.' }
    $config = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$config.hooks) { return }
    $changed = $false
    foreach ($property in @($config.hooks.PSObject.Properties)) {
        $groups = @()
        foreach ($group in @($property.Value)) {
            $handlers = @($group.hooks | Where-Object { $_.statusMessage -notlike 'Alpha Island / *' })
            if ($handlers.Count -ne @($group.hooks).Count) { $changed = $true }
            if ($handlers.Count) { $group.hooks = $handlers; $groups += $group }
        }
        $config.hooks.PSObject.Properties.Remove($property.Name)
        if ($groups.Count) { $config.hooks | Add-Member NoteProperty $property.Name $groups }
    }
    if ($changed -and !$ValidateOnly) { Write-AtomicJson $Path $config }
}
function Get-IslandHookPaths {
    if ($HookDirectory) { return (Join-Path $HookDirectory 'hooks.json') }
    $paths = @((Join-Path $env:USERPROFILE '.codex\hooks.json'))
    if ($env:CODEX_HOME) { $paths += Join-Path $env:CODEX_HOME 'hooks.json' }
    $registration = Join-Path $DataRoot 'config\codex-hooks.json'
    if (Test-Path -LiteralPath $registration) {
        $saved = Get-Content -LiteralPath $registration -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($saved.hookPath -and [IO.Path]::IsPathRooted($saved.hookPath)) { $paths += $saved.hookPath }
    }
    return $paths | Select-Object -Unique
}
function Remove-ExternalAgentIntegrations([string]$Root, [switch]$ValidateOnly) {
    $claude = Join-Path $Root 'config\claude-code-hooks.json'
    if (Test-Path -LiteralPath $claude) {
        $registration = Get-Content -LiteralPath $claude -Raw -Encoding UTF8 | ConvertFrom-Json
        $path = $registration.hookPath
        if ($path -and (Test-Path -LiteralPath $path)) {
            if (![IO.Path]::IsPathRooted($path)) { throw 'Invalid Claude hook path.' }
            Remove-IslandHooks $path -ValidateOnly:$ValidateOnly
        }
    }
    $antigravity = Join-Path $Root 'config\antigravity-hooks.json'
    if (Test-Path -LiteralPath $antigravity) {
        $settings = Get-Content -LiteralPath $antigravity -Raw -Encoding UTF8 | ConvertFrom-Json
        $path = $settings.hookPath
        if ($path -and (Test-Path -LiteralPath $path)) {
            if (!( [IO.Path]::IsPathRooted($path)) -or ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid Antigravity hook path.' }
            $hooks = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($hooks -isnot [pscustomobject]) { throw 'Invalid Antigravity hook configuration.' }
            $owned = $hooks.PSObject.Properties['alpha-island-agent-bridge']
            if ($owned) {
                $ownershipText = $owned.Value | ConvertTo-Json -Depth 40
                foreach ($match in [regex]::Matches($ownershipText, '-EncodedCommand ([A-Za-z0-9+/=]+)')) {
                    try { $ownershipText += [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($match.Groups[1].Value)) } catch {}
                }
                if ($ownershipText -notmatch 'antigravity-hooks\.json') { throw 'Unknown Antigravity hook with the Alpha Island name.' }
                $hooks.PSObject.Properties.Remove('alpha-island-agent-bridge')
                if (!$ValidateOnly) { Write-AtomicJson $path $hooks }
            }
        }
    }
    $harness = Join-Path $Root 'config\deepseek-harness.json'
    if (Test-Path -LiteralPath $harness) {
        $settings = Get-Content -LiteralPath $harness -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($settings.profile -notmatch '^[a-zA-Z0-9_-]+$' -or ![IO.Path]::IsPathRooted($settings.home)) { throw 'Invalid DeepSeek Harness profile registration.' }
        $path = Join-Path $settings.home ('profiles\' + $settings.profile + '\cordis.patch.yml')
        if (Test-Path -LiteralPath $path) {
            if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Harness patch cannot be a file link.' }
            $text = [IO.File]::ReadAllText($path)
            $start = '# BEGIN ALPHA ISLAND AGENT'; $end = '# END ALPHA ISLAND AGENT'
            if ($text.Contains($start) -ne $text.Contains($end)) { throw 'Incomplete Alpha Island Harness marker.' }
            $clean = [regex]::Replace($text, '(?ms)^# BEGIN ALPHA ISLAND AGENT\r?\n.*?^# END ALPHA ISLAND AGENT\r?\n?', '')
            if (!$ValidateOnly -and $text -ne $clean) { [IO.File]::WriteAllText($path, $clean, [Text.UTF8Encoding]::new($false)) }
        }
    }
}
function Install-BundledPlugins([string]$Source, [string]$Root) {
    if (!(Test-Path -LiteralPath $Source)) { return }
    $ledgerPath = Join-Path $Root 'config\installed-plugins.json'
    $ledger = if (Test-Path -LiteralPath $ledgerPath) { Get-Content -LiteralPath $ledgerPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
    foreach ($plugin in Get-ChildItem -LiteralPath $Source -Directory) {
        $manifest = Get-Content -LiteralPath (Join-Path $plugin.FullName 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($manifest.id -notmatch '^[a-z0-9][a-z0-9._-]*$' -or $plugin.Name -ne $manifest.id) { throw 'Invalid bundled plugin id.' }
        $destination = Join-Path $Root ('plugins\' + $manifest.id)
        $previous = $ledger.PSObject.Properties[$manifest.id]
        # Missing after a previous install means the user uninstalled it. An upgrade keeps that choice.
        if (!(Test-Path -LiteralPath $destination) -and $previous) { continue }
        if (Test-Path -LiteralPath $destination) {
            if (!$previous) { continue } # Existing manually supplied plugin belongs to the user.
            $modified = $false
            foreach ($file in $previous.Value.PSObject.Properties) {
                $path = Join-Path $destination $file.Name
                if (!(Test-Path -LiteralPath $path) -or (Get-InstallerHash $path) -ne $file.Value) { $modified = $true; break }
            }
            if ($modified) { continue }
        }
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        $hashes = [ordered]@{}
        foreach ($file in Get-ChildItem -LiteralPath $plugin.FullName -File) {
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
            $hashes[$file.Name] = Get-InstallerHash $file.FullName
        }
        $ledger | Add-Member -Force NoteProperty $manifest.id ([pscustomobject]$hashes)
    }
    Write-AtomicJson $ledgerPath $ledger
}

if ($FunctionsOnly) { return }
$install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
try {
$data = Assert-OrdinaryTree $DataRoot
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$registered = (Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue).$RunValue
$ownedStartup = $registered -and $registered -match '^"[^"]*\\alpha-island\.exe" --startup$'
if ($Mode -eq 'Install') {
    . (Join-Path $PSScriptRoot 'shortcut-icons.ps1')
    $installedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'alpha-island.exe')).FileVersion
    $iconVersion = ($installedVersion -split '\.')[0..2] -join '.'
    Set-IslandShortcutIcons -InstallDirectory $install -IconPath (Join-Path $install ('icons\alpha-island-' + $iconVersion + '.ico')) | Out-Null
    if ($ownedStartup) { Set-ItemProperty -LiteralPath $runKey -Name $RunValue -Value ('"' + (Join-Path $install 'alpha-island.exe') + '" --startup') }
    $setup = Join-Path $install 'agent-integrations\setup.ps1'
    if (Test-Path -LiteralPath $setup) {
        $port = 8765
        $optionsPath = Join-Path $data 'config\options.json'
        if (Test-Path -LiteralPath $optionsPath) {
            try { $savedPort = (Get-Content -LiteralPath $optionsPath -Raw -Encoding UTF8 | ConvertFrom-Json).Extras.AgentBridgePort; if ($savedPort -ge 1024 -and $savedPort -le 65535) { $port = [int]$savedPort } } catch {}
        }
        $setupReport = & $setup -Mode Auto -InstallDirectory $install -DataRoot $data -Port $port
        [IO.File]::WriteAllText((Join-Path $install 'installer\agent-setup-report.json'), ($setupReport -join [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    }
    return
}
if ($PurgeData) {
    foreach ($path in Get-IslandHookPaths) { Remove-IslandHooks $path -ValidateOnly }
    Remove-ExternalAgentIntegrations $data -ValidateOnly
}
if ($Mode -eq 'ValidateRemoval') { return }
# Release file handles through the application's normal WPF close/exit path before cleaning data.
foreach ($process in Get-Process alpha-island -ErrorAction SilentlyContinue) {
    if ($process.Path -eq (Join-Path $install 'alpha-island.exe')) {
        $null = $process.CloseMainWindow()
        if (!$process.WaitForExit(5000)) { throw 'Please exit Alpha Island from its tray menu, then retry uninstalling.' }
    }
}
if ($ownedStartup) {
    $registeredPath = $registered.Substring(1, $registered.IndexOf('"', 1) - 1)
    if ($PurgeData -or [IO.Path]::GetDirectoryName($registeredPath) -eq $install) {
        Remove-ItemProperty -LiteralPath $runKey -Name $RunValue
    }
}
if ($PurgeData) {
    foreach ($path in Get-IslandHookPaths) { Remove-IslandHooks $path }
    Remove-ExternalAgentIntegrations $data
    # Revalidate the full tree and absolute target immediately before the recursive deletion.
    $validatedData = Assert-OrdinaryTree $data
    if (Test-Path -LiteralPath $validatedData) { Remove-Item -LiteralPath $validatedData -Recurse -Force }
}
} catch {
    try { [IO.File]::AppendAllText((Join-Path $install 'installer\maintenance-errors.log'), $_.Exception.ToString() + [Environment]::NewLine) } catch { }
    throw
}
