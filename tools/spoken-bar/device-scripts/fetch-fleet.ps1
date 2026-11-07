# The 9B, the 27B and the 4B's DFlash 2 drafter, one at a time, rate-limited for the SMR disk: resumable into
# <file>.part, checked against the catalogue's SHA-256, and only then given their names. A disk stall stops the run.
$ErrorActionPreference = 'Continue'
$root = Join-Path $env:LOCALAPPDATA 'CircleAI\models'
$files = @(
    @{ Folder = 'Ternary-Bonsai-2-9B'; Repo = 'prism-ml/Ternary-Bonsai-2-9B-gguf'; Name = 'Ternary-Bonsai-2-9B-PTQ1_0.gguf'; Size = 1997486976; Sha = '3561df7ac7d26a71ffa991ac2db740b10841c3a350aa2676ccd3ff5d428b4e8b' },
    @{ Folder = 'Ternary-Bonsai-2-4B-DFlash2'; Repo = 'prism-ml/Ternary-Bonsai-2-4B-gguf'; Name = 'Ternary-Bonsai-2-4B-DFlash2-BF16.gguf'; Size = 1575278208; Sha = '4402be311018d01e31ecd0a894326e2f7bd28bc27a288c5b949799fe000808d8' },
    @{ Folder = 'Ternary-Bonsai-2-27B'; Repo = 'prism-ml/Ternary-Bonsai-2-27B-gguf'; Name = 'Ternary-Bonsai-2-27B-PTQ1_0.gguf'; Size = 5946648928; Sha = '53107f530aa52eb00912263ab1ee29bd199261c87cd7b4ad4ca1318c1fe33ee3' }
)
foreach ($f in $files) {
    $dir = Join-Path $root $f.Folder
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $final = Join-Path $dir $f.Name
    if (Test-Path $final) { "$($f.Name): already here"; continue }
    $stalls = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = (Get-Date).AddMinutes(-15) } -ErrorAction SilentlyContinue).Count
    if ($stalls -gt 0) { "DISK STALLS in the last 15 min: $stalls - stopping before $($f.Name)"; exit 3 }
    $part = "$final.part"
    $url = "https://huggingface.co/$($f.Repo)/resolve/main/$($f.Name)"
    "$(Get-Date -Format 'HH:mm:ss') fetching $($f.Name) ($([math]::Round($f.Size / 1MB)) MB)"
    & curl.exe -L --fail --silent --show-error --retry 5 --retry-delay 10 -C - --limit-rate 30M -o $part $url
    if ($LASTEXITCODE -ne 0) { "curl exit $LASTEXITCODE on $($f.Name)"; exit 1 }
    $got = (Get-Item $part).Length
    if ($got -ne $f.Size) { "$($f.Name): $got bytes, expected $($f.Size)"; exit 1 }
    $sha = (Get-FileHash -Algorithm SHA256 -Path $part).Hash.ToLowerInvariant()
    if ($sha -ne $f.Sha) { "$($f.Name): hash $sha, expected $($f.Sha)"; exit 1 }
    Move-Item -Path $part -Destination $final
    "$(Get-Date -Format 'HH:mm:ss') $($f.Name): verified"
}
"all three here"
