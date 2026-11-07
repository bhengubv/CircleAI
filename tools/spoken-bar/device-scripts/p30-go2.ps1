# The same run as p30-go.ps1 on the build with the reading fix and the tools read ahead, inside Circle AI: first
# the tools are left to be read, then every check a broadcast can drive. No screen touched, no device setting
# changed, nothing downloaded. Each step's lines stay in s9-<step>-go2.log, so yesterday's -go logs stay to compare.
param([string]$Serial = '192.168.0.115:5555', [int]$SoakMinutes = 30)
$ErrorActionPreference = 'Continue'
$s = '.'

"######## the tools read ahead $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go2' -Steps setup, prime

"######## the questions that failed $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go2' -Steps asked, tool, tool-model, sum, tool-cold

"######## kernels and answers $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go2' -Steps kernels, managed, prompts

"######## speed, the C# engine $(Get-Date -Format HH:mm:ss)"
& "$s\p30-speed.ps1" -Serial $Serial -Runs 3 -Tag '-go2-managed'
"######## speed, the native fork $(Get-Date -Format HH:mm:ss)"
& "$s\p30-speed.ps1" -Serial $Serial -Runs 3 -Tag '-go2-native' -Engine LlamaCpp

"######## a photo, the log, a cold start $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go2' -Steps photo-csharp, see, logs, cold-csharp, cold-queue

"######## a flat hot phone $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go2' -Steps pretend

"######## a brain too big $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go2' -Steps too-big-held

"######## the soak, $SoakMinutes min $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go2' -Steps setup-end, logs-end -Soak -SoakMinutes $SoakMinutes

"######## finished $(Get-Date -Format HH:mm:ss)"
