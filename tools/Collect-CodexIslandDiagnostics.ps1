param([string]$CodexDirectory='',[string]$DataDirectory='',[string]$OutputDirectory='', [switch]$SkipBridge)
$ErrorActionPreference='Stop'
if(!$CodexDirectory){$CodexDirectory=if($env:CODEX_HOME){$env:CODEX_HOME}else{Join-Path $env:USERPROFILE '.codex'}}
if(!$DataDirectory){$DataDirectory=Join-Path $env:LOCALAPPDATA 'alpha-island'}
if(!$OutputDirectory){$OutputDirectory=[Environment]::GetFolderPath('Desktop')}
function Read-Json([string]$path){if(!(Test-Path -LiteralPath $path -PathType Leaf)){return $null};try{return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json}catch{return $null}}
function Read-FileHash([string]$path){$stream=[IO.File]::OpenRead($path);$hash=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','')}finally{$hash.Dispose();$stream.Dispose()}}
function File-Info([string]$path){$exists=Test-Path -LiteralPath $path -PathType Leaf;[ordered]@{Path=$path;Exists=$exists;SHA256=$(if($exists){(Read-FileHash $path)}else{$null})}}
function Features([string]$path){$result=[ordered]@{File=$path;Exists=(Test-Path -LiteralPath $path -PathType Leaf);Hooks=$null;CodexHooks=$null};if($result.Exists){$section='';foreach($line in (Get-Content -LiteralPath $path -Encoding UTF8)){if($line -match '^\s*\[([^\]]+)\]'){$section=$Matches[1].Trim()};if($section -eq 'features' -and $line -match '^\s*(hooks|codex_hooks)\s*=\s*(true|false)\s*(?:#.*)?$'){$key=if($Matches[1] -eq 'hooks'){'Hooks'}else{'CodexHooks'};$result[$key]=($Matches[2] -eq 'true')}}};return $result}
$hookPath=Join-Path $CodexDirectory 'hooks.json'
$hooks=Read-Json $hookPath
$entries=@()
if($hooks -and $hooks.hooks){foreach($event in $hooks.hooks.PSObject.Properties){foreach($group in @($event.Value)){foreach($handler in @($group.hooks)){if($handler.statusMessage -like 'Alpha Island / *'){
 $paths=@();foreach($match in [regex]::Matches([string]$handler.command,"'([^']+)'")){$value=$match.Groups[1].Value;if([IO.Path]::IsPathRooted($value)){$paths+=File-Info $value}}
 $entries+=[ordered]@{Event=$event.Name;Type=$handler.type;Timeout=$handler.timeout;ReferencedFiles=$paths}
}}}}}
$registrationPath=Join-Path $DataDirectory 'config\codex-hooks.json'
$registration=Read-Json $registrationPath
$options=Read-Json (Join-Path $DataDirectory 'config\options.json')
$port=8765;if($options -and $options.Extras.AgentBridgePort){$port=[int]$options.Extras.AgentBridgePort}
$bridge=[ordered]@{Port=$port;Reachable=$false;StatusReporting=$false;Sources=@();ErrorType=$null}
if(!$SkipBridge){try{$health=Invoke-RestMethod -Uri ('http://127.0.0.1:'+ $port +'/health') -TimeoutSec 3;$bridge.Reachable=($health.service -eq 'alpha-island-agent-bridge');$bridge.StatusReporting=($health.statusReporting -eq $true);$status=Invoke-RestMethod -Uri ('http://127.0.0.1:'+ $port +'/status') -TimeoutSec 3;foreach($connection in @($status.connections)){if($connection){$bridge.Sources+=([ordered]@{Source=$connection.source;State=$connection.state})}}}catch{$bridge.ErrorType=$_.Exception.GetType().Name}}
$exe=Get-Process -Name alpha-island -ErrorAction SilentlyContinue | Select-Object -First 1
$islandVersion=$null;if($exe){try{$islandVersion=(Get-Item -LiteralPath $exe.Path).VersionInfo.FileVersion}catch{}}
$hookInfo=File-Info $hookPath
$bom=$false;if($hookInfo.Exists){$bytes=[IO.File]::ReadAllBytes($hookPath);$bom=$bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191}
$report=[ordered]@{Generated=(Get-Date).ToString('o');IslandVersion=$islandVersion;CodexHome=$CodexDirectory;HookFile=$hookInfo;HookFileHasUtf8Bom=$bom;HookJsonParsed=($null -ne $hooks);IslandHandlerCount=$entries.Count;IslandHandlers=$entries;UserFeatureFlags=(Features (Join-Path $CodexDirectory 'config.toml'));RegistrationFile=(File-Info $registrationPath);Registration=$null;IslandOptions=$null;Bridge=$bridge;HookTrust='Not determined by configuration files; review Alpha Island hooks in Codex.';SessionReload='After installing or repairing hooks, reopen or restart the Codex conversation.'}
if($registration){$report.Registration=[ordered]@{AllProjects=$registration.allProjects;ProjectRoot=$registration.projectRoot;ProjectExists=$(if($registration.projectRoot){Test-Path -LiteralPath $registration.projectRoot -PathType Container}else{$null});ApprovalMode=$registration.approvalMode;EndpointMatchesIsland=($registration.endpoint -eq ('http://127.0.0.1:'+ $port +'/'));AdapterFile=(File-Info (Join-Path $DataDirectory 'integrations\codex\hook.mjs'));RuntimeFile=(File-Info (Join-Path $DataDirectory 'integrations\runtime\node.exe'))}}
if($options){$report.IslandOptions=[ordered]@{AgentBridgeEnabled=$options.Extras.AgentBridgeEnabled;AgentStatusEnabled=$options.Extras.AgentStatusEnabled;AgentApprovalNotifyEnabled=$options.Extras.AgentApprovalNotifyEnabled;QuietMode=$options.Extras.NotificationQuietMode}}
$repair=Read-Json (Join-Path $DataDirectory 'logs\oneclick-codex-result.json')
if($repair){$native=@($repair.CodexHooks.handlers);$report['OneClickRepair']=[ordered]@{Configured=$repair.Configured;BridgeReady=$repair.BridgeReady;ThinkingReceived=$repair.Probe.thinkingReceived;WaitingReceived=$repair.Probe.waitingReceived;CodexReviewRequired=$repair.CodexReviewRequired;NativeInspectionAvailable=$repair.CodexHooks.available;NativeHooksEnabled=$repair.CodexHooks.hooksEnabled;UserHooksAllowed=$repair.CodexHooks.userHooksAllowed;NativeHandlerCount=@($native|Where-Object{$null -ne $_}).Count;NativeTrustedEnabledCount=@($native|Where-Object{$_.enabled -and $_.trust -eq 'trusted'}).Count;NativeDisabledCount=@($native|Where-Object{$null -ne $_ -and !$_.enabled}).Count;Failed=($null -ne $repair.Error)}}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$target=Join-Path $OutputDirectory ('codex-island-diagnostics-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream=[IO.File]::Open($target,[IO.FileMode]::CreateNew)
try{$archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true);try{$writer=[IO.StreamWriter]::new($archive.CreateEntry('codex-island-report.json').Open(),[Text.UTF8Encoding]::new($false));try{$writer.Write(($report | ConvertTo-Json -Depth 10))}finally{$writer.Dispose()}}finally{$archive.Dispose()}}finally{$stream.Dispose()}
Write-Output ('Diagnostic package: '+$target)