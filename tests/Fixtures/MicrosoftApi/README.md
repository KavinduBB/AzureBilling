# Microsoft API fixtures

WireMock.Net mappings for integration tests. One folder per provider, one file per scenario.
Naming: `{provider}/{operation}-{scenario}.json` e.g. `graph/subscribedSkus-mosa.json`, `billing/billingSubscriptions-mca.json`, `costmgmt/query-by-rg-mca.json`.
Scenarios: mosa, mca, ea, csp-managed, throttled-429, empty-200, forbidden-403.
Never commit real tenant data. Redact GUIDs to the 00000000-… pattern.
