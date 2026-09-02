# ADR-004 — Dashboards read SQL; Microsoft APIs are called only by sync jobs and explicit user actions
Status: Accepted · Date: 2026-09-01

## Context
Azure cost data refreshes ~4h; CSP usage ~24h; Cost Management Query is limited to ~4 calls/min/scope and ~20/min/tenant.

## Decision
All dashboard data is served from synced tables. Sync runs through a staging → validate → MERGE pipeline. Every figure carries "as of". Interactive Microsoft calls are limited to onboarding probes, invoice PDF download, and gated lifecycle operations.
