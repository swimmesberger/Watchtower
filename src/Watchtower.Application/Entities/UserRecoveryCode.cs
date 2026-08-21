namespace Watchtower.Application.Entities;

/// <summary>
/// One unused two-factor recovery code of a <see cref="User"/> — the way back in when the authenticator
/// app is on a phone that is lost, wiped or simply not to hand.
/// </summary>
/// <remarks>
/// Only the SHA-256 hash of the code is stored, exactly as session tokens and login codes are
/// (<see cref="Services.AuthSessionService.HashToken"/>): a database read must not yield a credential that
/// can be replayed against the login endpoint. The codes are therefore shown to their owner exactly once,
/// when they are generated, and can never be shown again.
/// <para>
/// Redemption <em>deletes</em> the row rather than flagging it, which is what makes a code single-use under
/// concurrency: the delete's affected-row count is the claim, so two simultaneous logins with the same code
/// produce one winner — the same mechanism as
/// <see cref="Services.AuthSessionService.RedeemLoginCodeAsync"/>. A spent code leaving no trace is
/// deliberate: how many remain is the only fact anyone needs, and that is a <c>COUNT</c>.
/// </para>
/// </remarks>
public sealed class UserRecoveryCode {
    public int Id { get; set; }

    /// <summary>The account the code belongs to. Deleting the account takes its codes with it.</summary>
    public int UserId { get; set; }

    /// <inheritdoc cref="UserId"/>
    public User? User { get; set; }

    /// <summary>SHA-256 hash of the code as it was shown to the owner. Never the code itself.</summary>
    public required string CodeHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
