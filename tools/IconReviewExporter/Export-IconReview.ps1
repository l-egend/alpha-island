param([string]$ProjectRoot = (Join-Path $PSScriptRoot '..\..'))
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
$taskProject = Join-Path $taskRoot 'alpha-island.csproj'
if (-not (Test-Path -LiteralPath $taskProject -PathType Leaf)) { throw 'Alpha Island project directory is required.' }
[xml]$taskXml = Get-Content -LiteralPath $taskProject -Raw
$taskExpected = [string]($taskXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$taskDll = Join-Path $taskRoot 'bin\Release\net8.0-windows10.0.19041.0\alpha-island.dll'
if (-not (Test-Path -LiteralPath $taskDll -PathType Leaf)) { throw 'Build the production Release project before exporting.' }
$taskVersion = [Reflection.AssemblyName]::GetAssemblyName($taskDll).Version.ToString()
if ($taskVersion -ne ($taskExpected + '.0')) { throw 'The production Release build does not match the project version; build it before exporting.' }
$taskToolProject = Join-Path $PSScriptRoot 'IconReviewExporter.csproj'
dotnet restore $taskToolProject --ignore-failed-sources -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Icon exporter restore failed.' }
dotnet build $taskToolProject -c Release --no-restore -p:BuildProjectReferences=false -m:1 -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Icon exporter build failed.' }
$taskExporter = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows10.0.19041.0\AlphaIsland.IconReviewExporter.exe'
& $taskExporter $taskRoot
if ($LASTEXITCODE -ne 0) { throw 'Icon rendering or GIF verification failed.' }
$taskFolder = [IO.Path]::GetFullPath((Join-Path $taskRoot ('bin\IconReview-2026-10-11-v'+$taskExpected)))
$taskZip = [IO.Path]::GetFullPath((Join-Path $taskRoot ('bin\IconReview-2026-10-11-v'+$taskExpected+'.zip')))
$taskBin = [IO.Path]::GetFullPath((Join-Path $taskRoot 'bin')).TrimEnd('\') + '\'
if (-not $taskFolder.StartsWith($taskBin, [StringComparison]::OrdinalIgnoreCase) -or -not $taskZip.StartsWith($taskBin, [StringComparison]::OrdinalIgnoreCase)) { throw 'Export paths escaped the project bin directory.' }
$taskItems = Get-ChildItem -LiteralPath $taskFolder -Force -Recurse
if ($taskItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Export contains a linked path.' }
$taskFiles = @($taskItems | Where-Object { -not $_.PSIsContainer })
$taskExtensions = @('.png','.gif','.ico','.svg','.md','.cs','.xaml','.json','.html','.txt')
if ($taskFiles | Where-Object { $_.Extension -notin $taskExtensions -or $_.Name -match '^(options|config|settings|sessions|history|error)\.' }) { throw 'Export contains an unexpected file.' }
if (Test-Path -LiteralPath $taskZip) {
    if ((Get-Item -LiteralPath $taskZip -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Archive path is linked.' }
    Remove-Item -LiteralPath $taskZip -Force
}
[IO.Compression.ZipFile]::CreateFromDirectory($taskFolder, $taskZip, [IO.Compression.CompressionLevel]::Optimal, $true)
$taskArchive = [IO.Compression.ZipFile]::OpenRead($taskZip)
try {
    $taskArchived = @($taskArchive.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
    if ($taskArchived.Count -ne $taskFiles.Count) { throw 'ZIP entry count differs from exported files.' }
} finally { $taskArchive.Dispose() }
$taskManifest = Get-Content -LiteralPath (Join-Path $taskFolder 'manifest.json') -Raw | ConvertFrom-Json
if ($taskManifest.Version -ne $taskVersion) { throw 'Exported manifest version differs from the production build.' }
[pscustomobject]@{
    Version = $taskVersion; Folder = $taskFolder; Archive = $taskZip; Files = $taskFiles.Count
    Icons = $taskManifest.Entries.Count; GIFs = @($taskManifest.Entries | Where-Object AnimationPath).Count
    Bytes = (Get-Item -LiteralPath $taskZip).Length; SHA256 = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash
}
