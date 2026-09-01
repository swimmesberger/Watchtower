using Watchtower.Application.Services.Acme;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Resolves a domain's A/AAAA records so the UI can show the operator whether DNS is pointed here
/// before a certificate can be issued. A best-effort preflight — resolution failures are reported as
/// "does not resolve", not errors.
/// </summary>
/// <remarks>
/// Deliberately the same <see cref="DnsPreflight"/> the certificate issuer runs before it opens an
/// order: an operator who has just been told "this resolves" and then sees the route sit at
/// <c>awaitingDns</c> has been given two answers to one question.
/// </remarks>
[Handler("proxy.checkDns")]
public sealed class CheckDns(DnsPreflight dns)
    : IHandler<CheckDns.Command, Result<CheckDns.Response>> {
    /// <param name="Domain">
    /// The hostname to resolve. Nullable so a client holding a port route's empty domain field can send
    /// what it has; a blank value is refused with a sentence that reads correctly for both callers it
    /// can come from — someone who has not typed a name yet, and a port route (ADR-0033), which is
    /// addressed by number and has none to resolve.
    /// </param>
    public sealed record Command(string? Domain);
    public sealed record Response(bool Resolves, IReadOnlyList<string> Addresses);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Domain))
            return AppError.Validation("Enter a domain to check; a port route has none to resolve.");
        if (!DesiredHosts.TryNormalize(command.Domain, out var domain, out var reason))
            return AppError.Validation(reason);
        var addresses = await dns.ResolveAsync(domain, ct);
        return new Response(addresses.Count > 0, addresses);
    }
}
