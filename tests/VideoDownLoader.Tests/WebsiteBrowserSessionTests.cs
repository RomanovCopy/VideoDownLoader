using VideoDownLoader.Models;

namespace VideoDownLoader.Tests;

public sealed class WebsiteBrowserSessionTests
{
    [Fact]
    public void BuildCookieHeader_IncludesOnlyCookiesValidForRequest()
    {
        var session = new WebsiteBrowserSession(
            new Uri("https://members.example.test/gallery"),
            "<html></html>",
            "Test Browser",
            [
                new WebsiteSessionCookie("root", "one", ".example.test", "/", true, null),
                new WebsiteSessionCookie("gallery", "two", "members.example.test", "/gallery", true, null),
                new WebsiteSessionCookie("other", "three", "other.example.test", "/", true, null),
                new WebsiteSessionCookie("expired", "four", ".example.test", "/", true, DateTime.UtcNow.AddMinutes(-1))
            ]);

        var header = session.BuildCookieHeader(new Uri("https://members.example.test/gallery/42"));

        Assert.Equal("gallery=two; root=one", header);
    }

    [Fact]
    public void BuildCookieHeader_DoesNotSendSecureCookieOverHttpOrToSimilarPath()
    {
        var session = new WebsiteBrowserSession(
            new Uri("https://example.test/gallery"),
            string.Empty,
            "Test Browser",
            [new WebsiteSessionCookie("session", "secret", "example.test", "/gallery", true, null)]);

        Assert.Null(session.BuildCookieHeader(new Uri("http://example.test/gallery/42")));
        Assert.Null(session.BuildCookieHeader(new Uri("https://example.test/gallery-other")));
    }
}
