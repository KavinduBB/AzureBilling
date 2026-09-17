# ADR-014 — Target net10.0 (LTS); supersedes ADR-011
Status: Accepted · Date: 2026-09-17 · Supersedes: ADR-011

## Context

ADR-011 targeted net8.0 because only the .NET 8 SDK was available, while `CLAUDE.md` named .NET 9. Per the [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) (checked 2026-09-17):

| Version | Type | End of support |
|---|---|---|
| .NET 8 | LTS | 10 Nov 2026 |
| .NET 9 | STS | 10 Nov 2026 |
| .NET 10 | LTS | 14 Nov 2028 |

Moving to .NET 9 would buy eight weeks. The .NET 10 SDK (10.0.400) is installed on the build machine.

## Decision

- Target **net10.0** / C# 14, set once in `Directory.Build.props`. Project files no longer repeat `<TargetFramework>`.
- `global.json` pins SDK `10.0.100` with `rollForward: latestFeature`, so the analyzer set is predictable. CI uses `global-json-file`.
- EF Core, ASP.NET Core and `Microsoft.Extensions.*` move to 10.0.x. The rest of the package set is bumped to current stable, which also clears every NuGet audit advisory (Kiota via Microsoft.Graph, System.Security.Cryptography.Xml, Microsoft.Bcl.Memory, and the test-only WireMock/Testcontainers transitive chain).
- Container images move to `mcr.microsoft.com/dotnet/{sdk,aspnet,runtime}:10.0-noble`.
- `CLAUDE.md` is updated to name .NET 10 / EF Core 10.

## Consequences

+ Supported until November 2028; no forced upgrade in eight weeks.
+ Restore is clean under `TreatWarningsAsErrors`.
− The .NET 10 analyzers add CA1873 (expensive logging arguments). It is set to `suggestion` in `.editorconfig`. **Backlog:** move logging to `[LoggerMessage]` source generation, then raise CA1873 back to `warning`.
− The machine-wide `dotnet` on this workstation is SDK 8. Use the user-local SDK (`%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`) or install SDK 10 machine-wide.
