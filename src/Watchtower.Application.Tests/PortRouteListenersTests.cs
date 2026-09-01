using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The vocabulary the two ends of a port route's listener share (ADR-0033). Two properties carry the
/// weight: the rendering is <em>canonical</em>, because the provider compares its own rendering against
/// the stored one to decide whether to write at all and two spellings of the same set would make every
/// pass a write; and reading never throws, because it happens before the host exists, where an exception
/// is a process that does not start.
/// </summary>
public sealed class PortRouteListenersTests {
    [Fact]
    public void Format_SortsAndDeduplicates() =>
        Assert.Equal("9001,9002,9003", PortRouteListeners.Format([9003, 9001, 9002, 9001]));

    [Fact]
    public void Format_OfNothing_IsTheEmptyString() => Assert.Equal("", PortRouteListeners.Format([]));

    /// <summary>A port outside the range is not a listener, so it is not in the rendering either.</summary>
    [Fact]
    public void Format_DropsWhatCouldNotBeBound() =>
        Assert.Equal("9001", PortRouteListeners.Format([0, -1, 70000, 9001]));

    /// <summary>Round-trips, which is the property the compare-then-write depends on.</summary>
    [Fact]
    public void ParseThenFormat_IsTheCanonicalForm() =>
        Assert.Equal("9001,9002", PortRouteListeners.Format(PortRouteListeners.Parse(" 9002 , 9001 , 9002 ")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    public void Parse_OfNothing_IsEmpty(string? value) => Assert.Empty(PortRouteListeners.Parse(value));

    /// <summary>
    /// One unreadable entry costs that entry and nothing else. The alternative — refusing the whole value
    /// — would take every other route's listener down over one stray character.
    /// </summary>
    [Theory]
    [InlineData("9001,nonsense,9002")]
    [InlineData("9001,,9002")]
    [InlineData("9001,70000,9002,0,-3")]
    // A leading sign would be a second spelling of a canonical value, so it is not a port.
    [InlineData("9001,+9002,9002")]
    public void Parse_DropsJunkAndKeepsTheRest(string value) =>
        Assert.Equal([9001, 9002], PortRouteListeners.Parse(value));

    [Fact]
    public void EndpointName_IsThePortsOwn() {
        Assert.Equal("ProxyPort9001", PortRouteListeners.EndpointName(9001));
        Assert.True(PortRouteListeners.IsPortEndpointName("ProxyPort9001"));
        Assert.True(PortRouteListeners.TryParseEndpointName("ProxyPort9001", out var port));
        Assert.Equal(9001, port);
    }

    /// <summary>
    /// Neither the ingress endpoints nor an operator's own may be read as a port route's. The names are
    /// what the projection masks on, so a rule that swallowed <c>ProxyHttps</c> would take TLS ingress
    /// away, and one that swallowed <c>ProxyPortal</c> would take an operator's listener away.
    /// </summary>
    [Theory]
    [InlineData("ProxyHttp")]
    [InlineData("ProxyHttps")]
    [InlineData("Http")]
    [InlineData("ProxyPort")]
    [InlineData("ProxyPortal")]
    [InlineData("ProxyPort9001x")]
    [InlineData("ProxyPort0")]
    [InlineData("ProxyPort70000")]
    [InlineData(null)]
    public void SomeoneElsesEndpointName_IsNotAPortRoutes(string? name) =>
        Assert.False(PortRouteListeners.IsPortEndpointName(name));
}
