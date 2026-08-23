using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// What happens when issuance does not work — which is most of the time, in a deployment where operators
/// add routes before they add DNS records. ADR-0022.
/// </summary>
/// <remarks>
/// Every case here is about the same two things: the operator is told something they can act on, and the
/// next attempt is far enough away not to spend the CA's budget. Let's Encrypt allows five failed
/// validations per hostname per hour and a fixed number of certificates per domain per week, so a retry
/// loop that is merely "polite" is not good enough — the intervals are part of the contract.
/// </remarks>
public sealed class AcmeFailurePathTests {
    private const string Host = "app.example.invalid";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The CA's own sentence, all the way onto the route row — and a retry no sooner than fifteen
    /// minutes, because a validation failure is not something a faster retry can fix.
    /// </summary>
    [Fact]
    public async Task AFailedValidation_LandsTheCaSentenceOnTheRoute_AndBacksOff() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        estate.Ca.FailValidationWith = (
            AcmeProblemTypes.Unauthorized,
            $"{Host}: Invalid response from http://{Host}/.well-known/acme-challenge/xyz: 404");

        var outcome = await estate.Certificates.RenewNowAsync(Host, Ct);

        var failed = Assert.IsType<IssueOutcome.Failed>(outcome);
        Assert.Equal(AcmeFailureClass.Validation, failed.Class);
        Assert.Contains("Invalid response", failed.Detail);

        var route = await estate.RouteAsync(Host);
        Assert.Equal(RouteStatus.Error, route.Status);
        Assert.Contains("Invalid response", route.Detail);

        var state = estate.State(Host);
        Assert.Equal("error", state.State);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.NotNull(state.NextAttemptAt);
        // Fifteen minutes, less the ±20% jitter of a ten-minute window.
        Assert.True(
            state.NextAttemptAt >= DateTimeOffset.UtcNow.AddMinutes(12),
            $"next attempt was {state.NextAttemptAt}, too soon after a validation failure");
    }

    /// <summary>
    /// A rate limit is the one failure where retrying is actively harmful. A full day, and longer if the
    /// CA named one.
    /// </summary>
    [Fact]
    public async Task ARateLimit_ParksTheHostForADay() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        estate.Ca.RateLimitOrdersFor = TimeSpan.FromHours(3);

        var outcome = await estate.Certificates.RenewNowAsync(Host, Ct);

        var failed = Assert.IsType<IssueOutcome.Failed>(outcome);
        Assert.Equal(AcmeFailureClass.RateLimited, failed.Class);
        Assert.Equal(TimeSpan.FromHours(3), failed.RetryAfter);

        var state = estate.State(Host);
        Assert.True(
            state.NextAttemptAt >= DateTimeOffset.UtcNow.AddHours(19),
            $"next attempt was {state.NextAttemptAt}, too soon after a rate limit");
    }

    /// <summary>
    /// The cheapest check there is, and the one that saves the most: a domain that does not resolve
    /// cannot pass HTTP-01, so asking the CA would spend a validation failure to learn what a DNS lookup
    /// answers for free. Zero requests reach the CA — not one, zero.
    /// </summary>
    [Fact]
    public async Task ADomainThatDoesNotResolve_NeverReachesTheCa() {
        await using var estate = await AcmeEstate.StartAsync();
        estate.Dns.DoesNotResolve(Host);
        await estate.AddRouteAsync(Host);

        var outcome = await estate.Certificates.RenewNowAsync(Host, Ct);

        var awaiting = Assert.IsType<IssueOutcome.AwaitingDns>(outcome);
        Assert.Contains("A/AAAA", awaiting.Detail);
        Assert.Empty(estate.Ca.Requests);

        var route = await estate.RouteAsync(Host);
        Assert.Equal(RouteStatus.AwaitingDns, route.Status);
        Assert.Contains("does not resolve yet", route.Detail);

        var state = estate.State(Host);
        Assert.Equal("awaitingDns", state.State);
        // Not on the backoff ladder: the preflight costs nothing, and an operator who has just added the
        // record expects the next ordinary pass to pick it up.
        Assert.Null(state.NextAttemptAt);
        Assert.Equal(0, state.ConsecutiveFailures);
    }

    /// <summary>
    /// One host per order, so one host's failure is one host's problem. A single multi-identifier order
    /// would take every site down with the one customer whose DNS is wrong.
    /// </summary>
    [Fact]
    public async Task OneHostFailing_DoesNotStopAnother() {
        await using var estate = await AcmeEstate.StartAsync();
        const string Healthy = "good.example.invalid";
        estate.Dns.DoesNotResolve(Host);
        await estate.AddRouteAsync(Host);
        await estate.AddRouteAsync(Healthy);

        await estate.Certificates.ReconcileAsync(Ct);

        Assert.Null(estate.Store.Find(Host));
        Assert.NotNull(estate.Store.Find(Healthy));
        Assert.Equal(RouteStatus.AwaitingDns, (await estate.RouteAsync(Host)).Status);
        Assert.Equal(RouteStatus.Active, (await estate.RouteAsync(Healthy)).Status);
    }

    /// <summary>
    /// The self-check exists to fail before the CA does. A listener that cannot answer its own challenge
    /// will not answer the CA's either, and finding that out here costs nothing — so the challenge is
    /// never triggered at all.
    /// </summary>
    [Fact]
    public async Task AFailingSelfCheck_StopsBeforeTheChallengeIsTriggered() {
        await using var estate = await AcmeEstate.StartAsync(selfCheck: true);
        // Port 1 on loopback: nothing listens there, which is what an unbound or unreachable HTTP
        // listener looks like from inside the process.
        estate.Factory.Services.GetRequiredService<YarpListenerState>()
            .Update(s => s with { LocalHttpAddress = "http://127.0.0.1:1" });
        await estate.AddRouteAsync(Host);

        var outcome = await estate.Certificates.RenewNowAsync(Host, Ct);

        var failed = Assert.IsType<IssueOutcome.Failed>(outcome);
        Assert.Equal(AcmeFailureClass.Terminal, failed.Class);
        Assert.Contains("cannot serve its own ACME challenge", failed.Detail);
        Assert.Equal(0, estate.Ca.ChallengesTriggered);
    }

    /// <summary>
    /// A renewal that fails leaves the certificate in place and the site up. Reporting the host as broken
    /// because a renewal attempt failed would send an operator looking for an outage that is not there.
    /// </summary>
    [Fact]
    public async Task AFailedRenewal_KeepsTheCertificateAndReportsTheError() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        await estate.Certificates.RenewNowAsync(Host, Ct);
        var served = estate.Store.Find(Host)!.Thumbprint;

        estate.Ca.FailValidationWith = (AcmeProblemTypes.Connection, "connection refused");
        await estate.Certificates.RenewNowAsync(Host, Ct);

        Assert.Equal(served, estate.Store.Find(Host)!.Thumbprint);
        var state = estate.State(Host);
        Assert.Equal("active", state.State);
        Assert.Contains("connection refused", state.LastError);
        Assert.Equal(1, state.ConsecutiveFailures);
    }

    /// <summary>Failures compound: the second attempt waits longer than the first.</summary>
    [Fact]
    public async Task ConsecutiveFailuresClimbTheLadder() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        // A transport failure rather than a validation one, so what moves is the ordinary ladder.
        estate.Ca.Offline = true;

        await estate.Certificates.RenewNowAsync(Host, Ct);
        var first = estate.State(Host).NextAttemptAt;
        await estate.Certificates.RenewNowAsync(Host, Ct);
        var second = estate.State(Host).NextAttemptAt;

        Assert.Equal(2, estate.State(Host).ConsecutiveFailures);
        Assert.True(second > first, $"second attempt at {second} was not later than the first at {first}");
    }
}
