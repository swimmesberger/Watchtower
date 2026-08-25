using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Pipeline;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the <c>xmin</c> concurrency tokens (ADR-0024 decision 3) and the one decorator that turns a
/// lost race into a result the caller can act on.
/// </summary>
public sealed class OptimisticConcurrencyTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoWritersOnOneRoute_TheSecondSaveIsRefused() {
        using var host = AuthTestHost.Start();
        int routeId;

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            db.Stacks.Add(NewStack("demo"));
            await db.SaveChangesAsync(Ct);
            var stackId = await db.Stacks.Select(s => s.Id).SingleAsync(Ct);
            var route = new Route {
                StackId = stackId,
                Domain = "app.example.invalid",
                ServiceName = "web",
                ContainerPort = 8080,
            };
            db.Routes.Add(route);
            await db.SaveChangesAsync(Ct);
            routeId = route.Id;
        }

        // Two scopes, two contexts, one row — the shape of two administrators editing the same route
        // on two instances.
        await using var first = host.Services.CreateAsyncScope();
        await using var second = host.Services.CreateAsyncScope();
        var firstDb = first.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        var asSeenByFirst = await firstDb.Routes.SingleAsync(r => r.Id == routeId, Ct);
        var asSeenBySecond = await secondDb.Routes.SingleAsync(r => r.Id == routeId, Ct);

        asSeenByFirst.ServiceName = "api";
        await firstDb.SaveChangesAsync(Ct);

        asSeenBySecond.ServiceName = "worker";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync(Ct));

        // The winner's write stands; the loser's is not half-applied.
        await using var check = host.Services.CreateAsyncScope();
        var stored = await check.ServiceProvider.GetRequiredService<WatchtowerDbContext>()
            .Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, Ct);
        Assert.Equal("api", stored.ServiceName);
    }

    /// <summary>
    /// The decorator is what makes the token usable: a caller gets "someone else changed this", not a
    /// stack trace. Exercised through the decorator directly rather than through a handler that happens
    /// to lose a race, because provoking a real race inside one handler would be a timing test.
    /// </summary>
    [Fact]
    public async Task TheDecorator_TurnsAConcurrencyFailureIntoAConflict() {
        var decorator = new ConcurrencyConflictDecorator<string, Result<string>>(new AlwaysConcurrencyFailsHandler());

        var result = await decorator.HandleAsync("anything", Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
    }

    private static Stack NewStack(string name) => new() {
        Name = name,
        ComposeProjectName = name,
        Product = TestProducts.New(name),
    };
}

/// <summary>
/// An inner handler for <see cref="ConcurrencyConflictDecorator{TRequest,TResponse}"/> that always
/// loses the race.
/// </summary>
/// <remarks>
/// Top-level and public because the Elarion generator discovers <c>IHandler</c> implementations by
/// interface and emits a registration for each — which it cannot do for a private nested type.
/// Harmless: nothing in this assembly runs the generated bootstrapper.
/// </remarks>
public sealed class AlwaysConcurrencyFailsHandler : IHandler<string, Result<string>> {
    public ValueTask<Result<string>> HandleAsync(string request, CancellationToken ct) =>
        throw new DbUpdateConcurrencyException();
}
