# ADR-021 — Regional stacks behind one front door, with a global tenant→region directory
Status: Accepted · Date: 2026-09-17 · Implements: docs/03 §5.3, docs/05 P0-10

## Context

docs/03 §5.3 deploys one stack per region (EU and US at launch), chooses the region at connection, and never migrates it silently. Nothing specified how a visitor reaches the right stack before signing in, or what happens when a user of an EU-connected tenant lands on the US stack.

## Decision

- **Hostnames.** One public hostname (`app.<domain>`) on **Azure Front Door**. It serves the static landing pages from any region and routes to regional origins, `eu.app.<domain>` and `us.app.<domain>`, which are also directly addressable. Each regional stack is a complete deployment of `infra/main.bicep` with `region` set.
- **Global tenant directory.** A geo-redundant Azure Table (`TenantRegions`, in a global storage account deployed by `infra/global.bicep`) maps `tid → region`. It holds only the tenant GUID, the region code and the registration timestamp: no names, no customer data. Each regional stack writes to it when a tenant first registers there, and reads it at sign-in. The workload identities get *Storage Table Data Contributor* on that table only.
- **Sign-in flow** (`RegionRoutingMiddleware`, after authentication):
  1. Look up `tid` (cached for 10 min).
  2. **Known, and a different region** → redirect to that region's host (same path) and sign out of this host's cookie. The cookie is host-only, so no session crosses regions.
  3. **Known, and this region** → continue.
  4. **Unknown** → continue. The tenant registers here, and the region is written at first registration using a conditional insert. If another region won the race, redirect there.
- **Choosing the region.** The landing page has a region picker, defaulting from the Front Door geo header and remembered in a cookie. The Connect page states the region explicitly ("Your organisation's data will be stored in the EU"). The admin confirms it as part of the connect action, and it is recorded in the audit row.
- **Changing the region** later is an ops-assisted export/delete/re-register; it is never automatic.
- **Local development.** The directory is an in-memory implementation; a single region is assumed.

## Consequences

+ Customer data never leaves its region. The only global data is the `tid → region` mapping.
+ A user never sees an empty dashboard because they hit the wrong region.
− One more global resource (Front Door plus a storage table) and one more sign-in lookup, which is cached.
