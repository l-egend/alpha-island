$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $repo ('bin\ArtifactRetentionTests\' + [Guid]::NewGuid().ToString('N'))
$bin = Join-Path $fixture 'bin'
$packages = Join-Path $bin 'Installers'
$stages = Join-Path $bin 'InstallerStage'
[IO.Directory]::CreateDirectory($packages) | Out-Null
[IO.Directory]::CreateDirectory($stages) | Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'alpha-island.csproj'), '<Project />')
foreach ($version in @('0.1.9','0.1.10','0.1.11')) {
    foreach ($suffix in @('win-x64-Setup.exe','build.json','compatibility.json')) {
        [IO.File]::WriteAllText((Join-Path $packages ('AlphaIsland-' + $version + '-' + $suffix)), 'fixture')
    }
    $stage = Join-Path $stages ($version + '-20261001-120000')
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    [IO.File]::WriteAllText((Join-Path $stage 'coreclr.dll'), 'fixture runtime')
    $sources = Join-Path $bin ('InstallerSources\' + $version)
    [IO.Directory]::CreateDirectory($sources) | Out-Null
    [IO.File]::WriteAllText((Join-Path $sources 'source.cs'), 'fixture source copy')
}
$retainedStage = Join-Path $stages '0.1.11-20261001-120000'
$runtime = Join-Path $bin 'RegressionRuntime123'; [IO.Directory]::CreateDirectory($runtime) | Out-Null
[IO.File]::WriteAllText((Join-Path $runtime 'runtime.dll'), 'old test copy')
foreach ($name in @('Debug','InstallerTools','UserArtwork','RegressionRuntimeCustom')) {
    $folder = Join-Path $bin $name; [IO.Directory]::CreateDirectory($folder) | Out-Null
    [IO.File]::WriteAllText((Join-Path $folder 'preserve.txt'), 'preserve')
}
[IO.File]::WriteAllText((Join-Path $packages 'AlphaIsland-DeviceBatteryPlugin-1.1.0.zip'), 'independent plugin')
$gitCache = Join-Path $bin 'RegressionRuntime124'; [IO.Directory]::CreateDirectory((Join-Path $gitCache '.git')) | Out-Null
[IO.File]::WriteAllText((Join-Path $gitCache '.git\HEAD'), 'preserve Git')
$script = Join-Path $PSScriptRoot 'Clean-BuildArtifacts.ps1'
$checks = 0
function Check([bool]$Value,[string]$Message) {
    if (!$Value) { throw ('FAIL: ' + $Message) }
    $script:checks++; Write-Output ('PASS: ' + $Message)
}
$plan = & $script -Workspace $fixture -CurrentStage $retainedStage | ConvertFrom-Json
Check ((Test-Path -LiteralPath (Join-Path $packages 'AlphaIsland-0.1.9-win-x64-Setup.exe')) -and $plan.RemovedCount -eq 0) 'Preview does not delete artifacts'
Check (($plan.RetainedVersions -join ',') -eq '0.1.11,0.1.10') 'Version ordering is numeric rather than lexical'
$report = Join-Path $bin 'cleanup.json'
$null = & $script -Workspace $fixture -CurrentStage $retainedStage -Apply -Confirm:$false -ReportPath $report
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
Check (@(Get-ChildItem -LiteralPath $packages -Filter '*Setup.exe').Count -eq 2) 'Only the newest two main installers remain'
Check (!(Test-Path -LiteralPath (Join-Path $packages 'AlphaIsland-0.1.9-build.json')) -and !(Test-Path -LiteralPath (Join-Path $packages 'AlphaIsland-0.1.9-compatibility.json'))) 'Obsolete installer receipts are removed with their package'
Check ((Test-Path -LiteralPath $retainedStage) -and @(Get-ChildItem -LiteralPath $stages -Directory).Count -eq 1) 'Only the explicitly retained expanded stage remains'
Check (!(Test-Path -LiteralPath $runtime)) 'Old regression runtime copies are removed'
Check ((Test-Path -LiteralPath (Join-Path $bin 'InstallerSources\0.1.10')) -and !(Test-Path -LiteralPath (Join-Path $bin 'InstallerSources\0.1.9'))) 'Source copies follow the same two-version retention rule'
Check ((Test-Path -LiteralPath (Join-Path $bin 'Debug\preserve.txt')) -and (Test-Path -LiteralPath (Join-Path $bin 'InstallerTools\preserve.txt'))) 'Current Debug output and build tools are preserved'
Check ((Test-Path -LiteralPath (Join-Path $bin 'UserArtwork\preserve.txt')) -and (Test-Path -LiteralPath (Join-Path $bin 'RegressionRuntimeCustom\preserve.txt'))) 'Unknown assets and similarly named directories are preserved'
Check ((Test-Path -LiteralPath (Join-Path $packages 'AlphaIsland-DeviceBatteryPlugin-1.1.0.zip'))) 'Independent plugin archives are preserved'
Check ((Test-Path -LiteralPath (Join-Path $gitCache '.git\HEAD')) -and @($result.Skipped).Count -eq 1) 'Embedded Git repositories are explicitly protected'
$rejected = $false
try { $null = & $script -Workspace $fixture -CurrentStage (Split-Path $fixture -Parent) } catch { $rejected = $true }
Check $rejected 'A stage outside the allowed bin subtree is rejected'
$link = Join-Path $bin 'RegressionRuntime125'
$null = New-Item -ItemType Junction -Path $link -Target (Join-Path $bin 'UserArtwork')
$linked = & $script -Workspace $fixture -CurrentStage $retainedStage -Apply -Confirm:$false | ConvertFrom-Json
Check ((Test-Path -LiteralPath (Join-Path $bin 'UserArtwork\preserve.txt')) -and @($linked.Skipped | Where-Object Path -eq $link).Count -eq 1) 'Directory links are skipped without touching their target'
$again = & $script -Workspace $fixture -CurrentStage $retainedStage -Apply -Confirm:$false | ConvertFrom-Json
Check ($again.RemovedCount -eq 0) 'Repeated cleanup is idempotent'
[ordered]@{Passed=$true;Checks=$script:checks;Fixture=$fixture} | ConvertTo-Json -Compress
