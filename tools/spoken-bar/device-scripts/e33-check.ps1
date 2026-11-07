# OPEN-GAPS E33: the new tests and every test class that reaches a changed type (the coordinator, the
# in-process worker, AIService, the crash verdict, the risky-step marker, the worker wire, and the source
# readers of the supervisor, the worker and the hosts), on both legs; then both Android heads and the
# desktop head compiled. locked-build.ps1 exits 3 when the disk stalled in the last 15 minutes: wait three
# minutes and try again, at most ten times per step.
param([switch]$Net10Only, [switch]$SkipNet10)
$s = '.'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$classes = @('SlowIsNotStuckTests', 'TheBrainThatWasWorkingTests', 'APictureTurnCarriesNoHistoryTests',
    'AIHttpClientArgTests', 'AIHttpClientConstructorTests', 'AIHttpClientIntegrationTests', 'AIServiceDisposeGeneratorTests',
    'AIServiceEdgeCaseTests', 'AIServiceLifecycleContractTests', 'AIServiceNeuronTests', 'AIServicePathResolutionTests',
    'AIServiceStreamContractTests', 'AIServiceTests', 'AgenticChatTests', 'AgenticMaxIterGuardTests', 'BrainBalanceTests',
    'CatalogueHousekeepingTests', 'ContextEnrichmentTests', 'CrashVerdictTests', 'DeviceBalanceTests', 'DurableQuestionsTests',
    'EffortRouterTests', 'EpisodicStoreConcurrencyTests', 'GenerationOptionsDefaultsTests', 'HealingVerbTests',
    'HealthEventSiteTests', 'HousekeepingBeforePullTests', 'HttpLoopbackEndpointConfigTests', 'HttpLoopbackEndpointTests',
    'InProcessEndpointTests', 'JsonPersonaStoreConcurrencyTests', 'KernelThreadsTests', 'MalformedToolCallTests',
    'ManagedEngineSeamTests', 'ManagedThinkingBudgetTests', 'ManagedToolCallTests', 'ModelOfferTests', 'NeuronNodeTests',
    'OverviewAnswerTests', 'P1AIOptionsDefaultsTests', 'P1AgenticMaxIterationsFromTierTests', 'P1AutoResolutionTests',
    'P1ConcurrencyFromTierTests', 'P1ContextWindowFromTierTests', 'ParseToolCallTests', 'PersonaStateTopicTests',
    'PictureAcrossTheLinkTests', 'PicturesToABrainThatCanSeeTests', 'RagContextBuilderEmbedderResilienceTests',
    'RiskyStepsTests', 'StreamAsyncEnrichmentTests', 'StreamAsyncEpisodicStorageTests', 'SubmitFeedbackTests',
    'TheBrainFloorTests', 'ToolsThroughTheServiceTests', 'TranslationThroughTheBrainTests', 'WolverineOutboxTests',
    # the docs' readers: CLAIMS rows must name tests that exist, and the OPEN-GAPS readers
    'GenuineProductTests', 'DocumentedCountTests', 'BrainPlanTests', 'DomainPacksTests', 'EveryModelArrivesByDefaultTests',
    'FirstRunFollowsTheLanguageTests', 'HousekeepingSweepTests', 'KokoroItalianTests', 'SpeculativeDecodingTests',
    'MicWatchTests', 'MnnWorkingMemoryTests', 'OneModelCatalogueTests', 'SearchByMeaningTests', 'SolutionMembershipTests',
    'TheCatalogueIsTheSelectorTests', 'TransportSelectorTests', 'WhoSaidWhatTests')
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
