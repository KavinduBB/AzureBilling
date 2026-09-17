# ADR-025 — The demo dashboard ships in Phase 1; ADR-008 is narrowed to publisher verification and consent requests
Status: Accepted · Date: 2026-09-17 · Amends: ADR-008, docs/03 §3

## Context

ADR-008 and docs/03 §3 place the pre-consent demo dashboard in Phase 0. `docs/05` places it in Phase 1 (P1-11), and `CLAUDE.md` states that nothing user-facing ships until Phase 0's criteria are green.

## Decision

- The demo dashboard belongs to **Phase 1 (P1-11)**. It demonstrates the Phase 1 dashboard, which does not exist until Phase 1, and building it earlier would mean building the dashboard twice.
- **Phase 0 keeps from ADR-008:**
  - publisher verification submitted;
  - non-admin sign-in;
  - the NotConnected page with the "Ask my admin" consent request flow (P0-6);
  - tier 1 only at connection (tier 2 is a separate registration, ADR-015).
- Until P1-11, the NotConnected page shows a static, clearly labelled *preview* (screenshots with sample data) and no interactive dashboard.

## Consequences

+ The plan, the ADRs and `CLAUDE.md` agree.
− Phase 0 conversion for non-admin visitors relies on the preview images, which is acceptable because Phase 0 is not publicly launched.
