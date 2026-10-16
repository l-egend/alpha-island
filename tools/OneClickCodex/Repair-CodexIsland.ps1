param([string]$CodexDirectory='', [string]$DataRoot=(Join-Path $env:LOCALAPPDATA 'alpha-island'),
 [string]$UserProfileDirectory=$env:USERPROFILE, [string]$LocalDataDirectory=$env:LOCALAPPDATA,
 [string]$InstallDirectory='', [int]$Port=0, [switch]$Isolated, [switch]$NoRestart, [switch]$Review)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$payload=Join-Path $PSScriptRoot 'payload'
function Read-Config([string]$path){if(!(Test-Path -LiteralPath $path)){return $null};try{$v=Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json;if($v -isnot [pscustomobject]){throw 'Invalid object'};return $v}catch{throw '现有配置不是有效 JSON，已保留原文件，请先修复配置。'}}
function Read-FileHash([string]$path){$stream=[IO.File]::OpenRead($path);$hash=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','')}finally{$hash.Dispose();$stream.Dispose()}}
function Find-CodexCli {
 $paths=@();$command=Get-Command codex.exe -ErrorAction SilentlyContinue;if($command){$paths+=$command.Source}
 foreach($p in @(Get-Process -Name Codex -ErrorAction SilentlyContinue)){try{if($p.Path -and [string]::IsNullOrEmpty([Diagnostics.FileVersionInfo]::GetVersionInfo($p.Path).ProductName)){$paths+=$p.Path};$paths+=Join-Path (Split-Path $p.Path -Parent) 'resources\codex.exe'}catch{}}
 $cache=Join-Path $LocalDataDirectory 'OpenAI\Codex\bin';if(Test-Path -LiteralPath $cache -PathType Container){foreach($dir in @(Get-ChildItem -LiteralPath $cache -Directory|Sort-Object LastWriteTime -Descending)){$paths+=Join-Path $dir.FullName 'codex.exe'}}
 $packages=@();try{if(!(Get-Command Get-AppxPackage -ErrorAction SilentlyContinue)){$module=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\Modules\Appx\Appx.psd1';if(Test-Path -LiteralPath $module){Import-Module $module -ErrorAction Stop}};$packages=@(Get-AppxPackage -Name '*Codex*' -ErrorAction SilentlyContinue)}catch{}
 foreach($package in $packages){if($package.InstallLocation){$paths+=Join-Path $package.InstallLocation 'app\resources\codex.exe';$paths+=Join-Path $package.InstallLocation 'resources\codex.exe'}}
 foreach($path in $paths){if(Test-Path -LiteralPath $path -PathType Leaf){return $path}};return ''
}
if($Review){$cli=Find-CodexCli;if(!$cli){throw '没有检测到 Codex CLI。请在已安装的 Codex CLI 中输入 /hooks，审核 Alpha Island 处理器。'};Write-Host '请在下面的 Codex 窗口输入 /hooks，审核并信任 Alpha Island 的 10 个处理器。';Write-Host '此入口不会自动确认信任，也不会改变工具审批策略。';& $cli --no-alt-screen;exit $LASTEXITCODE}
$logDirectory=Join-Path $DataRoot 'logs'
$result=[ordered]@{Configured=$false;AllProjects=$true;BridgeReady=$false;Probe=$null;CodexHooks=$null;CodexReviewRequired=$true;ToolsApprovalPolicyChanged=$false;Error=$null}
$stoppedIsland=$false
try {
 if(!(Test-Path -LiteralPath (Join-Path $payload 'agent-integrations\setup.ps1'))){throw '请先完整解压接入包，不能直接在压缩包中运行。'}
 if(!$CodexDirectory){foreach($scope in @('Process','User','Machine')){$candidate=[Environment]::GetEnvironmentVariable('CODEX_HOME',$scope);if($candidate){$CodexDirectory=$candidate;break}};if(!$CodexDirectory){$CodexDirectory=Join-Path $UserProfileDirectory '.codex'}}
 if(![IO.Path]::IsPathRooted($CodexDirectory) -or !(Test-Path -LiteralPath $CodexDirectory -PathType Container)){throw '没有找到当前用户的 Codex 配置目录，请先打开 Codex 并登录一次。'}
 $configDirectory=Join-Path $DataRoot 'config';$optionsPath=Join-Path $configDirectory 'options.json'
 $options=Read-Config $optionsPath;if(!$options){$options=[pscustomobject]@{Extras=[pscustomobject]@{}}};if(!$options.Extras){$options|Add-Member -Force NoteProperty Extras ([pscustomobject]@{})}
 if($options.Extras -isnot [pscustomobject]){throw '现有 Extras 配置格式无效，原配置已保留。'}
 if(!$Port){$Port=if($options.Extras.AgentBridgePort){[int]$options.Extras.AgentBridgePort}else{8765}}
 if($Port -lt 1024 -or $Port -gt 65535){throw '桥接端口无效。'}
 $sourceNode=Join-Path $payload 'agent-integrations\runtime\node.exe'
 if((Read-FileHash $sourceNode) -ne 'BA4E6D110E8C1592A1ECD390F6B05F3DA124B13871A5BE62B341A07A853C6C32'){throw '附带的 Node.js 校验失败，请重新下载接入包。'}
 $toggleNames=@('AgentBridgeEnabled','AgentStatusEnabled','AgentApprovalNotifyEnabled')
 $changeOptions=@($toggleNames|Where-Object{$options.Extras.$_ -ne $true}).Count -gt 0
 $running=@();$exe=''
 if(!$Isolated){$running=@(Get-Process -Name alpha-island -ErrorAction SilentlyContinue);if($running.Count -gt 1){throw '检测到多个岛进程，请退出多余进程后重试。'};if($running.Count){$exe=$running[0].Path}
  if(!$exe -and $InstallDirectory){$exe=Join-Path $InstallDirectory 'alpha-island.exe'}
  if(!$exe){foreach($key in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')){$item=Get-ItemProperty -Path $key -ErrorAction SilentlyContinue|Where-Object{$_.DisplayName -eq 'Alpha Island' -and $_.InstallLocation}|Select-Object -First 1;if($item){$exe=Join-Path $item.InstallLocation 'alpha-island.exe';break}}}
  if(!$NoRestart -and (!(Test-Path -LiteralPath $exe -PathType Leaf))){throw '请先安装 Alpha Island，接入包不包含主程序。'}
 }
 if($changeOptions -and $running.Count -and !$NoRestart){
  Add-Type @"
using System;using System.Text;using System.Runtime.InteropServices;
public static class CodexIslandClose {public delegate bool Callback(IntPtr h,IntPtr l);[DllImport("user32.dll")]static extern bool EnumWindows(Callback c,IntPtr l);[DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);[DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder s,int n);[DllImport("user32.dll")]static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);public static void Close(int pid){EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);var s=new StringBuilder(160);GetWindowText(h,s,s.Capacity);if(p==(uint)pid&&s.ToString()=="Alpha Island")PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero);return true;},IntPtr.Zero);}}
"@
  [CodexIslandClose]::Close($running[0].Id);if(!$running[0].WaitForExit(10000)){throw '岛未正常退出，请从托盘退出后重新运行。'};$running=@();$stoppedIsland=$true;$options=Read-Config $optionsPath;if(!$options -or $options.Extras -isnot [pscustomobject]){throw '岛退出后配置无效，保留原文件。'}
 }
 Write-Host '正在修复当前用户的 Codex 接入，并设置所有项目可用…'
 $arguments=@{Mode='Install';Provider='codex';InstallDirectory=$payload;DataRoot=$DataRoot;UserProfileDirectory=$UserProfileDirectory;LocalDataDirectory=$LocalDataDirectory;CodexHome=$CodexDirectory;Port=$Port;AllProjects=$true;Isolated=[bool]$Isolated}
 $setupText=& (Join-Path $payload 'agent-integrations\setup.ps1') @arguments
 $setup=$setupText|ConvertFrom-Json;$provider=$setup.providers|Where-Object{$_.id -eq 'codex'}
 if($setup.error -or !$provider -or $provider.status -ne 'configured'){throw '接入安装失败，未报告为成功。请查看补充诊断。'}
 if($changeOptions){foreach($name in $toggleNames){$options.Extras|Add-Member -Force NoteProperty $name $true};New-Item -ItemType Directory -Path $configDirectory -Force|Out-Null;$temporary=$optionsPath+'.oneclick.tmp';try{[IO.File]::WriteAllText($temporary,($options|ConvertTo-Json -Depth 50),[Text.UTF8Encoding]::new($false));if(Test-Path -LiteralPath $optionsPath){[IO.File]::Replace($temporary,$optionsPath,$optionsPath+'.bak')}else{[IO.File]::Move($temporary,$optionsPath)}}finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}}
 $result.Configured=$true
 if(!$Isolated -and !$NoRestart -and !$running.Count){Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -WindowStyle Hidden;$stoppedIsland=$false}
 $health=$null;for($i=0;$i -lt 20;$i++){try{$health=Invoke-RestMethod ('http://127.0.0.1:'+$Port+'/health') -TimeoutSec 1;if($health.service -eq 'alpha-island-agent-bridge' -and $health.statusReporting){break}}catch{};Start-Sleep -Milliseconds 500}
 if(!$health -or $health.service -ne 'alpha-island-agent-bridge' -or !$health.statusReporting){throw '接入配置完成，但桥接不可达；请启动岛并检查桥接开关。'}
 $result.BridgeReady=$true;$node=Join-Path $DataRoot 'integrations\runtime\node.exe';$adapter=Join-Path $DataRoot 'integrations\codex\hook.mjs';$config=Join-Path $configDirectory 'codex-hooks.json'
 $probeText=& $node (Join-Path $payload 'probe.mjs') $adapter $config
 $result.Probe=$probeText|ConvertFrom-Json;if($LASTEXITCODE -ne 0 -or !$result.Probe.thinkingReceived -or !$result.Probe.waitingReceived){throw '思考或等待授权的报送测试失败，未报告为完全接通。'}
 if(!$Isolated){$cli=Find-CodexCli;if($cli){$nativeText=& $node (Join-Path $payload 'native-hooks-info.mjs') $cli $CodexDirectory $UserProfileDirectory;$result.CodexHooks=$nativeText|ConvertFrom-Json;if($result.CodexHooks.available){$owned=@($result.CodexHooks.handlers);$ready=@($owned|Where-Object{$_.enabled -and $_.trust -eq 'trusted'});$result.CodexReviewRequired=$owned.Count -ne 10 -or $ready.Count -ne 10 -or $result.CodexHooks.errors -ne 0 -or $result.CodexHooks.hooksEnabled -eq $false -or $result.CodexHooks.userHooksAllowed -eq $false}}}
 Write-Host '接入配置与思考/等待授权报送测试已通过。'
 if($result.CodexHooks.hooksEnabled -eq $false){Write-Host 'Codex 的 Hooks 功能已被关闭，请先在 Codex 中开启 Hooks，再重新运行接入。'}elseif($result.CodexHooks.userHooksAllowed -eq $false){Write-Host '当前 Codex 策略不允许用户 Hooks，需要按当前组织策略处理。'}elseif($result.CodexReviewRequired){Write-Host '还需在 Codex 审核 Alpha Island Hooks。可双击 确认信任.bat，进入 CLI 后输入 /hooks。'}else{Write-Host 'Codex 当前检测到的 10 个处理器已开启且已信任。'}
 Write-Host '最后彻底退出并重新打开 Codex，让桌面会话加载最新接入。工具授权仍由原来的方式决定。'
} catch {$result.Error=$_.Exception.Message;Write-Host ('接入未完成：'+$result.Error);$failure=$true}
finally {
 if($stoppedIsland -and !$Isolated -and $exe){try{Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -WindowStyle Hidden}catch{Write-Host '请从开始菜单重新启动岛。'}}
 New-Item -ItemType Directory -Path $logDirectory -Force|Out-Null
 $resultPath=Join-Path $logDirectory 'oneclick-codex-result.json';[IO.File]::WriteAllText($resultPath,($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
 try{$diagArgs=@{CodexDirectory=$CodexDirectory;DataDirectory=$DataRoot;OutputDirectory=$(if($Isolated){$logDirectory}else{[Environment]::GetFolderPath('Desktop')});SkipBridge=[bool]$Isolated};& (Join-Path $payload 'Collect-CodexIslandDiagnostics.ps1') @diagArgs}catch{Write-Host '补充诊断生成失败，结果仍保存在日志目录。'}
 Write-Host ('运行结果：'+$resultPath)
}
if($failure){exit 1}