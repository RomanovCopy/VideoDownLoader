namespace VideoDownLoader.Models;

public sealed record WebsiteBrowserSession(
    Uri PageUri,
    string Html,
    string UserAgent,
    IReadOnlyList<WebsiteSessionCookie> Cookies)
{
    public string? BuildCookieHeader(Uri requestUri)
    {
        var now = DateTime.UtcNow;
        var values = Cookies
            .Where(cookie => cookie.Matches(requestUri, now))
            .Where(cookie => IsSafeHeaderValue(cookie.Name) && IsSafeHeaderValue(cookie.Value))
            .OrderByDescending(cookie => cookie.Path.Length)
            .Select(cookie => $"{cookie.Name}={cookie.Value}")
            .ToArray();
        return values.Length == 0 ? null : string.Join("; ", values);
    }

    private static bool IsSafeHeaderValue(string value)
    {
        return !string.IsNullOrEmpty(value) && !value.Contains('\r') && !value.Contains('\n');
    }
}

public sealed record WebsiteSessionCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool Secure,
    DateTime? Expires)
{
    public bool Matches(Uri uri, DateTime utcNow)
    {
        if (Secure && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (Expires is { } expires && expires != DateTime.MinValue && expires.ToUniversalTime() <= utcNow)
        {
            return false;
        }

        var cookieDomain = Domain.TrimStart('.');
        var domainMatches = uri.Host.Equals(cookieDomain, StringComparison.OrdinalIgnoreCase) ||
                            (Domain.StartsWith('.') && uri.Host.EndsWith('.' + cookieDomain, StringComparison.OrdinalIgnoreCase));
        if (!domainMatches)
        {
            return false;
        }

        var cookiePath = string.IsNullOrEmpty(Path) ? "/" : Path;
        return cookiePath == "/" ||
               uri.AbsolutePath.Equals(cookiePath, StringComparison.Ordinal) ||
               uri.AbsolutePath.StartsWith(
                   cookiePath.EndsWith('/') ? cookiePath : cookiePath + "/",
                   StringComparison.Ordinal);
    }
}
