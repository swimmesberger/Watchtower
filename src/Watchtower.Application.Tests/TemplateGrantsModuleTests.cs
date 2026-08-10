using System.Security.Claims;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Modules.Tenancy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the template management grants — the rows that are the entire authorization behind the public
/// management API (<c>/api/mgmt/*</c>) — through their real generated handler pipelines, so the
/// <c>[RequireRole("Admin")]</c> decorator is exercised together with the module's own rules.
/// </summary>
/// <remarks>
/// Two of those rules carry the security weight: a grant may not be given to a stack that is itself a
/// tenant of the target template (which would let one tenant manage its neighbours), and the write is an
/// upsert on <c>(stack, template)</c> so a revoke can never leave a second, forgotten row still granting
/// access.
/// </remarks>
public sealed class TemplateGrantsModuleTests {
    /// <summary>The grant handlers, added the way the generated module registration does.</summary>
    private static readonly Action<IServiceCollection> WithGrantHandlers = services => {
        services.AddListGrants();
        services.AddGrantManagement();
        services.AddRevokeManagement();
    };

    // -- Granting --------------------------------------------------------------------------------

    [Fact]
    public async Task Grant_CreatesTheGrant_AndListsItWithTheStackName() {
        using var host = AuthTestHost.Start(WithGrantHandlers);
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("vendor-console");

        await using var scope = host.Services.CreateAsyncScope();
        var granted = await SendAsync<GrantManagement.Command, GrantManagement.Response>(
            scope.ServiceProvider, new GrantManagement.Command(templateId, stackId, AllowDelete: true));

        Assert.True(granted.IsSuccess, Describe(granted));
        Assert.Equal(stackId, granted.Value.Grant.StackId);
        Assert.Equal("vendor-console", granted.Value.Grant.StackName);
        Assert.True(granted.Value.Grant.AllowDelete);

        var listed = await SendAsync<ListGrants.Query, ListGrants.Response>(
            scope.ServiceProvider, new ListGrants.Query(templateId));
        Assert.True(listed.IsSuccess, Describe(listed));
        var only = Assert.Single(listed.Value.Grants);
        Assert.Equal(stackId, only.StackId);
        Assert.True(only.AllowDelete);
    }

    /// <summary>
    /// Re-granting must adjust the existing row, not add a second one: a duplicate would survive the
    /// revoke that deletes "the" grant and keep the surface open.
    /// </summary>
    [Fact]
    public async Task Grant_IsAnUpsert_KeepingOneRowAndItsOriginalCreatedAt() {
        using var host = AuthTestHost.Start(WithGrantHandlers);
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("vendor-console");

        DateTimeOffset createdAt;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var first = await SendAsync<GrantManagement.Command, GrantManagement.Response>(
                scope.ServiceProvider, new GrantManagement.Command(templateId, stackId, AllowDelete: false));
            Assert.True(first.IsSuccess, Describe(first));
            createdAt = first.Value.Grant.CreatedAt;
        }

        host.Time.Advance(TimeSpan.FromHours(1));

        await using (var scope = host.Services.CreateAsyncScope()) {
            var second = await SendAsync<GrantManagement.Command, GrantManagement.Response>(
                scope.ServiceProvider, new GrantManagement.Command(templateId, stackId, AllowDelete: true));
            Assert.True(second.IsSuccess, Describe(second));
            Assert.True(second.Value.Grant.AllowDelete);
            // CreatedAt records when management was first granted, not when the capability last changed.
            Assert.Equal(createdAt, second.Value.Grant.CreatedAt);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.Equal(1, await db.TemplateManagementGrants.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Grant_RefusesATenantOfTheTemplateItWouldManage() {
        using var host = AuthTestHost.Start(WithGrantHandlers);
        var templateId = await host.AddTemplateAsync("billing");
        var tenantStackId = await host.AddStackAsync("billing-acme", templateId, tenantSlug: "acme");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<GrantManagement.Command, GrantManagement.Response>(
            scope.ServiceProvider, new GrantManagement.Command(templateId, tenantStackId, AllowDelete: false));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);

        await using var check = host.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.TemplateManagementGrants.AnyAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A tenant of a <em>different</em> template is an ordinary stack and may be granted.</summary>
    [Fact]
    public async Task Grant_AllowsATenantOfAnotherTemplate() {
        using var host = AuthTestHost.Start(WithGrantHandlers);
        var billing = await host.AddTemplateAsync("billing");
        var portal = await host.AddTemplateAsync("portal");
        var portalTenant = await host.AddStackAsync("portal-acme", portal, tenantSlug: "acme");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<GrantManagement.Command, GrantManagement.Response>(
            scope.ServiceProvider, new GrantManagement.Command(billing, portalTenant, AllowDelete: false));

        Assert.True(result.IsSuccess, Describe(result));
    }

    [Fact]
    public async Task Grant_ReportsAnUnknownTemplateOrStackAsNotFound() {
        using var host = AuthTestHost.Start(WithGrantHandlers);
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("vendor-console");

        await using var scope = host.Services.CreateAsyncScope();

        var noTemplate = await SendAsync<GrantManagement.Command, GrantManagement.Response>(
            scope.ServiceProvider, new GrantManagement.Command(404, stackId, AllowDelete: false));
        Assert.False(noTemplate.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, noTemplate.Error.Kind);

        var noStack = await SendAsync<GrantManagement.Command, GrantManagement.Response>(
            scope.ServiceProvider, new GrantManagement.Command(templateId, 404, AllowDelete: false));
        Assert.False(noStack.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, noStack.Error.Kind);
    }

    // -- Listing ---------------------------------------------------------------------------------

    [Fact]
    public async Task List_ReportsAnUnknownTemplateAsNotFound() {
        using var host = AuthTestHost.Start(WithGrantHandlers);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListGrants.Query, ListGrants.Response>(
            scope.ServiceProvider, new ListGrants.Query(404));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    [Fact]
    public async Task List_ShowsOnlyTheGrantsOfTheTemplateAsked() {
        using var host = AuthTestHost.Start(WithGrantHandlers);
        var billing = await host.AddTemplateAsync("billing");
        var portal = await host.AddTemplateAsync("portal");
        var console = await host.AddStackAsync("vendor-console");
        var other = await host.AddStackAsync("other-console");
        await host.AddGrantAsync(console, billing);
        await host.AddGrantAsync(other, portal);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListGrants.Query, ListGrants.Response>(
            scope.ServiceProvider, new ListGrants.Query(billing));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal([console], result.Value.Grants.Select(g => g.StackId));
    }

    // -- Revoking --------------------------------------------------------------------------------

    [Fact]
    public async Task Revoke_RemovesTheGrant_AndIsIdempotent() {
        using var host = AuthTestHost.Start(WithGrantHandlers);
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("vendor-console");
        await host.AddGrantAsync(stackId, templateId);

        await using var scope = host.Services.CreateAsyncScope();

        var first = await SendAsync<RevokeManagement.Command, RevokeManagement.Response>(
            scope.ServiceProvider, new RevokeManagement.Command(templateId, stackId));
        Assert.True(first.IsSuccess, Describe(first));
        Assert.True(first.Value.Removed);

        var second = await SendAsync<RevokeManagement.Command, RevokeManagement.Response>(
            scope.ServiceProvider, new RevokeManagement.Command(templateId, stackId));
        Assert.True(second.IsSuccess, Describe(second));
        Assert.False(second.Value.Removed);
    }

    /// <summary>Deleting the granted stack takes its grants with it — a recycled id must not inherit them.</summary>
    [Fact]
    public async Task DeletingTheGrantedStack_CascadesTheGrantAway() {
        using var host = AuthTestHost.Start(WithGrantHandlers);
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("vendor-console");
        await host.AddGrantAsync(stackId, templateId);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Stacks.Where(s => s.Id == stackId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        Assert.False(await db.TemplateManagementGrants.AnyAsync(TestContext.Current.CancellationToken));
    }

    // -- Authorization ---------------------------------------------------------------------------

    /// <summary>
    /// Grant management is privilege management, so all three handlers are <c>[RequireRole("Admin")]</c>:
    /// a signed-in non-administrator is refused by the generated decorator before the handler runs.
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("grant")]
    [InlineData("revoke")]
    public async Task GrantHandlers_WithAuthEnabled_AreDeniedToANonAdministrator(string operation) {
        using var host = AuthTestHost.Start(WithGrantHandlers, ("Watchtower:Auth:Enabled", "true"));
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("vendor-console");

        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        SeedPrincipal(sp, isAdmin: false);

        var kind = operation switch {
            "list" => await DeniedKindAsync<ListGrants.Query, ListGrants.Response>(
                sp, new ListGrants.Query(templateId)),
            "grant" => await DeniedKindAsync<GrantManagement.Command, GrantManagement.Response>(
                sp, new GrantManagement.Command(templateId, stackId, AllowDelete: true)),
            "revoke" => await DeniedKindAsync<RevokeManagement.Command, RevokeManagement.Response>(
                sp, new RevokeManagement.Command(templateId, stackId)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        Assert.Equal(ErrorKind.Forbidden, kind);

        await using var check = host.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.TemplateManagementGrants.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GrantHandlers_WithAuthEnabled_AreAllowedToAnAdministrator() {
        using var host = AuthTestHost.Start(WithGrantHandlers, ("Watchtower:Auth:Enabled", "true"));
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("vendor-console");

        await using var scope = host.Services.CreateAsyncScope();
        SeedPrincipal(scope.ServiceProvider, isAdmin: true);

        var result = await SendAsync<GrantManagement.Command, GrantManagement.Response>(
            scope.ServiceProvider, new GrantManagement.Command(templateId, stackId, AllowDelete: false));

        Assert.True(result.IsSuccess, Describe(result));
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>()
            .HandleAsync(request, TestContext.Current.CancellationToken);

    private static async ValueTask<ErrorKind> DeniedKindAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) {
        var result = await SendAsync<TRequest, TResponse>(scope, request);
        Assert.False(result.IsSuccess);
        return result.Error.Kind;
    }

    /// <summary>Applies a principal the way every Elarion transport does — through the dispatch-scope rail.</summary>
    private static void SeedPrincipal(IServiceProvider scope, bool isAdmin) {
        var claims = new List<Claim> {
            new(WatchtowerClaims.UserId, "7"),
            new(WatchtowerClaims.Name, "caller"),
        };
        if (isAdmin) claims.Add(new Claim(WatchtowerClaims.Role, WatchtowerClaims.AdminRole));

        var context = new DispatchScopeContext();
        context.Set(new ClaimsPrincipal(new ClaimsIdentity(
            claims, "WatchtowerSession", WatchtowerClaims.Name, WatchtowerClaims.Role)));
        scope.SeedScope(context);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
