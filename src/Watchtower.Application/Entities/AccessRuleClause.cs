namespace Watchtower.Application.Entities;

/// <summary>
/// What one <see cref="AccessRuleClause"/> names (ADR-0039 decision 3). The vocabulary is deliberately
/// short, and exactly one member — <see cref="ExternalPolicy"/> — is bound to a particular edge; which
/// enforcement points can honour each is declared once in <c>AccessClauseSupport</c>.
/// </summary>
public enum AccessClauseKind {
    /// <summary>
    /// A single Watchtower account, by id. Admits that account in process; projects to its email address
    /// at the edge, so an account without one contributes nothing there.
    /// </summary>
    User,

    /// <summary>
    /// A whole <see cref="Group"/>, by id — the portable subject set. Membership is evaluated per request
    /// in process and flattened to member emails at reconcile time at the edge, so a removal takes effect
    /// immediately on the one and at the next reconcile on the other.
    /// </summary>
    Group,

    /// <summary>
    /// One email address, verbatim. <b>Edge-only by design, not for lack of code:</b> Cloudflare's identity
    /// provider verifies an address before asserting it, while <see cref="User.Email"/> is nullable and
    /// carries no unique index — so matching a Watchtower session on it would make an unverified,
    /// non-unique column an authorization key.
    /// </summary>
    Email,

    /// <summary>
    /// Every address at one domain (<c>example.com</c>). Edge-only for the same reason as
    /// <see cref="Email"/>.
    /// </summary>
    EmailDomain,

    /// <summary>
    /// A subject set the <em>provider</em> owns, referenced by its id — a Cloudflare Zero Trust
    /// <b>Access group</b>, which is the natural fit when the allow-list already lives there (a group of
    /// Entra ID users, say). Opaque like <see cref="ExternalPolicy"/>, and the per-route spelling of the
    /// instance-wide <c>AccessGroupIds</c> setting.
    /// </summary>
    ExternalGroup,

    /// <summary>
    /// An allow-list the <em>provider</em> owns, referenced by its id — a Cloudflare reusable Access policy
    /// maintained in the dashboard. Opaque by nature: Watchtower attaches it and never reads, writes or
    /// evaluates it, which is why no in-process path can honour it.
    /// </summary>
    ExternalPolicy,
}

/// <summary>
/// One predicate inside an <see cref="AccessRule"/> — a single answer to "who gets in?". A rule admits the
/// union of its clauses.
/// </summary>
/// <remarks>
/// The subject columns are separate rather than one polymorphic string because two of the kinds are
/// foreign keys and must behave like it: deleting a group or an account removes the clauses that named it
/// (cascade), the same way it revokes a <see cref="RouteAccessGrant"/>. Which column a row may use is
/// decided by <see cref="Kind"/> and enforced by a CHECK constraint rather than only by the handlers — a
/// row whose kind and columns disagree would be a clause whose meaning depends on which column a reader
/// consults first, and this table decides who reaches a hostname.
/// </remarks>
public sealed class AccessRuleClause {
    public int Id { get; set; }

    public int AccessRuleId { get; set; }
    public AccessRule? AccessRule { get; set; }

    /// <summary>Which of the columns below carries this clause's subject.</summary>
    public AccessClauseKind Kind { get; set; }

    /// <summary>The named account, set iff <see cref="Kind"/> is <see cref="AccessClauseKind.User"/>.</summary>
    public int? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>The named group, set iff <see cref="Kind"/> is <see cref="AccessClauseKind.Group"/>.</summary>
    public int? GroupId { get; set; }
    public Group? Group { get; set; }

    /// <summary>
    /// The literal subject — an email address, an email domain or an external policy id — set iff
    /// <see cref="Kind"/> is one of <see cref="AccessClauseKind.Email"/>,
    /// <see cref="AccessClauseKind.EmailDomain"/> or <see cref="AccessClauseKind.ExternalPolicy"/>.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Position within the rule, so a rule renders and projects in a stable order. It carries no
    /// precedence of its own — a rule admits the union of its clauses, and ordering between <em>rules</em>
    /// is what <see cref="RouteAccessRule.Order"/> decides.
    /// </summary>
    public int Order { get; set; }
}
