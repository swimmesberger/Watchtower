using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Realms.Handlers;

/// <summary>
/// Creates an empty realm. It holds nobody until accounts are created in it and reaches nothing until a
/// category is placed in it, so creating one grants no access by itself.
/// </summary>
/// <remarks>
/// <paramref name="AuthHost"/> is optional because DNS usually is not ready yet: a realm without one is a
/// perfectly good population that simply cannot be logged into, and its protected routes fail closed at
/// challenge time until it has one (see <c>WatchtowerAccessEndpoints</c>).
/// <para>
/// The uniqueness checks are reads followed by an insert, and the unique indexes on <c>realms.slug</c> and
/// <c>realms.auth_host</c> are what actually settle a race between two administrators — the checks exist to
/// turn the common case into a clear <c>Conflict</c> rather than a database exception.
/// </para>
/// </remarks>
[Handler("realms.create")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class CreateRealm(
    WatchtowerDbContext db,
    IOptionsMonitor<WatchtowerOptions> options,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<CreateRealm.Command, Result<CreateRealm.Response>> {

    public sealed record Command(string Name, string Slug, string? AuthHost = null);
    public sealed record Response(RealmDto Realm);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (RealmMapping.ValidateName(command.Name, out var name) is { } badName)
            return badName;
        if (RealmMapping.ValidateSlug(command.Slug, out var slug) is { } badSlug)
            return badSlug;
        if (RealmMapping.ValidateAuthHost(command.AuthHost, out var authHost) is { } badHost)
            return badHost;

        if (await db.Realms.AsNoTracking().AnyAsync(r => r.Slug == slug, ct))
            return AppError.Conflict($"A realm with the slug '{slug}' already exists.");

        if (authHost is not null) {
            if (RealmMapping.ValidateAuthHost(options.CurrentValue.Auth.Host, out var operatorHost) is null &&
                string.Equals(authHost, operatorHost, StringComparison.Ordinal)) {
                // The operator login page is served on that host and is never gated; handing it to a realm
                // as well would make one host resolve to two populations, and the one it resolved to would
                // decide who can administer the instance.
                return AppError.Validation(
                    $"'{authHost}' is the Watchtower login host and cannot also be a realm's auth host.");
            }
            if (await db.Realms.AsNoTracking().AnyAsync(r => r.AuthHost == authHost, ct))
                return AppError.Conflict($"Another realm already uses the auth host '{authHost}'.");
        }

        var realm = new Realm {
            Name = name,
            Slug = slug,
            AuthHost = authHost,
            // Never settable from a request: the system realm is the one the migration seeded, and a second
            // one would make "the operator population" ambiguous.
            IsSystem = false,
            CreatedAt = time.GetUtcNow(),
        };
        db.Realms.Add(realm);
        await db.SaveChangesAsync(ct);

        // Past the commit point: the realm exists, so the trail is written uncancellably.
        await RealmMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.RealmCreated, realm.Id, realm.Slug,
            $"authHost={authHost ?? "(none)"}");

        return new Response(RealmMapping.ToDto(realm, userCount: 0, groupCount: 0, templateCount: 0));
    }
}
