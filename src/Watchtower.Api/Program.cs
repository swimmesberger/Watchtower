using System.Text.Json;
using System.Text.Json.Serialization;
using Elarion.Abstractions.Modules;
using Elarion.AspNetCore;
using Elarion.JsonRpc;
using Elarion.Session;
using Elarion.Settings.Configuration;
using Microsoft.EntityFrameworkCore;
using Watchtower.Api;
using Watchtower.Api.Endpoints;
using Watchtower.Application;
using Watchtower.Application.Persistence;

// ── Coordinator mode ──────────────────────────────────────────────────────────
// Spawned as a sibling container to perform the actual self-update compose run.
// The web host is NOT started in this mode.
if (CoordinatorMode.IsApplicable(args))
    await CoordinatorMode.RunAndExitAsync(args);

// ── Schema export mode ──────────────────────────────────────────────────────────
// Generates rpc-schema.json (consumed by the frontend client generator) and exits without
// starting the web server or touching the database.
//   dotnet run --project src/Watchtower.Api -- --export-schema <output-path>
if (args is ["--export-schema", var schemaOutputPath]) {
    var schemaBuilder = WebApplication.CreateSlimBuilder(args);
    // Only the module registry + JSON-RPC dispatcher are needed to enumerate methods; handlers are
    // never instantiated, so their DbContext dependency is irrelevant here.
    schemaBuilder.Host.UseDefaultServiceProvider(o => {
        o.ValidateOnBuild = false;
        o.ValidateScopes = false;
    });
    schemaBuilder.Services.AddElarion(schemaBuilder.Configuration);
    // Session bootstrap (ADR-0030) joins the schema so the client generator emits session-client.ts, and the
    // capability vocabulary (modules + [ClientFeatures]) rides the exported schema's capabilities block (ADR-0032).
    schemaBuilder.Services.AddElarionSession(schemaBuilder.Configuration.GetClientCapabilityManifest());
    schemaBuilder.Services.AddElarionJsonRpc((dispatcher, configuration) =>
        ElarionBootstrapper.RegisterHandlers(dispatcher, configuration).MapElarionSession());
    using var schemaApp = schemaBuilder.Build();
    var schemaDispatcher = schemaApp.Services.GetRequiredService<JsonRpcDispatcher>();
    File.WriteAllText(
        schemaOutputPath,
        JsonRpcSchemaExporter.Generate(schemaDispatcher, jsonOptions: null, new JsonRpcSchemaExportOptions {
            // Registered by AddElarionSession above — modules + their [ClientFeatures] (ADR-0032).
            ClientCapabilities = schemaApp.Services.GetRequiredService<ClientCapabilityManifest>(),
        }));
    Console.WriteLine(
        $"JSON-RPC schema written to {schemaOutputPath} ({schemaDispatcher.MethodNames.Count} methods)");
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);

// Single-line console logging for clean Docker/Portainer output.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

// JSON for the plain minimal-API endpoints (webhook + SSE): camelCase, omit nulls.
builder.Services.ConfigureHttpJsonOptions(o => {
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// CORS for development: when the SPA runs on the Vite dev server (a different origin — e.g. under the
// Aspire AppHost, which injects the API URL as VITE_API_URL) it calls /rpc, /api/* and the SSE streams
// cross-origin. In production the SPA is served same-origin from wwwroot, so no CORS is applied.
const string DevCorsPolicy = "watchtower-dev-frontend";
builder.Services.AddCors(o => o.AddPolicy(DevCorsPolicy, p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// Application infrastructure: strongly-typed options, the SQLite EF Core context, the Docker/compose/
// git service layer, the deploy engine, and the background update checkers.
builder.Services.AddWatchtowerServices(builder.Configuration);

// Layer the Elarion settings store into IConfiguration ABOVE env/appsettings (added last → wins).
// Global-scope settings under "Watchtower:*" (e.g. the automation toggles) live-reload here, so
// IOptionsMonitor<WatchtowerOptions> re-binds when they change — no restart required.
builder.AddElarionSettingsConfiguration();

// Elarion framework composition:
//   AddElarion         — every enabled module's handlers, [Service] impls, and source-generated JSON contexts.
//   AddElarionSession  — the client-capability bootstrap (ADR-0030): module map + [ClientFeatures] flags
//                        (e.g. Metrics' "metrics-history") for the frontend's contribution gating.
//   AddElarionJsonRpc  — the JSON-RPC transport + shared handler dispatcher.
builder.Services.AddElarion(builder.Configuration);
// The session bootstrap composes ICurrentUser; Watchtower is unauthenticated (reverse-proxy auth — see
// README), so a fixed anonymous user stands in: isAuthenticated=false, no roles/grants.
builder.Services.AddSingleton<Elarion.Abstractions.Identity.ICurrentUser, AnonymousCurrentUser>();
builder.Services.AddElarionSession(builder.Configuration.GetClientCapabilityManifest());
builder.Services.AddElarionJsonRpc((dispatcher, configuration) =>
    ElarionBootstrapper.RegisterHandlers(dispatcher, configuration).MapElarionSession());

var app = builder.Build();

// Apply migrations, enable WAL, and recover deploys interrupted by a previous crash.
await InitializeDatabaseAsync(app);

// Allow the cross-origin Vite dev server to call the API (development only).
if (app.Environment.IsDevelopment())
    app.UseCors(DevCorsPolicy);

// Serve the built React SPA from wwwroot/ (index.html is the SPA entry point).
app.UseDefaultFiles();
app.UseStaticFiles();

// JSON-RPC endpoint (POST /rpc).
app.MapElarionJsonRpc();
// Auto-discovered [HttpEndpoint] handlers (feature-flag gated; none today).
app.MapElarionEndpoints(app.Configuration);
// Webhook, SSE streams, and health.
app.MapWatchtowerHttpEndpoints();

// SPA fallback: any unmatched route returns index.html so the client router handles it.
app.MapFallbackToFile("index.html");

await app.RunAsync();

static async Task InitializeDatabaseAsync(WebApplication app) {
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

    // Idempotent — safe to run every startup.
    await db.Database.MigrateAsync();
    // WAL improves concurrent read/write; the setting persists in the database file.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    // Reset any deploys stuck in 'running'/'queued' from a previous crash.
    await db.DeployEvents
        .Where(e => e.Status == "running" || e.Status == "queued")
        .ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, "failed")
            .SetProperty(e => e.FinishedAt, DateTimeOffset.UtcNow)
            .SetProperty(e => e.Output,
                e => (e.Output ?? "") + "\n[Reset: process restarted while deploy was in progress]"));
}

namespace Watchtower.Api {
    /// <summary>Exposes the entry point for integration tests / WebApplicationFactory.</summary>
    public partial class Program;
}
