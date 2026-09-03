using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Config;
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
            // Identity forwarding defaults to JWT-only: no plaintext identity header unless opted in.
            Assert.Equal(IdentityHeaderMode.None, route.IdentityHeaderMode);

            // Enums are persisted by name, so the column stays readable in a psql session. The alias is
            // quoted because PostgreSQL folds bare identifiers to lower case, and SqlQuery<T> matches the
            // returned column against the shape's "Value" member by name.
            var stored = await db.Database
                .SqlQuery<string>($"SELECT access_mode AS \"Value\" FROM routes WHERE id = {routeId}")
                .SingleAsync(ct);
            Assert.Equal(nameof(AccessMode.Public), stored);

            var storedMode = await db.Database
                .SqlQuery<string>($"SELECT identity_header_mode AS \"Value\" FROM routes WHERE id = {routeId}")
                .SingleAsync(ct);
            Assert.Equal(nameof(IdentityHeaderMode.None), storedMode);
        }
    }

    [Fact]
    public async Task RouteWithAnIdentityHeaderMode_RoundTripsItByName() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;

        int routeId;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var stack = NewStack("authelia");
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(ct);

            var route = new Route {
                StackId = stack.Id,
                Domain = "authelia.example.invalid",
                ServiceName = "web",
                ContainerPort = 3000,
                AccessMode = AccessMode.Authenticated,
                IdentityHeaderMode = IdentityHeaderMode.Remote,
            };
            db.Routes.Add(route);
            await db.SaveChangesAsync(ct);
            routeId = route.Id;
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, ct);
            Assert.Equal(IdentityHeaderMode.Remote, route.IdentityHeaderMode);

            var stored = await db.Database
                .SqlQuery<string>($"SELECT identity_header_mode AS \"Value\" FROM routes WHERE id = {routeId}")
                .SingleAsync(ct);
            Assert.Equal(nameof(IdentityHeaderMode.Remote), stored);
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

    /// <summary>
    /// The reason <c>access_mode</c> and <c>identity_header_mode</c> declare their defaults on the model
    /// rather than only in the migration that first added them: a writer that does not mention the column
    /// — a hand-written INSERT, or historically the SQLite importer — must land a value the enum converter
    /// can read,
    /// not the empty string a scaffolded <c>defaultValue: ""</c> would have given it. This inserts such a
    /// row and then loads it through EF.
    /// </summary>
    [Fact]
    public async Task RowInsertedWithoutAccessMode_IsBackFilledAsPublic() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            db.Stacks.Add(NewStack("legacy"));
            await db.SaveChangesAsync(ct);

            var stackId = await db.Stacks.Where(s => s.Name == "legacy").Select(s => s.Id).SingleAsync(ct);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO routes (stack_id, domain, service_name, container_port, tls_enabled, is_primary, kind, status, created_at)
                VALUES ({stackId}, 'legacy.example.invalid', 'web', 8080, true, false, 'Managed', 'Pending', TIMESTAMPTZ '2026-01-01 00:00:00+00')
                """, ct);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Domain == "legacy.example.invalid", ct);
            Assert.Equal(AccessMode.Public, route.AccessMode);
            // identity_header_mode is filled by its own "None" default for the same reason — an empty
            // string would not read back as the enum.
            Assert.Equal(IdentityHeaderMode.None, route.IdentityHeaderMode);
        }
    }

    /// <summary>
    /// The entity default and the <c>proxy.createRoute</c> default differ on purpose (ADR-0035). The
    /// column stays <see cref="AccessMode.Public"/> because it is what every writer that is not the
    /// create handler means — the tenant provisioner, stack adoption, a hand-written INSERT — and
    /// because flipping it would have rewritten the meaning of every existing row. "Protected by
    /// default" is a decision about what an operator publishes, so it lives in the handler that
    /// publishes, over a setting an operator can see.
    /// </summary>
    [Fact]
    public void TheEntityDefaultIsPublic_WhileTheHandlerDefaultIsAuthenticated() {
        Assert.Equal(AccessMode.Public, new Route {
            Domain = "demo.example.invalid", ServiceName = "web", ContainerPort = 8080,
        }.AccessMode);
        Assert.Equal(AccessMode.Authenticated, new ProxyOptions().ResolveDefaultAccessMode());
    }

    /// <summary>A stack that satisfies the entity's required members; a route needs one to hang off.</summary>
    private static Stack NewStack(string name) => new() {
        Name = name,
        ComposeProjectName = name,
        Product = TestProducts.New(name),
    };
}
