// .NET Aspire orchestration for Watchtower: runs the API and the Vite + React web frontend together as
// one unit. `dotnet run --project src/Watchtower.AppHost` starts both (and opens the Aspire dashboard),
// injecting the API's URL into the frontend as VITE_API_URL so its JSON-RPC client and SSE streams point
// at the right endpoint. Watchtower stores its data in SQLite, so no database container is provisioned.
//
// Run `npm install` in src/watchtower-web once beforehand.
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Watchtower_Api>("api");

builder.AddViteApp("web", "../watchtower-web")
    .WithReference(api)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();
