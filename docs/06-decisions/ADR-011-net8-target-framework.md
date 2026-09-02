# ADR-011 — Target net8.0 instead of the net9.0 named in CLAUDE.md
Status: Accepted · Date: 2026-09-02

## Context

`CLAUDE.md` fixes the stack at ".NET 9, ASP.NET Core" and "C# 13", marked *do not substitute*. The build machine has SDKs 7.0.410 and 8.0.302 only; no .NET 9 SDK, and installing one was declined.

Silently building against net8.0 while the standing instruction says net9.0 is the worst option: a future session reading `CLAUDE.md` would treat the target framework as a defect and "fix" it, and the discrepancy would resurface on every onboarding.

## Decision

Target **net8.0** across the solution, set once in `Directory.Build.props`. `LangVersion` is `latest`, which resolves to C# 12 on this SDK. EF Core, ASP.NET Core and `Microsoft.Extensions.*` are pinned to 8.0.x in `Directory.Packages.props`.

`CLAUDE.md` is left as written — it is the customer's document, not ours to edit — and this ADR is the authority on the actual target.

## Consequences

+ The solution builds and tests on the available toolchain today.
+ Every net8.0 dependency used so far (EF Core 9's features are not required by Phase 0) has an 8.0.x line under support until November 2026.
− No C# 13 features: no `params` collections, no `field` keyword, no partial properties. Nothing in Phase 0 needs them.
− .NET 8 leaves support in November 2026, so the upgrade is a scheduled task rather than an open choice.

## How to reverse

Install the .NET 9 SDK, change `<TargetFramework>` in `Directory.Build.props` to `net9.0`, and bump the `8.0.x` package versions in `Directory.Packages.props` to their `9.0.x` equivalents. No source change is expected; if any is needed, that is a defect in this ADR's assumption and should be recorded here.
