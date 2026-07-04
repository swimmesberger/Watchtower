# Elarion

Watchtower is built on **[Elarion](https://github.com/swimmesberger/Elarion)** — an opinionated .NET
application framework for module-based handler pipelines, compile-time registration, and JSON-RPC
hosting. Elarion is consumed as **published NuGet packages** (and one npm package for the frontend),
pinned once via `ElarionVersion` in [`Directory.Packages.props`](../Directory.Packages.props).

| Package | Referenced by | Why |
| --- | --- | --- |
| `Elarion` | `Watchtower.Application`, `Watchtower.Api` | Core handler/module/pipeline primitives; bundles the module + handler source generator. |
| `Elarion.EntityFrameworkCore` | `Watchtower.Application` | `[GenerateDbSets]` / `[EntityConfiguration]` and the DbContext generator (provider-neutral; used here with the SQLite provider). |
| `Elarion.AspNetCore` | `Watchtower.Api` | ASP.NET host glue: `MapElarionJsonRpc`, `MapElarionEndpoints`, and the `[assembly: GenerateModuleBootstrapper]` trigger. |
| `Elarion.JsonRpc` | `Watchtower.Api` | JSON-RPC transport, `JsonRpcDispatcher`, `JsonRpcSchemaExporter`. |
| `@swimmesberger/elarion-jsonrpc-client-generator` | `src/watchtower-web` (dev dependency) | Generates the TypeScript RPC types + Zod schemas from `rpc-schema.json`. |

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
- **Schema export** — `dotnet run --project src/Watchtower.Api -- --export-schema rpc-schema.json`
  regenerates the JSON-RPC schema the frontend client generator consumes.

To upgrade the framework, bump `ElarionVersion` (and the npm generator version in
[`src/watchtower-web/package.json`](../src/watchtower-web/package.json)), then rebuild and re-export
the schema.
