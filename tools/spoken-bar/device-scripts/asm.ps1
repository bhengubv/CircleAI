# Asks the ARM64 JIT what machine code it makes of the kernels: copies them without AggressiveOptimization (crossgen2 skips such methods),
# builds them, and has crossgen2 disassemble the methods named. Nothing here runs on anything.
param([string]$Methods = 'Vectors PartRows', [string]$Out = 'kernels.asm')
$ErrorActionPreference = 'Stop'
$here = '.\jit'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI\src\CircleAI\Inference\Managed'
$msb = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe'
$cg = "$env:USERPROFILE\.nuget\packages\microsoft.netcore.app.crossgen2.win-x64\10.0.14\tools\crossgen2.exe"
$fw = (Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory | Where-Object { $_.Name -like '10.0.*' } | Sort-Object { [version]$_.Name } | Select-Object -Last 1).FullName
New-Item -ItemType Directory -Force "$here\src" | Out-Null
foreach ($f in 'Kernels\Ptq1Pair.cs', 'Kernels\Ptq1.cs', 'Kernels\Q8Activations.cs', 'Kernels\KernelChoice.cs', 'Runtime\WorkerPool.cs') {
    $t = Get-Content "$repo\$f" -Raw
    $t = $t -replace '\[MethodImpl\(MethodImplOptions\.AggressiveOptimization\)\]', ''
    Set-Content -Path "$here\src\$(Split-Path $f -Leaf)" -Value $t -Encoding utf8
}
& $msb "$here\jit.csproj" -t:Restore -low -m:1 -nodeReuse:false -v:m -nologo | Select-Object -Last 2
& $msb "$here\jit.csproj" -t:Build -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release -v:m -nologo | Select-String -Pattern ' error |Build succeeded|-> ' | Select-Object -First 8 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) }
$dll = "$here\bin\Release\net10.0\jit.dll"
$asm = "$here\$Out"
if (Test-Path $asm) { Remove-Item $asm }
& $cg $dll -o "$here\r2r.dll" -r "$fw\*.dll" --targetos:linux --targetarch:arm64 -O --codegenopt "JitDisasm=$Methods" --codegenopt "JitStdOutFile=$asm" 2>&1 | Select-Object -Last 4
"asm: $((Get-Item $asm -ErrorAction SilentlyContinue).Length) bytes"
