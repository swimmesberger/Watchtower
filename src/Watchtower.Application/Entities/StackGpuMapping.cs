namespace Watchtower.Application.Entities;

/// <summary>
/// The "map host GPU(s)" intent for one compose service of a stack (ADR-0031). Deliberately
/// path-free: which render nodes exist is the deploying host's business, resolved by
/// <see cref="Services.HostGpuProbe"/> at deploy time — which is what makes the same row valid on
/// every host, GPU or not. Sits beside <see cref="StackDeviceMapping"/>'s literal paths and is
/// replaced by the same atomic <c>stacks.setDevices</c> call.
/// </summary>
public sealed class StackGpuMapping {
    public int Id { get; set; }
    public int StackId { get; set; }
    public Stack? Stack { get; set; }
    /// <summary>The compose service name (<c>com.docker.compose.service</c>).</summary>
    public required string Service { get; set; }
}
