using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Forwarder;

namespace Watchtower.Api.Tests;

/// <summary>
/// One request the dispatcher handed to the forwarder, as the upstream would have received it.
/// </summary>
/// <param name="DestinationPrefix">The upstream address the dispatcher chose — <c>http://{alias}:{port}</c>.</param>
/// <param name="Method">The forwarded method.</param>
/// <param name="RequestUri">The absolute URI the transformer built from the prefix, path and query.</param>
/// <param name="Host">
/// The <c>Host</c> header on the outgoing request. Its own property rather than an entry in
/// <paramref name="Headers"/> because <see cref="HttpRequestMessage"/> models it that way, and preserving
/// it is one of the two things the transformer exists for.
/// </param>
/// <param name="Headers">
/// Every other header on the outgoing request, joined per name. Case-insensitive, as headers are.
/// </param>
/// <param name="MaxRequestBodySize">
/// What the body-size limit was at the moment of forwarding: <see langword="null"/> means "lifted", which
/// is what a proxied upload needs. Captured here because the feature is request state that is gone by the
/// time the test sees a response.
/// </param>
public sealed record ForwardedRequest(
    string DestinationPrefix,
    HttpMethod Method,
    Uri? RequestUri,
    string? Host,
    IReadOnlyDictionary<string, string> Headers,
    long? MaxRequestBodySize) {
    /// <summary>The single value of <paramref name="name"/>, or <see langword="null"/> when absent.</summary>
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>Whether the outgoing request carries <paramref name="name"/> at all.</summary>
    public bool Has(string name) => Headers.ContainsKey(name);
}

/// <summary>
/// An <see cref="IHttpForwarder"/> that records what would have been forwarded and answers with a marker
/// body instead of opening a connection.
/// </summary>
/// <remarks>
/// It runs the real <see cref="HttpTransformer"/> the dispatcher passed in, against a fresh
/// <see cref="HttpRequestMessage"/>, which is the whole point: the properties worth testing — the preserved
/// <c>Host</c>, the transport headers set rather than appended, the identity headers the access decision
/// produced — are all properties of that transformation, and asserting on the inbound
/// <see cref="HttpContext"/> instead would test nothing but the middleware's own bookkeeping.
/// <para>
/// The marker body is what lets a test tell "forwarded" apart from "answered by Watchtower": the SPA
/// fallback also returns 200, so a status code alone would not distinguish them.
/// </para>
/// </remarks>
public sealed class RecordingHttpForwarder : IHttpForwarder {
    /// <summary>The body the stand-in upstream answers with.</summary>
    public const string MarkerBody = "forwarded-by-the-recording-upstream";

    private readonly List<ForwardedRequest> _forwarded = [];

    /// <summary>Every request handed to the forwarder, in order.</summary>
    public IReadOnlyList<ForwardedRequest> Forwarded {
        get { lock (_forwarded) return [.. _forwarded]; }
    }

    /// <summary>The single forwarded request; fails when the count is anything but one.</summary>
    public ForwardedRequest Single() {
        var all = Forwarded;
        if (all.Count != 1)
            throw new InvalidOperationException(
                $"Expected exactly one forwarded request, but {all.Count} were recorded.");
        return all[0];
    }

    /// <summary>What the forwarder reports; set it to exercise the dispatcher's failure branch.</summary>
    public ForwarderError Error { get; set; } = ForwarderError.None;

    /// <summary>
    /// The status the forwarder sets on its way out when it fails. The real one does this — a timeout
    /// becomes 504, a refused connection a 502 — and the dispatcher must not flatten that diagnosis back
    /// into its own default. Null leaves the response untouched at 200.
    /// </summary>
    public int? FailureStatusCode { get; set; }

    public async ValueTask<ForwarderError> SendAsync(
        HttpContext context,
        string destinationPrefix,
        HttpMessageInvoker httpClient,
        ForwarderRequestConfig requestConfig,
        HttpTransformer transformer) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transformer);

        using var outgoing = new HttpRequestMessage();
        // The real forwarder attaches the body before transforming, and the base transformer moves the
        // content headers onto it — without a Content they would simply be dropped, and a test about a
        // proxied upload would be quietly testing nothing.
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            outgoing.Content = new StreamContent(context.Request.Body);

        await transformer.TransformRequestAsync(context, outgoing, destinationPrefix, context.RequestAborted);
        // The real forwarder fills the destination address in after the transformer, and only when the
        // transformer left it alone. Reproduced here so what is recorded is the URI the upstream would
        // actually have been asked for.
        outgoing.RequestUri ??= RequestUtilities.MakeDestinationAddress(
            destinationPrefix, context.Request.Path, context.Request.QueryString);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in outgoing.Headers)
            headers[header.Key] = string.Join(", ", header.Value);
        if (outgoing.Content is not null)
            foreach (var header in outgoing.Content.Headers)
                headers[header.Key] = string.Join(", ", header.Value);
        // Host lives on its own property and is deliberately not in the enumeration above.
        headers.Remove("Host");

        var record = new ForwardedRequest(
            destinationPrefix,
            outgoing.Method,
            outgoing.RequestUri,
            outgoing.Headers.Host,
            headers,
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize);
        lock (_forwarded) _forwarded.Add(record);

        if (Error != ForwarderError.None) {
            if (FailureStatusCode is { } status) context.Response.StatusCode = status;
            return Error;
        }

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(MarkerBody, context.RequestAborted);
        return ForwarderError.None;
    }
}
