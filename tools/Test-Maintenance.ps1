param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$SkipLiveGraphics,
    [string[]]$Only,
    [int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $projectRoot ('bin\maintenance-tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
[IO.Directory]::CreateDirectory($output) | Out-Null
Push-Location -LiteralPath $projectRoot
try {
    if (!$SkipBuild) {
        & dotnet build 'tools\RegressionTests\RegressionTests.csproj' -c $Configuration -m:1 -p:DeployPlugin=false -v minimal *> (Join-Path $output 'build.log')
        if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $output 'build.log') -Tail 30; throw 'Build failed.' }
    }
    $executable = Join-Path $projectRoot "tools\RegressionTests\bin\$Configuration\net8.0-windows10.0.19041.0\AlphaIsland.RegressionTests.exe"
    $flags = @(
        '--maintenance-only','--clipboard-history-preferences-only','--cover-clipboard-only','--plugin-settings-surface-only','--ai-settings-layout-only','--ai-config-only','--productivity-corners-only','--codex-terminal-only','--codex-status-recovery-only','--island-zorder-only','--agent-startup-latency-only','--handoff-responsiveness-only','--agent-window-responsiveness-only','--agent-window-handoff-only','--agent-handoff-only','--notification-stack-only','--notification-visual-stack-only','--notification-startup-only','--agent-session-identity-only','--external-agent-only','--agent-lifetime-only','--core-logic-only','--optimization-core-only','--optimization-ui-only',
        '--plugin-isolation-only','--bundled-plugin-isolation-only','--cold-plugin-resource-check','--plugin-lifecycle-only','--plugins-uninstall-only',
        '--agent-background-resume-only','--agent-multi-session-only','--agent-avatar-routing-only','--agent-avatar-loop-only',
        '--component-hit-area-only','--cpu-only','--energy-saver-only','--game-quiet-only',
        '--liquid-cell-motion-only','--liquid-glass-core-only','--liquid-glass-ui-only','--liquid-glass-native-only','--liquid-glass-live-only',
        '--music-state-stability-only','--media-sync-only','--media-handoff-only','--bluetooth-media-latency-only','--usb-media-state-only',
        '--network-curves-only','--performance-only','--power-island-lifecycle-only',
        '--settings-appearance-only','--status-display-only','--status-split-only','--cards-focus-only',
        '--media-traffic-only','--ai-conversations-only','--downloads-regression-only','--notification-presentation-only','--notification-full-only','--volume-wheel-only'
    )
    if ($SkipLiveGraphics) { $flags = $flags | Where-Object { $_ -notin @('--liquid-glass-native-only','--liquid-glass-live-only') } }
    if ($Only) {
        foreach ($flag in $Only) { if ($flag -notin $flags) { throw "Unknown maintenance check: $flag" } }
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
} finally { Pop-Location }
