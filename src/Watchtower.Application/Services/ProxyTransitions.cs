namespace Watchtower.Application.Services;

/// <summary>What a proxy-options change means for one provider's managed topology.</summary>
public enum ProxyTransition {
    /// <summary>Nothing relevant to this provider changed.</summary>
    None,
    /// <summary>Inactive → active for this provider: full reconcile (networks, container, routes, config).</summary>
    Start,
    /// <summary>Active → inactive (disabled, or the operator switched provider): tear the data plane down.</summary>
    Stop,
    /// <summary>Still active but a value changed (e.g. an email or token): re-project the configuration.</summary>
    Refresh,
}

/// <summary>
/// The pure decision seam behind every provider's <c>IOptionsMonitor.OnChange</c> reaction, split out
/// for tests. "Active" means <c>Proxy:Enabled</c> <em>and</em> this provider is the selected backend —
/// so switching <c>Proxy:Provider</c> stops the old provider and starts the new one, each side
/// computing its own transition from the same options change.
/// </summary>
public static class ProxyTransitions {
    public static ProxyTransition Decide(bool wasActive, bool nowActive, bool optionsChanged) {
        if (!optionsChanged) return ProxyTransition.None;
        if (nowActive && !wasActive) return ProxyTransition.Start;
        if (!nowActive && wasActive) return ProxyTransition.Stop;
        return nowActive ? ProxyTransition.Refresh : ProxyTransition.None;
    }
}
