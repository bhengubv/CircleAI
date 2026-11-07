# The tools-off-plain-turns fix on the P30, inside Circle AI: build, install in place, then a tool question, a plain
# one and a tool question again. No screen touched, no device setting changed, nothing downloaded. Each step's lines
# stay in s9-<step>-latch<n>.log. p30-s9.ps1 runs its steps in file order, so each question is its own call.
param([string]$Serial = '192.168.0.115:5555')
$ErrorActionPreference = 'Continue'
$s = '.'

$since = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
"######## build $since"
& "$s\locked-build.ps1" -Repo 'C:\Dev\Solutions\com.bhengubv\CircleAI' -Command "& '$s\build-service.ps1'"

"######## install $(Get-Date -Format HH:mm:ss)"
& "$s\install-service.ps1" -Since $since -Serial $Serial
if ($LASTEXITCODE -ne 0) { "NOT RUNNING: the install did not settle"; exit 1 }

"######## tool question $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-latch1' -Steps tool-model
"######## plain question $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-latch2' -Steps asked
"######## tool question again $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-latch3' -Steps tool-model
"######## finished $(Get-Date -Format HH:mm:ss)"
