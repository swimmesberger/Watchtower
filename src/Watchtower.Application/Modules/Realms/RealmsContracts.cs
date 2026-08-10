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
/// True for the built-in operator realm: it cannot be deleted, its slug cannot change, and its login host
/// is the configured <c>Auth:Host</c> rather than <paramref name="AuthHost"/> (which stays null).
/// </param>
public sealed record RealmDto(
    int Id,
    string Name,
    string Slug,
    string? AuthHost,
    bool IsSystem,
    int UserCount,
    int GroupCount,
    int TemplateCount,
    DateTimeOffset CreatedAt);

/// <summary>
/// The rules every write handler in this module shares: what a realm's name, slug and login host may be,
/// and the audit trail.
/// </summary>
public static partial class RealmMapping {
    /// <summary>Longest accepted login host — the DNS limit for a fully-qualified name.</summary>
    public const int MaxAuthHostLength = 253;

    /// <summary>
    /// Lowercase DNS-label-ish shape: alphanumerics, single hyphens between them, never leading or
    /// trailing. The same reading <c>TenancyMapping</c> applies to a tenant slug, and for the same reason —
    /// a slug ends up in places (a claim value, a future host label) where anything else would need
    /// escaping.
    /// </summary>
    [GeneratedRegex("^[a-z0-9](?:-?[a-z0-9])*$")]
    private static partial Regex SlugPattern();

    /// <summary>Projects a realm for the API.</summary>
    public static RealmDto ToDto(Realm realm, int userCount, int groupCount, int templateCount) {
        ArgumentNullException.ThrowIfNull(realm);
        return new RealmDto(
            realm.Id, realm.Name, realm.Slug, realm.AuthHost, realm.IsSystem,
            userCount, groupCount, templateCount, realm.CreatedAt);
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
    /// Validates a submitted login host and yields its normalised (lowercased) form, or the refusal.
    /// A blank value yields <see langword="null"/> — a realm may legitimately exist before its DNS does.
    /// </summary>
    /// <remarks>
    /// A host and nothing else: no scheme, port, path, userinfo or whitespace. The value decides which
    /// population a visitor arriving on that host authenticates into and becomes the <c>iss</c> of every
    /// assertion the realm's apps receive, so a value that is not exactly a host name would be a value two
    /// different parsers could read two different ways. The shape is checked by round-tripping through the
    /// URI parser and requiring the result to equal the input: anything the parser had to <em>correct</em>
    /// is rejected rather than accepted in its corrected form.
    /// </remarks>
    public static AppError? ValidateAuthHost(string? authHost, out string? normalized) {
        normalized = null;
        var trimmed = authHost?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        if (trimmed.Length > MaxAuthHostLength)
            return AppError.Validation($"Auth host must be at most {MaxAuthHostLength} characters.");

        var lowered = trimmed.ToLowerInvariant();
        var parsed = RouteAccessPolicy.NormalizeForwardedHost(lowered);
        // NormalizeForwardedHost drops a port and takes the first entry of a comma-separated list, both of
        // which are the right thing for an attacker-supplied header and the wrong thing for a stored
        // setting — so the result has to come back unchanged, not merely non-null.
        if (parsed is null || !string.Equals(parsed, lowered, StringComparison.Ordinal))
            return AppError.Validation(
                $"'{trimmed}' is not a host name. Give the bare hostname the realm's login page is " +
                "served on, with no scheme, port or path.");

        normalized = parsed;
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

        db.AuthEvents.Add(new AuthEvent {
            Kind = kind,
            UserId = null,
            RouteId = null,
            Detail = detail,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
