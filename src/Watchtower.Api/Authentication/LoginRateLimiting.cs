using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Watchtower.Api.Endpoints;
using Watchtower.Application.Config;

namespace Watchtower.Api.Authentication;

/// <summary>
/// The per-IP rate limit on <c>POST /api/auth/login</c> (docs/central-auth/design.md §9): a coarse
/// backstop that sits <em>on top of</em> the per-account Identity lockout, so a caller cannot cheaply
/// spray guesses across many different accounts from one source without the lockout ever engaging.
/// </summary>
/// <remarks>
/// The partition key is the <em>connection</em> remote IP, chosen deliberately over
/// <c>X-Forwarded-For</c>. Watchtower processes only <c>X-Forwarded-Proto</c> (see the forwarded-headers
/// note in <c>Program.cs</c>); trusting <c>X-Forwarded-For</c> here would mean a direct client on the
/// published port could set it to any value and rotate past the limit at will, defeating the backstop.
/// The connection peer cannot be spoofed. The cost is that behind the single reverse proxy every login
/// shares Caddy's address, so the limit is instance-global there — acceptable because it is only a
/// backstop, the per-account lockout is the primary control, and on the published-port recovery path
/// (direct connections) the partition is the genuine client IP. The instance-global case is documented
/// on <see cref="AuthOptions.LoginRateLimitPerMinute"/> for operators who need to raise it.
/// </remarks>
public static class LoginRateLimiting {
    /// <summary>Name of the rate-limiter policy the login endpoint opts into via <c>RequireRateLimiting</c>.</summary>
    public const string PolicyName = "watchtower-login";

    /// <summary>
    /// Partition key used when the connection carries no remote address — as with the in-process test
    /// server. All such requests then share one bucket, which is the safe (stricter) direction.
    /// </summary>
    private const string UnknownClientKey = "unknown";

    /// <summary>
    /// The one body a throttled caller ever gets. It says nothing about credentials or accounts: the
    /// throttle fires on the IP before the request body is even read, so it cannot — and must not — become
    /// an account-existence oracle (design.md §9).
    /// </summary>
    private static readonly WatchtowerAuthEndpoints.AuthErrorResponse TooManyAttempts =
        new("Too many login attempts. Please wait a moment and try again.");

    /// <summary>
    /// Registers the fixed-window login limiter. Only meaningful when central authorization is enabled and
    /// the real login endpoint is mapped, so the host calls this only in that mode; the policy is attached
    /// to the single login route with <c>RequireRateLimiting(<see cref="PolicyName"/>)</c>.
    /// </summary>
    public static IServiceCollection AddWatchtowerLoginRateLimiter(this IServiceCollection services) {
        services.AddRateLimiter(options => {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = static async (context, cancellationToken) => {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                // Tell a well-behaved client when to come back, without revealing anything about the account.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                await response.WriteAsJsonAsync(TooManyAttempts, cancellationToken);
            };
            options.AddPolicy(PolicyName, static httpContext => {
                var limit = httpContext.RequestServices
                    .GetRequiredService<IOptionsMonitor<WatchtowerOptions>>()
                    .CurrentValue.Auth.LoginRateLimitPerMinute;
                // A mistyped 0 or negative must not disable the backstop; clamp to at least one attempt.
                if (limit < 1) limit = 1;

                var client = httpContext.Connection.RemoteIpAddress?.ToString() ?? UnknownClientKey;
                return RateLimitPartition.GetFixedWindowLimiter($"login:{client}", _ =>
                    new FixedWindowRateLimiterOptions {
                        PermitLimit = limit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
        });
        return services;
    }
}
