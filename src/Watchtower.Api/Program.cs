using System.Text.Json;
using System.Text.Json.Serialization;
using Elarion.Abstractions.Modules;
using Elarion.AspNetCore;
using Elarion.AspNetCore.Identity;
using Elarion.JsonRpc;
using Elarion.Session;
using Elarion.Settings.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Watchtower.Api;
using Watchtower.Api.Authentication;
using Watchtower.Api.Endpoints;
using Watchtower.Api.Proxy;
using Watchtower.Application;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

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

// JSON for the plain minimal-API endpoints (webhook + App API): camelCase, omit nulls. The
// source-generated context is inserted ahead of the default resolver so these bodies serialize
// without reflection; its own [JsonSourceGenerationOptions] carry the same naming/null policy.
builder.Services.ConfigureHttpJsonOptions(o => {
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, WatchtowerHttpJsonContext.Default);
});

// CORS for development: when the SPA runs on the Vite dev server (a different origin — e.g. under the
// Aspire AppHost, which injects the API URL as VITE_API_URL) it calls /rpc, /api/* and the SSE streams
// cross-origin. In production the SPA is served same-origin from wwwroot, so no CORS is applied.
// SetIsOriginAllowed rather than AllowAnyOrigin because the login cookie needs AllowCredentials, and the
// CORS protocol forbids combining credentials with a wildcard origin (ASP.NET throws at request time).
// The predicate is restricted to loopback: this policy approves CREDENTIALED cross-origin calls, so an
// allow-anything predicate would let any page a developer happens to visit drive their local Watchtower
// with their session cookie. The dev server always runs on localhost, so loopback loses nothing.
const string DevCorsPolicy = "watchtower-dev-frontend";
builder.Services.AddCors(o => o.AddPolicy(DevCorsPolicy, p =>
    p.SetIsOriginAllowed(DevCorsOrigins.IsLoopback).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// Layer the Elarion settings store into IConfiguration BELOW the environment variables: stored
// settings live-reload into IOptionsMonitor<WatchtowerOptions> (no restart), but a WATCHTOWER__* env
// var pins its setting — env is the infrastructure-as-code layer and always wins, and the Settings UI
// disables pinned fields instead of losing to them. The boot snapshot makes the stored values visible
// to the pre-DI reads below (Auth:Enabled, Auth:KeyPath), which run before the live provider loads.
// Ordered before AddWatchtowerServices so those same reads inside it see the stored values too.
builder.AddElarionSettingsConfiguration();
var settingsDbPath = builder.Configuration.GetValue<string>("Watchtower:DbPath") ?? "/data/watchtower.db";
RuntimeSettingsLayering.MakeEnvironmentWin(
    builder.Configuration, RuntimeSettingsLayering.LoadStoredGlobalSettings(settingsDbPath));

// Application infrastructure: strongly-typed options, the SQLite EF Core context, the Docker/compose/
// git service layer, the deploy engine, and the background update checkers.
builder.Services.AddWatchtowerServices(builder.Configuration);

// The in-process proxy's request path (ADR-0017, forthcoming): YARP's direct forwarder and the singleton
// client it forwards on. Registered unconditionally — the proxy provider is switchable from Settings at
// runtime, and neither the container nor the pipeline is rebuilt for that.
builder.Services.AddWatchtowerProxyForwarding();

// The in-process reverse proxy's TLS listener (ADR-0017, forthcoming): a second Kestrel endpoint that
// picks its certificate per connection from the SNI name. Only added where one is configured — the
// shipped container sets Kestrel__Endpoints__ProxyHttps__Url; development, Aspire and the integration
// tests never do, so the host they boot is unchanged.
ProxyHttpsEndpoint.Configure(builder);

// Elarion framework composition:
//   AddElarion         — every enabled module's handlers, [Service] impls, and source-generated JSON contexts.
//   AddElarionSession  — the client-capability bootstrap (ADR-0030): module map + [ClientFeatures] flags
//                        (e.g. Metrics' "metrics-history") for the frontend's contribution gating.
//   AddElarionJsonRpc  — the JSON-RPC transport + shared handler dispatcher.
builder.Services.AddElarion(builder.Configuration);
// ICurrentUser (claims-backed when Auth:Enabled, an implicit local administrator otherwise) and the
// IAuthorizer behind [assembly: ElarionAuthorizationDefaults] are registered by AddWatchtowerServices
// above — both are mode decisions driven by Watchtower:Auth:Enabled, so they live next to the rest of the
// auth wiring instead of being split across the host.
builder.Services.AddElarionSession(builder.Configuration.GetClientCapabilityManifest());
builder.Services.AddElarionJsonRpc((dispatcher, configuration) =>
    ElarionBootstrapper.RegisterHandlers(dispatcher, configuration).MapElarionSession());

// Native login (docs/central-auth/design.md §2.5). The scheme reads the __wt_sso cookie and resolves it
// against the auth_sessions table on every request, so revocation is immediate; ASP.NET's cookie handler
// is deliberately unused because its self-contained tickets could not be revoked at all.
var authEnabled = builder.Configuration.GetValue<bool>("Watchtower:Auth:Enabled");
if (authEnabled) {
    builder.Services
        .AddAuthentication(WatchtowerSessionDefaults.AuthenticationScheme)
        .AddScheme<AuthenticationSchemeOptions, WatchtowerSessionAuthenticationHandler>(
            WatchtowerSessionDefaults.AuthenticationScheme, configureOptions: null);
    builder.Services.AddAuthorization(o => o.AddPolicy(
        WatchtowerSessionDefaults.SystemRealmPolicy,
        p => p
            // Authenticated first, so an anonymous caller still gets the 401 challenge it always did
            // rather than a 403 that tells it a session would not have helped.
            .RequireAuthenticatedUser()
            // …and then the same operator-realm rule the handler pipeline applies, read off the principal.
            .RequireAssertion(context => WatchtowerClaims.IsSystemRealm(context.User))));
    // Per-IP throttle on the login endpoint (design.md §9). Registered only in this mode because the
    // route it protects is only mapped here; the policy is attached to that one route, not global.
    builder.Services.AddWatchtowerLoginRateLimiter();
}

// Trust X-Forwarded-Proto so HttpContext.Request.IsHttps reflects the scheme the *browser* used, not the
// plain HTTP hop from the TLS-terminating proxy to Kestrel. Without this the session cookie never gets the
// Secure attribute in any shipped deployment, because Kestrel only ever sees http://.
//
// Spoofing analysis for the cleared KnownNetworks/KnownProxies (which otherwise reject forwarded headers
// from unknown sources): Caddy is a sibling container on a Docker network whose subnet is assigned
// dynamically at creation time, so there is no stable address to pin — the alternative to clearing the
// lists is the header being ignored, which is the bug. The exposure is bounded because nothing on the
// server keys a trust decision off the scheme: a direct client that spoofs X-Forwarded-Proto=https only
// changes the attributes of the cookie *it* receives, and a Secure cookie it cannot replay over http is a
// self-inflicted denial of service, not an escalation. Only the proto header is processed — X-Forwarded-For
// and -Host are deliberately left alone so nothing downstream can be fooled about the client address or
// the host name.
//
// None of this governs a request the in-process proxy handles (ADR-0017, forthcoming): that dispatcher runs
// *before* this middleware, so its scheme decisions come from the real connection rather than from a header
// a client could write, and it stamps the X-Forwarded-* trio itself on the one thing it hands back to this
// pipeline — the /.watchtower/* callback and logout served on an app's own domain.
builder.Services.Configure<ForwardedHeadersOptions>(o => {
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    // KnownIPNetworks, not the deprecated KnownNetworks (ASPDEPR005).
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// Apply migrations, enable WAL, and recover deploys interrupted by a previous crash.
await InitializeDatabaseAsync(app);

// ACME HTTP-01 challenges, for any host and over either scheme (ADR-0017, forthcoming). Ahead of the host
// dispatcher so a challenge is never forwarded to an upstream and never redirected to HTTPS — the CA calls
// it over plain HTTP by definition, and either outcome would fail the validation.
app.UseAcmeHttpChallenge();

// The in-process proxy's host dispatch: a request whose Host is in the route table is access-checked and
// forwarded to its container instead of entering the pipeline below. Deliberately before
// UseForwardedHeaders — its scheme decisions have to come from the real connection.
app.UseYarpHostDispatch();

// Record what the host actually bound, once it has bound it — the only signal the in-process proxy has
// about its own data plane, since there is no container to inspect.
ProxyListenerStateInitializer.Register(app);

// First in the pipeline for everything Watchtower serves itself: everything after it — including the
// cookie issuance in the login endpoint — must see the corrected scheme.
app.UseForwardedHeaders();

// Allow the cross-origin Vite dev server to call the API (development only).
if (app.Environment.IsDevelopment())
    app.UseCors(DevCorsPolicy);

// Serve the built React SPA from wwwroot/ (index.html is the SPA entry point). Before authentication:
// the login page is part of that bundle, so it has to be reachable without a session.
app.UseDefaultFiles();
app.UseStaticFiles();

// Identity, then the ICurrentUser snapshot, then endpoint authorization — all before anything is mapped,
// so every transport sees the same principal. UseElarionCurrentUser seeds the request scope for
// [HttpEndpoint] handlers; JSON-RPC captures HttpContext.User into its own per-call scope itself.
if (authEnabled) {
    app.UseAuthentication();
    app.UseElarionCurrentUser();
    app.UseAuthorization();
    // After routing has selected the endpoint (WebApplication inserts UseRouting at the front of the
    // pipeline), so the login route's RequireRateLimiting metadata is in effect here.
    app.UseRateLimiter();
}

// Login/logout/continue (only mapped when Auth:Enabled).
app.MapWatchtowerAuthEndpoints(authEnabled);
// Forward-auth: the verify endpoint Caddy consults for protected apps, the callback and per-app logout
// served on each app's own domain, and the public JWKS. All anonymous — verify *is* the auth check.
app.MapWatchtowerAccessEndpoints(authEnabled);
// JSON-RPC endpoint (POST /rpc).
app.MapElarionJsonRpc();
// Auto-discovered [HttpEndpoint] handlers (feature-flag gated; none today).
app.MapElarionEndpoints(app.Configuration);
// Webhook, SSE streams, and health.
app.MapWatchtowerHttpEndpoints(authEnabled);

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
    // Same sweep for backups interrupted by a crash or shutdown (the queue does not persist).
    await db.BackupEvents
        .Where(e => e.Status == "running" || e.Status == "queued")
        .ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, "failed")
            .SetProperty(e => e.FinishedAt, DateTimeOffset.UtcNow)
            .SetProperty(e => e.Output,
                e => (e.Output ?? "") + "\n[Reset: process restarted while backup was in progress]"));
}

namespace Watchtower.Api {
    /// <summary>Exposes the entry point for integration tests / WebApplicationFactory.</summary>
    public partial class Program;
}
