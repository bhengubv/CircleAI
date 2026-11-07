# crossgen2 on the built kernels, with a time limit; disassembles the methods named.
param([string]$Methods = 'Vectors PartRows Vectors4', [string]$Out = 'kernels.asm', [int]$MostSeconds = 150)
$here = '.\jit'
$cg = "$env:USERPROFILE\.nuget\packages\microsoft.netcore.app.crossgen2.win-x64\10.0.14\tools\crossgen2.exe"
$fw = (Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory | Where-Object { $_.Name -like '10.0.*' } | Sort-Object { [version]$_.Name } | Select-Object -Last 1).FullName
$rsp = "$here\cg.rsp"
@(
  "$here\bin\Release\net10.0\jit.dll"
  "-o:$here\r2r.dll"
  "-r:$fw\*.dll"
  '--targetos:linux'
  '--targetarch:arm64'
  '-O'
  '--parallelism:1'
  '--instruction-set:neon,lse'
  "--codegenopt:JitDisasm=$Methods"
  "--codegenopt:JitStdOutFile=$here\$Out"
) | Set-Content -Path $rsp -Encoding ascii
if (Test-Path "$here\$Out") { Remove-Item "$here\$Out" }
if (Test-Path "$here\r2r.dll") { Remove-Item "$here\r2r.dll" }
$p = Start-Process -FilePath $cg -ArgumentList "@`"$rsp`"" -NoNewWindow -PassThru -RedirectStandardOutput "$here\cg-out.txt" -RedirectStandardError "$here\cg-err.txt"
if (-not $p.WaitForExit($MostSeconds * 1000)) { "still running after $MostSeconds s - stopping it"; $p.Kill() } else { "exit $($p.ExitCode)" }
Get-Content "$here\cg-out.txt" -TotalCount 12 -ErrorAction SilentlyContinue
Get-Content "$here\cg-err.txt" -TotalCount 12 -ErrorAction SilentlyContinue
"asm: $((Get-Item "$here\$Out" -ErrorAction SilentlyContinue).Length) bytes"
