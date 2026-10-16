param([string]$Root = (Join-Path $PSScriptRoot '..\bin\ShortcutIconTest'))
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$test = [IO.Path]::GetFullPath($Root)
if (!$test.StartsWith(($repo + '\bin\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Test output must stay inside the project bin directory.' }
. (Join-Path $repo 'packaging\shortcut-icons.ps1')
$install = Join-Path $test 'installation'
$icons = Join-Path $install 'icons'
$links = Join-Path $test 'shortcuts'
New-Item -ItemType Directory -Path $icons,$links -Force | Out-Null
$icon = Join-Path $icons 'alpha-island-test.ico'
Copy-Item -LiteralPath (Join-Path $repo 'Assets\alpha-island.ico') -Destination $icon -Force
$exe = Join-Path $install 'alpha-island.exe'
[IO.File]::WriteAllText($exe, 'fixture')
$shell = New-Object -ComObject WScript.Shell
try {
    foreach ($name in @('Alpha Island','renamed island','unrelated')) {
        $shortcut = $shell.CreateShortcut((Join-Path $links ($name + '.lnk')))
        $shortcut.TargetPath = if ($name -eq 'unrelated') { Join-Path $env:SystemRoot 'notepad.exe' } else { $exe }
        $shortcut.Arguments = '--fixture'
        $shortcut.IconLocation = (Join-Path $env:SystemRoot 'notepad.exe') + ',0'
        $shortcut.Save()
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
    }
    $count = Set-IslandShortcutIcons -InstallDirectory $install -IconPath $icon -ShortcutRoots @($links)
    if ($count -ne 2) { throw 'Expected precisely two owned shortcuts.' }
    foreach ($name in @('Alpha Island','renamed island','unrelated')) {
        $shortcut = $shell.CreateShortcut((Join-Path $links ($name + '.lnk')))
        if ($shortcut.Arguments -ne '--fixture') { throw 'Shortcut arguments changed.' }
        if ($name -eq 'unrelated') {
            if ($shortcut.IconLocation -eq ($icon + ',0')) { throw 'Unrelated shortcut changed.' }
        } elseif ($shortcut.IconLocation -ne ($icon + ',0')) { throw 'Owned shortcut did not adopt versioned icon.' }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
    }
    $rejected = $false
    try { Set-IslandShortcutIcons -InstallDirectory $install -IconPath (Join-Path $repo 'Assets\alpha-island.ico') -ShortcutRoots @($links) | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw 'An icon outside the installation was accepted.' }
    Write-Host 'PASS: owned and renamed shortcuts updated, arguments preserved, unrelated shortcut untouched, external icon path rejected.'
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
