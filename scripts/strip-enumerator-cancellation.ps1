# strip-enumerator-cancellation.ps1
#
# (3.4.0) Strip [EnumeratorCancellation] from StreamAsync overrides that
# only delegate to an inner session — they're not async iterators, so
# the attribute has no effect (CS8424).

$root = Join-Path $PSScriptRoot "..\src"
$targets = Get-ChildItem -Path $root -Recurse -File -Include "*CompanionAdapter.cs", "ICompanionSession.cs"

$updated = 0
foreach ($f in $targets) {
    $orig = [System.IO.File]::ReadAllText($f.FullName)
    # Strip the attribute. Two common forms.
    $new = $orig -replace '\[System\.Runtime\.CompilerServices\.EnumeratorCancellation\]\s*', '' `
                -replace '\[EnumeratorCancellation\]\s*', ''
    if ($new -ne $orig) {
        [System.IO.File]::WriteAllText($f.FullName, $new)
        $updated++
    }
}

Write-Output "Stripped [EnumeratorCancellation] from $updated file(s)."
