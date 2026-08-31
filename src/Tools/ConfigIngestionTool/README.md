# ConfigIngestionTool

Phase 6's data-ingestion tool. Reads
[`../../IdentityServerHost/Configurations/IdentityServerConfig.json`](../../IdentityServerHost/Configurations/IdentityServerConfig.json)
and writes its Clients/IdentityResources/ApiScopes/ApiResources into the same SQL Server
database `IdentityServerHost` reads from (`ConfigurationDbContext`, Phase 5) — a
key already in the database gets replaced outright with the JSON's version; a new key
gets inserted. See [`IdentityServerHost/README.md`](../../IdentityServerHost/README.md)'s
Phase 6 section for the full write-up, including what this simplifies relative to the
real IdG's own (since-deleted) `IdentityGatewayConfigurationExporter`.

Standalone on purpose — running it is a separate, explicit step from `dotnet run`ning
IdentityServerHost itself, the same way a real deployment ingests config independently
of starting the app.

## Running it

```bash
cd src/Tools/ConfigIngestionTool
dotnet run
```

Needs `IdentityServerHost`'s database to already have its schema migrated (`dotnet run`
in `IdentityServerHost` at least once — it migrates on every startup, seeds nothing).
Safe to re-run any time the JSON file changes, or just to confirm the database still
matches it.

[`../../../test-phase6.ps1`](../../../test-phase6.ps1) drives this end-to-end: corrupts a
client directly in the database, re-runs this tool, and confirms both the row and a real
login are back to matching the JSON.
