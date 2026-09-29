using System.Net;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// How a reverse lookup's answer is read. The lookup itself needs a resolver and is not tested here;
/// what is testable — and what decides whether an operator is offered a name or an address dressed up
/// as one — is the reading.
/// </summary>
public sealed class DnsPreflightTests {
    [Fact]
    public void TheCanonicalNameAndEveryAlias_AreAllNames() {
        var entry = new IPHostEntry { HostName = "nas.lan", Aliases = ["nas", "media.lan"] };

        Assert.Equal(["nas.lan", "nas", "media.lan"], DnsPreflight.NamesFrom(entry));
    }

    /// <summary>
    /// With nothing to answer, <c>GetHostEntry</c> hands the address back as the host name. Reported as
    /// a name it would become a chip offering to add an address the operator already has.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("::ffff:192.168.1.10")]
    [InlineData("fd00::10")]
    public void AnEchoedAddress_IsNotAName(string echoed) {
        var entry = new IPHostEntry { HostName = echoed, Aliases = [] };

        Assert.Empty(DnsPreflight.NamesFrom(entry));
    }

    [Fact]
    public void AnAddressAmongTheAliases_IsDroppedAndTheRestKept() {
        var entry = new IPHostEntry { HostName = "nas.lan", Aliases = ["192.168.1.10", "nas"] };

        Assert.Equal(["nas.lan", "nas"], DnsPreflight.NamesFrom(entry));
    }

    [Fact]
    public void TheSameNameTwice_IsOneName() {
        var entry = new IPHostEntry { HostName = "nas.lan", Aliases = ["NAS.LAN", " nas.lan "] };

        Assert.Equal(["nas.lan"], DnsPreflight.NamesFrom(entry));
    }

    [Fact]
    public void BlankAndMissingAnswers_YieldNothing() {
        Assert.Empty(DnsPreflight.NamesFrom(null));
        Assert.Empty(DnsPreflight.NamesFrom(new IPHostEntry { HostName = "", Aliases = [] }));
        Assert.Empty(DnsPreflight.NamesFrom(new IPHostEntry { HostName = "   ", Aliases = null! }));
    }
}
