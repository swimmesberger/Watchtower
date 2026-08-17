using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Returns the effective automation toggles (background auto-check, stack-check and dangling-image
/// prune enablement and intervals). Values come from <see cref="IOptionsMonitor{WatchtowerOptions}"/>,
/// so they reflect any runtime overrides layered over the appsettings defaults by the settings
/// provider — except where a <c>WATCHTOWER__*</c> env var pins the value (env wins); those paths are
/// listed in <c>PinnedPaths</c> so the UI can disable the fields.
/// </summary>
[Handler("system.getAutomation")]
public sealed class GetAutomation(IOptionsMonitor<WatchtowerOptions> options, EnvironmentSettingPins pins)
    : IHandler<GetAutomation.Query, Result<GetAutomation.Response>> {
    public sealed record Query;
    public sealed record Response(
        bool AutoCheckEnabled,
        int AutoCheckIntervalMinutes,
        bool StackCheckEnabled,
        int StackCheckIntervalMinutes,
        bool ImagePruneEnabled,
        int ImagePruneIntervalMinutes,
        string[] PinnedPaths);

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var o = options.CurrentValue;
        var response = new Response(
            o.AutoCheckEnabled,
            o.AutoCheckIntervalMinutes,
            o.StackCheckEnabled,
            o.StackCheckIntervalMinutes,
            o.ImagePruneEnabled,
            o.ImagePruneIntervalMinutes,
            pins.Pinned(UpdateAutomation.AutomationPaths));
        return ValueTask.FromResult<Result<Response>>(response);
    }
}
