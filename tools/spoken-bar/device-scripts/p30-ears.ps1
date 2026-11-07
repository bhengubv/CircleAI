# Where the ears' time goes: a clip of quiet room (the encoder and little else) against a spoken clip, and which cores their
# threads sit on. Pushes one test clip; reads /proc only; no setting changes.
param([string]$Serial = 'UTKDU19919000815')
$ErrorActionPreference = 'Continue'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$dir = '/sdcard/Android/data/com.bhengubv.circleai.service/files'
$scratch = '.'
$file = "$scratch\clips\q-quiet.wav"
if (-not (Test-Path $file)) {
    $n = 16000 * 2
    $random = [System.Random]::new(11)
    $pcm = [byte[]]::new($n * 2)
    for ($i = 0; $i -lt $n; $i++) { $b = [BitConverter]::GetBytes([int16]$random.Next(-40, 41)); $pcm[$i * 2] = $b[0]; $pcm[$i * 2 + 1] = $b[1] }
    $f = [System.IO.File]::Create($file)
    $w = [System.IO.BinaryWriter]::new($f)
    $w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + $pcm.Length)); $w.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
    $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]16000); $w.Write([int]32000); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]$pcm.Length); $w.Write($pcm); $w.Close()
    & $adb -s $Serial push $file "$dir/q-quiet.wav" 2>&1 | Select-Object -Last 1
}
$svc = ("$(& $adb -s $Serial shell "pidof $pkg" 2>$null)".Trim() -split '\s+')[0]
$since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
foreach ($clip in 'q-quiet', 'q-bird') {
    & $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do hear --es file $clip.wav --ei from 800 --ei to 3400 --ei times 3" 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
    $script = @'
for t in /proc/PID/task/*; do s=$(cat $t/stat 2>/dev/null); set -- ${s#*) }; if [ "$1" = "R" ]; then echo "${t##*/}:${37}"; fi; done
'@.Replace('PID', $svc)
    $cores = @{}
    for ($i = 0; $i -lt 6; $i++) {
        foreach ($line in @(& $adb -s $Serial shell $script 2>$null)) { if ($line -match '^\d+:(\d+)') { $cores[$Matches[1]] = 1 + [int]$cores[$Matches[1]] } }
    }
    "$clip running threads by core: " + (($cores.GetEnumerator() | Sort-Object { [int]$_.Key } | ForEach-Object { "cpu$($_.Key)=$($_.Value)" }) -join ' ')
    Start-Sleep -Seconds 4
}
& $adb -s $Serial logcat -d -v threadtime -T "$since" 2>$null | Where-Object { $_ -match 'hear:|stt: \d' } | ForEach-Object { $_.Substring(31, [Math]::Min(150, $_.Length - 31)) }
