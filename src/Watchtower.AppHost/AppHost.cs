// .NET Aspire orchestration for Watchtower: runs PostgreSQL, the API and the Vite + React web frontend
// together as one unit. `dotnet run --project src/Watchtower.AppHost` starts all three (and opens the
// Aspire dashboard), injecting the API's URL into the frontend as VITE_API_URL so its JSON-RPC client
// and SSE streams point at the right endpoint.
//
// Run `npm install` in src/watchtower-web once beforehand.
var builder = DistributedApplication.CreateBuilder(args);

// Watchtower's database (ADR-0024). A data volume so a restart of the AppHost is a restart and not a
// fresh install — the API migrates on startup, but the estate you set up yesterday should still be
// there. WithReference publishes it as ConnectionStrings:watchtower, which is the fallback key
// WatchtowerConnectionString reads, so the API needs no Aspire-specific configuration.
//
// The password is PINNED (appsettings, dev-only orchestrator, never shipped) rather than left to
// Aspire's per-project generated secret. POSTGRES_PASSWORD only applies when the volume is first
// initialized, so a generated password plus a persistent volume drift apart the moment the secret
// store and the volume disagree about history (a cleared user-secrets store, a second worktree) —
// after which every start loops on "password authentication failed" until someone deletes the
// volume. A fixed dev password makes the volume reusable from any checkout. If a volume from the
// generated-password era refuses this one, reset it once:
//   docker volume rm $(docker volume ls -q | grep -i postgres)   (dev data only)
var pgPassword = builder.AddParameter("postgres-password", "watchtower-dev", secret: true);
var database = builder.AddPostgres("postgres", password: pgPassword)
    .WithDataVolume()
    .AddDatabase("watchtower");

var api = builder.AddProject<Projects.Watchtower_Api>("api")
    .WithReference(database)
    .WaitFor(database);

builder.AddViteApp("web", "../watchtower-web")
    .WithReference(api)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();
