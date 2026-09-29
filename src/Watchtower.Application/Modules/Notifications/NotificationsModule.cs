using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Watchtower.Application.Modules.Notifications.Services;
using Watchtower.Application.Modules.Notifications.WebPush;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Notifications;

/// <summary>
/// Web Push notifications for operators (ADR-0041): browsers register their push subscription through the
/// <c>notifications.*</c> handlers, and Watchtower notifies them — even while the app is closed — when a
/// stack deploy fails.
/// </summary>
/// <remarks>
/// <para>
/// Two layers. The <c>WebPush</c> namespace holds the generic Web Push seams — VAPID key management
/// (<see cref="IVapidKeyProvider"/>), subscription storage (<see cref="IPushSubscriptionStore"/>), the
/// send loop (<see cref="WebPushSender"/>) and the transport (<see cref="IPushServiceClient"/>). They are
/// an interim implementation: Elarion is expected to ship a Web Push package, and these seams are shaped
/// so it can replace them wholesale (<c>TODO(elarion#162)</c> marks each one). On top sit the parts
/// that are Watchtower's own: who the operators are (<see cref="PushNotifier"/>) and when a failed deploy
/// is worth telling them about (<see cref="DeployFailureNotifier"/>).
/// </para>
/// <para>
/// Disabling the module (<c>Modules:Notifications:Enabled=false</c>) removes the handlers and leaves the
/// deploy engine without a failure listener — it takes the sink as optional for exactly that reason.
/// </para>
/// </remarks>
[AppModule("Notifications")]
public static partial class NotificationsModule {
    /// <summary>Returns the JSON type info resolver for Notifications module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => NotificationsJsonContext.Default;

    /// <summary>
    /// The registrations the generators cannot infer: the interim Web Push seams, and the deploy-failure
    /// notifier, which is at once a hosted loop and the deploy engine's <see cref="IDeployFailureSink"/> and
    /// so has to be one instance behind both.
    /// </summary>
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration) {
        AddInterimWebPush(services);

        services.AddSingleton<DeployFailureNotifier>();
        services.AddSingleton<IDeployFailureSink>(sp => sp.GetRequiredService<DeployFailureNotifier>());
        services.AddHostedService(sp => sp.GetRequiredService<DeployFailureNotifier>());
    }

    /// <summary>
    /// The generic Web Push layer in one place, so the future Elarion package replaces a single call.
    /// </summary>
    /// <remarks>TODO(elarion#162): replace with the Elarion Web Push package's registration.</remarks>
    internal static void AddInterimWebPush(IServiceCollection services) {
        // One HttpClient for the process's push traffic; stateless otherwise.
        services.AddSingleton<IPushServiceClient, LibWebPushServiceClient>();
        // Scoped: both read and write through the scoped WatchtowerDbContext.
        services.AddScoped<IVapidKeyProvider, VapidKeyProvider>();
        services.AddScoped<IPushSubscriptionStore, EfPushSubscriptionStore>();
        services.AddScoped<WebPushSender>();
    }
}
