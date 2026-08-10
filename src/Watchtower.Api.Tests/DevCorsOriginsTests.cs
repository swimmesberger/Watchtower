using Watchtower.Api;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The development CORS policy grants <b>credentialed</b> cross-origin access, so its origin predicate is
/// the thing standing between a developer's live session and any page they happen to open. These cases
/// pin down that it admits the dev server and nothing else.
/// </summary>
public sealed class DevCorsOriginsTests {
    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("https://localhost:7080")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://[::1]:5173")]
    public void Admits_TheDevServerOnThisMachine(string origin) =>
        Assert.True(DevCorsOrigins.IsLoopback(origin));

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("https://evil.example")]
    // Substring matching would have let these through; Uri.IsLoopback does not.
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://127.0.0.1.evil.example")]
    [InlineData("http://192.168.1.5:3000")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("not a url")]
    public void Refuses_EverythingElse(string origin) =>
        Assert.False(DevCorsOrigins.IsLoopback(origin));
}
