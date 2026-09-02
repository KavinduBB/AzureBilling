# MLCP — Microsoft License & Cost Management Platform

Public multi-tenant SaaS: any organization holding Microsoft licences signs in with Entra ID and tracks licences, usage, subscriptions, Azure costs, and — where the agreement allows — prices, invoices, and renewals.

Start with `CLAUDE.md`, then `docs/00-README.md`.

Quick start (local):
```
docker compose up -d           # SQL Server, Redis, Azurite
dotnet user-secrets init -p src/Mlcp.Web
dotnet run --project src/Mlcp.Web
```
