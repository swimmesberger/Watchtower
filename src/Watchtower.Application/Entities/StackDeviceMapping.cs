namespace Watchtower.Application.Entities;

/// <summary>
/// One host device mapped into one compose service of a stack (ADR-0030) — what the service's
/// <c>devices:</c> entry would say, stored in Watchtower instead of the repository because the value
/// is host-specific (which <c>/dev/dri</c> render node exists differs per machine, the compose file
/// is shared by every stack of the product). Applied on deploy through the ADR-0012 generated
/// override. Keyed by service name, so the row survives redeploys and applies to every replica.
/// </summary>
public sealed class StackDeviceMapping {
    public int Id { get; set; }
    public int StackId { get; set; }
    public Stack? Stack { get; set; }
    /// <summary>The compose service name (<c>com.docker.compose.service</c>).</summary>
    public required string Service { get; set; }
    /// <summary>Absolute device path on the host, e.g. <c>/dev/dri/renderD128</c>.</summary>
    public required string HostPath { get; set; }
    /// <summary>
    /// Absolute device path inside the container. Stored resolved — the set handler defaults it to
    /// <see cref="HostPath"/> — so "host and container disagree" is always readable off the row.
    /// </summary>
    public required string ContainerPath { get; set; }
    /// <summary>
    /// Cgroup permissions (some subset of <c>rwm</c>, e.g. <c>"rw"</c>), or null for Docker's
    /// default (<c>rwm</c>). Null rather than a stored default so the rendered override only says
    /// what the operator actually chose.
    /// </summary>
    public string? Permissions { get; set; }
}
