using Elarion.Abstractions.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Groups;

/// <summary>
/// The public projection of a <see cref="Group"/>. <paramref name="MemberCount"/> is derived per read
/// rather than stored — a denormalised counter would be one more thing that can disagree with the
/// membership rows, and the roster is operator-sized.
/// </summary>
public sealed record GroupDto(int Id, string Name, int MemberCount);

/// <summary>
/// The rules every write handler in this module shares: what a group name may be, and the audit trail.
/// </summary>
public static class GroupMapping {
    /// <summary>Longest accepted group name. Long enough for a readable label, short enough to stay a label.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Projects a group for the API.</summary>
    public static GroupDto ToDto(Group group, int memberCount) {
        ArgumentNullException.ThrowIfNull(group);
        return new GroupDto(group.Id, group.Name, memberCount);
    }

    /// <summary>The normalized form uniqueness is enforced on — the <see cref="User"/> precedent.</summary>
    public static string Normalize(string name) {
        ArgumentNullException.ThrowIfNull(name);
        return name.ToUpperInvariant();
    }

    /// <summary>
    /// Validates a submitted group name and yields its trimmed form, or the refusal.
    /// </summary>
    /// <remarks>
    /// This is the only place a group name enters the system, and it is deliberately strict, because a
    /// group name is not an internal label: it is forwarded verbatim to protected upstreams in the
    /// comma-joined <c>Remote-Groups</c>/<c>X-Auth-Request-Groups</c> header and in the JWT's
    /// <c>groups</c> claim, where a group-aware application maps it onto its own roles.
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>No comma.</b> The separator both ecosystems use. A name carrying one would be read downstream
    ///     as two memberships, so a group called <c>viewers,admins</c> would hand its members an
    ///     <c>admins</c> role nobody granted. Rejecting it at the source is the only place the rule can be
    ///     enforced once instead of at every forwarding site.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Printable ASCII only.</b> Control characters are header-injection material and non-ASCII has
    ///     no agreed encoding in an HTTP header value, so either would mean the name that reaches an
    ///     upstream is not the name an administrator typed — or, once the header-safety filter drops the
    ///     whole value, that one odd group silently costs the account every other group it is in.
    ///   </description></item>
    /// </list>
    /// </remarks>
    /// <param name="name">The submitted name.</param>
    /// <param name="trimmed">The name to persist when the result is <see langword="null"/>.</param>
    /// <returns>The refusal, or <see langword="null"/> when the name is acceptable.</returns>
    public static AppError? ValidateName(string? name, out string trimmed) {
        trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return AppError.Validation("Group name is required.");
        if (trimmed.Length > MaxNameLength)
            return AppError.Validation($"Group name must be at most {MaxNameLength} characters.");

        foreach (var c in trimmed) {
            if (c == ',')
                return AppError.Validation(
                    "Group name must not contain a comma — group names are forwarded to applications as a " +
                    "comma-separated list.");
            if (c is < ' ' or > '~')
                return AppError.Validation(
                    "Group name must use printable ASCII characters only — it is forwarded to applications " +
                    "in an HTTP header.");
        }

        return null;
    }

    /// <summary>
    /// Appends an <see cref="AuthEvent"/> for a group change and commits it. Kinds come from
    /// <see cref="Services.AuthEventKinds"/>.
    /// </summary>
    /// <remarks>
    /// The same post-commit discipline the Users and Proxy access handlers use: no
    /// <see cref="CancellationToken"/> is taken and the save runs with <see cref="CancellationToken.None"/>,
    /// because every call site runs after the change has already committed — honouring the request token
    /// here would let a caller that hangs up keep its own administrative act out of the trail.
    /// <para>
    /// <see cref="AuthEvent.UserId"/> is left null even though the subject is an account set: the column
    /// means "the account this event is about", the actor may be the implicit local administrator (which is
    /// no row at all — design.md §2.6), and the group is named in <see cref="AuthEvent.Detail"/> so the row
    /// survives the group being deleted.
    /// </para>
    /// </remarks>
    public static async Task RecordAsync(
        WatchtowerDbContext db,
        ICurrentUser actor,
        TimeProvider time,
        string kind,
        int groupId,
        string groupName,
        string? details = null) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(time);

        var actorId = string.IsNullOrEmpty(actor.UserId) ? "unknown" : actor.UserId;
        var detail = $"actor={actorId}; group={groupName}#{groupId}";
        if (!string.IsNullOrEmpty(details)) detail = $"{detail}; {details}";

        db.AuthEvents.Add(new AuthEvent {
            Kind = kind,
            // Group administration is neither account- nor route-scoped; stated rather than defaulted.
            UserId = null,
            RouteId = null,
            Detail = detail,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
