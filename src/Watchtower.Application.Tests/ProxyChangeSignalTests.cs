using Elarion.Abstractions;
using Elarion.Settings;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Config;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The cross-instance change signal — ADR-0024 decision 6 — exercised the only way it can meaningfully
/// be: two hosts over one PostgreSQL, with a change made through one instance's handler and observed on
/// the other without any direct call between them.
/// </summary>
/// <remarks>
/// <para>
/// What is under test is the whole chain and not a unit of it: a write handler bumps
/// <c>Watchtower:Proxy:RoutesVersion</c>, the Elarion settings store commits it, PostgreSQL's
/// <c>LISTEN/NOTIFY</c> carries it to the other host's listener, its change token fires, and the
/// debounced watcher re-projects the route table and re-reads the certificate rows. Any link of that
/// missing is a cluster whose second node serves last hour's routes, which is precisely the failure that
/// is invisible in a single-node test.
/// </para>
/// <para>
/// Waiting is inherent — a notification is asynchronous, and the watcher deliberately debounces — so the
/// assertions poll to a generous ceiling rather than sleeping a fixed time. A poll that gives up says
/// the signal did not arrive; a fixed sleep would only say the machine was busy.
/// </para>
/// </remarks>
public sealed class ProxyChangeSignalTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>How long a signal may take to cross before the test calls it a failure.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(20);

    private const string Host = "signal.example.invalid";

    [Fact]
    public async Task ARouteCreatedOnOneInstance_ReachesTheOthersRouteTable() {
        using var a = StartInstance();
        using var b = StartInstance(a);
        await StartWatchersAsync(a, b);

        // Through the handler, not through a direct ApplyAsync: what is being tested is that an ordinary
        // operator action on one node propagates, not that a re-projection works when asked for.
        var stackId = await a.AddStackAsync("billing", composeProjectName: "billing");
        await CreateRouteAsync(a, stackId, Host);

        // The instance the operator was talking to is correct immediately — that is the local ApplyAsync,
        // and the reason the signal did not replace it.
        Assert.True(Table(a).Current.TryGet(Host, out _));
        // …and the other one converges without anybody calling it.
        await EventuallyAsync(() => Table(b).Current.TryGet(Host, out _), "host B never saw the route");
    }

    [Fact]
    public async Task ACertificateInstalledOnOneInstance_IsServedByTheOther() {
        using var a = StartInstance();
        using var b = StartInstance(a);
        await StartWatchersAsync(a, b);
        using var chain = TestCertificates.Create(Host);

        await Store(a).InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        await EventuallyAsync(
            () => Store(b).SelectContext(Host) is not null, "host B never picked up the certificate");
        Assert.Equal(chain.Leaf.Thumbprint, Store(b).SelectCertificate(Host)!.Thumbprint);
    }

    /// <summary>
    /// The value is a fresh random string rather than a counter, so two instances bumping at once is a
    /// non-event instead of a lost update or a retry loop.
    /// </summary>
    [Fact]
    public async Task EachBump_WritesADifferentValue() {
        using var host = StartInstance();
        var signal = host.Services.GetRequiredService<ProxyChangeSignal>();

        await signal.BumpAsync("first", Ct);
        var first = await VersionAsync(host);
        await signal.BumpAsync("second", Ct);
        var second = await VersionAsync(host);

        Assert.NotNull(first);
        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Two properties that pull against each other, both required. A pass re-projects the route table
    /// and rebuilds certificate contexts, so two of them interleaving would have the callback racing
    /// itself over the same maps — but a change signalled <em>while</em> a pass runs must still earn one
    /// of its own, because that pass may have read the database before the change landed.
    /// </summary>
    [Fact]
    public async Task Watchers_NeverOverlap_AndAlwaysRunAfterTheLastBump() {
        using var host = StartInstance();
        await host.StartSettingsChangeListenerAsync(Ct);
        var signal = host.Services.GetRequiredService<ProxyChangeSignal>();

        var concurrent = 0;
        var overlapped = false;
        var completed = 0;
        var entered = new SemaphoreSlim(0);
        var release = new SemaphoreSlim(0);

        using var watch = signal.Watch(
            async _ => {
                if (Interlocked.Increment(ref concurrent) > 1) overlapped = true;
                entered.Release();
                // Held open, so a bump arriving mid-pass has somewhere to race to.
                await release.WaitAsync(Ct);
                Interlocked.Decrement(ref concurrent);
                Interlocked.Increment(ref completed);
            },
            TimeSpan.FromMilliseconds(20));

        await signal.BumpAsync("first", Ct);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(20), Ct), "the first pass never ran");

        // Two more while the first is still inside the callback. Neither may start a second pass now,
        // and together they must earn exactly one more once the first finishes.
        await signal.BumpAsync("second", Ct);
        await signal.BumpAsync("third", Ct);
        await Task.Delay(200, Ct);
        Assert.False(overlapped);
        Assert.Equal(0, Volatile.Read(ref completed));

        release.Release();
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(20), Ct), "no pass followed the bumps");
        release.Release();

        await EventuallyAsync(() => Volatile.Read(ref completed) >= 2, "the second pass never finished");
        Assert.False(overlapped);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One instance, with the in-process proxy active. <paramref name="sibling"/> shares its database,
    /// which is what makes the pair a two-node deployment rather than two deployments.
    /// </summary>
    private static AuthTestHost StartInstance(AuthTestHost? sibling = null) {
        (string, string?)[] settings = [
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"),
        ];
        return sibling is null
            ? AuthTestHost.Start(services => services.AddCreateRoute(), settings)
            : sibling.Restart(settings);
    }

    /// <summary>
    /// Starts the two watchers the host would start. <see cref="YarpProxyProvider"/> registers its
    /// subscription in <c>StartAsync</c> and the certificate store in <c>InitializeAsync</c> — the test
    /// host runs the latter and no hosted services, so the provider is started by hand.
    /// </summary>
    private static async Task StartWatchersAsync(params AuthTestHost[] hosts) {
        foreach (var host in hosts) {
            // The LISTEN loop first: a notification sent before a node is listening is lost, because
            // PostgreSQL does not queue for absent listeners.
            await host.StartSettingsChangeListenerAsync(Ct);
            await host.Services.GetRequiredService<YarpProxyProvider>().StartAsync(Ct);
        }
    }

    private static ProxyRouteTable Table(AuthTestHost host) =>
        host.Services.GetRequiredService<ProxyRouteTable>();

    private static CertificateStore Store(AuthTestHost host) =>
        host.Services.GetRequiredService<CertificateStore>();

    private static async Task<string?> VersionAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsManager>()
            .GetStringAsync(WatchtowerSettingPaths.ProxyRoutesVersion, SettingsScope.Global, Ct);
    }

    private static async Task CreateRouteAsync(AuthTestHost host, int stackId, string domain) {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider
            .GetRequiredService<IHandler<CreateRoute.Command, Result<CreateRoute.Response>>>();
        var result = await handler.HandleAsync(
            new CreateRoute.Command(
                StackId: stackId, Domain: domain, ServiceName: "web", ContainerPort: 80,
                TlsEnabled: true, IsPrimary: true, Kind: null),
            Ct);
        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or the ceiling passes. Polling rather than one
    /// sleep because the interesting failure is "it never arrived", and a fixed wait cannot tell that
    /// apart from "the machine was busy".
    /// </summary>
    private static async Task EventuallyAsync(Func<bool> condition, string because) {
        var deadline = DateTimeOffset.UtcNow + Ceiling;
        while (DateTimeOffset.UtcNow < deadline) {
            if (condition()) return;
            await Task.Delay(100, Ct);
        }
        Assert.Fail($"{because} within {Ceiling.TotalSeconds:0} s.");
    }
}
