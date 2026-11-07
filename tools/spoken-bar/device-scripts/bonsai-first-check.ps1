# The owner's rule, 2026-11-04: no speed gate - every brain pick takes the Bonsai 2 fleet first, then rank
# (ModelChoice.Best). Every test class that exercises brain selection, the E33 classes and the docs' readers, on
# one leg or both; then both Android heads and the desktop head compiled. locked-build.ps1 exits 3 when the disk
# stalled in the last 15 minutes: wait three minutes and try again, at most ten times per step.
param([switch]$Net10Only, [switch]$SkipNet10, [switch]$NoHeads)
$s = '.'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$classes = @(
    # brain selection, everywhere it is asked
    'AKnownAnswerIsTheBrainsOwnTests', 'APictureTurnCarriesNoHistoryTests', 'Bonsai2FleetTests', 'BrainPlanTests',
    'CapabilityManifestTests', 'CatalogModelSelectorTests', 'CatalogueBootstrapTests', 'CatalogueRamClaimTests',
    'CatalogueSeedTests', 'ContextWindowBudgetManagerTests', 'CrashVerdictTests', 'DefaultDeviceContextTests',
    'DeviceAwareModelSelectorRegistryTests', 'DeviceAwareModelSelectorTests', 'DeviceCapabilityReportTests',
    'EveryModelArrivesByDefaultTests', 'EveryPickTakesTheBonsaiFleetFirstTests', 'FallbackChainTests',
    'HousekeepingBeforePullTests', 'InferenceGapTests', 'KokoroItalianTests', 'MemoryPressureSourceTests',
    'ModelCatalogueTests', 'ModelCatalogueWiringTests', 'ModelChoiceTests', 'ModelDownloadServiceTests', 'ModelFitTests',
    'ModelModalityTests', 'ModelOfferTests', 'ModelReviewTests', 'SelectionQualityTests', 'SetupOfferSurvivesLowMemoryTests',
    'SlowIsNotStuckTests', 'StepFourPlanTests', 'TheAskNamesTheBrainWorkingOnItTests', 'TheBestInstalledModelWinsTests',
    'TheBrainFloorTests', 'TheBrainThatWasWorkingTests', 'TheCatalogueIsTheSelectorTests', 'BrainBalanceTests',
    'KernelThreadsTests', 'PictureAcrossTheLinkTests', 'PicturesToABrainThatCanSeeTests', 'FirstRunFollowsTheLanguageTests',
    'OneModelCatalogueTests',
    # the docs' readers: CLAIMS rows must name tests that exist, and the OPEN-GAPS readers
    'GenuineProductTests', 'DocumentedCountTests', 'DeviceBalanceTests', 'DomainPacksTests', 'HousekeepingSweepTests',
    'SpeculativeDecodingTests', 'MicWatchTests', 'MnnWorkingMemoryTests', 'SearchByMeaningTests', 'SolutionMembershipTests',
    'ToolsThroughTheServiceTests', 'TransportSelectorTests', 'WhoSaidWhatTests')
$filter = ($classes | Sort-Object -Unique | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
"$(($classes | Sort-Object -Unique).Count) test classes"
$steps = @()
if (-not $SkipNet10) { $steps += @{ Name = 'net10.0'; Args = @{ Repo = $repo; Framework = 'net10.0'; Filter = $filter } } }
if (-not $Net10Only) { $steps += @{ Name = 'net9.0'; Args = @{ Repo = $repo; Framework = 'net9.0'; Filter = $filter } } }
if (-not $NoHeads) {
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
