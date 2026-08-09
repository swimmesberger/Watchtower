using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Guards the one change the auth migration makes to an existing table: routes must keep behaving
/// exactly as before unless someone opts them into access control.
/// </summary>
public sealed class RouteAccessModeTests {
    [Fact]
    public async Task NewRoute_DefaultsToPublic_AndIsStoredAsTheEnumName() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;

        int routeId;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var stack = NewStack("demo");
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(ct);

            var route = new Route {
                StackId = stack.Id,
                Domain = "demo.example.invalid",
                ServiceName = "web",
                ContainerPort = 8080,
            };
            db.Routes.Add(route);
            await db.SaveChangesAsync(ct);
            routeId = route.Id;
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, ct);
            Assert.Equal(AccessMode.Public, route.AccessMode);
            Assert.Null(route.BypassPaths);

            // Enums are persisted by name, so the column stays readable in the SQLite file.
            var stored = await db.Database
                .SqlQuery<string>($"SELECT access_mode AS Value FROM routes WHERE id = {routeId}")
                .SingleAsync(ct);
            Assert.Equal(nameof(AccessMode.Public), stored);
        }
    }

    [Fact]
    public async Task RestrictedRoute_RoundTripsItsModeAndBypassPaths() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;

        int routeId;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var stack = NewStack("tenant");
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(ct);

            var route = new Route {
                StackId = stack.Id,
                Domain = "tenant.example.invalid",
                ServiceName = "web",
                ContainerPort = 3000,
                AccessMode = AccessMode.Restricted,
                BypassPaths = "/webhooks/\n/healthz",
            };
            db.Routes.Add(route);
            await db.SaveChangesAsync(ct);
            routeId = route.Id;
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, ct);
            Assert.Equal(AccessMode.Restricted, route.AccessMode);
            Assert.Equal(["/webhooks/", "/healthz"], route.BypassPaths!.Split('\n'));
        }
    }

    /// <summary>A stack that satisfies the entity's required members; a route needs one to hang off.</summary>
    private static Stack NewStack(string name) => new() {
        Name = name,
        RepositoryUrl = $"https://example.invalid/{name}.git",
        ComposeFilePath = "docker-compose.yml",
        Branch = "main",
        ComposeProjectName = name,
    };
}
