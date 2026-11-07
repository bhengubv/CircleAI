# The recorded questions for the spoken-turn test: Windows' own speech, written to 16 kHz mono WAVs with a second of quiet room
# before the words and half a second after. Nothing of ours runs here; the files are pushed to the service's sideload folder.
param([switch]$Push)
$ErrorActionPreference = 'Stop'
$scratch = '.'
$out = "$scratch\clips"
New-Item -ItemType Directory -Force -Path $out | Out-Null
Add-Type -AssemblyName System.Speech
$questions = [ordered]@{
    'q-kenya' = 'What is the capital of Kenya?'
    'q-bird'  = 'Name one kind of bird.'
    'q-sky'   = 'Why is the sky blue?'
    'q-rain'  = 'Write one short sentence about the rain.'
}
$format = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$random = [System.Random]::new(7)
function Room([int]$ms) {
    $n = 16 * $ms
    $bytes = [byte[]]::new($n * 2)
    for ($i = 0; $i -lt $n; $i++) {
        $v = [int16]($random.Next(-40, 41))
        $b = [BitConverter]::GetBytes($v)
        $bytes[$i * 2] = $b[0]; $bytes[$i * 2 + 1] = $b[1]
    }
    , $bytes
}
foreach ($name in $questions.Keys) {
    $synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
    $raw = "$out\$name.raw.wav"
    $synth.SetOutputToWaveFile($raw, $format)
    $synth.Speak($questions[$name])
    $synth.SetOutputToNull()
    $synth.Dispose()
    $wav = [System.IO.File]::ReadAllBytes($raw)
    $at = 12
    while ([Text.Encoding]::ASCII.GetString($wav, $at, 4) -ne 'data') { $at += 8 + [BitConverter]::ToInt32($wav, $at + 4) }
    $len = [BitConverter]::ToInt32($wav, $at + 4)
    $speech = $wav[($at + 8)..($at + 7 + $len)]
    $pcm = (Room 1000) + $speech + (Room 500)
    $file = [System.IO.File]::Create("$out\$name.wav")
    $w = [System.IO.BinaryWriter]::new($file)
    $w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + $pcm.Length)); $w.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
    $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]16000); $w.Write([int]32000); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]$pcm.Length); $w.Write([byte[]]$pcm); $w.Close()
    Remove-Item $raw
    "$name : $([int]($len / 32)) ms of speech"
}
if ($Push) {
    $adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
    $dir = '/sdcard/Android/data/com.bhengubv.circleai.service/files'
    foreach ($name in $questions.Keys) { & $adb -s UTKDU19919000815 push "$out\$name.wav" "$dir/$name.wav" 2>&1 | Select-Object -Last 1 }
    & $adb -s UTKDU19919000815 shell "ls -la $dir/*.wav"
}
