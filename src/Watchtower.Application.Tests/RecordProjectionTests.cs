using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Guards the record-as-tuples opt-in on the shared <c>NpgsqlDataSource</c>. Several services
/// (StackUpdateService, DeployQueueService, SelfUpdateService, ProxyIngressNetworks,
/// StackDesiredStateReconciler) project query results into <c>ValueTuple</c>s, which EF translates
/// to <c>ROW(...)</c> selects; a data source built without <c>EnableRecordsAsTuples</c> cannot read
/// those back and every such query throws <c>InvalidCastException</c> at runtime. This goes through
/// <c>AddWatchtowerServices</c> so it fails if the production registration loses the opt-in.
/// </summary>
public sealed class RecordProjectionTests {
    [Fact]
    public async Task ValueTupleProjectionReadsBackThroughSharedDataSource() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        db.Credentials.Add(new Credential { Name = "registry", Username = "alice", Token = "secret" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The same shape as StackUpdateService.GetCredential.
        var credential = db.Credentials
            .Where(c => c.Name == "registry")
            .Select(c => new ValueTuple<string, string>(c.Username, c.Token))
            .Cast<(string, string)?>()
            .FirstOrDefault();

        Assert.Equal(("alice", "secret"), credential);
    }
}
