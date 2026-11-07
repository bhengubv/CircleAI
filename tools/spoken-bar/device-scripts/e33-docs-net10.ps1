# The docs' readers again on net10.0, after OPEN-GAPS and CLAIMS were edited during the first net10.0 run:
# CLAIMS rows must name tests that exist, and the classes that read OPEN-GAPS. Retries on a disk stall (exit 3).
$s = '.'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$classes = @('GenuineProductTests', 'DocumentedCountTests', 'BrainPlanTests', 'DeviceBalanceTests', 'DomainPacksTests',
    'EveryModelArrivesByDefaultTests', 'FirstRunFollowsTheLanguageTests', 'HousekeepingBeforePullTests', 'HousekeepingSweepTests',
    'KokoroItalianTests', 'SpeculativeDecodingTests', 'MicWatchTests', 'MnnWorkingMemoryTests', 'OneModelCatalogueTests',
    'SearchByMeaningTests', 'SolutionMembershipTests', 'BrainBalanceTests', 'KernelThreadsTests', 'PictureAcrossTheLinkTests',
    'TheCatalogueIsTheSelectorTests', 'ToolsThroughTheServiceTests', 'TransportSelectorTests', 'WhoSaidWhatTests')
$filter = ($classes | Sort-Object -Unique | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
for ($i = 0; $i -lt 10; $i++) {
    & "$s\locked-build.ps1" -Repo $repo -Framework 'net10.0' -Filter $filter
    if ($LASTEXITCODE -ne 3) { break }
    "[$(Get-Date -Format HH:mm:ss)] waits three minutes for a calm disk"
    Start-Sleep -Seconds 180
}
