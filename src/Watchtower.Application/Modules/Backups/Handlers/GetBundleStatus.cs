using Elarion.Abstractions.Authorization;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Whether a full backup bundle is staged for download, and what it holds (ADR-0027 §4) — what the
/// Settings card polls while an export runs and reads afterwards to offer the link.
/// </summary>
[Handler("backups.getBundleStatus")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class GetBundleStatus(BundleExportState state)
    : IHandler<GetBundleStatus.Query, Result<GetBundleStatus.Response>> {
    public sealed record Query;

    public sealed record Response(BackupBundleDto? Bundle);

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) =>
        ValueTask.FromResult<Result<Response>>(new Response(
            state.Current is { } staged
                ? new BackupBundleDto(
                    staged.FileName, staged.SizeBytes, staged.CreatedAtUtc, staged.StackCount,
                    staged.MissingStackCount)
                : null));
}
