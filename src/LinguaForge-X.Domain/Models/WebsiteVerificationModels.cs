namespace LinguaForge_X.Domain.Models;

public enum VideoWebsite { YouTube, Bilibili }

public sealed record WebsiteCookie(string Domain, string Path, string Name, string Value,
    bool Secure, bool HttpOnly, double ExpiresUnixSeconds, bool Session);

public sealed record WebsiteSession(VideoWebsite Website, IReadOnlyList<WebsiteCookie> Cookies,
    string UserAgent, string Proxy, DateTimeOffset CapturedAt);

public static class VideoWebsites
{
    public static VideoWebsite? FromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var host = uri.IdnHost;
        if (IsDomain(host, "youtube.com") || IsDomain(host, "youtu.be")) return VideoWebsite.YouTube;
        if (IsDomain(host, "bilibili.com") || IsDomain(host, "b23.tv")) return VideoWebsite.Bilibili;
        return null;
    }

    public static string CookieDomain(VideoWebsite website) => website switch
    {
        VideoWebsite.YouTube => "youtube.com",
        VideoWebsite.Bilibili => "bilibili.com",
        _ => throw new ArgumentOutOfRangeException(nameof(website))
    };

    public static bool IsDomain(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
