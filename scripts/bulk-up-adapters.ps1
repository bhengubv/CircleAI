# bulk-up-adapters.ps1
#
# (3.3.0) Append 4 substantive domain methods to every *CompanionAdapter.cs
# in src/CircleAI.* — auto-detecting the inner-session field name (_i vs _inner).

$root = Join-Path $PSScriptRoot "..\src"
$adapters = Get-ChildItem -Path $root -Filter "*CompanionAdapter.cs" -Recurse -File

$inserted = 0
$skipped  = 0
foreach ($f in $adapters) {
    $content = [System.IO.File]::ReadAllText($f.FullName)

    if ($content.Contains("ImpactAssessmentAsync")) {
        # Already injected (possibly with wrong field) — strip prior injection so we can re-inject correctly.
        $marker = "    public Task<string> ImpactAssessmentAsync"
        $idx = $content.IndexOf($marker)
        if ($idx -ge 0) {
            $lastBrace = $content.LastIndexOf("}")
            $content = $content.Substring(0, $idx).TrimEnd() + "`n" + $content.Substring($lastBrace)
        }
    }

    # Detect field name from a wrapper method.
    $field = "_inner"
    if     ($content.Contains("_inner.AgentAsync")) { $field = "_inner" }
    elseif ($content.Contains("_i.AgentAsync"))     { $field = "_i" }
    elseif ($content.Contains("_session.AgentAsync")) { $field = "_session" }
    else {
        $skipped++
        continue
    }

    $vertical = $f.BaseName.Replace("CompanionAdapter", "")

    $block = @'

    public Task<string> ImpactAssessmentAsync(string proposedChange, string stakeholders, CancellationToken ct=default)
        => FIELD.AgentAsync($"Assess the impact of this proposed change in VERTICAL: {proposedChange}. Stakeholders affected: {stakeholders}. Identify benefits, risks, mitigations, and a recommended go/no-go.", ct);

    public Task<string> SummariseAsync(string content, int maxBullets=5, CancellationToken ct=default)
        => FIELD.AgentAsync($"Summarise the following VERTICAL content into at most {maxBullets} bullets, preserving facts and key numbers: {content}", ct);

    public Task<string> NextActionsAsync(string currentState, int horizonDays=7, CancellationToken ct=default)
        => FIELD.AgentAsync($"Given this VERTICAL situation: {currentState}. Propose the 3 most useful next actions for the next {horizonDays} days, with owner and due date.", ct);

    public Task<string> ExplainAsync(string concept, string audience="adult layperson", CancellationToken ct=default)
        => FIELD.AgentAsync($"Explain the VERTICAL concept '{concept}' for a {audience}. Use 3 short paragraphs and a real example.", ct);

'@
    $block = $block.Replace("VERTICAL", $vertical).Replace("FIELD", $field)

    $lastBrace = $content.LastIndexOf("}")
    if ($lastBrace -lt 0) { continue }

    $newContent = $content.Substring(0, $lastBrace).TrimEnd() + "`n" + $block + "}`n"
    [System.IO.File]::WriteAllText($f.FullName, $newContent)
    $inserted++
}

Write-Output "Inserted: $inserted, Skipped: $skipped"
