param(
    [Parameter(Mandatory=$true)][string]$Stage,
    [string]$ReportPath = ''
)
$ErrorActionPreference = 'Stop'
$stageRoot = (Resolve-Path -LiteralPath $Stage).Path.TrimEnd('\')
if (!(Test-Path -LiteralPath $stageRoot -PathType Container)) { throw 'A published package directory is required.' }
if (!('PackageImageMetadata' -as [type])) { Add-Type -TypeDefinition (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PackageImageMetadata.cs.txt') -Raw) }
foreach ($required in @('alpha-island.exe','alpha-island.dll','alpha-island.runtimeconfig.json','coreclr.dll','clrjit.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $stageRoot $required) -PathType Leaf)) { throw ('Missing self-contained package file: ' + $required) }
}
$runtime = (Get-Content -LiteralPath (Join-Path $stageRoot 'alpha-island.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
if ($runtime.framework -or $runtime.frameworks -or @($runtime.includedFrameworks).Count -lt 2) { throw 'Package must carry its own .NET and WPF runtime.' }
foreach ($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
    if (@($runtime.includedFrameworks | Where-Object name -eq $framework).Count -ne 1) { throw ('Missing bundled framework: ' + $framework) }
}
$files = @(Get-ChildItem -LiteralPath $stageRoot -Recurse -File)
$optionalPlugins=@($files|Where-Object { $_.Name -eq 'plugin.json' -or $_.Name -match '^AlphaIsland\.(Plugins\.|Examples\.ClockPlugin)' -or $_.Name -eq 'AlphaIsland.TrafficMonitorBridge.dll' })
if($optionalPlugins.Count){throw 'The main installer must not contain optional plugin packages.'}
$personal = @($files | Where-Object {
    $relative = $_.FullName.Substring($stageRoot.Length+1)
    $relative -match '(^|\\)(options\.json(?:\.bak)?|secrets\.dat|clipboard_history\.json|error\.log|integrations\.json)$' -or
    $relative -match '(^|\\)(sessions|cache|logs)(\\|$)' -or $_.Extension -in @('.pfx','.key','.dmp')
})
if ($personal.Count) { throw ('Personal data or debugging output entered the package: ' + (($personal | ForEach-Object {$_.FullName.Substring($stageRoot.Length+1)}) -join ', ')) }
$metadata = @()
$privateRuntime = '^(vcruntime\d|msvcp\d|concrt\d|libgcc|libstdc\+\+|libwinpthread)'
foreach ($file in @($files | Where-Object Extension -in @('.dll','.exe'))) {
    $image = [PackageImageMetadata]::Read($file.FullName)
    $relative = $file.FullName.Substring($stageRoot.Length+1)
    if ($image.Machine -ne 0x8664 -and !($image.Managed -and $image.Machine -eq 0x14c -and !$image.Requires32Bit)) {
        throw ('A binary cannot run in the win-x64 package: ' + $relative)
    }
    # Managed references are resolved by .NET; native imports must be bundled or
    # supplied by Windows. Do not let this developer machine's VC++ install hide
    # a redistributable missing from the deliverable.
    if (!$image.Managed) {
        foreach ($dependency in $image.Imports) {
            if ($dependency -ne [IO.Path]::GetFileName($dependency)) { throw ('Invalid imported DLL name: ' + $relative) }
            if ((Test-Path -LiteralPath (Join-Path $file.DirectoryName $dependency)) -or (Test-Path -LiteralPath (Join-Path $stageRoot $dependency))) { continue }
            if ($dependency -match $privateRuntime) { throw ('Unbundled native runtime: ' + $relative + ' -> ' + $dependency) }
            if ($dependency -match '^(api-ms-|ext-ms-)') { continue }
            if (!(Test-Path -LiteralPath (Join-Path ([Environment]::SystemDirectory) $dependency) -PathType Leaf)) {
                throw ('Unresolved native dependency: ' + $relative + ' -> ' + $dependency)
            }
        }
    }
    $metadata += [pscustomobject]@{Path=$relative;Machine=('0x{0:X4}' -f $image.Machine);Managed=$image.Managed;Imports=@($image.Imports)}
}
$report = [ordered]@{
    Passed=$true;CheckedAt=(Get-Date).ToString('o');Stage=$stageRoot;Target='win-x64';MinimumInstallerBuild=19041
    BundledFrameworks=@($runtime.includedFrameworks);Files=$files.Count;Binaries=$metadata.Count
    NativeBinaries=@($metadata | Where-Object {!$_.Managed});PersonalFiles=0
    OptionalPlugins=0
    Limitations=@('Native imports are checked against bundled files and this Windows system; an older OS still requires real-machine testing.','Optional dynamically loaded APIs and device firmware are covered by runtime degradation checks, not PE imports.','Windows ARM64 x64 emulation has not been tested.')
}
if ($ReportPath) {
    $reportFile = [IO.Path]::GetFullPath($ReportPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportFile)) | Out-Null
    $report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $reportFile -Encoding utf8
}
[pscustomobject]@{Passed=$true;Files=$files.Count;Binaries=$metadata.Count;NativeBinaries=@($metadata|Where-Object {!$_.Managed}).Count;PersonalFiles=0} | ConvertTo-Json -Compress
