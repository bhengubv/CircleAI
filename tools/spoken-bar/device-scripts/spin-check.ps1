# OPEN-GAPS E34: the engine's workers held awake through a turn. Every test class of the C# engine and every class
# that builds it or its kernels, plus the docs' readers, on both legs; then both Android heads compiled.
# locked-build.ps1 exits 3 when the disk stalled in the last 15 minutes: wait three minutes and try again.
param([switch]$NoHeads)
$s = '.'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$classes = @(
    'ByteLevelBpeTests', 'GgufFileTests', 'JinjaTemplateTests', 'ManagedChatGeneratorTests', 'ManagedEngineSeamTests',
    'ManagedToolCallTests', 'ManagedVisionTests', 'Pq2KernelTests', 'Pq2ModelTests', 'PrismHadamardTests', 'Ptq1KernelTests',
    'Q8WeightsTests', 'Qwen35ModelTests', 'SpeculativeDecodingTests', 'TokenSamplerTests', 'ToolCallGrammarTests',
    'TurboKvCacheTests', 'VisionImageTests', 'VisionTowerDiscoveryTests', 'WeightMatrixTests', 'WorkerPoolTests',
    'Bonsai2FleetTests', 'BrainBalanceTests', 'BrainPlanTests', 'BrainTuningTests', 'FirstRunFetchStartsByItselfTests',
    'HealingVerbTests', 'HealthEventSiteTests', 'KernelThreadsTests', 'ManagedThinkingBudgetTests', 'PictureAcrossTheLinkTests',
    'PicturesToABrainThatCanSeeTests', 'RiskyStepsTests',
    # the docs' readers
    'GenuineProductTests', 'DocumentedCountTests', 'DeviceBalanceTests', 'DomainPacksTests', 'EveryModelArrivesByDefaultTests',
    'FirstRunFollowsTheLanguageTests', 'HousekeepingBeforePullTests', 'HousekeepingSweepTests', 'KokoroItalianTests',
    'MicWatchTests', 'MnnWorkingMemoryTests', 'OneModelCatalogueTests', 'SearchByMeaningTests', 'SolutionMembershipTests',
    'TheCatalogueIsTheSelectorTests', 'ToolsThroughTheServiceTests', 'TransportSelectorTests', 'WhoSaidWhatTests')
$filter = ($classes | Sort-Object -Unique | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
"$(($classes | Sort-Object -Unique).Count) test classes"
$steps = @(
    @{ Name = 'net10.0'; Args = @{ Repo = $repo; Framework = 'net10.0'; Filter = $filter } },
    @{ Name = 'net9.0'; Args = @{ Repo = $repo; Framework = 'net9.0'; Filter = $filter } })
if (-not $NoHeads) { $steps += @{ Name = 'android'; Args = @{ Repo = $repo; Android = $true } } }
foreach ($step in $steps) {
    $a = $step.Args
    for ($i = 0; $i -lt 10; $i++) {
        & "$s\locked-build.ps1" @a
        if ($LASTEXITCODE -ne 3) { break }
        "[$(Get-Date -Format HH:mm:ss)] $($step.Name) waits three minutes for a calm disk"
        Start-Sleep -Seconds 180
    }
}
