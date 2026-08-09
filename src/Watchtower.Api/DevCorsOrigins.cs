namespace Watchtower.Api;

/// <summary>
/// The origin predicate behind the development-only CORS policy in <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// A named type rather than a local function because this decides who may make <b>credentialed</b>
/// cross-origin calls — i.e. who may drive a developer's Watchtower using their session cookie — and that
/// is worth having regression cover on. Only the Vite dev server (and the Aspire AppHost fronting it) ever
/// needs this, and both run on the developer's own machine.
/// </remarks>
public static class DevCorsOrigins {
    /// <summary>
    /// Whether a CORS <c>Origin</c> header names a host on this machine. Anything unparseable is rejected
    /// rather than guessed at.
    /// </summary>
    public static bool IsLoopback(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback;
}
