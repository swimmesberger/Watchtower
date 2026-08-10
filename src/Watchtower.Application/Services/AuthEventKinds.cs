namespace Watchtower.Application.Services;

/// <summary>
/// The <see cref="Entities.AuthEvent.Kind"/> vocabulary of the access-control plane
/// (docs/central-auth/design.md §9) — dotted identifiers, shared by the two ends that write the trail:
/// the login endpoints in <c>Watchtower.Api</c> and the Users module in the application layer.
/// </summary>
/// <remarks>
/// Constants rather than literals because the trail is queried by kind: a typo on the writing side would
/// not fail anything, it would quietly produce a row that no audit view ever selects. Same reasoning as
/// <see cref="WatchtowerClaims"/>.
/// </remarks>
public static class AuthEventKinds {
    /// <summary>A successful password login.</summary>
    public const string LoginOk = "login.ok";

    /// <summary>A rejected login — unknown name, bad password, disabled or locked-out account alike.</summary>
    public const string LoginFailed = "login.failed";

    /// <summary>A global sign-out.</summary>
    public const string Logout = "logout";

    /// <summary>A signed-in visitor was refused an app they hold no grant for (and refused <c>redirect_uri</c> handovers).</summary>
    public const string AccessDenied = "access.denied";

    /// <summary>An administrator changed a route's access policy — its mode, bypass paths or the set of granted users.</summary>
    public const string RouteAccessChanged = "route.access.changed";

    /// <summary>An account was created by an administrator.</summary>
    public const string UserCreated = "user.created";

    /// <summary>An account was renamed, had its address changed, or gained/lost the Admin role.</summary>
    public const string UserUpdated = "user.updated";

    /// <summary>An account was deleted. The row survives it, so the name lives in <see cref="Entities.AuthEvent.Detail"/>.</summary>
    public const string UserDeleted = "user.deleted";

    /// <summary>An administrator set a new password, which also signs the account out everywhere.</summary>
    public const string UserPasswordReset = "user.password.reset";

    /// <summary>An account was suspended.</summary>
    public const string UserDisabled = "user.disabled";

    /// <summary>A suspended account was brought back.</summary>
    public const string UserEnabled = "user.enabled";

    /// <summary>
    /// The startup break-glass hook (<c>WATCHTOWER__AUTH__RESETPASSWORD</c>) reset the <c>admin</c>
    /// password or recreated the account (design.md §11). Recorded so an out-of-band recovery leaves a
    /// row in the trail rather than only a log line.
    /// </summary>
    public const string BreakGlass = "auth.breakglass";
}
