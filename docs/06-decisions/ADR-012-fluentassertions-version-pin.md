# ADR-012 — Pin FluentAssertions to 7.x
Status: Accepted · Date: 2026-09-02

## Context

`CLAUDE.md` names FluentAssertions as the assertion library. FluentAssertions 8.0.0 changed licence: versions from 8.0.0 onward require a paid commercial licence for use outside open-source projects. MLCP is a commercial SaaS, so an unnoticed bump to 8.x would create a licence liability with no build failure to signal it. Version 7.x remains Apache-2.0.

## Decision

Pin `FluentAssertions` to `7.0.0` in `Directory.Packages.props`, with a comment at the pin explaining why it must not be bumped. Central package management means the version exists in exactly one place, so the pin cannot be circumvented by a per-project reference.

## Consequences

+ No licence exposure; the assertion style `CLAUDE.md` asks for is preserved.
− 7.x receives no new features. This has not constrained anything so far.
− A dependency-update bot will keep proposing 8.x. Those PRs must be closed, not merged.

## If the pin becomes untenable

Migrate to `Shouldly` (BSD) or xUnit's built-in assertions. Both are a mechanical change to test code only and touch no production source. Revisit if 7.x develops a security advisory, since an unpatched assertion library in the test tree is a smaller risk than a licence breach but not a zero one.
