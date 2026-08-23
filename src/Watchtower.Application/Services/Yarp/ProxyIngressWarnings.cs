using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// Where <see cref="ProxyIngressKestrelConfiguration"/> reports a configuration it had to refuse — today,
/// an ingress port that collides with the management port.
/// </summary>
/// <remarks>
/// It exists because the projection is built <em>before</em> the host is, so there is no
/// <see cref="ILogger"/> to write to yet, while the reloads that re-run it happen long after there is one.
/// Messages raised before a logger is attached are held and flushed once it is, so a conflict present at
/// startup still lands in the ordinary log rather than on stderr where nothing collects it. Each distinct
/// message is reported once: the projection re-runs on every settings write, and a pinned bad port would
/// otherwise repeat the same line for the life of the process.
/// </remarks>
public sealed class ProxyIngressWarnings {
    private readonly Lock _gate = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly List<string> _pending = [];
    private ILogger? _logger;

    /// <summary>Reports a message, at most once per distinct text.</summary>
    public void Warn(string message) {
        ILogger? logger;
        lock (_gate) {
            if (!_reported.Add(message)) return;
            if (_logger is null) {
                _pending.Add(message);
                return;
            }
            logger = _logger;
        }
        logger.LogWarning("Proxy ingress: {Warning}", message);
    }

    /// <summary>Attaches the host's logger and flushes anything raised before it existed.</summary>
    public void UseLogger(ILogger logger) {
        ArgumentNullException.ThrowIfNull(logger);
        string[] pending;
        lock (_gate) {
            _logger = logger;
            pending = [.. _pending];
            _pending.Clear();
        }
        foreach (var message in pending) logger.LogWarning("Proxy ingress: {Warning}", message);
    }
}
