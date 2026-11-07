# After the last edits: the tests that read the docs or the service's sources, and every test class
# that reaches the downloader or the model store (the stale-sidecar fix), on both legs; then both
# Android heads compiled. locked-build.ps1 exits 3 when the disk stalled in the last 15 minutes;
# wait three minutes and try again, at most ten times per step.
$s = '.'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$readers = 'AssistantPersonaTests BrainBalanceTests BrainPlanTests CrashVerdictTests DeviceBalanceTests DeviceCapabilityReportTests DeviceModelAssessorTests DocumentedCountTests DomainPacksTests EveryModelArrivesByDefaultTests FirstRunFollowsTheLanguageTests GenuineProductTests HousekeepingBeforePullTests HousekeepingSweepTests KernelThreadsTests KokoroItalianTests MemoryRelevanceMeasure MicWatchTests MnnWorkingMemoryTests ModelReviewTests OneModelCatalogueTests PictureAcrossTheLinkTests PicturesToABrainThatCanSeeTests SearchByMeaningTests ServiceMicrophoneTests SkillMatchDiagnosis SkillRelevanceMeasure SolutionMembershipTests SpeculativeDecodingTests StubGuardTests TheBrainFloorTests TheCatalogueIsTheSelectorTests ToolsThroughTheServiceTests TransportSelectorTests WakeWordStartsWhenItsBundleLandsTests WhoSaidWhatTests'
$store = 'AIServiceDisposeGeneratorTests AIServiceEdgeCaseTests AIServiceLifecycleContractTests AIServicePathResolutionTests AIServiceStreamContractTests AIServiceTests Bonsai2FleetTests CarriedSidecarsTests ContextWindowBudgetManagerTests DownloadSidecarTests EmbeddedVoiceConfigsTests FirstRunFetchStartsByItselfTests GitHubReleaseLayoutTests InferenceGapTests LocalModelLoaderTests LocalModelManagerTests ModelCacheCompletenessTests ModelCatalogueTests ModelChoiceTests ModelDownloadServiceBundleTests ModelDownloadServiceTests ModelDownloadServiceVerifyTests ModelDownloaderTests ModelOfferTests ModelUpgradeTests P1AIOptionsDefaultsTests P1AgenticMaxIterationsFromTierTests P1AutoResolutionTests P1ConcurrencyFromTierTests P1ContextWindowFromTierTests ResumeRangeSemanticsTests SegmentedDownloadRetryTests SetupBoardTests SetupStatusTests StepFourPlanTests TheBestInstalledModelWinsTests'
$classes = @(($readers + ' ' + $store) -split ' ' | Where-Object { $_ } | Sort-Object -Unique)
$filter = ($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
"$($classes.Count) test classes"
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
