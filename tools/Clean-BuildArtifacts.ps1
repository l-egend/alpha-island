[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [string]$Workspace = '',
    [ValidateRange(2,20)][int]$KeepInstallerVersions = 2,
    [string]$CurrentStage = '',
    [switch]$Apply,
    [string]$ReportPath = ''
)
$ErrorActionPreference = 'Stop'
if (!$Workspace) { $Workspace = Split-Path $PSScriptRoot -Parent }
$workspaceRoot = [IO.Path]::GetFullPath($Workspace).TrimEnd('\')
if (!(Test-Path -LiteralPath (Join-Path $workspaceRoot 'alpha-island.csproj') -PathType Leaf)) { throw 'A verified Alpha Island project directory is required.' }
$binRoot = Join-Path $workspaceRoot 'bin'
if (!(Test-Path -LiteralPath $binRoot -PathType Container)) { throw 'No build output directory exists.' }
$stageRoot = Join-Path $binRoot 'InstallerStage'
$installerRoot = Join-Path $binRoot 'Installers'
$script:cleanupPlan = [Collections.Generic.List[object]]::new()
$script:skipped = [Collections.Generic.List[object]]::new()

function Assert-ArtifactPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (!$full.StartsWith($binRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target must be inside this project bin directory.' }
    $cursor = $full
    while ($cursor -and $cursor.Length -ge $workspaceRoot.Length) {
        if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Cleanup refuses directory links.' }
        if ($cursor -eq $workspaceRoot) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}
function Measure-ArtifactTree([string]$Path) {
    $bytes = 0L
    $stack = [Collections.Generic.Stack[IO.FileSystemInfo]]::new()
    if (([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::Directory) -ne 0) { $stack.Push([IO.DirectoryInfo]::new($Path)) }
    else { $stack.Push([IO.FileInfo]::new($Path)) }
    while ($stack.Count) {
        $item = $stack.Pop()
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Cleanup refuses directory or file links.' }
        if ($item.Name -eq '.git') { throw 'Cleanup preserves embedded Git repositories.' }
        if ($item -is [IO.FileInfo]) { $bytes += $item.Length; continue }
        foreach ($child in $item.EnumerateFileSystemInfos()) { $stack.Push($child) }
    }
    return $bytes
}
$runningPaths = @()
foreach ($process in [Diagnostics.Process]::GetProcesses()) {
    try { if ($process.MainModule.FileName) { $runningPaths += $process.MainModule.FileName } } catch {} finally { $process.Dispose() }
}
$tracked = @()
if (Test-Path -LiteralPath (Join-Path $workspaceRoot '.git')) {
    $tracked = @(& git -c ('safe.directory=' + $workspaceRoot.Replace('\','/')) -C $workspaceRoot ls-files -- bin 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify tracked files; cleanup stopped.' }
}
function Add-Artifact([string]$Path, [string]$Reason) {
    try {
        $full = Assert-ArtifactPath $Path
        $prefix = $full + '\'
        if (@($runningPaths | Where-Object { $_ -eq $full -or $_.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) }).Count) { throw 'Artifact is used by a running process.' }
        foreach ($relative in $tracked) {
            $file = Join-Path $workspaceRoot $relative
            if ($file -eq $full -or $file.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact contains tracked files.' }
        }
        $script:cleanupPlan.Add([pscustomobject]@{Path=$full;Reason=$Reason;Bytes=(Measure-ArtifactTree $full)})
    } catch { $script:skipped.Add([pscustomobject]@{Path=$Path;Reason=$_.Exception.Message}) }
}

$packages = @()
if (Test-Path -LiteralPath $installerRoot) {
    foreach ($file in Get-ChildItem -LiteralPath $installerRoot -File) {
        if ($file.Name -match '^AlphaIsland-(\d+\.\d+\.\d+(?:\.\d+)?)-win-x64-Setup\.exe$') {
            $packages += [pscustomobject]@{Version=[version]$Matches[1];Text=$Matches[1];File=$file.FullName}
        }
    }
}
$kept = @($packages | Sort-Object Version -Descending | Select-Object -First $KeepInstallerVersions)
if (!$kept.Count) { throw 'No installer exists; cleanup stopped to protect unpublished output.' }
$keptVersions = @($kept | ForEach-Object Text)
if (!$CurrentStage) {
    $receipt = Join-Path $installerRoot ('AlphaIsland-' + $kept[0].Text + '-build.json')
    if (Test-Path -LiteralPath $receipt) { $CurrentStage = (Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json).Stage }
}
if ($CurrentStage) {
    $CurrentStage = Assert-ArtifactPath $CurrentStage
    if ([IO.Path]::GetDirectoryName($CurrentStage) -ne $stageRoot -or [IO.Path]::GetFileName($CurrentStage) -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?-\d{8}-\d{6}$' -or
        !(Test-Path -LiteralPath $CurrentStage -PathType Container)) { throw 'The retained package stage is invalid.' }
}
foreach ($file in Get-ChildItem -LiteralPath $installerRoot -File) {
    if ($file.Name -match '^AlphaIsland-(\d+\.\d+\.\d+(?:\.\d+)?)-(win-x64-Setup\.exe|build\.json|compatibility\.json)$' -and $Matches[1] -notin $keptVersions) {
        Add-Artifact $file.FullName 'Retain newest installer versions'
    }
}
if ($CurrentStage -and (Test-Path -LiteralPath $stageRoot)) {
    foreach ($folder in Get-ChildItem -LiteralPath $stageRoot -Directory) {
        if ($folder.Name -match '^\d+\.\d+\.\d+(?:\.\d+)?-\d{8}-\d{6}$' -and $folder.FullName -ne $CurrentStage) {
            Add-Artifact $folder.FullName 'Obsolete self-contained package stage'
        }
    }
}
$sourcesRoot = Join-Path $binRoot 'InstallerSources'
if (Test-Path -LiteralPath $sourcesRoot) {
    foreach ($folder in Get-ChildItem -LiteralPath $sourcesRoot -Directory) {
        if ($folder.Name -match '^\d+\.\d+\.\d+(?:\.\d+)?$' -and $folder.Name -notin $keptVersions) { Add-Artifact $folder.FullName 'Obsolete packaged source copy' }
    }
}
foreach ($folder in Get-ChildItem -LiteralPath $binRoot -Directory) {
    if ($folder.Name -match '^RegressionRuntime\d+$') { Add-Artifact $folder.FullName 'Reproducible old regression runtime' }
}
$removed = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[object]]::new()
foreach ($item in $script:cleanupPlan) {
    if ($Apply -and $PSCmdlet.ShouldProcess($item.Path, 'Remove reproducible build artifact')) {
        try {
            $verified = Assert-ArtifactPath $item.Path
            $null = Measure-ArtifactTree $verified
            Remove-Item -LiteralPath $verified -Recurse -Force
            $removed.Add($item)
        } catch { $errors.Add([pscustomobject]@{Path=$item.Path;Reason=$_.Exception.Message}) }
    }
}
$result = [ordered]@{
    Workspace=$workspaceRoot;Applied=[bool]$Apply;RetainedVersions=$keptVersions;RetainedStage=$CurrentStage
    PlannedCount=$script:cleanupPlan.Count;PlannedBytes=($script:cleanupPlan | Measure-Object Bytes -Sum).Sum
    RemovedCount=$removed.Count;RemovedBytes=($removed | Measure-Object Bytes -Sum).Sum
    Skipped=@($script:skipped.ToArray());Errors=@($errors.ToArray());Removed=@($removed.ToArray())
}
if ($ReportPath) {
    $report = [IO.Path]::GetFullPath($ReportPath)
    if (!$report.StartsWith($binRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup report must be inside this project bin directory.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($report)) | Out-Null
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report -Encoding utf8
}
$result | ConvertTo-Json -Depth 6
if ($errors.Count) { throw 'Some build artifacts could not be removed; inspect the cleanup report.' }
