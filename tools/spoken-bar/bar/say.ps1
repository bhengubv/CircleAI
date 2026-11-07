# Windows' own voices say each piece to a 16 kHz mono WAV. Nothing of ours runs here.
param([string]$Jobs, [string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$list = Get-Content $Jobs -Raw -Encoding UTF8 | ConvertFrom-Json
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
foreach ($job in $list) {
    $synth.SelectVoice($job.voice)
    $synth.Rate = [int]$job.rate
    $synth.SetOutputToWaveFile((Join-Path $Out ($job.name + '.wav')), $format)
    $synth.Speak($job.text)
    $synth.SetOutputToNull()
}
$synth.Dispose()
"said $($list.Count) pieces"
