# Today's build on the P30, inside Circle AI: every check of the plan's that a broadcast can drive, one after the
# other. No screen touched, no device setting changed, nothing downloaded. Speed runs before the checks that step
# the brain down, and the soak runs last. Each step's lines stay in s9-<step>-go.log.
param([string]$Serial = '192.168.0.115:5555', [int]$SoakMinutes = 30)
$ErrorActionPreference = 'Continue'
$s = '.'

"######## kernels and answers $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go' -Steps setup, kernels, managed, prompts, asked, tool, tool-model

"######## speed, the C# engine $(Get-Date -Format HH:mm:ss)"
& "$s\p30-speed.ps1" -Serial $Serial -Runs 3 -Tag '-go-managed'
"######## speed, the native fork $(Get-Date -Format HH:mm:ss)"
& "$s\p30-speed.ps1" -Serial $Serial -Runs 3 -Tag '-go-native' -Engine LlamaCpp

"######## a photo, the log, a cold start $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go' -Steps photo-csharp, see, logs, cold-csharp, cold-queue

"######## a flat hot phone $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go' -Steps pretend

"######## a brain too big $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go' -Steps too-big-held

"######## the soak, $SoakMinutes min $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-go' -Steps setup-end, logs-end -Soak -SoakMinutes $SoakMinutes

"######## finished $(Get-Date -Format HH:mm:ss)"
