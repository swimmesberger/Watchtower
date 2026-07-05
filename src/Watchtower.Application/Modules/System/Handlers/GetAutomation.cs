using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Returns the effective automation toggles (background auto-check + stack-check enablement and
/// intervals). Values come from <see cref="IOptionsMonitor{WatchtowerOptions}"/>, so they reflect
/// any runtime overrides layered over the env/appsettings defaults by the settings provider.
/// </summary>
[Handler("system.getAutomation")]
public sealed class GetAutomation(IOptionsMonitor<WatchtowerOptions> options)
    : IHandler<GetAutomation.Query, Result<GetAutomation.Response>> {
    public sealed record Query;
    public sealed record Response(
        bool AutoCheckEnabled,
        int AutoCheckIntervalMinutes,
        bool StackCheckEnabled,
        int StackCheckIntervalMinutes);

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var o = options.CurrentValue;
        var response = new Response(
            o.AutoCheckEnabled,
            o.AutoCheckIntervalMinutes,
            o.StackCheckEnabled,
            o.StackCheckIntervalMinutes);
        return ValueTask.FromResult<Result<Response>>(response);
    }
}
