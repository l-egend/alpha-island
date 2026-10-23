param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$SkipLiveGraphics,
    [string[]]$Only,
    [string]$RuntimeVersion = '',
    [int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $projectRoot ('bin\maintenance-tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
[IO.Directory]::CreateDirectory($output) | Out-Null
Push-Location -LiteralPath $projectRoot
try {
    $runtimeOutput = $null
    if ($RuntimeVersion) {
        if ($RuntimeVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid test runtime version.' }
        $versionProject = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'alpha-island.csproj') -Raw)
        $runtimeOutput = Join-Path $projectRoot ('bin\RegressionRuntime' + ([string]$versionProject.Project.PropertyGroup.Version).Split('.')[-1])
    }
    if (!$SkipBuild) {
        if ($runtimeOutput) {
            & dotnet publish 'tools\RegressionTests\RegressionTests.csproj' -c $Configuration -r win-x64 --self-contained true "-p:InstallerRuntimeVersion=$RuntimeVersion" -p:DeployPlugin=false -m:1 -o $runtimeOutput -v minimal *> (Join-Path $output 'build.log')
        } else {
            & dotnet build 'tools\RegressionTests\RegressionTests.csproj' -c $Configuration -m:1 -p:DeployPlugin=false -v minimal *> (Join-Path $output 'build.log')
        }
        if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $output 'build.log') -Tail 30; throw 'Build failed.' }
    }
    $executable = Join-Path $projectRoot "tools\RegressionTests\bin\$Configuration\net8.0-windows10.0.19041.0\AlphaIsland.RegressionTests.exe"
    if ($runtimeOutput) { $executable = Join-Path $runtimeOutput 'AlphaIsland.RegressionTests.exe' }
    $flags = @(
        '--agent-completion-visual-only','--cross-machine-compatibility-only','--volume-flyout-only','--api-model-avatar-only','--runtime-reliability-only','--interaction-feedback-only','--greeting-handoff-only','--startup-morph-only','--greeting-media-only','--weather-only','--settings-icon-media-pin-only','--boot-timing-only','--notification-logo-fit-only','--short-hints-only','--speech-setup-only','--hint-bounds-only','--bluetooth-identity-only','--speech-protocol-only','--speech-caption-only','--notification-refinement-only','--semantic-icons-only','--redesigned-icons-only','--api-persona-only','--island-information-only','--api-chat-input-only','--voice-assistant-only','--receiver-latency-only',
        '--media-peripheral-lifecycle-only','--resident-memory-only','--keyboard-spectrum-only','--gaming-notifications-only','--gaming-memory-only',
        '--peripheral-interactions-only','--earbuds-plugin-only',
        '--screen-tools-open-only','--personal-reminders-only','--desktop-insights-only',
        '--device-battery-enhancements-only','--card-wheel-only','--component-menu-input-only','--launcher-input-only',
        '--assistant-reminders-only','--desktop-assistant-only',
        '--maintenance-only','--clipboard-history-preferences-only','--cover-clipboard-only','--plugin-settings-surface-only','--ai-settings-layout-only','--ai-config-only','--productivity-corners-only','--codex-terminal-only','--codex-status-recovery-only','--island-zorder-only','--agent-startup-latency-only','--handoff-responsiveness-only','--agent-window-responsiveness-only','--agent-window-handoff-only','--agent-handoff-only','--notification-stack-only','--notification-visual-stack-only','--notification-startup-only','--agent-session-identity-only','--external-agent-only','--agent-lifetime-only','--core-logic-only','--optimization-core-only','--optimization-ui-only',
        '--plugin-isolation-only','--bundled-plugin-isolation-only','--cold-plugin-resource-check','--plugin-lifecycle-only','--plugins-uninstall-only',
        '--agent-background-resume-only','--agent-multi-session-only','--agent-avatar-routing-only','--agent-avatar-loop-only',
        '--component-hit-area-only','--cpu-only','--energy-saver-only','--game-quiet-only','--game-compact-lock-only',
        '--liquid-cell-motion-only','--liquid-glass-core-only','--liquid-glass-ui-only','--liquid-glass-native-only','--liquid-glass-live-only',
        '--music-state-stability-only','--media-sync-only','--media-handoff-only','--bluetooth-media-latency-only','--usb-media-state-only',
        '--network-curves-only','--performance-only','--power-island-lifecycle-only',
        '--settings-appearance-only','--settings-consolidation-only','--daily-traffic-only','--device-batteries-only','--status-display-only','--status-split-only','--cards-focus-only',
        '--media-traffic-only','--ai-conversations-only','--downloads-regression-only','--notification-presentation-only','--notification-full-only','--messenger-notifications-only','--volume-wheel-only'
    )
    if ($SkipLiveGraphics) { $flags = $flags | Where-Object { $_ -notin @('--liquid-glass-native-only','--liquid-glass-live-only') } }
    if ($Only) {
        foreach ($flag in $Only) { if ($flag -notin $flags -and $flag -notin @('--volume-flash-live','--volume-game-quiet-live','--speech-audio-live','--boot-timing-live','--launch-links-only','--screen-tools-theme-only')) { throw "Unknown maintenance check: $flag" } }
        $flags = $Only
    }
    $results = @()
    foreach ($flag in $flags) {
        $log = Join-Path $output ($flag.TrimStart('-') + '.log')
        $errorLog = Join-Path $output ($flag.TrimStart('-') + '.stderr.log')
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath $executable -ArgumentList $flag -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError $errorLog
        # Keep the native handle open so Windows PowerShell 5.1 retains ExitCode.
        $nativeHandle = $process.Handle
        $timedOut = !$process.WaitForExit([Math]::Max(10,[Math]::Min(600,$TimeoutSeconds)) * 1000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $process.Refresh()
        $exitCode = if ($timedOut) { -1 } else { $process.ExitCode }
        if ($null -eq $exitCode) { throw ('Missing process exit code: ' + $flag) }
        $checks = @(Select-String -LiteralPath $log -Pattern '^PASS:').Count
        $results += [PSCustomObject]@{ Flag=$flag; Exit=$exitCode; Checks=$checks; Seconds=[Math]::Round($watch.Elapsed.TotalSeconds,2); Timeout=$timedOut }
        $results[-1] | ConvertTo-Json -Compress | Write-Output
        if ($exitCode -ne 0) { Get-Content -LiteralPath $log -Tail 8; Get-Content -LiteralPath $errorLog -Tail 8 }
        $process.Dispose()
    }
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
    Write-Output "Results: $output"
    if (@($results | Where-Object Exit -ne 0).Count -gt 0) { exit 1 }
    exit 0
} finally { Pop-Location }
