using System.Net;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Resolves a domain's A/AAAA records so the UI can show the operator whether DNS is pointed here
/// before a certificate can be issued. A best-effort preflight — resolution failures are reported as
/// "does not resolve", not errors.
/// </summary>
[Handler("proxy.checkDns")]
public sealed class CheckDns
    : IHandler<CheckDns.Command, Result<CheckDns.Response>> {
    public sealed record Command(string Domain);
    public sealed record Response(bool Resolves, IReadOnlyList<string> Addresses);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var domain = RouteMapping.NormalizeDomain(command.Domain);
        if (domain is null)
            return AppError.Validation("Domain is required.");
        try {
            var addresses = await Dns.GetHostAddressesAsync(domain, ct);
            var ips = addresses.Select(a => a.ToString()).ToList();
            return new Response(ips.Count > 0, ips);
        } catch (Exception) {
            return new Response(false, []);
        }
    }
}
