# Every test project in the repo, one at a time through the build lock: CircleAI.Tests on both legs, then each other
# project on each leg it targets. CircleAI.DeviceTests is an Android head: dotnet test has nothing to run there.
# A disk stall makes the lock script refuse (exit 3): this waits a minute and asks again. A failing suite does not stop the run.
$ErrorActionPreference = 'Continue'
$lock = '.\locked-build.ps1'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'

function Invoke-Locked([hashtable]$Arguments) {
    do {
        & $lock @Arguments
        $code = $LASTEXITCODE
        if ($code -eq 3) { Start-Sleep -Seconds 60 }
    } while ($code -eq 3)
}

"=== started $(Get-Date -Format 'HH:mm:ss')"
foreach ($leg in 'net10.0', 'net9.0') {
    "=== SUITE CircleAI.Tests $leg"
    Invoke-Locked @{ Repo = $repo; Framework = $leg; Filter = 'FullyQualifiedName~CircleAI' }
}

$projects = Get-ChildItem "$repo\tests" -Directory | ForEach-Object { Get-ChildItem $_.FullName -Filter *.csproj -File } |
    Where-Object { $_.BaseName -notin 'CircleAI.Tests', 'CircleAI.DeviceTests' } | Sort-Object BaseName
foreach ($p in $projects) {
    $xml = [xml](Get-Content $p.FullName -Raw)
    $frameworks = @($xml.Project.PropertyGroup | ForEach-Object { $_.TargetFrameworks; $_.TargetFramework } | Where-Object { $_ }) -join ';'
    foreach ($leg in 'net10.0', 'net9.0') {
        if ($frameworks -notmatch [regex]::Escape($leg)) { continue }
        "=== SUITE $($p.BaseName) $leg"
        $relative = $p.FullName.Substring($repo.Length + 1)
        $command = "dotnet test '$relative' -c Release -f $leg --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false --logger 'console;verbosity=normal' 2>&1 | Select-String -Pattern 'Passed!|Failed!|^\s+Failed |Total tests|Test Run Aborted|error '"
        Invoke-Locked @{ Repo = $repo; Command = $command }
    }
}
"=== finished $(Get-Date -Format 'HH:mm:ss')"
