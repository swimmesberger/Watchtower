using System.Text.RegularExpressions;
using Elarion.Abstractions.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Realms;

/// <summary>
/// The public projection of a <see cref="Realm"/>. The counts are derived per read rather than stored —
/// they exist so an administrator can see what a realm is holding before trying to delete it, and a
/// denormalised counter would be one more thing that can disagree with the rows it claims to count.
/// </summary>
/// <param name="IsSystem">
/// True for the built-in operator realm: it cannot be deleted, its slug cannot change, and it is the only
/// realm that falls back to the configured <c>Auth:Host</c> when it has no login route.
/// </param>
/// <param name="LoginRouteId">
/// The <see cref="RouteTarget.Watchtower"/> route this realm's login page is served on (ADR-0023), or null
/// when it has none yet.
/// </param>
/// <param name="LoginHost">
/// The effective login host: the login route's domain, or — on the system realm alone — the configured
/// <c>Auth:Host</c>. Null when the realm's protected apps have nowhere to redirect anonymous visitors.
/// </param>
public sealed record RealmDto(
    int Id,
    string Name,
    string Slug,
    bool IsSystem,
    int UserCount,
    int GroupCount,
    int TemplateCount,
    DateTimeOffset CreatedAt,
    int? LoginRouteId,
    string? LoginHost);

/// <summary>
/// The rules every write handler in this module shares: what a realm's name, slug and login domain may
/// be, and the audit trail.
/// </summary>
public static partial class RealmMapping {
    /// <summary>Longest accepted login host — the DNS limit for a fully-qualified name.</summary>
    public const int MaxLoginHostLength = 253;

    /// <summary>
    /// Lowercase DNS-label-ish shape: alphanumerics, single hyphens between them, never leading or
    /// trailing. The same reading <c>TenancyMapping</c> applies to a tenant slug, and for the same reason —
    /// a slug ends up in places (a claim value, a future host label) where anything else would need
    /// escaping.
    /// </summary>
    [GeneratedRegex("^[a-z0-9](?:-?[a-z0-9])*$")]
    private static partial Regex SlugPattern();

    /// <summary>
    /// Projects a realm for the API. <paramref name="loginHost"/> is resolved by
    /// <see cref="RealmResolver.LoginHostForAsync"/> — the caller passes it in so the DTO cannot invent a
    /// second reading of "where does this population log in".
    /// </summary>
    public static RealmDto ToDto(
        Realm realm, int userCount, int groupCount, int templateCount, string? loginHost) {
        ArgumentNullException.ThrowIfNull(realm);
        return new RealmDto(
            realm.Id, realm.Name, realm.Slug, realm.IsSystem,
            userCount, groupCount, templateCount, realm.CreatedAt,
            realm.LoginRouteId, loginHost);
    }

    /// <summary>Validates a submitted display name and yields its trimmed form, or the refusal.</summary>
    public static AppError? ValidateName(string? name, out string trimmed) {
        trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return AppError.Validation("Realm name is required.");
        if (trimmed.Length > Realm.MaxNameLength)
            return AppError.Validation($"Realm name must be at most {Realm.MaxNameLength} characters.");
        return null;
    }

    /// <summary>
    /// Validates a submitted slug and yields its normalised form, or the refusal.
    /// </summary>
    /// <remarks>
    /// Deliberately not lowercased for the caller: a slug is immutable once created and travels in every
    /// assertion's <c>realm</c> claim, so silently accepting <c>ACME</c> and storing <c>acme</c> would mean
    /// an administrator's records disagree with what their applications receive, permanently. Trimming is
    /// the only correction made.
    /// </remarks>
    public static AppError? ValidateSlug(string? slug, out string trimmed) {
        trimmed = slug?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return AppError.Validation("Realm slug is required.");
        if (trimmed.Length > Realm.MaxSlugLength)
            return AppError.Validation($"Realm slug must be at most {Realm.MaxSlugLength} characters.");
        if (!SlugPattern().IsMatch(trimmed))
            return AppError.Validation(
                "Realm slug must be lowercase letters, digits and single hyphens, starting and ending " +
                "with a letter or digit.");
        return null;
    }

    /// <summary>
    /// Validates a submitted login domain and yields its normalised (lowercased) form, or the refusal.
    /// A blank value yields <see langword="null"/> — a realm may legitimately exist before its DNS does.
    /// </summary>
    /// <remarks>
    /// Delegated to <see cref="Services.Acme.DesiredHosts.TryNormalize"/>, which is what
    /// <c>proxy.createRoute</c> applies to the very same string: a login domain <em>is</em> a route domain
    /// since ADR-0023, so accepting one here that the route handler would refuse would only produce a
    /// refusal one call later.
    /// </remarks>
    public static AppError? ValidateLoginDomain(string? loginDomain, out string? normalized) {
        normalized = null;
        var trimmed = loginDomain?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        if (trimmed.Length > MaxLoginHostLength)
            return AppError.Validation($"Login host must be at most {MaxLoginHostLength} characters.");
        if (!Services.Acme.DesiredHosts.TryNormalize(trimmed, out var host, out var reason))
            return AppError.Validation(reason);

        normalized = host;
        return null;
    }

    /// <summary>
    /// Appends an <see cref="AuthEvent"/> for a realm change and commits it. Kinds come from
    /// <see cref="AuthEventKinds"/>.
    /// </summary>
    /// <remarks>
    /// The same post-commit discipline the Users and Groups handlers use: no <see cref="CancellationToken"/>
    /// is taken and the save runs with <see cref="CancellationToken.None"/>, because every call site runs
    /// after the change has already committed — honouring the request token here would let a caller that
    /// hangs up keep its own administrative act out of the trail.
    /// <para>
    /// <see cref="AuthEvent.UserId"/> is left null: the subject is a population, the actor may be the
    /// implicit local administrator (which is no row at all — design.md §2.6), and the realm is named in
    /// <see cref="AuthEvent.Detail"/> so the row survives the realm being deleted.
    /// </para>
    /// </remarks>
    public static async Task RecordAsync(
        WatchtowerDbContext db,
        ICurrentUser actor,
        TimeProvider time,
        string kind,
        int realmId,
        string slug,
        string? details = null) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(time);

        var actorId = string.IsNullOrEmpty(actor.UserId) ? "unknown" : actor.UserId;
        var detail = $"actor={actorId}; realm={slug}#{realmId}";
        if (!string.IsNullOrEmpty(details)) detail = $"{detail}; {details}";

        db.AuditEvents.Add(new AuditEvent {
            Category = AuthEventKinds.CategoryOf(kind),
            Action = kind,
            Target = slug,
            Detail = detail,
            Actor = await AuditLog.ResolveActorAsync(db, actor.UserId),
            Success = true,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
