# Elarion

Watchtower is built on **[Elarion](https://github.com/swimmesberger/Elarion)** — an opinionated .NET
application framework for module-based handler pipelines, compile-time registration, and JSON-RPC
hosting. Elarion is consumed as **published NuGet packages** (and one npm package for the frontend),
pinned once via `ElarionVersion` in [`Directory.Packages.props`](../Directory.Packages.props).

| Package | Referenced by | Why |
| --- | --- | --- |
| `Elarion` | `Watchtower.Application`, `Watchtower.Api` | Core handler/module/pipeline primitives; bundles the module + handler source generator. |
| `Elarion.EntityFrameworkCore` | `Watchtower.Application` | `[GenerateDbSets]` / `[EntityConfiguration]` and the DbContext generator (provider-neutral; used here with the Npgsql provider). |
| `Elarion.AspNetCore` | `Watchtower.Api` | ASP.NET host glue: `MapElarionJsonRpc`, `MapElarionEndpoints`, and the `[assembly: GenerateModuleBootstrapper]` trigger. |
| `Elarion.JsonRpc` | `Watchtower.Api` | JSON-RPC transport, `JsonRpcDispatcher`, `JsonRpcSchemaExporter`. |
| `Elarion.AspNetCore.Mcp` | `Watchtower.Api` | Projects the same handlers as MCP tools. |
| `Elarion.Settings` / `.EntityFrameworkCore` / `.Configuration` | `Watchtower.Application`, `Watchtower.Api` | The versioned settings store (`ISettingsStore`, optimistic `expectedVersion` writes), its EF-backed table, and the `IConfiguration` provider over it. |
| `@swimmesberger/elarion-jsonrpc-client-generator` | `src/watchtower-web` (dev dependency) | Generates the TypeScript RPC types + Zod schemas from `rpc-schema.json`. |
| `@swimmesberger/elarion-contributions` | `src/watchtower-web` | The frontend module/contribution model the SPA's `src/modules/` folders plug into. |

## Integration points

- **Handlers** are plain `sealed` classes annotated with `[Handler("module.operation")]` implementing
  `IHandler<TRequest, Result<TResponse>>`. The generator registers them and exposes them over JSON-RPC.
  Failures are returned as `AppError.NotFound(…)` / `AppError.Validation(…)` and mapped to JSON-RPC
  error codes (e.g. NotFound → `-32001`).
- **Modules** are `[AppModule("Name")]` static partial classes with a `GetJsonTypeInfoResolver()` that
  returns the module's source-generated `JsonSerializerContext`. Their handlers are auto-registered by
  the generated module defaults; each module is feature-gated by `Modules:{Name}:Enabled` (default on).
- **The host** ([`Program.cs`](../src/Watchtower.Api/Program.cs)) opts into generation via
  `[assembly: GenerateModuleBootstrapper]` ([`ElarionAssembly.cs`](../src/Watchtower.Api/ElarionAssembly.cs)),
  then calls `AddElarion` / `AddElarionJsonRpc(ElarionBootstrapper.RegisterHandlers)` and maps the
  `/rpc` endpoint.
- **Persistence** uses `[GenerateDbSets]` on the concrete `WatchtowerDbContext` and `[EntityConfiguration]`
  on each `IEntityTypeConfiguration<T>`. Because the singleton deploy engine and background services can't
  hold a scoped `DbContext`, they open short-lived scopes via `IServiceScopeFactory`.
- **Schema export** — `dotnet run --project src/Watchtower.Api -- --export-schema "$PWD/rpc-schema.json"`
  regenerates the JSON-RPC schema the frontend client generator consumes. Pass an **absolute** repo-root
  path: `dotnet run --project` runs the app with its CWD set to the project directory, so a bare relative
  path writes to `src/Watchtower.Api/` instead of the repo-root file the toolchain reads.

## Upgrading

Keep the .NET and npm sides on the **same** Elarion version — the RPC schema is the contract between
them, and a generator from a different version can silently emit a client for a different schema shape.

1. Bump `ElarionVersion` in [`Directory.Packages.props`](../Directory.Packages.props), and both
   `@swimmesberger/*` versions in [`src/watchtower-web/package.json`](../src/watchtower-web/package.json)
   (`elarion-jsonrpc-client-generator` and `elarion-contributions`) to match.
2. `dotnet build Watchtower.slnx -c Release` and fix every `EL*` generator diagnostic at its cause —
   they are the generators enforcing framework conventions, never something to suppress. Look each id up
   in Elarion's `reference/diagnostics` page.
3. `dotnet ef migrations has-pending-model-changes --project src/Watchtower.Application --startup-project src/Watchtower.Api`
   — a framework table whose shape changed (the Elarion `settings` table, for one) shows up here and
   needs a migration.
4. `dotnet test Watchtower.slnx -c Release`, then re-export the schema (see above) and run
   `npm ci && npm run generate:rpc && npm run typecheck && npm run build` in `src/watchtower-web`.

The current pin is `0.2.6`.
