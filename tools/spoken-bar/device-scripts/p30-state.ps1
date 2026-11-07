# Reads, and only reads, what the phone thinks of our processes: what is in front, each process's core group, how Android ranks it.
param([string]$Serial = 'UTKDU19919000815')
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
"=== $(Get-Date -Format HH:mm:ss) focus"
& $adb -s $Serial shell "dumpsys window | grep -E 'mCurrentFocus'"
& $adb -s $Serial shell "dumpsys power | grep -E 'mWakefulness=|Display Power: state'"
"--- core groups"
& $adb -s $Serial shell 'for p in $(ps -A -o PID,NAME | grep -E "circleai" | sed "s/^ *//" | cut -d" " -f1); do echo "$p $(cat /proc/$p/cmdline | tr "\0" " " | sed "s/ *$//") cpuset=$(cat /proc/$p/cpuset) adj=$(cat /proc/$p/oom_score_adj) $(grep Cpus_allowed_list /proc/$p/status | tr "\t" " ")"; done'
"--- threads by group"
& $adb -s $Serial shell 'for p in $(ps -A -o PID,NAME | grep -E "circleai.service" | sed "s/^ *//" | cut -d" " -f1); do echo "pid $p:"; for t in /proc/$p/task/*; do cat $t/cpuset; done | sort | uniq -c; done'
"--- android's ranking"
& $adb -s $Serial shell "dumpsys activity oom | grep -A7 -E 'Proc #.*circleai'"
