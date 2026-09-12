; Diagnostics this analyser reports that have not appeared in a released version yet.
; Format documented at https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------------------------
HOLLOW001 | Hollow.Codegen | Error | A type marked [HollowGeneratedApi] roots a data model that cannot be mapped onto Hollow schemas.
