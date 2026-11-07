# S5 after merging master (S8's tools, effort and translation; S6's Wolverine), in one hold of the
# lock, from the worktree: the whole suite as the house runs it (default lengths), net10.0 then net9.0.
$ErrorActionPreference = 'Continue'
$scratch = "."
$s5 = Join-Path $scratch 's5-sync-and-test.ps1'
& $s5 -NoSync -Framework net10.0 -Filter 'FullyQualifiedName~CircleAI' -Tag 's5-merge2'
& $s5 -NoSync -Framework net9.0 -Filter 'FullyQualifiedName~CircleAI' -Tag 's5-merge2'
