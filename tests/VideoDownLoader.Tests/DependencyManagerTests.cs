using VideoDownLoader.Services;

namespace VideoDownLoader.Tests;

public sealed class DependencyManagerTests
{
    [Theory]
    [InlineData("2026.07.04", "2026.07.04", false)]
    [InlineData("2026.06.01", "2026.07.04", true)]
    [InlineData("2026.08.01", "2026.07.04", false)]
    [InlineData("2026.07.04.120000", "2026.07.04", false)]
    [InlineData("v2026.06.01", "2026.07.04", true)]
    [InlineData("2026.08.18.100000", "2026.08.18.122307", true)]
    [InlineData("2026.08.18.122307", "2026.08.18.100000", false)]
    public void IsYtDlpUpdateAvailable_ComparesReleaseDates(
        string current,
        string latest,
        bool expected)
    {
        Assert.Equal(expected, DependencyManager.IsYtDlpUpdateAvailable(current, latest));
    }

    [Theory]
    [InlineData("2.9.3", "2.9.3", false)]
    [InlineData("2.8.0", "2.9.3", true)]
    [InlineData("2.10.0", "2.9.3", false)]
    [InlineData("v2.8.0", "2.9.3", true)]
    public void IsSemanticVersionUpdateAvailable_ComparesVersions(
        string current,
        string latest,
        bool expected)
    {
        Assert.Equal(expected, DependencyManager.IsSemanticVersionUpdateAvailable(current, latest));
    }

    [Fact]
    public void ParsePublishedChecksum_AcceptsStandardFormat()
    {
        const string hash = "171efab55ac6b9881fd53ee4c20f8bf3bb1340ffc618483746909014db12216a";

        var actual = DependencyManager.ParsePublishedChecksum(
            $"{hash}  deno-x86_64-pc-windows-msvc.zip",
            "deno-x86_64-pc-windows-msvc.zip");

        Assert.Equal(hash, actual);
    }

    [Fact]
    public void ParsePublishedChecksum_AcceptsPowerShellFormat()
    {
        const string hash = "171EFAB55AC6B9881FD53EE4C20F8BF3BB1340FFC618483746909014DB12216A";
        var content = $"""
            Algorithm : SHA256
            Hash      : {hash}
            Path      : C:\a\deno\deno\target\release\deno-x86_64-pc-windows-msvc.zip
            """;

        var actual = DependencyManager.ParsePublishedChecksum(
            content,
            "deno-x86_64-pc-windows-msvc.zip");

        Assert.Equal(hash, actual);
    }

    [Theory]
    [InlineData("sha256:ab4a406234cef5f15782f9da5cc69c0db27361a2d555fa99fe8422d5f15010db",
        "ab4a406234cef5f15782f9da5cc69c0db27361a2d555fa99fe8422d5f15010db")]
    [InlineData("md5:abc", null)]
    [InlineData(null, null)]
    public void ParseSha256Digest_ValidatesGitHubDigest(string? digest, string? expected)
    {
        Assert.Equal(expected, DependencyManager.ParseSha256Digest(digest));
    }
}
