using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// What the server has <em>actually</em> bound, read from Kestrel's address feature. Diagnostics only —
/// <see cref="YarpListenerState"/> stays the truth the dispatcher acts on.
/// </summary>
/// <remarks>
/// <para>
/// The two can only disagree after a failed rebind, and that is exactly what this exists to notice. A
/// bind failure at <em>startup</em> is fatal (Kestrel rethrows out of <c>StartAsync</c> and the process
/// exits), so a running instance whose configuration and listeners disagree got there by moving an
/// ingress port at runtime onto one something else already holds: Kestrel logs the failure at Critical,
/// keeps the endpoints it had, and carries on. The status surface reads this and says so, because
/// otherwise the only symptom is traffic not arriving on a port the UI claims is configured.
/// </para>
/// <para>
/// Resolved lazily and tolerantly. There is no <see cref="IServer"/> at all in the unit-test hosts, and
/// <c>TestServer</c> exposes no address feature; both mean "cannot tell", which is reported as
/// <see langword="null"/> and never as "nothing is bound".
/// </para>
/// </remarks>
public class BoundListenerPorts(IServiceProvider services) {
    /// <summary>
    /// The ports the server reports listening on, or <see langword="null"/> where that cannot be
    /// established. Read at the moment it is asked for, so it never needs to race a reload.
    /// </summary>
    /// <remarks>Virtual so a test can state what the server bound; there is no socket in one.</remarks>
    public virtual IReadOnlySet<int>? Current {
        get {
            var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is null || addresses.Count == 0) return null;
            return addresses.Select(ListenerUrl.PortOf).OfType<int>().ToHashSet();
        }
    }
}
