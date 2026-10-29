param([string]$CodexDirectory='',[string]$DataDirectory='',[string]$OutputDirectory='', [switch]$SkipBridge)
$ErrorActionPreference='Stop'
if(!$CodexDirectory){$CodexDirectory=if($env:CODEX_HOME){$env:CODEX_HOME}else{Join-Path $env:USERPROFILE '.codex'}}
if(!$DataDirectory){$DataDirectory=Join-Path $env:LOCALAPPDATA 'alpha-island'}
if(!$OutputDirectory){$OutputDirectory=[Environment]::GetFolderPath('Desktop')}
function Protect-DiagnosticText([string]$text) {
    if (!$text) { return $text }
    $text=[regex]::Replace($text,'(?i)\bBearer\s+[^\s"'',;]+|\bsk-[A-Za-z0-9_-]{8,}','[hidden]')
    $text=[regex]::Replace($text,'(?i)((?:api[_-]?key|token|secret|password|authorization|cookie)\s*[=:]\s*)[^\s,;&]+','$1[hidden]')
    $text=[regex]::Replace($text,'(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n"''<>|]*','[path hidden]')
    $text=[regex]::Replace($text,'(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b','[address hidden]')
    return $text
}
function Protect-DiagnosticValue($value) {
    if ($null -eq $value) { return $null }
    if ($value -is [string]) { return Protect-DiagnosticText $value }
    if ($value -is [ValueType]) { return $value }
    if ($value -is [System.Collections.IDictionary]) {
        $copy=[ordered]@{}
        foreach($key in $value.Keys) { $copy[$key]=Protect-DiagnosticValue $value[$key] }
        return $copy
    }
    if ($value -is [pscustomobject]) {
        $copy=[ordered]@{}
        foreach($property in $value.PSObject.Properties) { $copy[$property.Name]=Protect-DiagnosticValue $property.Value }
        return $copy
    }
    if ($value -is [System.Collections.IEnumerable]) {
        $copy=@(foreach($item in $value) { Protect-DiagnosticValue $item })
        return ,$copy
    }
    return $value
}
function Read-Json([string]$path){if(!(Test-Path -LiteralPath $path -PathType Leaf)){return $null};try{return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json}catch{return $null}}
function Read-FileHash([string]$path){$stream=[IO.File]::OpenRead($path);$hash=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','')}finally{$hash.Dispose();$stream.Dispose()}}
function File-Info([string]$path){$exists=Test-Path -LiteralPath $path -PathType Leaf;[ordered]@{Path=$path;Exists=$exists;SHA256=$(if($exists){(Read-FileHash $path)}else{$null})}}
function Features([string]$path){
 $result=[ordered]@{File=$path;Exists=(Test-Path -LiteralPath $path -PathType Leaf);Hooks=$null;CodexHooks=$null;ManagedHooksOnly=$null}
 if($result.Exists -and (Get-Item -LiteralPath $path).Length -le 1MB){$section='';foreach($line in (Get-Content -LiteralPath $path -Encoding UTF8)){
  if($line -match '^\s*\[([^\]]+)\]'){$section=$Matches[1].Trim()}
  if($section -eq 'features' -and $line -match '^\s*(hooks|codex_hooks)\s*=\s*(true|false)\s*(?:#.*)?$'){$key=if($Matches[1] -eq 'hooks'){'Hooks'}else{'CodexHooks'};$result[$key]=($Matches[2] -eq 'true')}
  if($section -in @('','hooks') -and $line -match '^\s*allow_managed_hooks_only\s*=\s*(true|false)\s*(?:#.*)?$'){$result.ManagedHooksOnly=($Matches[1] -eq 'true')}
 }};return $result
}
$hookPath=Join-Path $CodexDirectory 'hooks.json'
$hooks=Read-Json $hookPath
$entries=@()
if($hooks -and $hooks.hooks){foreach($event in $hooks.hooks.PSObject.Properties){foreach($group in @($event.Value)){foreach($handler in @($group.hooks)){if($handler.statusMessage -like 'Alpha Island / *'){
 $paths=@()
 foreach($match in [regex]::Matches([string]$handler.command, '"([^"]+)"|''([^'']+)''')) {
  $value=if($match.Groups[1].Success){$match.Groups[1].Value}else{$match.Groups[2].Value}
  if([IO.Path]::IsPathRooted($value)){$paths+=File-Info $value}
 }
 $entries+=[ordered]@{Event=$event.Name;Type=$handler.type;Timeout=$handler.timeout;ReferencedFiles=$paths}
}}}}}
$registrationPath=Join-Path $DataDirectory 'config\codex-hooks.json'
$registration=Read-Json $registrationPath
$options=Read-Json (Join-Path $DataDirectory 'config\options.json')
$port=8765;if($options -and $options.Extras.AgentBridgePort){$port=[int]$options.Extras.AgentBridgePort}
$bridge=[ordered]@{Port=$port;Reachable=$false;StatusReporting=$false;Sources=@();ErrorType=$null}
if(!$SkipBridge){try{$health=Invoke-RestMethod -Uri ('http://127.0.0.1:'+ $port +'/health') -TimeoutSec 3;$bridge.Reachable=($health.service -eq 'alpha-island-agent-bridge');$bridge.StatusReporting=($health.statusReporting -eq $true);$status=Invoke-RestMethod -Uri ('http://127.0.0.1:'+ $port +'/status') -TimeoutSec 3;foreach($connection in @($status.connections)){if($connection){$bridge.Sources+=([ordered]@{Source=$(if([string]$connection.source -match '^(codex|claude|antigravity|deepseek|dsh)'){$Matches[1]}else{'other'});State=$(if(@('thinking','working','streaming','idle','waiting','tool-wait','input-wait','error','completed') -contains $connection.state){$connection.state}else{'unknown'})})}}}catch{$bridge.ErrorType=$_.Exception.GetType().Name}}
if($bridge.Reachable -and $health.codexNativeStatus){$native=$health.codexNativeStatus;$bridge['CodexNativeStatus']=[ordered]@{
 Supported=($native.supported -eq $true);Mode=$(if([string]$native.mode -match '^[a-z-]{1,40}$'){$native.mode}else{'unknown'});
 Observed=$(if($native.observed -is [ValueType]){[Math]::Min(64,[Math]::Max([int]$native.observed,0))}else{0})
}}
$exe=Get-Process -Name alpha-island -ErrorAction SilentlyContinue | Select-Object -First 1
$islandVersion=$null;if($exe){try{$islandVersion=(Get-Item -LiteralPath $exe.Path).VersionInfo.FileVersion}catch{}}
$hookInfo=File-Info $hookPath
$bom=$false;if($hookInfo.Exists){$bytes=[IO.File]::ReadAllBytes($hookPath);$bom=$bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191}
$report=[ordered]@{Generated=(Get-Date).ToString('o');IslandVersion=$islandVersion;CodexHome=$CodexDirectory;HookFile=$hookInfo;HookFileHasUtf8Bom=$bom;HookJsonParsed=($null -ne $hooks);IslandHandlerCount=$entries.Count;IslandHandlers=$entries;UserFeatureFlags=(Features (Join-Path $CodexDirectory 'config.toml'));RegistrationFile=(File-Info $registrationPath);Registration=$null;IslandOptions=$null;Bridge=$bridge;HookTrust='Not determined by configuration files; review Alpha Island hooks in Codex.';SessionReload='After installing or repairing hooks, reopen or restart the Codex conversation.'}
if($registration){$report.Registration=[ordered]@{AllProjects=$registration.allProjects;ProjectRoot=$registration.projectRoot;ProjectExists=$(if($registration.projectRoot){Test-Path -LiteralPath $registration.projectRoot -PathType Container}else{$null});ApprovalMode=$registration.approvalMode;EndpointMatchesIsland=($registration.endpoint -eq ('http://127.0.0.1:'+ $port +'/'));AdapterFile=(File-Info (Join-Path $DataDirectory 'integrations\codex\hook.mjs'));RuntimeFile=(File-Info (Join-Path $DataDirectory 'integrations\runtime\node.exe'))}}
if($options){$report.IslandOptions=[ordered]@{AgentBridgeEnabled=$options.Extras.AgentBridgeEnabled;AgentStatusEnabled=$options.Extras.AgentStatusEnabled;AgentApprovalNotifyEnabled=$options.Extras.AgentApprovalNotifyEnabled;QuietMode=$options.Extras.NotificationQuietMode}}
$repair=Read-Json (Join-Path $DataDirectory 'logs\oneclick-codex-result.json')
if($repair){$native=@($repair.CodexHooks.handlers);$report['OneClickRepair']=[ordered]@{Configured=$repair.Configured;BridgeReady=$repair.BridgeReady;ThinkingReceived=$repair.Probe.thinkingReceived;WaitingReceived=$repair.Probe.waitingReceived;CodexReviewRequired=$repair.CodexReviewRequired;NativeInspectionAvailable=$repair.CodexHooks.available;NativeHooksEnabled=$repair.CodexHooks.hooksEnabled;UserHooksAllowed=$repair.CodexHooks.userHooksAllowed;NativeHandlerCount=@($native|Where-Object{$null -ne $_}).Count;NativeTrustedEnabledCount=@($native|Where-Object{$_.enabled -and $_.trust -eq 'trusted'}).Count;NativeDisabledCount=@($native|Where-Object{$null -ne $_ -and !$_.enabled}).Count;Failed=($null -ne $repair.Error)}}
$findings=@()
function Add-Finding([string]$code,[string]$severity,[string]$message) { $script:findings += [ordered]@{Code=$code;Severity=$severity;Message=$message} }
if (!$hookInfo.Exists) { Add-Finding 'CODEX_HOOKS_MISSING' 'warning' 'Codex hooks.json is missing. Open Codex once, then reconnect in Alpha Island.' }
elseif (!$hooks) { Add-Finding 'CODEX_HOOKS_INVALID_JSON' 'error' 'Codex hooks.json could not be parsed.' }
elseif (!$entries.Count) { Add-Finding 'ISLAND_HANDLER_MISSING' 'warning' 'No Alpha Island handler is registered in Codex.' }
elseif ($entries.Count -ne 10) { Add-Finding 'ISLAND_HANDLERS_INCOMPLETE' 'warning' 'Expected 10 Alpha Island handlers. Repair this computers integration.' }
$features=$report.UserFeatureFlags
if ($features.Hooks -eq $false -or $null -eq $features.Hooks -and $features.CodexHooks -eq $false) { Add-Finding 'CODEX_HOOKS_DISABLED' 'warning' 'Codex disabled native hooks. Basic task state can use the local metadata fallback when supported; precise tool states require enabled and trusted hooks.' }
if ($features.ManagedHooksOnly -eq $true) { Add-Finding 'CODEX_HOOK_POLICY_BLOCKED' 'warning' 'Codex policy allows managed hooks only. Alpha Island does not change or bypass this policy.' }
if ($entries | Where-Object { @($_.ReferencedFiles | Where-Object { !$_.Exists }).Count }) { Add-Finding 'HANDLER_PATH_MISSING' 'error' 'A registered runtime or handler path no longer exists. Repair the integration on this computer.' }
if ($registration -and $registration.projectRoot -and !(Test-Path -LiteralPath $registration.projectRoot -PathType Container)) { Add-Finding 'PROJECT_SCOPE_MISSING' 'error' 'The saved project folder is missing, often after copying configuration to another computer.' }
if ($options -and $options.Extras.AgentBridgeEnabled -eq $false) { Add-Finding 'BRIDGE_DISABLED' 'warning' 'Enable the local Agent bridge in Alpha Island settings.' }
if (!$SkipBridge -and !$bridge.Reachable) { Add-Finding 'BRIDGE_UNREACHABLE' 'error' 'The configured local bridge did not respond; check whether Alpha Island is running and its port is available.' }
if ($bridge.Reachable -and !$bridge.StatusReporting) { Add-Finding 'STATUS_REPORTING_DISABLED' 'warning' 'The bridge is reachable but Agent status reporting is disabled.' }
if ($bridge.Reachable -and !$bridge.Sources.Count) { Add-Finding 'NO_STATUS_EVENT_YET' 'info' 'No live Agent event has arrived; check Hook trust and reopen the Codex conversation. CLI availability is checked separately in the Local Agent plugin.' }
$report['Findings']=$findings
$report['Privacy']='Paths, credentials and email addresses are filtered. No authentication files or conversation history are collected.'
$report=Protect-DiagnosticValue $report
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$target=Join-Path $OutputDirectory ('codex-island-diagnostics-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.zip')
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$stream=[IO.File]::Open($target,[IO.FileMode]::CreateNew)
try{$archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true);try{$writer=[IO.StreamWriter]::new($archive.CreateEntry('codex-island-report.json').Open(),[Text.UTF8Encoding]::new($false));try{$writer.Write(($report | ConvertTo-Json -Depth 10))}finally{$writer.Dispose()}}finally{$archive.Dispose()}}finally{$stream.Dispose()}
Write-Output ('Diagnostic package: '+$target)
