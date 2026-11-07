# The final build on the P30, inside Circle AI: the brain stepped down and grown back, with a tool turn on each shape.
# Each shape's tools should already be on disk, so nothing should be read again. No screen touched, no device setting
# changed, nothing downloaded. Each step's lines stay in s9-<step>-flip*.log.
param([string]$Serial = '192.168.0.115:5555')
$ErrorActionPreference = 'Continue'
$s = '.'

"######## stepped down $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-flip' -Steps pretend
"######## a tool turn on the lean shape $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-flip-lean' -Steps tool-model
"######## growing back $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-flip' -Steps grow-back
"######## a tool turn on the full shape $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-flip-full' -Steps tool-model
"######## finished $(Get-Date -Format HH:mm:ss)"
