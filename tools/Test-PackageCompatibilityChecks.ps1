param([Parameter(Mandatory=$true)][string]$Stage)
$ErrorActionPreference = 'Stop'
$check = Join-Path $PSScriptRoot 'Test-PackageCompatibility.ps1'
& $check -Stage $Stage | Out-Null
$fixture = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin'))) ('package-compatibility-fixture-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$required = @('alpha-island.exe','alpha-island.dll','alpha-island.runtimeconfig.json','coreclr.dll','clrjit.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll')
function Expect-Rejection([string]$Reason, [string]$Pattern) {
    $rejected = $false
    try { & $check -Stage $fixture | Out-Null } catch { if ($_.Exception.Message -notmatch $Pattern) { throw }; $rejected = $true }
    if (!$rejected) { throw ('Package check accepted invalid delivery: ' + $Reason) }
    Write-Output ('PASS: ' + $Reason)
}
try {
    foreach ($file in Get-ChildItem -LiteralPath $Stage -Recurse -File) {
        if ($file.Name -notin $required -and ($file.Extension -notin @('.dll','.exe') -or [PackageImageMetadata]::Read($file.FullName).Managed)) { continue }
        $relative = $file.FullName.Substring((Resolve-Path -LiteralPath $Stage).Path.TrimEnd('\').Length+1)
        $destination = Join-Path $fixture $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    & $check -Stage $fixture | Out-Null
    Write-Output 'PASS: Self-contained runtime and supported PE binaries are accepted'
    $options = Join-Path $fixture 'options.json'
    [IO.File]::WriteAllText($options, '{}')
    Expect-Rejection 'Personal configuration is rejected' 'Personal data'
    Remove-Item -LiteralPath $options
    $hostFile = Join-Path $fixture 'hostfxr.dll'
    Move-Item -LiteralPath $hostFile -Destination ($hostFile + '.held')
    Expect-Rejection 'Missing runtime loader is rejected' 'Missing self-contained'
    Move-Item -LiteralPath ($hostFile + '.held') -Destination $hostFile
    $nativeFile = Join-Path $fixture 'incompatible.dll'
    $bytes = [IO.File]::ReadAllBytes((Join-Path $fixture 'alpha-island.exe'))
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    $bytes[$pe+4] = 0x64; $bytes[$pe+5] = 0xaa
    [IO.File]::WriteAllBytes($nativeFile, $bytes)
    Expect-Rejection 'ARM64 native binary inside x64 delivery is rejected' 'cannot run'
    Remove-Item -LiteralPath $nativeFile
    $bytes = [IO.File]::ReadAllBytes((Join-Path $fixture 'alpha-island.exe'))
    $offset = [Text.Encoding]::ASCII.GetString($bytes).IndexOf("KERNEL32.dll`0", [StringComparison]::OrdinalIgnoreCase)
    if ($offset -lt 0) { throw 'Native import fixture could not be prepared.' }
    [Array]::Copy([Text.Encoding]::ASCII.GetBytes('msvcp999.dll'),0,$bytes,$offset,12)
    [IO.File]::WriteAllBytes($nativeFile, $bytes)
    Expect-Rejection 'Missing VC++ runtime is rejected even on a developer PC' 'Unbundled native runtime'
    Remove-Item -LiteralPath $nativeFile
    [IO.File]::WriteAllBytes($nativeFile, [byte[]]@(0x4d,0x5a,0))
    Expect-Rejection 'Truncated executable is rejected' 'read|stream|header|读取|流|末尾'
    Remove-Item -LiteralPath $nativeFile
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin')).TrimEnd('\') + '\package-compatibility-fixture-'
    if (!$resolved.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture path escaped the test workspace.' }
    if (Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Refusing to recursively remove a linked fixture.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
