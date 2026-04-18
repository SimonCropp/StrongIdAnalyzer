# StrongIdAnalyzer

Roslyn analyzer + code fix + source generator that prevents primitive ID values being crossed between domain types at compile time. Mirrors the conventions of the `StringSyntaxAttributeAnalyzer` project.

## Layout
- `src/StrongIdAnalyzer/` — analyzer + source generator (emits `IdAttribute` into every consumer compilation)
- `src/StrongIdAnalyzer.CodeFixes/` — code fix for SIA002/SIA003
- `src/StrongIdAnalyzer.Tests/` — NUnit unit tests
- `IntegrationTests/` — tests that consume the built NuGet from `nugets/`

## Diagnostic prefix
`SIA` (SIA001/SIA002/SIA003).

## Build
- `dotnet build src --configuration Release` — produces `nugets/StrongIdAnalyzer.<version>.nupkg`
- `dotnet build IntegrationTests --configuration Release` — consumes the built package
- Tests: `dotnet test src` and `dotnet test IntegrationTests`

## Notes
- `[Id(...)]` values are compared with ordinal string equality.
- The attribute is `internal sealed` and source-generated per consumer — no runtime dependency.
- `ProjectDefaults` package provides `Cancel` alias for `CancellationToken`, `Date`/`Time` aliases on net6+, and build-time `.editorconfig` sync.
