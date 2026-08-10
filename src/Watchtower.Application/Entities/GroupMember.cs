namespace Watchtower.Application.Entities;

/// <summary>
/// One account's membership of one <see cref="Group"/>. Both ends cascade: a membership row is
/// meaningless once either the group or the account is gone, and leaving it behind would silently
/// re-grant a recycled id.
/// </summary>
public sealed class GroupMember {
    public int Id { get; set; }

    public int GroupId { get; set; }
    public Group? Group { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }
}
