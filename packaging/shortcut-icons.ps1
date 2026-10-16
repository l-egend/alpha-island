# Update only shortcuts that target this installation. Versioned icon paths avoid
# Explorer reusing the old executable icon cache; no global cache reset is needed.
function Set-IslandShortcutIcons {
    param([Parameter(Mandatory)][string]$InstallDirectory,
          [Parameter(Mandatory)][string]$IconPath,
          [string[]]$ShortcutRoots)
    $install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $target = Join-Path $install 'alpha-island.exe'
    $icon = [IO.Path]::GetFullPath($IconPath)
    if (!$icon.StartsWith(($install + '\icons\'), [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $icon -PathType Leaf)) {
        throw 'Shortcut icon must be an existing asset inside this installation.'
    }
    if (!$ShortcutRoots) {
        $ShortcutRoots = @([Environment]::GetFolderPath('Desktop'), (Join-Path ([Environment]::GetFolderPath('Programs')) 'Alpha Island'))
    }
    if (!('AlphaIslandShortcutNotifications' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AlphaIslandShortcutNotifications {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);
}
'@
    }
    $shell = New-Object -ComObject WScript.Shell
    $updated = 0
    try {
        foreach ($root in $ShortcutRoots) {
            if (!(Test-Path -LiteralPath $root -PathType Container)) { continue }
            foreach ($file in Get-ChildItem -LiteralPath $root -Filter '*.lnk' -File) {
                $shortcut = $null
                try {
                    $shortcut = $shell.CreateShortcut($file.FullName)
                    if (!$shortcut.TargetPath -or ![string]::Equals([IO.Path]::GetFullPath($shortcut.TargetPath), $target, [StringComparison]::OrdinalIgnoreCase)) { continue }
                    $shortcut.IconLocation = $icon + ',0'
                    $shortcut.Save()
                    [AlphaIslandShortcutNotifications]::SHChangeNotify(0x2000, 0x0005, $file.FullName, [IntPtr]::Zero)
                    $updated++
                } finally { if ($shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) } }
            }
        }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    return $updated
}
