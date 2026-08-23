using Elarion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Modules.Proxy.Handlers;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Exactly one instance orders certificates — ADR-0024 decision 5. Every instance serves from the table;
/// the one holding the <c>acme-issuer</c> role lease is the only one that talks to the CA.
/// </summary>
/// <remarks>
/// The failure this rules out is not subtle: without the gate, three instances open three orders for
/// every host and spend the deployment's Let's Encrypt rate limit three times over to obtain one
/// certificate. The lease is substituted rather than acquired, because acquiring it means waiting on a
/// heartbeat — and because the interesting half is the <em>non</em>-holder, which no amount of waiting
/// produces reliably.
/// </remarks>
public sealed class AcmeIssuerLeaseTests {
    private const string Host = "app.example.invalid";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheHolder_Issues() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        estate.Factory.IssuerLease.IsHeld = true;

        await estate.Certificates.ReconcileAsync(Ct);

        Assert.NotNull(estate.Store.Find(Host));
        Assert.Equal("active", estate.State(Host).State);
    }

    /// <summary>
    /// The one that matters: a non-holder makes <em>no CA request at all</em>. Asserting on the CA's own
    /// request log rather than on the absence of a certificate, because "no certificate yet" is also what
    /// a failed order looks like.
    /// </summary>
    [Fact]
    public async Task ANonHolder_NeverReachesTheCa() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        estate.Factory.IssuerLease.IsHeld = false;
        estate.Factory.IssuerLease.CurrentHolder = "node-b:abc";

        estate.Ca.ForgetRequests();
        await estate.Certificates.ReconcileAsync(Ct);

        Assert.Empty(estate.Ca.Requests);
        Assert.Null(estate.Store.Find(Host));
    }

    /// <summary>
    /// A non-holder still reports what it is serving. Certificate state on the Routes page is about the
    /// table, not about who ordered — an instance that showed "waiting for a certificate" for a host it
    /// is serving perfectly well would be lying to the operator looking at it.
    /// </summary>
    [Fact]
    public async Task ANonHolder_StillProjectsWhatItServes() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        // Obtained while this instance was the holder — or, in a deployment, by another instance.
        await estate.Certificates.RenewNowAsync(Host, Ct);
        estate.Factory.IssuerLease.IsHeld = false;
        estate.Factory.IssuerLease.CurrentHolder = "node-b:abc";
        estate.Ca.ForgetRequests();

        await estate.Certificates.ReconcileAsync(Ct);

        Assert.False(estate.Ca.SawAnOrder);
        Assert.Equal("active", estate.State(Host).State);
        var route = await estate.RouteAsync(Host);
        Assert.Equal(Application.Entities.RouteStatus.Active, route.Status);
    }

    /// <summary>
    /// "Renew now" on a non-holder is refused with the instance that can do it, rather than forwarded. A
    /// holder proxy needs the advertised address on the lease row and an authenticated hop between
    /// instances; half of it — a silent forward with no error path — would be worse than saying where the
    /// work happens.
    /// </summary>
    [Fact]
    public async Task RenewNow_OnANonHolder_IsAConflictNamingTheHolder() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        estate.Factory.IssuerLease.IsHeld = false;
        estate.Factory.IssuerLease.CurrentHolder = "node-b:abc";

        Result<RenewCertificate.Response>? result = null;
        await estate.Factory.WithScopeAsync(async sp => {
            result = await sp
                .GetRequiredService<IHandler<RenewCertificate.Command, Result<RenewCertificate.Response>>>()
                .HandleAsync(new RenewCertificate.Command(Host), Ct);
        });

        Assert.False(result!.Value.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Value.Error.Kind);
        Assert.Contains("node-b:abc", result.Value.Error.Message, StringComparison.Ordinal);
        Assert.False(estate.Ca.SawAnOrder);
    }

    /// <summary>With no holder at all, the message names nowhere rather than "retry at ''".</summary>
    [Fact]
    public async Task WithNoHolderAtAll_TheReasonSaysSo() {
        await using var estate = await AcmeEstate.StartAsync();
        estate.Factory.IssuerLease.IsHeld = false;
        estate.Factory.IssuerLease.CurrentHolder = null;

        var reason = estate.Certificates.IssuanceUnavailableReason();

        Assert.NotNull(reason);
        Assert.Contains("No instance currently holds", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHolder_HasNoReasonToRefuse() {
        await using var estate = await AcmeEstate.StartAsync();
        estate.Factory.IssuerLease.IsHeld = true;

        Assert.Null(estate.Certificates.IssuanceUnavailableReason());
    }
}
