# OPEN-GAPS E33, the third round: the balance's smaller brains checked against Wolverine's step-downs and the
# free memory a brain was killed at. Only the Android head's supervisor changed, so: the E33 classes (the new
# source read among them) and the docs' readers on both legs, then both Android heads compiled - the only build
# that compiles the change. locked-build.ps1 exits 3 when the disk stalled in the last 15 minutes: wait three
# minutes and try again, at most ten times per step.
$s = '.'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$classes = @(
    'SlowIsNotStuckTests', 'TheBrainThatWasWorkingTests', 'APictureTurnCarriesNoHistoryTests',
    'TheAskNamesTheBrainWorkingOnItTests', 'AKnownAnswerIsTheBrainsOwnTests', 'TheBrainKeptAnsweringIsQuickEnoughTests',
    'BrainBalanceTests', 'KernelThreadsTests', 'PictureAcrossTheLinkTests',
    # the docs' readers: CLAIMS rows must name tests that exist, and the OPEN-GAPS readers
    'GenuineProductTests', 'DocumentedCountTests', 'BrainPlanTests', 'DeviceBalanceTests', 'DomainPacksTests',
    'EveryModelArrivesByDefaultTests', 'FirstRunFollowsTheLanguageTests', 'HousekeepingBeforePullTests',
    'HousekeepingSweepTests', 'KokoroItalianTests', 'SpeculativeDecodingTests', 'MicWatchTests', 'MnnWorkingMemoryTests',
    'OneModelCatalogueTests', 'SearchByMeaningTests', 'SolutionMembershipTests', 'TheCatalogueIsTheSelectorTests',
    'ToolsThroughTheServiceTests', 'TransportSelectorTests', 'WhoSaidWhatTests')
$filter = ($classes | Sort-Object -Unique | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
"$(($classes | Sort-Object -Unique).Count) test classes"
$steps = @(
    @{ Name = 'net10.0'; Args = @{ Repo = $repo; Framework = 'net10.0'; Filter = $filter } },
    @{ Name = 'net9.0'; Args = @{ Repo = $repo; Framework = 'net9.0'; Filter = $filter } },
    @{ Name = 'android'; Args = @{ Repo = $repo; Android = $true } })
foreach ($step in $steps) {
    $a = $step.Args
    for ($i = 0; $i -lt 10; $i++) {
        & "$s\locked-build.ps1" @a
        if ($LASTEXITCODE -ne 3) { break }
        "[$(Get-Date -Format HH:mm:ss)] $($step.Name) waits three minutes for a calm disk"
        Start-Sleep -Seconds 180
    }
}
