# Two of Windows' own voices taking turns, written as one 16 kHz mono WAV: a test recording for "who said what"
# and for search by meaning on the P30. Run with Windows PowerShell 5.1 (System.Speech).
param([string]$Out = "$PSScriptRoot\two-voices.wav")
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voices = $synth.GetInstalledVoices() | Where-Object { $_.Enabled } | ForEach-Object { $_.VoiceInfo }
"voices: " + (($voices | ForEach-Object { "$($_.Name) ($($_.Gender), $($_.Culture))" }) -join '; ')
$male = ($voices | Where-Object { $_.Gender -eq 'Male' -and $_.Culture.Name -like 'en-*' } | Select-Object -First 1).Name
$female = ($voices | Where-Object { $_.Gender -eq 'Female' -and $_.Culture.Name -like 'en-*' } | Select-Object -First 1).Name
if (-not $male -or -not $female) { "need one male and one female English voice"; exit 1 }
"male: $male; female: $female"

$lines = @(
    @($male,   "The tractor broke down this morning, so the maize went in a week late."),
    @($female, "Then we should ask the extension officer about fertiliser before the rains come."),
    @($male,   "My clinic appointment is on Thursday at nine, so I cannot go to the market."),
    @($female, "I will take the eggs to the market and bring back seed for the garden.")
)
$prompt = New-Object System.Speech.Synthesis.PromptBuilder
foreach ($l in $lines) {
    $prompt.StartVoice($l[0]); $prompt.AppendText($l[1]); $prompt.EndVoice()
    $prompt.AppendBreak([TimeSpan]::FromMilliseconds(900))
}
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$synth.SetOutputToWaveFile($Out, $format)
$synth.Speak($prompt)
$synth.SetOutputToNull()
$synth.Dispose()
$f = Get-Item $Out
"wrote $($f.FullName): $($f.Length) bytes, about $([Math]::Round(($f.Length - 44) / 32000, 1)) s"
