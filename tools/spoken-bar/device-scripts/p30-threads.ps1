# Reads, and only reads, every thread of our two service processes: name, core group, allowed cores, nice, scheduling class.
param([string]$Serial = 'UTKDU19919000815')
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$script = @'
for p in $(ps -A -o PID,NAME | grep -E "circleai.service" | sed "s/^ *//" | cut -d" " -f1); do
  echo "== pid $p $(cat /proc/$p/cmdline | tr "\0" " " | sed "s/ *$//")"
  for t in /proc/$p/task/*; do
    tid=${t##*/}
    comm=$(cat $t/comm)
    set -- $(sed "s/.*) //" $t/stat)
    nice=${17}
    policy=${39}
    rt=${38}
    last=${37}
    allowed=$(grep Cpus_allowed_list $t/status | cut -f2)
    echo "$tid|$comm|$(cat $t/cpuset)|$allowed|nice=$nice|policy=$policy|rt=$rt|lastcpu=$last"
  done
done
'@
$tmp = [IO.Path]::GetTempFileName()
[IO.File]::WriteAllText($tmp, $script.Replace("`r`n", "`n"))
& $adb -s $Serial push $tmp /data/local/tmp/threads.sh 2>$null | Out-Null
Remove-Item $tmp
& $adb -s $Serial shell "sh /data/local/tmp/threads.sh"
& $adb -s $Serial shell "rm /data/local/tmp/threads.sh"
