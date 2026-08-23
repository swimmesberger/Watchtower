using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The scheduling arithmetic, which is where the CA's rate limits are actually respected. Pure, so the
/// cases worth pinning — a six-hour certificate from a test CA, a host that has failed eight times, a
/// rate limit with a <c>Retry-After</c> — are all one function call.
/// </summary>
public sealed class CertificateRenewalPolicyTests {
    private static readonly DateTimeOffset Issued = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Let's Encrypt's 90 days: renewal opens at day 60, a third of the lifetime out.</summary>
    [Fact]
    public void ANinetyDayCertificate_IsDueWithThirtyDaysLeft() {
        var expires = Issued.AddDays(90);

        Assert.False(CertificateRenewalPolicy.IsRenewalDue(Issued.AddDays(59), Issued, expires));
        Assert.True(CertificateRenewalPolicy.IsRenewalDue(Issued.AddDays(60), Issued, expires));
        Assert.Equal(Issued.AddDays(60), CertificateRenewalPolicy.RenewalDueAt(Issued, expires));
    }

    /// <summary>
    /// A one-day certificate — what step-ca issues by default, and where a fixed 30-day lead would say
    /// "renew" from the moment of issuance and never stop. A third of a day is eight hours, so renewal
    /// opens with eight hours of life left.
    /// </summary>
    [Fact]
    public void AOneDayCertificate_IsDueWithEightHoursLeft() {
        var expires = Issued.AddHours(24);

        Assert.False(CertificateRenewalPolicy.IsRenewalDue(Issued.AddHours(15), Issued, expires));
        Assert.True(CertificateRenewalPolicy.IsRenewalDue(Issued.AddHours(16), Issued, expires));
        Assert.Equal(expires.AddHours(-8), CertificateRenewalPolicy.RenewalDueAt(Issued, expires));
    }

    /// <summary>
    /// The rule stays proportional however short the certificate is. An absolute floor — "always renew
    /// with at least a day left" — would put the renewal instant before issuance for anything shorter
    /// than three days, so every pass would find it due and the deployment would renew in a loop.
    /// </summary>
    [Fact]
    public void AVeryShortCertificate_StaysProportional() {
        var expires = Issued.AddHours(6);

        Assert.Equal(Issued.AddHours(4), CertificateRenewalPolicy.RenewalDueAt(Issued, expires));
        Assert.False(CertificateRenewalPolicy.IsRenewalDue(Issued, Issued, expires));
        Assert.False(CertificateRenewalPolicy.IsRenewalDue(Issued.AddHours(3), Issued, expires));
        Assert.True(CertificateRenewalPolicy.IsRenewalDue(Issued.AddHours(4), Issued, expires));
    }

    [Fact]
    public void TheLadderIsMonotonic_AndClampsAtADay() {
        var previous = TimeSpan.Zero;
        for (var failures = 1; failures <= 8; failures++) {
            var backoff = CertificateRenewalPolicy.BackoffFor(failures, AcmeFailureClass.Transport, null);
            Assert.True(backoff > previous, $"rung {failures} did not grow");
            previous = backoff;
        }
        Assert.Equal(TimeSpan.FromMinutes(1), CertificateRenewalPolicy.BackoffFor(1, AcmeFailureClass.Transport, null));
        Assert.Equal(TimeSpan.FromHours(24), CertificateRenewalPolicy.BackoffFor(8, AcmeFailureClass.Transport, null));
        // Beyond the ladder it stays at a day rather than growing without bound.
        Assert.Equal(TimeSpan.FromHours(24), CertificateRenewalPolicy.BackoffFor(50, AcmeFailureClass.Transport, null));
        // A zero or negative count is a caller bug, not a reason to retry instantly forever.
        Assert.Equal(TimeSpan.FromMinutes(1), CertificateRenewalPolicy.BackoffFor(0, AcmeFailureClass.Transport, null));
    }

    /// <summary>
    /// Let's Encrypt allows five failed validations per hostname per hour. Starting a validation failure
    /// at the one-minute rung would burn all five inside six minutes.
    /// </summary>
    [Fact]
    public void AValidationFailure_StartsAtFifteenMinutes() {
        Assert.Equal(
            TimeSpan.FromMinutes(15), CertificateRenewalPolicy.BackoffFor(1, AcmeFailureClass.Validation, null));
        Assert.Equal(
            TimeSpan.FromHours(1), CertificateRenewalPolicy.BackoffFor(2, AcmeFailureClass.Validation, null));
    }

    /// <summary>
    /// A terminal failure goes straight to the top rung. A name the CA refuses, terms that need
    /// accepting, or a listener that cannot answer its own challenge will say exactly the same thing in
    /// sixty seconds — and each attempt in between spends budget the operator will want once they have
    /// fixed the cause.
    /// </summary>
    [Fact]
    public void ATerminalFailure_WaitsADay_FromTheFirstOne() {
        Assert.Equal(
            TimeSpan.FromHours(24), CertificateRenewalPolicy.BackoffFor(1, AcmeFailureClass.Terminal, null));
        Assert.Equal(
            TimeSpan.FromHours(24), CertificateRenewalPolicy.BackoffFor(5, AcmeFailureClass.Terminal, null));
        // Still yields to a CA that asked for longer.
        Assert.Equal(
            TimeSpan.FromDays(3),
            CertificateRenewalPolicy.BackoffFor(1, AcmeFailureClass.Terminal, TimeSpan.FromDays(3)));
    }

    [Fact]
    public void ARateLimit_WaitsADay_EvenOnTheFirstFailure() {
        Assert.Equal(
            TimeSpan.FromHours(24), CertificateRenewalPolicy.BackoffFor(1, AcmeFailureClass.RateLimited, null));
    }

    /// <summary>A CA that names a time is stating a fact about its own limiter; it always wins upward.</summary>
    [Fact]
    public void ALongerRetryAfterWins_AndAShorterOneDoesNot() {
        Assert.Equal(
            TimeSpan.FromHours(36),
            CertificateRenewalPolicy.BackoffFor(1, AcmeFailureClass.RateLimited, TimeSpan.FromHours(36)));
        Assert.Equal(
            TimeSpan.FromHours(24),
            CertificateRenewalPolicy.BackoffFor(1, AcmeFailureClass.RateLimited, TimeSpan.FromMinutes(5)));
        Assert.Equal(
            TimeSpan.FromMinutes(30),
            CertificateRenewalPolicy.BackoffFor(1, AcmeFailureClass.Transport, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void JitterStaysWithinTwentyPercent_AndIsStablePerHost() {
        var target = Issued;
        var window = TimeSpan.FromHours(12);

        foreach (var host in new[] { "a.test", "b.test", "very-long-name.example.invalid", "" }) {
            var offset = CertificateRenewalPolicy.ApplyJitter(target, window, host) - target;
            Assert.InRange(offset, -0.2 * window, 0.2 * window);
        }

        // Stable across calls — the whole point is that a restart does not reshuffle the schedule.
        Assert.Equal(
            CertificateRenewalPolicy.ApplyJitter(target, window, "a.test"),
            CertificateRenewalPolicy.ApplyJitter(target, window, "a.test"));
    }

    /// <summary>Different hosts get different offsets, which is what actually breaks up the bunching.</summary>
    [Fact]
    public void JitterSpreadsHostsApart() {
        var window = TimeSpan.FromHours(12);
        var offsets = new[] { "a.test", "b.test", "c.test", "d.test", "e.test" }
            .Select(h => CertificateRenewalPolicy.ApplyJitter(Issued, window, h))
            .Distinct()
            .Count();
        Assert.Equal(5, offsets);
    }

    [Fact]
    public void AZeroWindowIsANoOp() {
        Assert.Equal(Issued, CertificateRenewalPolicy.ApplyJitter(Issued, TimeSpan.Zero, "a.test"));
    }
}
