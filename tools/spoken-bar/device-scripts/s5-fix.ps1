# The working-directory collection, and the classes the second merge touched, on both frameworks.
$ErrorActionPreference = 'Continue'
$scratch = "."
$s5 = Join-Path $scratch 's5-sync-and-test.ps1'
$f = 'FullyQualifiedName~NativeCacheDirectoryTests|FullyQualifiedName~PrefixCachePathTests|FullyQualifiedName~Bonsai2FleetTests|FullyQualifiedName~ManagedToolCallTests|FullyQualifiedName~ToolCallXmlTests|FullyQualifiedName~ToolsByTemplateTests|FullyQualifiedName~EffortRouterTests|FullyQualifiedName~ManagedChatGeneratorTests|FullyQualifiedName~SpeculativeDecodingTests|FullyQualifiedName~BrainPlanTests|FullyQualifiedName~BrainTuningTests'
& $s5 -NoSync -Framework net10.0 -Filter $f -Tag 's5-fix2'
& $s5 -NoSync -Framework net9.0 -Filter $f -Tag 's5-fix2'
