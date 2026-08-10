using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The one way an account's group names are read for forwarding (docs/central-auth/design.md §2.3). Both
/// channels that carry them — the signed JWT's <c>groups</c> claim and the plaintext
/// <c>Remote-Groups</c>/<c>X-Auth-Request-Groups</c> header — are fed from a single call per request, so
/// an upstream can never see one list in the assertion and a different one in the header.
/// </summary>
public static class GroupMembership {
    /// <summary>
    /// The names of the groups <paramref name="userId"/> belongs to, sorted ordinal, or an empty list when
    /// it belongs to none.
    /// </summary>
    /// <remarks>
    /// The sort is applied in memory rather than in SQL: the ordering is part of what protected apps see,
    /// and a database collation is a per-provider decision that must not be able to change it. The list is
    /// an operator-sized roster, so materialising it first costs nothing.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> NamesAsync(
        WatchtowerDbContext db, int userId, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);

        var names = await db.GroupMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.Group!.Name)
            .ToListAsync(ct);

        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
