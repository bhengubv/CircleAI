# The docs' readers on one leg (-Framework), after the E33 phone run's edits to OPEN-GAPS and CLAIMS:
# CLAIMS rows must name tests that exist, and the classes that read OPEN-GAPS. Retries on a disk stall (exit 3).
param([string]$Framework = 'net10.0')
$s = '.'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$classes = @('GenuineProductTests', 'DocumentedCountTests', 'BrainPlanTests', 'DeviceBalanceTests', 'DomainPacksTests',
    'EveryModelArrivesByDefaultTests', 'FirstRunFollowsTheLanguageTests', 'HousekeepingBeforePullTests', 'HousekeepingSweepTests',
    'KokoroItalianTests', 'SpeculativeDecodingTests', 'MicWatchTests', 'MnnWorkingMemoryTests', 'OneModelCatalogueTests',
    'SearchByMeaningTests', 'SolutionMembershipTests', 'BrainBalanceTests', 'KernelThreadsTests', 'PictureAcrossTheLinkTests',
    'TheCatalogueIsTheSelectorTests', 'ToolsThroughTheServiceTests', 'TransportSelectorTests', 'WhoSaidWhatTests')
$filter = ($classes | Sort-Object -Unique | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
for ($i = 0; $i -lt 10; $i++) {
    & "$s\locked-build.ps1" -Repo $repo -Framework $Framework -Filter $filter
    if ($LASTEXITCODE -ne 3) { break }
    "[$(Get-Date -Format HH:mm:ss)] waits three minutes for a calm disk"
    Start-Sleep -Seconds 180
}
