# ADR-0002: SQLite via EF Core; drop NativeAOT

- Status: Accepted
- Date: 2026-07-04
- Related: [ADR-0001](0001-rebuild-on-elarion.md) (the rebuild that forces this),
  [ADR-0004](0004-singleton-ef-scopes.md) (how singletons reach the scoped context).

## Context

The pre-Elarion Watchtower stored its data in SQLite through hand-written ADO.NET (`Microsoft.Data.Sqlite`
with raw SQL and manual `CREATE TABLE IF NOT EXISTS` migrations) and published as a **NativeAOT** binary —
a tiny, self-contained executable with fast cold start. That combination was viable precisely *because* it
avoided EF Core: AOT and EF Core don't mix well (EF's LINQ translation uses expression trees; Npgsql/EF
NativeAOT support is still incomplete — the same reason Swerp defers AOT).

The rebuild on Elarion ([ADR-0001](0001-rebuild-on-elarion.md)) makes the framework's EF Core generators
(`[GenerateDbSets]`, `[EntityConfiguration]`) the idiomatic persistence path — the same one Swerp uses. So
the choice is: keep raw ADO to preserve AOT, or adopt EF Core and lose it. And if EF Core, which provider?

## Decision

**Use EF Core with the SQLite provider, and drop NativeAOT.**

- The concrete `WatchtowerDbContext` carries `[GenerateDbSets]`; each `IEntityTypeConfiguration<T>` carries
  `[EntityConfiguration]`; the Elarion EF generator emits the `DbSet`s and applies the configurations.
- Persistence stays **SQLite** — a single file under `/data`, no external service — via
  `Microsoft.EntityFrameworkCore.Sqlite`, with `UseSnakeCaseNamingConvention()`.
- Entities keep **integer identity keys** (not Guids) so the JSON-RPC contract the frontend consumes is
  unchanged from the REST version.
- Schema is created by an EF migration applied on startup (`MigrateAsync`); WAL is enabled and any deploys
  left `running`/`queued` by a crash are reset to `failed`.
- Publishing is framework-dependent on the .NET runtime image (no `PublishAot`).

| Option | External deps | AOT | Elarion EF generators |
| --- | --- | --- | --- |
| Raw SQLite ADO (status quo) | none | yes | no |
| **EF Core + SQLite (chosen)** | none | no | yes |
| EF Core + PostgreSQL | a database service | no | yes |

## Consequences

- **Zero external dependencies preserved.** SQLite keeps Watchtower a single self-contained container —
  the right fit for a self-hosted tool — while still getting EF Core's model, migrations, and LINQ.
- **NativeAOT is gone.** The image now ships the .NET runtime instead of a native binary: larger image and
  a slightly slower cold start. Acceptable for a long-lived control-plane service (not a short-lived CLI).
- **Consistency with Swerp.** The `[GenerateDbSets]`/`[EntityConfiguration]` pattern and `dotnet ef`
  migrations match Swerp, minus the Postgres-specific extras (UnitOfWork, outbox, PG cache) that a
  single-node SQLite app does not need.
- **SQLite/EF constraints must be worked around.** Notably, the EF SQLite provider cannot translate
  `ORDER BY` on `DateTimeOffset`; the two queries that order by a timestamp (`deployments.active`,
  `stacks.events`) materialize first and sort client-side (the result sets are tiny).

### Rejected alternatives

- **Keep raw SQLite ADO to preserve NativeAOT.** Retains the tiny image and fast start, but diverges from
  Elarion's EF pattern, forgoes the generators/migrations, and keeps a hand-rolled data layer to maintain.
- **PostgreSQL via EF Core (mirror Swerp exactly).** Cleanest framework alignment, but adds a database
  service to what is deliberately a single-container, zero-dependency deployment.
