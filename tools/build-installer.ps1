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
    Copy-Item -LiteralPath (Join-Path $installerRepo 'README.en.md') -Destination (Join-Path $stage 'README.en.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\安装更新与卸载.md') -Destination (Join-Path $stage '安装说明.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\天气与提醒.md') -Destination (Join-Path $stage '天气与提醒.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\外部翻译快捷键.md') -Destination (Join-Path $stage '外部翻译快捷键.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\外部Agent接入.md') -Destination (Join-Path $stage '外部Agent接入.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\反重力权限自动跟随.md') -Destination (Join-Path $stage '反重力权限自动跟随.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\液态玻璃与多岛形变.md') -Destination (Join-Path $stage '液态玻璃与多岛形变.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\资源调度与稳定性优化.md') -Destination (Join-Path $stage '资源调度与稳定性优化.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\维护架构与工作区功能.md') -Destination (Join-Path $stage '维护架构与工作区功能.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\AI对话与历史.md') -Destination (Join-Path $stage 'AI对话与历史.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\图标短动画与蓝牙图片.md') -Destination (Join-Path $stage '图标短动画与蓝牙图片.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\通知卡片视觉堆叠.md') -Destination (Join-Path $stage '通知卡片视觉堆叠.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\通知堆叠交互与图标修复.md') -Destination (Join-Path $stage '通知堆叠交互与图标修复.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\Codex新任务提示提速.md') -Destination (Join-Path $stage 'Codex新任务提示提速.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\多岛胞吐与胞吞形变.md') -Destination (Join-Path $stage '多岛胞吐与胞吞形变.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\窗口可见性与限额终止.md') -Destination (Join-Path $stage '窗口可见性与限额终止.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\插件设置与剪切板历史.md') -Destination (Join-Path $stage '插件设置与剪切板历史.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\Codex状态补报与助手打开动画.md') -Destination (Join-Path $stage 'Codex状态补报与助手打开动画.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\桌面助手工具与本地保护.md') -Destination (Join-Path $stage '桌面助手工具与本地保护.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\通知中心助手提醒与本地规则.md') -Destination (Join-Path $stage '通知中心助手提醒与本地规则.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\游戏模式锁定与插件设置滚动修复.md') -Destination (Join-Path $stage '游戏模式锁定与插件设置滚动修复.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\设置分组与即时保存.md') -Destination (Join-Path $stage '设置分组与即时保存.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\游戏提示与设置整理补充.md') -Destination (Join-Path $stage '游戏提示与设置整理补充.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\音量浮层与模型形象状态修复.md') -Destination (Join-Path $stage '音量浮层与模型形象状态修复.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\对话等待与屏幕工具交互.md') -Destination (Join-Path $stage '对话等待与屏幕工具交互.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\设备电池与2.4G插件.md') -Destination (Join-Path $stage '设备电池与2.4G插件.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\卡片滚动与快速启动交互.md') -Destination (Join-Path $stage '卡片滚动与快速启动交互.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\设备电池合并与充放电提醒.md') -Destination (Join-Path $stage '设备电池合并与充放电提醒.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\本地习惯学习与屏幕工具.md') -Destination (Join-Path $stage '本地习惯学习与屏幕工具.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\耳机控制与取色交互修复.md') -Destination (Join-Path $stage '耳机控制与取色交互修复.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\游戏模式音量与通知内存优化.md') -Destination (Join-Path $stage '游戏模式音量与通知内存优化.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\蓝牙键盘电量与频谱可见性修复.md') -Destination (Join-Path $stage '蓝牙键盘电量与频谱可见性修复.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\频谱专辑色微调.md') -Destination (Join-Path $stage '频谱专辑色微调.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\常驻内存诊断与优化.md') -Destination (Join-Path $stage '常驻内存诊断与优化.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\媒体设备与交互修复.md') -Destination (Join-Path $stage '媒体设备与交互修复.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\语音唤醒与接收器快速刷新.md') -Destination (Join-Path $stage '语音唤醒与接收器快速刷新.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\语音识别启动与提示修复.md') -Destination (Join-Path $stage '语音识别启动与提示修复.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\助手识别与窗口交接响应修复.md') -Destination (Join-Path $stage '助手识别与窗口交接响应修复.md')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\通知堆叠与助手会话状态修复.md') -Destination (Join-Path $stage '通知堆叠与助手会话状态修复.md')
    foreach ($guide in @('开箱即用与跨设备.md','DeepSeekHarness接入.md','Antigravity审批接入调研.md','本机Agent对话插件.md','QQ与微信消息提醒.md','智能充电状态读取.md','充电器类型接口调研.md','USB耳机播放状态同步修复.md','多品牌耳机插件更新.md','音乐与智能充电状态稳定修复.md','音量卡片滚轮与主题进度条.md','后台助手与音乐岛恢复修复.md','系统音量浮层接管.md','游戏自动免打扰.md')) {
        Copy-Item -LiteralPath (Join-Path $installerRepo ('docs\' + $guide)) -Destination (Join-Path $stage $guide)
    }
    foreach ($guide in @('中英文界面支持.md','网卡采样卡顿与Codex状态刷新.md','浏览器最大化与全屏自动收起.md','插件详细设置按需打开.md','启动Logo与副岛点击.md','奶龙放大与通知提示音.md','启动欢迎图片与音效.md','设置语义图标与媒体侧岛置顶.md','开机时间兼容与屏幕工具.md','默认提示长度与通知图标.md','提示边界与语音接口测试.md','耳机电量重复与蓝牙身份修复.md','MiMo语音识别与下方翻译岛.md','通知图标与会话人格发送.md','API人格与按需读取.md','API助手岛内信息读取.md')) {
        Copy-Item -LiteralPath (Join-Path $installerRepo ('docs\' + $guide)) -Destination (Join-Path $stage $guide)
    }
    $agentAdapters = Join-Path $stage 'agent-integrations'
    foreach ($avatarGuide in @('Gemini动画与ClaudeCode接入.md','Codex-GPT形象接入.md','DeepSeek-DS形象接入.md','Claude形象接入.md','助手形象大小与出框显示.md','助手形象映射与循环播放修复.md')) {
        Copy-Item -LiteralPath (Join-Path $installerRepo ('docs\' + $avatarGuide)) -Destination (Join-Path $stage $avatarGuide)
    }
    New-Item -ItemType Directory -Path $agentAdapters -Force | Out-Null
    foreach ($adapterName in @('CodexHooks','AntigravityHooks','DeepSeekHarnessPlugin','ClaudeCodeHooks')) {
        Copy-Item -LiteralPath (Join-Path $installerRepo ('examples\' + $adapterName)) -Destination $agentAdapters -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $installerRepo 'packaging\agent-setup.ps1') -Destination (Join-Path $agentAdapters 'setup.ps1')
    Copy-Item -LiteralPath (Join-Path $installerRepo 'tools\OneClickCodex\native-hooks-info.mjs') -Destination $agentAdapters
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\Codex跨电脑状态识别.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\Agent等待回复提示.md') -Destination $stage
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
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\跨电脑兼容性检查与加固.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\音频频谱对比度增强.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\阿米娅启动动画与切换条取色.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\星野欢迎图片与随机组合.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\Codex结束状态与助手小岛背景修复.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\界面主题与交互优化.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\API对话按钮闪烁修复.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $installerRepo 'docs\歌词前奏与间奏圆点.md') -Destination $stage
    foreach ($guide in @('诊断脱敏与本机Agent文件接入.md','服务商预设与插件设置显示.md','项目结构.md','多显示器兼容.md')) {
        Copy-Item -LiteralPath (Join-Path $installerRepo ('docs\' + $guide)) -Destination $stage
    }
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
