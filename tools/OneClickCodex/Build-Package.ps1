param([string]$RuntimeDirectory='', [string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if(!$RuntimeDirectory){$RuntimeDirectory=Join-Path $repo 'bin\NodeRuntime\24.21.0\node-v24.21.0-win-x64';if(!(Test-Path -LiteralPath (Join-Path $RuntimeDirectory 'node.exe'))){$RuntimeDirectory='C:\ExampleInstall\Alpha Island\agent-integrations\runtime'}}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo 'bin\Codex-OneClick-Connect'}
$node=Join-Path $RuntimeDirectory 'node.exe';if((Get-FileHash -LiteralPath $node).Hash -ne 'BA4E6D110E8C1592A1ECD390F6B05F3DA124B13871A5BE62B341A07A853C6C32'){throw 'Verified Node.js 24.21.0 runtime required.'}
$payload=Join-Path $OutputDirectory 'payload';$integrations=Join-Path $payload 'agent-integrations'
New-Item -ItemType Directory -Path $OutputDirectory,$payload,(Join-Path $integrations 'CodexHooks'),(Join-Path $integrations 'runtime') -Force|Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Repair-CodexIsland.ps1') -Destination $OutputDirectory
foreach($file in @('probe.mjs','native-hooks-info.mjs')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $payload}
Copy-Item -LiteralPath (Join-Path $repo 'packaging\agent-setup.ps1') -Destination (Join-Path $integrations 'setup.ps1')
foreach($file in @('hook.mjs','install.ps1')){Copy-Item -LiteralPath (Join-Path $repo ('examples\CodexHooks\'+$file)) -Destination (Join-Path $integrations 'CodexHooks')}
foreach($file in @('node.exe','LICENSE','SHA256.txt')){Copy-Item -LiteralPath (Join-Path $RuntimeDirectory $file) -Destination (Join-Path $integrations 'runtime')}
Copy-Item -LiteralPath (Join-Path $repo 'tools\Collect-CodexIslandDiagnostics.ps1') -Destination $payload
$bat="@echo off`r`nchcp 65001 >nul`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Repair-CodexIsland.ps1`"`r`npause`r`n"
$review=$bat.Replace('Repair-CodexIsland.ps1"','Repair-CodexIsland.ps1" -Review')
[IO.File]::WriteAllText((Join-Path $OutputDirectory '一键接入.bat'),$bat,[Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $OutputDirectory '确认信任.bat'),$review,[Text.Encoding]::ASCII)
$readme=@"
Codex 与 Alpha Island 一键接入

1. 在需要接入的电脑上完整解压，使用运行 Codex 的 Windows 用户双击「一键接入.bat」。使用普通用户运行即可，无需管理员。
2. 自动修复接入路径，覆盖所有项目，开启岛的 Agent 桥接、状态提示和授权提醒；备份并保留其他设置、其他 Hook 和原来的审批方式。附带经 SHA256 校验的 Node.js 24.21.0，无需另外安装 Node，也不修改 PATH。
3. 若显示需要信任，双击「确认信任.bat」，在打开的 Codex CLI 内输入 /hooks，审核并信任 Alpha Island 处理器。此处是 Codex 的首次审核要求，脚本不会代替你确认。已信任且定义未改变时可以跳过。
4. 彻底退出并重新打开 Codex，让原桌面会话重新加载接入。确认岛正在运行，并把 Codex 切到后台，再发送测试消息。

测试会检查思考和等待授权状态能否到达岛。此测试不证明旧 Codex 桌面会话已重新加载 Hooks。成功时分别显示配置、报送与信任结果，不会把「文件已写入」当作「完全接通」。

主程序仍需已安装。接入包不包含 Alpha Island 安装器，适用于另一台已安装岛的电脑。既有手动静默、游戏免打扰、前台隐藏和组织策略仍按原规则生效。关闭的提醒开关需要应用时，脚本会正常关闭并重新启动岛；不强制结束进程，不重启 Codex。

结果保存到 %LOCALAPPDATA%\alpha-island\logs\oneclick-codex-result.json。桌面自动生成只读诊断 ZIP；失败时可以将诊断 ZIP 和结果文件发回。

官方 Hooks 信任说明：https://learn.chatgpt.com/docs/hooks
Node.js 许可证位于 payload\agent-integrations\runtime\LICENSE。
Node.js SHA256：BA4E6D110E8C1592A1ECD390F6B05F3DA124B13871A5BE62B341A07A853C6C32。
"@
[IO.File]::WriteAllText((Join-Path $OutputDirectory '使用说明.txt'),$readme,[Text.UTF8Encoding]::new($true))
$archive=Join-Path (Split-Path $OutputDirectory -Parent) 'Codex-OneClick-Connect.zip'
Compress-Archive -LiteralPath $OutputDirectory -DestinationPath $archive -Force
[pscustomobject]@{Directory=$OutputDirectory;Package=$archive;Bytes=(Get-Item -LiteralPath $archive).Length;SHA256=(Get-FileHash -LiteralPath $archive).Hash}|ConvertTo-Json -Compress