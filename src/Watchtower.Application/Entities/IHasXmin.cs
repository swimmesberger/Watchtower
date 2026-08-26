namespace Watchtower.Application.Entities;

/// <summary>
/// An entity whose optimistic-concurrency token is PostgreSQL's <c>xmin</c> system column
/// (ADR-0024 decision 3), carried as a <em>real</em> property on the entity rather than as an EF shadow
/// property.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a real property, when a shadow one looks tidier.</b> <c>xmin</c> is the database's own
/// bookkeeping — the id of the transaction that last wrote the row — so the instinct is to keep it out
/// of the domain model entirely. That instinct is what the Npgsql provider itself abandoned when it
/// removed <c>UseXminAsConcurrencyToken</c> (npgsql/efcore.pg#3539): a shadow property lives in the
/// <em>change tracker</em>, not on the object, so the value is lost the moment the entity leaves its
/// context. Anything that reads detached, mutates and attaches back therefore fails its next
/// <c>SaveChanges</c> as a phantom conflict — the token EF compares is default(uint), which matches no
/// row — and anything that serializes the entity drops the token silently.
/// </para>
/// <para>
/// Watchtower met that trap twice and worked around it both times: <c>User</c> deliberately carries
/// Identity's <c>ConcurrencyStamp</c> instead of <c>xmin</c> because <c>WatchtowerUserStore</c> is an
/// attach-based store, and <c>CiRepo</c> was left tokenless so <c>CiToolchainRecorder</c> could attach
/// a no-tracking read. As a real property the value travels with the instance, so both of those are
/// choices rather than constraints.
/// </para>
/// <para>
/// <b>Reading it is harmless; writing it is not possible.</b> The setter is private, so application
/// code cannot invent a token — the value only ever arrives from the database, through EF's
/// <c>ValueGeneratedOnAddOrUpdate</c> mapping. It is deliberately <em>not</em> on any DTO: entities
/// reach the wire only through explicit projections, and a transaction id is instance-local noise to
/// every consumer.
/// </para>
/// <para>
/// The mapping itself stays in one place — <c>XminConcurrency.UseXminAsConcurrencyToken</c>, whose
/// generic constraint is this interface, so an entity configured with the helper but missing the
/// property is a compile error rather than a shadow property nobody notices.
/// </para>
/// </remarks>
public interface IHasXmin {
    /// <summary>
    /// PostgreSQL's <c>xmin</c> system column for this row: the id of the transaction that last wrote
    /// it, which the database maintains and EF compares on every update. Read-only to application code.
    /// </summary>
    uint Xmin { get; }
}
