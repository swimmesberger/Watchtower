using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Backups.Handlers;
using Watchtower.Application.Modules.Stacks.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers pruning a stack's deploy and backup history (<c>stacks.deleteEvents</c>,
/// <c>backups.deleteEvents</c>): one event or every finished one, never a queued or running one, and
/// never another stack's.
/// </summary>
public sealed class EventHistoryPruningTests {
    private static readonly Action<IServiceCollection> WithHandlers = services => {
        services.AddDeleteDeployEvents();
        services.AddDeleteBackupEvents();
    };

    // ── stacks.deleteEvents ──────────────────────────────────────────────────

    [Fact]
    public async Task DeployEvents_DeletesOneEvent() {
        using var host = AuthTestHost.Start(WithHandlers);
        var stackId = await AddStackAsync(host, "shop");
        var keep = await AddDeployEventAsync(host, stackId, "success");
        var drop = await AddDeployEventAsync(host, stackId, "failed");

        var result = await SendAsync<DeleteDeployEvents.Command, DeleteDeployEvents.Response>(
            host, new DeleteDeployEvents.Command(stackId, drop));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal(1, result.Value.Deleted);
        Assert.Equal([keep], await DeployEventIdsAsync(host));
        await AssertAuditedAsync(host, StackLifecycle.AuditCategory, "stack.events.delete", "shop");
    }

    [Fact]
    public async Task DeployEvents_ClearKeepsActiveEventsAndOtherStacks() {
        using var host = AuthTestHost.Start(WithHandlers);
        var stackId = await AddStackAsync(host, "shop");
        var otherId = await AddStackAsync(host, "blog");
        await AddDeployEventAsync(host, stackId, "success");
        await AddDeployEventAsync(host, stackId, "failed");
        var running = await AddDeployEventAsync(host, stackId, "running");
        var queued = await AddDeployEventAsync(host, stackId, "queued");
        var other = await AddDeployEventAsync(host, otherId, "success");

        var result = await SendAsync<DeleteDeployEvents.Command, DeleteDeployEvents.Response>(
            host, new DeleteDeployEvents.Command(stackId));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal(2, result.Value.Deleted);
        // The worker still writes to an active deploy's row, and the live log streams from it.
        Assert.Equal([running, queued, other], await DeployEventIdsAsync(host));
        await AssertAuditedAsync(host, StackLifecycle.AuditCategory, "stack.events.clear", "shop");
    }

    [Fact]
    public async Task DeployEvents_RefusesARunningEvent() {
        using var host = AuthTestHost.Start(WithHandlers);
        var stackId = await AddStackAsync(host, "shop");
        var running = await AddDeployEventAsync(host, stackId, "running");

        var result = await SendAsync<DeleteDeployEvents.Command, DeleteDeployEvents.Response>(
            host, new DeleteDeployEvents.Command(stackId, running));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Equal([running], await DeployEventIdsAsync(host));
    }

    [Fact]
    public async Task DeployEvents_RefusesAnotherStacksEvent() {
        using var host = AuthTestHost.Start(WithHandlers);
        var stackId = await AddStackAsync(host, "shop");
        var otherId = await AddStackAsync(host, "blog");
        var other = await AddDeployEventAsync(host, otherId, "success");

        var result = await SendAsync<DeleteDeployEvents.Command, DeleteDeployEvents.Response>(
            host, new DeleteDeployEvents.Command(stackId, other));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
        Assert.Equal([other], await DeployEventIdsAsync(host));
    }

    // ── backups.deleteEvents ─────────────────────────────────────────────────

    [Fact]
    public async Task BackupEvents_DeletesOneEvent() {
        using var host = AuthTestHost.Start(WithHandlers);
        var stackId = await AddStackAsync(host, "shop");
        var keep = await AddBackupEventAsync(host, stackId, BackupStatuses.Success);
        var drop = await AddBackupEventAsync(host, stackId, BackupStatuses.Failed);

        var result = await SendAsync<DeleteBackupEvents.Command, DeleteBackupEvents.Response>(
            host, new DeleteBackupEvents.Command(stackId, drop));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal(1, result.Value.Deleted);
        Assert.Equal([keep], await BackupEventIdsAsync(host));
        await AssertAuditedAsync(host, BackupService.AuditCategory, "stack.events.delete", "shop");
    }

    [Fact]
    public async Task BackupEvents_ClearKeepsActiveRunsInstanceRunsAndOtherStacks() {
        using var host = AuthTestHost.Start(WithHandlers);
        var stackId = await AddStackAsync(host, "shop");
        var otherId = await AddStackAsync(host, "blog");
        await AddBackupEventAsync(host, stackId, BackupStatuses.Success);
        await AddBackupEventAsync(host, stackId, BackupStatuses.Failed);
        var running = await AddBackupEventAsync(host, stackId, BackupStatuses.Running);
        var other = await AddBackupEventAsync(host, otherId, BackupStatuses.Success);
        var instance = await AddBackupEventAsync(host, null, BackupStatuses.Success);

        var result = await SendAsync<DeleteBackupEvents.Command, DeleteBackupEvents.Response>(
            host, new DeleteBackupEvents.Command(stackId));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal(2, result.Value.Deleted);
        Assert.Equal([running, other, instance], await BackupEventIdsAsync(host));
        await AssertAuditedAsync(host, BackupService.AuditCategory, "stack.events.clear", "shop");
    }

    [Fact]
    public async Task BackupEvents_RefusesAQueuedRun() {
        using var host = AuthTestHost.Start(WithHandlers);
        var stackId = await AddStackAsync(host, "shop");
        var queued = await AddBackupEventAsync(host, stackId, BackupStatuses.Queued);

        var result = await SendAsync<DeleteBackupEvents.Command, DeleteBackupEvents.Response>(
            host, new DeleteBackupEvents.Command(stackId, queued));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Equal([queued], await BackupEventIdsAsync(host));
    }

    [Fact]
    public async Task UnknownStack_IsNotFound() {
        using var host = AuthTestHost.Start(WithHandlers);

        var deploy = await SendAsync<DeleteDeployEvents.Command, DeleteDeployEvents.Response>(
            host, new DeleteDeployEvents.Command(4242));
        var backup = await SendAsync<DeleteBackupEvents.Command, DeleteBackupEvents.Response>(
            host, new DeleteBackupEvents.Command(4242));

        Assert.Equal(ErrorKind.NotFound, deploy.Error.Kind);
        Assert.Equal(ErrorKind.NotFound, backup.Error.Kind);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        AuthTestHost host, TRequest request) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IHandler<TRequest, Result<TResponse>>>()
            .HandleAsync(request, Ct);
    }

    private static async Task<int> AddStackAsync(AuthTestHost host, string name) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = name,
            ComposeProjectName = name,
            Product = TestProducts.New(name, $"https://github.com/acme/{name}.git"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    private static async Task<int> AddDeployEventAsync(AuthTestHost host, int stackId, string status) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ev = new DeployEvent {
            StackId = stackId, TriggeredBy = "manual", Status = status, StartedAt = DateTimeOffset.UtcNow,
        };
        db.DeployEvents.Add(ev);
        await db.SaveChangesAsync(Ct);
        return ev.Id;
    }

    private static async Task<int> AddBackupEventAsync(AuthTestHost host, int? stackId, string status) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ev = new BackupEvent {
            StackId = stackId, TriggeredBy = "manual", Status = status, StartedAt = DateTimeOffset.UtcNow,
        };
        db.BackupEvents.Add(ev);
        await db.SaveChangesAsync(Ct);
        return ev.Id;
    }

    private static async Task<List<int>> DeployEventIdsAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.DeployEvents.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(Ct);
    }

    private static async Task<List<int>> BackupEventIdsAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.BackupEvents.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(Ct);
    }

    private static async Task AssertAuditedAsync(AuthTestHost host, string category, string action, string target) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Category == category && e.Action == action, Ct);
        Assert.Contains(target, row.Target);
    }
}
