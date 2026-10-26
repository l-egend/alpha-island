param([string]$Output = (Join-Path $PSScriptRoot '..\Assets\Sounds\NotificationGentle.wav'))
$ErrorActionPreference='Stop'
$rate=48000
$samples=[int]($rate * 0.48)
$stream=[IO.File]::Create([IO.Path]::GetFullPath($Output))
$writer=[IO.BinaryWriter]::new($stream)
try {
    $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int](36+$samples*2))
    $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([int]16)
    $writer.Write([int16]1); $writer.Write([int16]1); $writer.Write([int]$rate)
    $writer.Write([int]($rate*2)); $writer.Write([int16]2); $writer.Write([int16]16)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([int]($samples*2))
    for($index=0;$index -lt $samples;$index++) {
        $time=$index/[double]$rate
        $envelope=(1-[Math]::Exp(-$time*220))*[Math]::Exp(-$time*12)*[Math]::Min(1,(0.48-$time)/0.035)
        $tone=[Math]::Sin(2*[Math]::PI*880*$time)+0.36*[Math]::Sin(2*[Math]::PI*1320*$time)
        $writer.Write([int16]([Math]::Clamp($tone*$envelope*0.25,-1,1)*32767))
    }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Output ('Generated notification sound: '+[IO.Path]::GetFullPath($Output))
