# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Roslyn analyzer + code fix + source generator that prevents primitive ID values (`Guid`, `int`, `string`, ...) being crossed between domain types at compile time. Users tag declarations with `[Id("Customer")]` / `[UnionId("Customer","Product")]` and the analyzer flags cross-domain flows. No runtime wrapper type; the primitive stays a primitive.

Diagnostic prefix `SIA` — SIA001 (mismatch, fix: change attr or rename target), SIA002 (source missing, fix), SIA003 (target missing, fix), SIA004 (ambiguous convention, error, compilation-end), SIA005 (redundant `[Id]`, fix, compilation-end), SIA006 (single-option `[UnionId]`, fix).

## Build & test

- `dotnet build src --configuration Release` — builds analyzer + code fix and produces `nugets/StrongIdAnalyzer.<version>.nupkg`.
- `dotnet build IntegrationTests --configuration Release` — consumes the just-built nupkg from `nugets/`.
- `dotnet run --project src/StrongIdAnalyzer.Tests -c Release` — NUnit unit tests against the analyzer directly (fast). Tests run on Microsoft.Testing.Platform (MTP) — the project is `OutputType=Exe` with `EnableNUnitRunner=true`, so `dotnet test` is not used on .NET 10 SDK.
- `dotnet run --project IntegrationTests/IntegrationTests -c Release` — end-to-end against the packaged analyzer.
- Single test: append `-- --filter "Name~NameOfTest"` (MTP filter expression).
- Code coverage: append `-- --coverage --coverage-output-format cobertura` (via `Microsoft.Testing.Extensions.CodeCoverage`). Output lands in `TestResults/`.

## Architecture

Three source projects, all in `src/`:

- `StrongIdAnalyzer/` — `netstandard2.0`, `IsRoslynComponent=true`. Contains both the analyzer (`IdMismatchAnalyzer`) and the incremental source generator (`IdAttributeGenerator`). The generator emits `IdAttribute` and `UnionIdAttribute` as `internal sealed` types into every consumer compilation, so consumers take no runtime dependency. The analyzer resolves the generated attribute via `CompilationStartAction` — if the consumer hasn't run the generator, analysis is skipped.
- `StrongIdAnalyzer.CodeFixes/` — code fixes for SIA002/SIA003/SIA005/SIA006. Built by a `BeforeTargets="Build"` MSBuild target in the analyzer csproj, then the produced DLL is packed into the same `analyzers/dotnet/cs` folder of the analyzer nupkg via the `PackAnalyzer` target. Do **not** ship the CodeFixes project as a separate package.
- `StrongIdAnalyzer.Tests/` — NUnit tests using `Microsoft.CodeAnalysis.Testing`. `Samples.cs` contains mdsnippet-tagged snippets that the README pulls in — edits to those snippets flow to `readme.md` via mdsnippets.

`IntegrationTests/` is a separate solution with its own `nuget.config` pointing at `../nugets/` to consume the just-built package rather than project references.

### Diagnostic release tracking

`src/StrongIdAnalyzer/AnalyzerReleases.Shipped.md` and `AnalyzerReleases.Unshipped.md` are `AdditionalFiles` — Roslyn's release-tracking analyzer will fail the build if a new diagnostic ID is added to `SupportedDiagnostics` without a corresponding entry in `Unshipped.md`.

### Tag resolution model

Two sources of tags on a primitive ID member:

1. **Explicit** `[Id("x")]` / `[UnionId("x","y")]` on the property/field/parameter (or on a base property / interface member it overrides/implements).
2. **Naming convention** — a public member named `Id` or `XxxId` infers the tag from the declaring type (`Id` on `Customer` ⇒ `"Customer"`; `CustomerId` ⇒ `"Customer"`).

For inherited `Id` members, the effective tag set is the **union** of explicit tags plus convention tags from every type in the receiver's static-type chain between the receiver and the declaring type. Matching uses **set containment**: a single-tag target is satisfied if its tag appears anywhere in the source's set. See the "Inheritance and covariant Id tagging" section of `readme.md` for the four canonical shapes.

Record primary-constructor parameters: `[Id]` on the parameter is bridged onto the synthesized property (the compiler leaves it on the parameter by default). Explicit `[property: Id(...)]` still wins.

### Source resolution

SIA001/002/003 fire when the source expression is a **property / field / parameter reference**, or a **method invocation whose target method carries `[return: Id]` / `[return: UnionId]`** (override / interface-impl return attributes are walked). `await` is transparently unwrapped — a tagged async method's result flows through `await`. Literals, locals, untagged invocations, and compound expressions (ternaries, casts, null-coalescing) are deliberately `Unknown` and suppress all three diagnostics — this is what keeps noise down around `Guid.NewGuid()` and `Guid.Empty`. Don't "fix" this by analyzing more expression shapes without understanding why it's restricted.

### SIA002/003 suppression

SIA003 is suppressed when the target is: library metadata (BCL/third-party), `object`, an unconstrained generic `T`, or in a namespace matching `strongidanalyzer.suppressed_namespaces` (`.editorconfig`, defaults `System*,Microsoft*`). Trailing `*` = prefix match. Empty value disables namespace suppression but keeps the other suppressions. SIA002 is similarly suppressed when the source lives in referenced metadata.

## Notes

- `[Id(...)]` values are compared with ordinal string equality — case matters.
- Analyzer must target `netstandard2.0`; do not bump it.
- `ProjectDefaults` package provides the `Cancel` alias for `CancellationToken`, `Date`/`Time` aliases on net6+, and build-time `.editorconfig` sync across projects.
- The repo uses `mdsnippets` — snippets in `Samples.cs` flow into `readme.md`. Run `dotnet mdsnippets` (or let it run on build where configured) after editing sample code so the README stays in sync.
