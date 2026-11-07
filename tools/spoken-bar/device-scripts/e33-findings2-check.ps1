# OPEN-GAPS E33, the second round of findings: the installed fallback and the balance's smaller brain chosen
# speed-first, and a tool's own "do not use it for" no longer read as a cue. The new tests, every class that
# reaches a changed type (BrainSpeed, BundleModelLoader's fallback, AIService's tool choice, the supervisor's
# source readers) and the docs' readers, on both legs; then both Android heads and the desktop head compiled.
# locked-build.ps1 exits 3 when the disk stalled in the last 15 minutes: wait three minutes and try again, at
# most ten times per step.
param([switch]$Net10Only, [switch]$SkipNet10)
$s = '.'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$classes = @(
    # the E33 classes, the new tests among them
    'SlowIsNotStuckTests', 'TheBrainThatWasWorkingTests', 'APictureTurnCarriesNoHistoryTests',
    'TheAskNamesTheBrainWorkingOnItTests', 'AKnownAnswerIsTheBrainsOwnTests', 'TheBrainKeptAnsweringIsQuickEnoughTests',
    # the brain picks: BrainSpeed and the installed fallback
    'BrainPlanTests', 'CrashVerdictTests', 'ModelChoiceTests', 'TheBrainFloorTests', 'TheBestInstalledModelWinsTests',
    'BrainBalanceTests', 'KernelThreadsTests', 'DeviceBalanceTests',
    # AIService and its tools
    'CalculatorToolTests', 'ToolCuesAreUsableTests', 'ToolsThroughTheServiceTests',
    'AIHttpClientArgTests', 'AIHttpClientConstructorTests', 'AIHttpClientIntegrationTests', 'AIServiceDisposeGeneratorTests',
    'AIServiceEdgeCaseTests', 'AIServiceLifecycleContractTests', 'AIServiceNeuronTests', 'AIServicePathResolutionTests',
    'AIServiceStreamContractTests', 'AIServiceTests', 'AgenticChatTests', 'AgenticMaxIterGuardTests',
    'ContextEnrichmentTests', 'DurableQuestionsTests', 'EffortRouterTests', 'GenerationOptionsDefaultsTests',
    'MalformedToolCallTests', 'ManagedEngineSeamTests', 'ManagedThinkingBudgetTests', 'ManagedToolCallTests',
    'OverviewAnswerTests', 'ParseToolCallTests', 'PictureAcrossTheLinkTests', 'PicturesToABrainThatCanSeeTests',
    'StreamAsyncEnrichmentTests', 'StreamAsyncEpisodicStorageTests', 'TranslationThroughTheBrainTests', 'WolverineOutboxTests',
    # the docs' readers: CLAIMS rows must name tests that exist, and the OPEN-GAPS readers
    'GenuineProductTests', 'DocumentedCountTests', 'DomainPacksTests', 'EveryModelArrivesByDefaultTests',
    'FirstRunFollowsTheLanguageTests', 'HousekeepingBeforePullTests', 'HousekeepingSweepTests', 'KokoroItalianTests',
    'SpeculativeDecodingTests', 'MicWatchTests', 'MnnWorkingMemoryTests', 'OneModelCatalogueTests', 'SearchByMeaningTests',
    'SolutionMembershipTests', 'TheCatalogueIsTheSelectorTests', 'TransportSelectorTests', 'WhoSaidWhatTests')
$filter = ($classes | Sort-Object -Unique | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
"$(($classes | Sort-Object -Unique).Count) test classes"
$steps = @()
if (-not $SkipNet10) { $steps += @{ Name = 'net10.0'; Args = @{ Repo = $repo; Framework = 'net10.0'; Filter = $filter } } }
if (-not $Net10Only) {
    $steps += @{ Name = 'net9.0'; Args = @{ Repo = $repo; Framework = 'net9.0'; Filter = $filter } }
    $steps += @{ Name = 'android'; Args = @{ Repo = $repo; Android = $true } }
    $steps += @{ Name = 'desktop'; Args = @{ Repo = $repo; Command = "& '$s\compile-desktop-head.ps1'" } }
}
foreach ($step in $steps) {
    $a = $step.Args
    for ($i = 0; $i -lt 10; $i++) {
        & "$s\locked-build.ps1" @a
        if ($LASTEXITCODE -ne 3) { break }
        "[$(Get-Date -Format HH:mm:ss)] $($step.Name) waits three minutes for a calm disk"
        Start-Sleep -Seconds 180
    }
}
