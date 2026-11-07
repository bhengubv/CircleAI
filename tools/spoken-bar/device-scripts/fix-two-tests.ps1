# Remakes rotation.json with the changed reference tool, keeping the old file aside to compare, then runs the two
# tests and the rotation's own tests on both legs. Everything goes through the build lock, one step at a time.
$ErrorActionPreference = 'Continue'
$scratch = '.'
$lock = "$scratch\locked-build.ps1"
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$model = "$env:LOCALAPPDATA\CircleAI\models\Ternary-Bonsai-2-2B\Ternary-Bonsai-2-2B-PTQ1_0.gguf"
$fixture = "$repo\fixtures\bonsai2\2B\rotation.json"

Copy-Item $fixture "$scratch\rotation-before.json" -Force
$cmd = "& `$msb tools\bonsai-ref\bonsai-ref.csproj -restore -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release -v:m -nologo; if (`$LASTEXITCODE -eq 0) { & .\tools\bonsai-ref\bin\Release\net10.0\bonsai-ref.exe rotation '$model' fixtures\bonsai2\2B\logits.json fixtures\bonsai2\2B\rotation.json }"
& $lock -Repo $repo -Command $cmd

$old = Get-Content "$scratch\rotation-before.json" -Raw | ConvertFrom-Json
$new = Get-Content $fixture -Raw | ConvertFrom-Json
foreach ($chunk in $new.widths) {
    $before = $old.widths | Where-Object width -eq $chunk.width
    $same = 0; $flipped = 0; $other = 0
    for ($i = 0; $i -lt $chunk.x.Count; $i++) {
        if ($chunk.x[$i] -eq $before.x[$i]) { $same++ }
        elseif ($chunk.x[$i] -eq -$before.x[$i]) { $flipped++ }
        else { $other++ }
    }
    $yDiffers = @(for ($i = 0; $i -lt $chunk.y.Count; $i++) { if ($chunk.y[$i] -ne $before.y[$i]) { 1 } }).Count
    "width $($chunk.width): x same $same, sign flipped $flipped, otherwise different $other; y values changed $yDiffers"
}

foreach ($leg in 'net10.0', 'net9.0') {
    "=== $leg"
    & $lock -Repo $repo -Framework $leg -Filter 'FullyQualifiedName~HadamardChunkTests|FullyQualifiedName~SpeculativeDecodingTests|FullyQualifiedName~PrismHadamardTests'
}
"=== finished $(Get-Date -Format 'HH:mm:ss')"
