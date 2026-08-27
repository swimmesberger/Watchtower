using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Mvc;
using Watchtower.Api.Authentication;
using Watchtower.Application.Modules.Backups;
using Watchtower.Application.Services;

namespace Watchtower.Api.Endpoints;

/// <summary>
/// The instance backup surfaces that move whole files rather than JSON, and so cannot be JSON-RPC
/// handlers (ADR-0027): downloading the full backup bundle, and — from stage 3 — uploading one back.
/// </summary>
/// <remarks>
/// Admin-only through <see cref="WatchtowerSessionDefaults.SystemAdminPolicy"/>, not merely
/// operator-only: the bundle carries the key-protection secret, the backup passphrase and the storage
/// credentials in plain text, so downloading one is downloading the instance. With authentication
/// disabled nothing gates it, which is the same posture as the rest of the management plane — an
/// unauthenticated Watchtower is a Watchtower on a trusted network by the operator's choice.
/// </remarks>
public static class InstanceBackupEndpoints {
    /// <summary>Maps the instance backup file endpoints.</summary>
    /// <param name="app">The application to map onto.</param>
    /// <param name="authEnabled">Whether the session scheme is registered; policies exist only then.</param>
    public static WebApplication MapInstanceBackupEndpoints(this WebApplication app, bool authEnabled) {
        foreach (var route in new[] { MapBundleDownload(app), MapBundleUpload(app) })
            if (authEnabled) route.RequireAuthorization(WatchtowerSessionDefaults.SystemAdminPolicy);
        return app;
    }

    /// <summary>
    /// Accepts a full backup bundle, unpacks it and answers with this instance's verdict on restoring it
    /// (ADR-0027 §5). Nothing is replaced here — the upload is staged, and a separate confirmed call
    /// (<c>backups.startInstanceRestore</c>) is what acts on it.
    /// </summary>
    /// <remarks>
    /// The body is the tar itself rather than a multipart form: there is exactly one file, and streaming
    /// it straight to disk keeps a multi-gigabyte upload out of memory. The request size limit is lifted
    /// for this route alone, since a bundle is as large as the estate it carries.
    /// </remarks>
    private static RouteHandlerBuilder MapBundleUpload(WebApplication app) =>
        app.MapPost("/api/instance/restore/bundle", async (
            HttpRequest request, InstanceRestoreService restore, AuditLog audit, ICurrentUser currentUser,
            CancellationToken ct) => {
            try {
                var validation = await restore.StageAsync(request.Body, ct);
                await audit.RecordAsync(
                    BackupService.AuditCategory, "instance.restore.upload",
                    InstanceRestoreService.AuditTarget,
                    $"bundle from '{validation.InstanceName}' ({validation.AppVersion}) uploaded — "
                    + (validation.CanRestore
                        ? $"restorable, {validation.StackCount} stack archive(s)"
                        : $"refused: {string.Join(" ", validation.Blocking.Select(b => b.Code))}"),
                    actor: await audit.ActorAsync(currentUser, ct), ct: ct);
                return Results.Ok(RestoreValidationDto.From(validation));
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // The upload was not a bundle at all, or could not be unpacked. A 400 with the reason,
                // not a 500: the file is the caller's, and so is the fix.
                return Results.Problem(
                    title: "The upload is not a usable backup bundle", detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        })
        // Kestrel's 30 MB default would reject any real bundle. The body is streamed straight to disk,
        // so the ceiling is the container's temp space rather than memory.
        .WithMetadata(new RequestSizeLimitAttribute(long.MaxValue));

    /// <summary>
    /// Streams the staged full backup bundle. The tar is built by <c>backups.exportBundle</c> onto the
    /// container's own filesystem and only ever read from here — its path is never in an API response,
    /// so there is no name to traverse with.
    /// </summary>
    private static RouteHandlerBuilder MapBundleDownload(WebApplication app) =>
        app.MapGet("/api/instance/bundle", async (
            HttpResponse response, BundleExportState state, AuditLog audit, ICurrentUser currentUser,
            CancellationToken ct) => {
            if (state.Current is not { } staged) return Results.NotFound();

            await audit.RecordAsync(
                BackupService.AuditCategory, "bundle.download", BackupBundleService.AuditTarget,
                $"{staged.FileName} · {staged.SizeBytes} bytes",
                actor: await audit.ActorAsync(currentUser, ct), ct: ct);

            response.ContentType = "application/x-tar";
            response.Headers.ContentDisposition = $"attachment; filename=\"{staged.FileName}\"";
            // Known up front, so the browser can show real progress on what is typically a large file.
            response.ContentLength = staged.SizeBytes;
            await using var file = File.OpenRead(staged.Path);
            await file.CopyToAsync(response.Body, ct);
            return Results.Empty;
        });
}
