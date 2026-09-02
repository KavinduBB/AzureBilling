# ADR-001 — Single ASP.NET Core deployable (MVC + API) instead of separate MVC and Web API
Status: Accepted · Date: 2026-09-01

## Context
The original brief proposed ASP.NET MVC frontend calling a separate ASP.NET Core Web API, both authenticated with Entra ID.

## Decision
Ship one ASP.NET Core application hosting MVC/Razor views and `/api/v1` controllers, sharing one authentication pipeline. Keep strict project boundaries (`Web → Application → Domain`; `Persistence`/`Integration.*` via DI) so the API can be extracted later.

## Consequences
+ No On-Behalf-Of hop, one app registration, simpler consent surface, lower latency.
+ Extraction later is a deployment change, not a rewrite.
− A separate API tier for third parties would need OBO or client-credentials later (acceptable; not an MVP need).
