using System.Security.Cryptography;
using System.Text;

namespace Watchtower.Application.Services.Acme;

/// <summary>
/// What went wrong badly enough to change when the next attempt happens. The classes differ in what an
/// operator can do about them, which is exactly what should decide how hard Watchtower retries.
/// </summary>
public enum AcmeFailureClass {
    /// <summary>The CA could not be reached, or answered something unparseable. Usually transient.</summary>
    Transport,

    /// <summary>
    /// The CA reached the challenge and was not satisfied — DNS points elsewhere, a firewall is in the
    /// way, another server answered. Retrying quickly cannot help; the operator has to change something.
    /// </summary>
    Validation,

    /// <summary>The CA's rate limit. Retrying is actively harmful, and it told us when to come back.</summary>
    RateLimited,

    /// <summary>
    /// Nothing about repeating this soon will change it — the CA refuses the name, requires the operator
    /// to accept new terms, rejected what we sent as malformed, or the listener cannot answer its own
    /// challenge. Still retried, because the underlying cause is usually something an operator fixes
    /// without touching Watchtower, but straight onto the longest rung.
    /// </summary>
    Terminal,
}

/// <summary>
/// When to renew and when to try again — ADR-0017 (forthcoming). Pure and static: every decision here is
/// a function of the clock and a count, so the interesting cases (a 24-hour certificate from a test CA, a
/// rate-limited account, a host that has failed six times) are testable without a CA or a background loop.
/// </summary>
public static class CertificateRenewalPolicy {
    /// <summary>
    /// The backoff rungs, one per consecutive failure. Starts short because most first failures are a
    /// container that came up before its network did, and ends at a day because past that point the
    /// problem is something an operator has to fix and polling faster only fills the log.
    /// </summary>
    private static readonly TimeSpan[] Ladder = [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
        TimeSpan.FromHours(24),
    ];

    /// <summary>
    /// Where a <see cref="AcmeFailureClass.Validation"/> failure starts on the ladder. A minute is the
    /// right first retry for a network blip and the wrong one for "your DNS does not point here": Let's
    /// Encrypt allows five failed validations per hostname per hour, so a validation failure retried on
    /// the fast rungs would exhaust that budget before the operator has finished reading the error.
    /// </summary>
    private const int ValidationStartIndex = 2;

    /// <summary>
    /// How long a <see cref="AcmeFailureClass.Terminal"/> failure waits: the longest rung, from the very
    /// first one. There is no fast rung worth trying — a name the CA refuses, terms that need accepting,
    /// or a listener that cannot serve its own challenge will answer exactly the same in sixty seconds,
    /// and every attempt in between spends budget an operator will need once they have fixed it.
    /// </summary>
    private static TimeSpan TerminalBackoff => Ladder[^1];

    /// <summary>Whether a certificate should be renewed now: once it is into the last third of its life.</summary>
    /// <remarks>
    /// A third rather than a fixed 30 days because the lifetime is not ours to assume — Let's Encrypt
    /// issues 90 days today and has short-lived 6-day certificates in preview, and an internal CA may
    /// issue for 24 hours. A fraction gives every one of those the same proportional slack: two full
    /// renewal windows' worth of retries before anything is served expired.
    /// <para>
    /// Deliberately <em>no</em> absolute floor on the lead time. One is tempting — a day of runway is
    /// what the retry ladder wants — but a floor longer than a third of the lifetime puts the renewal
    /// instant before the certificate was even issued, so every pass finds it due and the deployment
    /// renews a short-lived certificate in a loop. Proportional is the only rule that is stable at both
    /// ends.
    /// </para>
    /// </remarks>
    public static bool IsRenewalDue(DateTimeOffset now, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        now >= RenewalDueAt(notBefore, notAfter);

    /// <summary>
    /// The instant <see cref="IsRenewalDue"/> starts saying yes. Exposed separately so a successful
    /// issuance can schedule its own renewal — and jitter it — instead of every certificate in the
    /// deployment coming due on the same five-minute tick.
    /// </summary>
    public static DateTimeOffset RenewalDueAt(DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        notAfter - (notAfter - notBefore) / 3;

    /// <summary>
    /// How long to wait before attempting <paramref name="consecutiveFailures"/>+1 for a host.
    /// </summary>
    /// <param name="retryAfter">
    /// What the CA asked for, when it said. Always honoured when it is longer than the rung: a CA that
    /// names a time is stating a fact about its own rate limiter, and second-guessing it downward is how
    /// an account gets locked out for longer.
    /// </param>
    public static TimeSpan BackoffFor(int consecutiveFailures, AcmeFailureClass cls, TimeSpan? retryAfter) {
        var index = Math.Max(0, consecutiveFailures - 1);
        if (cls == AcmeFailureClass.Validation) index += ValidationStartIndex;
        var rung = cls switch {
            // A rate limit is a day, minimum: the shortest window any of the CAs enforce is measured in
            // hours, and the failure itself consumed part of the budget.
            AcmeFailureClass.RateLimited => Ladder[^1],
            AcmeFailureClass.Terminal => TerminalBackoff,
            _ => Ladder[Math.Clamp(index, 0, Ladder.Length - 1)],
        };

        return retryAfter is { } asked && asked > rung ? asked : rung;
    }

    /// <summary>
    /// Spreads a scheduled attempt by ±20%, deterministically per host.
    /// </summary>
    /// <remarks>
    /// Renewals bunch by construction — every certificate issued during a first start comes due in the
    /// same hour three months later — and a deployment with fifty routes would then open fifty orders at
    /// once against a CA that rate-limits per account. Jitter is what breaks that up.
    /// <para>
    /// Derived from a stable hash of the host rather than a random number, so a restart does not reshuffle
    /// every schedule (which would defeat the point across restarts) and a test can assert an exact
    /// instant. SHA-256 and not <see cref="string.GetHashCode()"/>: the latter is seeded per process, so
    /// it is randomized across restarts by design — the one property this must not have. (A digest for a
    /// non-cryptographic purpose, rather than a NuGet dependency on a faster hash for one line.)
    /// </para>
    /// </remarks>
    public static DateTimeOffset ApplyJitter(DateTimeOffset target, TimeSpan window, string seedHost) {
        if (window <= TimeSpan.Zero) return target;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seedHost ?? ""));
        var hash = BitConverter.ToUInt32(digest, 0);
        // [0, 1) → [-0.2, +0.2)
        var fraction = hash / (double)uint.MaxValue * 0.4 - 0.2;
        return target + window * fraction;
    }
}
