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
    /// The reason the token is a real property and not an EF shadow property
    /// (npgsql/efcore.pg#3539): read in one context, carried across as a plain object, attached to
    /// another and saved. A shadow token lives in the change tracker it was read into, so the second
    /// context would compare <c>default(uint)</c> — matching no row — and throw a phantom
    /// <see cref="DbUpdateConcurrencyException"/> over a row nobody else had touched.
    /// </summary>
    /// <remarks>
    /// Two assertions, because the property surviving the detach is the mechanism and the save
    /// succeeding is the consequence, and only asserting the second would pass just as well against a
    /// row that happened to carry token 0. The third assertion is the other half of the contract, on
    /// the same detached path: a second copy detached at the same read is attached <em>after</em> the
    /// first one's save moved the row, and is refused on its carried token — so this bought
    /// detach-and-attach without buying last-writer-wins.
    /// </remarks>
    [Fact]
    public async Task AnXminEntity_ReadDetachedAndAttachedElsewhere_SavesWithoutAPhantomConflict() {
        using var host = AuthTestHost.Start();
        int stackId;
        await using (var seed = host.Services.CreateAsyncScope()) {
            var db = seed.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var stack = NewStack("detached");
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(Ct);
            stackId = stack.Id;
        }

        // Read with no tracking, in a scope that then goes away entirely — the shape
        // WatchtowerUserStore and CiToolchainRecorder are built around. Two copies from the same
        // moment: one will win the row, the other will come back stale.
        Stack carried;
        Stack staleCopy;
        await using (var reader = host.Services.CreateAsyncScope()) {
            var db = reader.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            carried = await db.Stacks.AsNoTracking().SingleAsync(s => s.Id == stackId, Ct);
            staleCopy = await db.Stacks.AsNoTracking().SingleAsync(s => s.Id == stackId, Ct);
        }

        // The token travelled on the object. This is the line that fails with a shadow property.
        Assert.NotEqual(0u, carried.Xmin);

        await using var writer = host.Services.CreateAsyncScope();
        var writerDb = writer.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        writerDb.Stacks.Attach(carried);
        carried.ComposeProjectName = "renamed";
        await writerDb.SaveChangesAsync(Ct);

        await using var check = host.Services.CreateAsyncScope();
        var stored = await check.ServiceProvider.GetRequiredService<WatchtowerDbContext>()
            .Stacks.AsNoTracking().SingleAsync(s => s.Id == stackId, Ct);
        Assert.Equal("renamed", stored.ComposeProjectName);

        // …and the carried token is still a token on the SAME detached path: the second copy from
        // that read is now genuinely behind (the successful save above moved the row), and attaching
        // it is refused on the token it carried rather than silently overwriting the rename.
        await using var stale = host.Services.CreateAsyncScope();
        var staleDb = stale.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        staleDb.Stacks.Attach(staleCopy);
        staleCopy.ComposeProjectName = "overwritten";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync(Ct));
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
