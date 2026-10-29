param(
    [string]$Compiler = '',
    [string]$RuntimeVersion = '8.0.31',
    [string]$NodeRuntimeDirectory = '',
    [ValidateRange(2,20)][int]$KeepInstallerVersions = 2
)
$ErrorActionPreference = 'Stop'
$installerRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Compiler) { $Compiler = Join-Path $installerRepo 'bin\InstallerTools\InnoSetup\ISCC.exe' }
if (!(Test-Path -LiteralPath $Compiler)) { throw 'Provide the path to the official Inno Setup ISCC.exe compiler.' }
if (!$NodeRuntimeDirectory) { $NodeRuntimeDirectory = Join-Path $installerRepo 'bin\InstallerTools\node-v24.21.0' }
$nodeHash = 'ba4e6d110e8c1592a1ecd390f6b05f3da124b13871a5be62b341a07a853c6c32'
New-Item -ItemType Directory -Path $NodeRuntimeDirectory -Force | Out-Null
$nodeFile = Join-Path $NodeRuntimeDirectory 'node.exe'
if (!(Test-Path -LiteralPath $nodeFile)) { Invoke-WebRequest 'https://nodejs.org/dist/v24.21.0/win-x64/node.exe' -OutFile $nodeFile }
if ((Get-FileHash -LiteralPath $nodeFile -Algorithm SHA256).Hash -ne $nodeHash) { throw 'Bundled Node.js does not match the pinned official SHA256.' }
$nodeLicense = Join-Path $NodeRuntimeDirectory 'LICENSE'
if (!(Test-Path -LiteralPath $nodeLicense)) { Invoke-WebRequest 'https://raw.githubusercontent.com/nodejs/node/v24.21.0/LICENSE' -OutFile $nodeLicense }
[IO.File]::WriteAllText((Join-Path $NodeRuntimeDirectory 'SHA256.txt'), $nodeHash)
$project = [xml](Get-Content -LiteralPath (Join-Path $installerRepo 'alpha-island.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
$stage = Join-Path $installerRepo ('bin\InstallerStage\' + $version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$output = Join-Path $installerRepo 'bin\Installers'
New-Item -ItemType Directory -Path $stage,$output -Force | Out-Null
Push-Location -LiteralPath $installerRepo
try {
    & dotnet publish alpha-island.csproj -c Release -r win-x64 --self-contained true -p:InstallerRuntimeVersion=$RuntimeVersion -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:DeployPlugin=false -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -o $stage --nologo -v minimal
    if ($LASTEXITCODE) { throw 'Self-contained publish failed.' }
    $iconStage = Join-Path $stage 'icons'
    New-Item -ItemType Directory -Path $iconStage -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $installerRepo 'Assets\alpha-island.ico') -Destination (Join-Path $iconStage ('alpha-island-' + $version + '.ico')) -Force
    foreach ($guide in @('README.md', 'README.en.md', 'docs\QUICKSTART.zh-CN.md', 'docs\QUICKSTART.en.md', 'docs\TERMINOLOGY.md', 'Assets\Lyrics\歌词源导入规范.md')) {
        $sourceGuide = Join-Path $installerRepo $guide
        if (!(Test-Path -LiteralPath $sourceGuide -PathType Leaf)) { throw ('Missing public package guide: ' + $guide) }
        $targetGuide = Join-Path $stage $guide
        New-Item -ItemType Directory -Path (Split-Path -Parent $targetGuide) -Force | Out-Null
        Copy-Item -LiteralPath $sourceGuide -Destination $targetGuide
    }
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\images') -Destination (Join-Path $stage 'docs\images') -Recurse -Force
    $agentAdapters = Join-Path $stage 'agent-integrations'
    New-Item -ItemType Directory -Path $agentAdapters -Force | Out-Null
    foreach ($adapterName in @('CodexHooks','AntigravityHooks','DeepSeekHarnessPlugin','ClaudeCodeHooks')) {
        Copy-Item -LiteralPath (Join-Path $installerRepo ('examples\' + $adapterName)) -Destination $agentAdapters -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $installerRepo 'packaging\agent-setup.ps1') -Destination (Join-Path $agentAdapters 'setup.ps1')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'tools\OneClickCodex\native-hooks-info.mjs') -Destination $agentAdapters
    $agentRuntime = Join-Path $agentAdapters 'runtime'
    New-Item -ItemType Directory -Path $agentRuntime -Force | Out-Null
    foreach ($name in @('node.exe','LICENSE','SHA256.txt')) { Copy-Item -LiteralPath (Join-Path $NodeRuntimeDirectory $name) -Destination $agentRuntime }
    $testTools = Join-Path $stage 'tools'
    New-Item -ItemType Directory -Path $testTools -Force | Out-Null
    foreach ($file in @('Collect-CodexIslandDiagnostics.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $testTools
    }
    $retired = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object Name -Match 'LibreHardware|PawnIO|HardwareWorker|RegressionTests'
    if ($retired) { throw 'A retired hardware backend or test executable entered the package.' }
    $compatibilityReport = Join-Path $output ('AlphaIsland-' + $version + '-compatibility.json')
    & (Join-Path $PSScriptRoot 'Test-PackageCompatibility.ps1') -Stage $stage -ReportPath $compatibilityReport
    & $Compiler ('/DAppVersion=' + $version) ('/DPublishDir=' + $stage) ('/DOutputDir=' + $output) (Join-Path $installerRepo 'packaging\AlphaIsland.iss')
    if ($LASTEXITCODE) { throw 'Installer compilation failed.' }
    $setup = Join-Path $output ('AlphaIsland-' + $version + '-win-x64-Setup.exe')
    $result = [ordered]@{Version=$version;Runtime=$RuntimeVersion;Stage=$stage;Installer=$setup;SHA256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash;Bytes=(Get-Item -LiteralPath $setup).Length;Built=(Get-Date -Format 'o')}
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output ('AlphaIsland-' + $version + '-build.json')) -Encoding utf8
    $cleanupReport = Join-Path $installerRepo 'bin\artifact-cleanup-last.json'
    $null = & (Join-Path $PSScriptRoot 'Clean-BuildArtifacts.ps1') -Workspace $installerRepo -CurrentStage $stage -KeepInstallerVersions $KeepInstallerVersions -Apply -Confirm:$false -ReportPath $cleanupReport
    $cleanup = Get-Content -LiteralPath $cleanupReport -Raw | ConvertFrom-Json
    $result['RetainedVersions'] = @($cleanup.RetainedVersions)
    $result['CleanedBytes'] = $cleanup.RemovedBytes
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output ('AlphaIsland-' + $version + '-build.json')) -Encoding utf8
    $result | ConvertTo-Json
} finally { Pop-Location }
