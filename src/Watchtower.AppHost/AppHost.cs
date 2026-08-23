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
var database = builder.AddPostgres("postgres")
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
