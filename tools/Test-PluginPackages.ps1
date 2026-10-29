param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root=[IO.Path]::GetFullPath($Directory)
$report=Get-Content -LiteralPath (Join-Path $root 'packages.json') -Raw -Encoding UTF8|ConvertFrom-Json
$checked=0
foreach($package in $report.Plugins){
 $file=Join-Path $root $package.Archive
 if((Get-FileHash -LiteralPath $file).Hash -ne $package.SHA256){throw 'Plugin package hash mismatch.'}
 $zip=[IO.Compression.ZipFile]::OpenRead($file)
 try{
  $entries=@{};foreach($entry in $zip.Entries){$name=$entry.FullName.Replace('\','/');if($name -match '(^|/)\.\.(/|$)|^[a-zA-Z]:|^/'){throw 'Unsafe package entry.'};$entries[$name]=$entry}
  $manifestEntry=$entries[$package.Id+'/plugin.json'];if(!$manifestEntry){throw 'Plugin manifest missing.'}
  foreach($guide in @('README.md','README.en.md')){if(!$entries[$package.Id+'/'+$guide]){throw 'Every plugin package must include Chinese and English usage instructions.'}}
  $reader=[IO.StreamReader]::new($manifestEntry.Open());try{$manifest=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
  if($manifest.id -ne $package.Id -or $manifest.assembly -ne [IO.Path]::GetFileName($manifest.assembly) -or !$entries[$package.Id+'/'+$manifest.assembly]){throw 'Plugin assembly does not match its manifest.'}
  if(@($entries.Keys|Where-Object{$_ -match '(^|/)(options\.json|secrets\.dat|clipboard_history\.json|error\.log)$'}).Count){throw 'Personal data entered a plugin package.'}
  $checked++
 }finally{$zip.Dispose()}
}
$source=[IO.Compression.ZipFile]::OpenRead((Join-Path $root $report.SourceArchive))
try{
 $names=@($source.Entries|ForEach-Object{$_.FullName.Replace('\','/')})
 foreach($required in @('examples/ClockPlugin/ClockPlugin.csproj','examples/ApiChatPlugin/ApiChatPlugin.csproj','examples/OppoPodsPlugin/LICENSE','PluginSdk/AlphaIsland.PluginSdk.csproj','PluginSdk/Localization/Strings.en.json','ConversationCore/AlphaIsland.ConversationCore.csproj','README.md','README.en.md','Plugins/TrafficMonitorBridge/README.md','Plugins/TrafficMonitorBridge/README.en.md')){
  if($required -notin $names){throw ('Corresponding source missing: '+$required)}
 }
 if(@($names|Where-Object{$_ -match '(^|/)(bin|obj|\.git)(/|$)'}).Count){throw 'Build caches entered corresponding source.'}
 if(@($names|Where-Object{$_ -match '(^|/)(local-tools|\.local|command-history)(/|$)|(^|/)(build-plugins|build-traffic-monitor-bridge|package-earbuds-plugin|package-vivo-plugin)\.ps1$'}).Count){throw 'Workstation release commands entered corresponding source.'}
}finally{$source.Dispose()}
$result=[ordered]@{Passed=$true;HostVersion=$report.HostVersion;CheckedPlugins=$checked;CorrespondingSource=$true;MainInstallerIncludesPlugins=$false}
$result|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'verification.json') -Encoding utf8
$result|ConvertTo-Json
