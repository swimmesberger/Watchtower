using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Tests whose subject reads the <c>HOSTNAME</c> environment variable, which is process-wide state. They
/// share a collection so they never run at the same time as each other.
/// </summary>
[CollectionDefinition(HostnameEnvironment.Name)]
public sealed class HostnameEnvironmentCollection;

/// <summary>Sets <c>HOSTNAME</c> for the duration of a block and puts back whatever was there.</summary>
internal static class HostnameEnvironment {
    public const string Name = "hostname-environment";

    public static IDisposable Set(string value) => new Scope(value);

    private sealed class Scope : IDisposable {
        private readonly string? _previous;

        public Scope(string value) {
            _previous = Environment.GetEnvironmentVariable("HOSTNAME");
            Environment.SetEnvironmentVariable("HOSTNAME", value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("HOSTNAME", _previous);
    }
}

/// <summary>
/// The in-process provider's startup reconcile, and specifically the half of it that is allowed to fail.
/// Joining the ingress networks is a conversation with the Docker daemon; projecting the route table is
/// Watchtower reading its own database. Tying the second to the first is what produced the worst failure
/// this proxy has had in the field: with the daemon unreachable the table stayed empty, so every routed
/// host fell through to Watchtower's own pipeline and a tenant domain answered with Watchtower's UI —
/// over the tenant's certificate — while every status surface reported the proxy as perfectly healthy.
/// </summary>
[Collection(HostnameEnvironment.Name)]
public sealed class YarpProxyProviderReconcileTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Host = "app.example.invalid";

    [Fact]
    public async Task Reconcile_ProjectsTheRouteTable_EvenWhenDockerIsUnreachable() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"));
        var stackId = await host.AddStackAsync("billing", composeProjectName: "billing");
        await host.AddRouteAsync(stackId, Host);

        var certs = new RecordingProxyCertificateManager();
        using var docker = UnreachableDocker();
        var networks = new ProxyIngressNetworks(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            docker,
            NullLogger<ProxyIngressNetworks>.Instance);
        using var provider = ActivatorUtilities.CreateInstance<YarpProxyProvider>(host.Services, networks, certs);

        // With HOSTNAME set the reconcile actually attempts the network join — which is the whole point:
        // unset, it skips it and the regression could not reproduce.
        using (HostnameEnvironment.Set("watchtower-proxy")) await provider.ReconcileAsync(Ct);

        var snapshot = host.Services.GetRequiredService<ProxyRouteTable>().Current;
        Assert.True(snapshot.TryGet(Host, out var route));
        Assert.Equal(ProxyIngressNetworks.EdgeAlias("billing", "web"), route.UpstreamHost);
        // And the certificate plane was told about the host too, so the domain is not left on plain HTTP
        // for as long as the daemon stays down.
        Assert.Contains(Host, certs.DesiredHosts);
    }

    /// <summary>
    /// The sweep is per stack, so one stack the daemon refuses must not cost the stacks behind it in the
    /// list their upstream hop. It still reports the failure — the caller decides what to do with it.
    /// </summary>
    [Fact]
    public async Task ConnectAllRoutedContainers_KeepsGoingPastAStackThatFails() {
        using var host = AuthTestHost.Start();
        var first = await host.AddStackAsync("billing", composeProjectName: "billing");
        var second = await host.AddStackAsync("crm", composeProjectName: "crm");
        await host.AddRouteAsync(first, "billing.example.invalid");
        await host.AddRouteAsync(second, "crm.example.invalid");

        using var docker = UnreachableDocker();
        var networks = new ProxyIngressNetworks(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            docker,
            NullLogger<ProxyIngressNetworks>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => networks.ConnectAllRoutedContainersAsync("watchtower-proxy", Ct));
        // Both were attempted: the first failure did not abandon the second stack.
        Assert.Contains("2 of 2", ex.Message);
    }

    /// <summary>A client whose daemon is not there at all — every call throws on connect.</summary>
    private static DockerEngineClient UnreachableDocker() =>
        new("1.43", new UnreachableHandler(), TimeSpan.FromMinutes(30));

    private sealed class UnreachableHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Cannot connect to the Docker daemon at unix:///var/run/docker.sock.");
    }
}
