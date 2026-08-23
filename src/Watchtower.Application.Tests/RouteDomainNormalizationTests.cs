using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The contract the host lookups depend on: <c>routes.domain</c> is stored normalized, so the reads
/// compare a normalized parameter against the raw column and ride the unique index rather than forcing
/// <c>lower(domain)</c> on every proxied request (ADR-0024).
/// </summary>
/// <remarks>
/// Case-insensitivity is a property of the <em>write</em> path here, not of the query. That is worth a
/// test of its own because it is invisible at the call site: a lookup that compares raw columns looks
/// case-sensitive, and only the handler's normalization makes it correct. If a write path ever stored a
/// domain as the operator typed it, the proxy would start missing routes — so this asserts the write.
/// </remarks>
public sealed class RouteDomainNormalizationTests {
    private static readonly Action<IServiceCollection> WithRouteHandlers = services => {
        services.AddCreateRoute();
        services.AddUpdateRoute();
        services.RemoveAll<IProxyProvider>();
        services.AddSingleton<IProxyProvider, RecordingProxyProvider>();
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMixedCaseDomain_IsStoredLowercased_AndFoundByTheHostLookups() {
        using var host = AuthTestHost.Start(WithRouteHandlers);

        await using var scope = host.Services.CreateAsyncScope();
        var created = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider,
            new CreateRoute.Command(
                StackId: 0, Domain: "  Login.EXAMPLE.Invalid.  ", ServiceName: "", ContainerPort: 0,
                TlsEnabled: true, IsPrimary: false, Kind: null, Target: "watchtower"));
        Assert.True(created.IsSuccess, Describe(created));

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        // The stored value, read without any normalization of its own.
        var stored = await db.Routes.AsNoTracking().Select(r => r.Domain).SingleAsync(Ct);
        Assert.Equal("login.example.invalid", stored);
        Assert.Empty(await db.Routes.AsNoTracking().Where(r => r.Domain != r.Domain.ToLower()).ToListAsync(Ct));

        // And the two lookups that now compare against the raw column still answer for a header that
        // arrives in any case, because they normalize the parameter instead.
        var route = await RouteAccessPolicy.FindRouteByHostAsync(
            db, RouteAccessPolicy.NormalizeForwardedHost("LOGIN.Example.INVALID")!, Ct);
        Assert.NotNull(route);

        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();
        var resolved = await realms.ResolveByHostAsync("Login.Example.Invalid:8443", Ct);
        Assert.True(resolved.IsSystem);
    }

    [Fact]
    public async Task AnEditCannotIntroduceAnUppercaseDomainEither() {
        using var host = AuthTestHost.Start(WithRouteHandlers);

        await using var scope = host.Services.CreateAsyncScope();
        var created = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider,
            new CreateRoute.Command(
                StackId: 0, Domain: "login.example.invalid", ServiceName: "", ContainerPort: 0,
                TlsEnabled: true, IsPrimary: false, Kind: null, Target: "watchtower"));
        Assert.True(created.IsSuccess, Describe(created));

        var edited = await SendAsync<UpdateRoute.Command, UpdateRoute.Response>(
            scope.ServiceProvider,
            new UpdateRoute.Command(
                created.Value.Route.Id, "PORTAL.Example.INVALID", ServiceName: "", ContainerPort: 0,
                TlsEnabled: true, IsPrimary: false));
        Assert.True(edited.IsSuccess, Describe(edited));

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal("portal.example.invalid", await db.Routes.AsNoTracking().Select(r => r.Domain).SingleAsync(Ct));
    }

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, Ct);

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
