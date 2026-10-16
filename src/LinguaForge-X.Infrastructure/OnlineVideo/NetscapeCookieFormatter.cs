using System.Globalization;
using System.Text;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.OnlineVideo;

/// <summary>Produces only cookies belonging to the selected website, without sharing other browser sessions.</summary>
public static class NetscapeCookieFormatter
{
    internal const int MaximumPayloadBytes = 5 * 1024 * 1024;
    public static readonly TimeSpan MaximumSessionAge = TimeSpan.FromHours(24);

    public static string Format(WebsiteSession session, DateTimeOffset? now = null)
    {
        var snapshot = ValidateAndSnapshot(session, now ?? DateTimeOffset.UtcNow);
        var result = new StringBuilder("# Netscape HTTP Cookie File\r\n# LinguaForge-X temporary website session\r\n");
        foreach (var cookie in snapshot.Cookies)
        {
            if (cookie.HttpOnly) result.Append("#HttpOnly_");
            result.Append(cookie.Domain).Append('\t')
                .Append(cookie.Domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t')
                .Append(cookie.Path).Append('\t').Append(cookie.Secure ? "TRUE" : "FALSE").Append('\t')
                .Append(cookie.Session ? "0" : Math.Floor(cookie.ExpiresUnixSeconds).ToString("0", CultureInfo.InvariantCulture)).Append('\t')
                .Append(cookie.Name).Append('\t').Append(cookie.Value).Append("\r\n");
        }
        if (Encoding.UTF8.GetByteCount(result.ToString()) > MaximumPayloadBytes) throw InvalidCookies();
        return result.ToString();
    }

    internal static WebsiteSession ValidateAndSnapshot(WebsiteSession session, DateTimeOffset now)
    {
        if (session is null || !Enum.IsDefined(session.Website) || session.Cookies is null ||
            session.Cookies.Count is 0 or > 3000 || !IsSafe(session.UserAgent, 2048) || string.IsNullOrWhiteSpace(session.UserAgent) ||
            !IsSafe(session.Proxy, 4096)) throw InvalidCookies();
        if (session.CapturedAt > now.AddMinutes(5) || session.CapturedAt < now - MaximumSessionAge)
            throw new InvalidOperationException("保存的网站验证会话已过期，请重新打开网站验证并保存会话。");

        var scope = VideoWebsites.CookieDomain(session.Website);
        var cookies = new List<WebsiteCookie>();
        var encodedSize = 0;
        foreach (var cookie in session.Cookies)
        {
            if (cookie is null || !IsSafe(cookie.Domain, 254)) throw InvalidCookies();
            var domain = cookie.Domain.StartsWith('.') ? cookie.Domain[1..] : cookie.Domain;
            // A suffix match must include the DNS label separator; notyoutube.com must never match.
            if (!VideoWebsites.IsDomain(domain, scope)) continue;
            if (Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.StartsWith('.') || domain.EndsWith('.') ||
                !IsSafe(cookie.Path, 2048) || !cookie.Path.StartsWith('/') ||
                !IsSafe(cookie.Name, 4096) || string.IsNullOrWhiteSpace(cookie.Name) || cookie.Name.Any(c => c is '=' or ';' || char.IsWhiteSpace(c)) ||
                !IsSafe(cookie.Value, 16_384) || !double.IsFinite(cookie.ExpiresUnixSeconds)) throw InvalidCookies();
            if (!cookie.Session && (cookie.ExpiresUnixSeconds < 0 || cookie.ExpiresUnixSeconds > 253402300799)) throw InvalidCookies();
            if (!cookie.Session && cookie.ExpiresUnixSeconds <= now.ToUnixTimeSeconds()) continue;
            var copy = cookie with
            {
                Domain = cookie.Domain.ToLowerInvariant(),
                ExpiresUnixSeconds = cookie.Session ? 0 : Math.Floor(cookie.ExpiresUnixSeconds)
            };
            encodedSize += Encoding.UTF8.GetByteCount(copy.Domain) + Encoding.UTF8.GetByteCount(copy.Path) +
                           Encoding.UTF8.GetByteCount(copy.Name) + Encoding.UTF8.GetByteCount(copy.Value) + 80;
            if (encodedSize > MaximumPayloadBytes) throw InvalidCookies();
            cookies.Add(copy);
        }
        if (cookies.Count == 0)
            throw new InvalidOperationException("没有找到该网站的有效会话，请在验证浏览器中打开视频，完成验证后重新保存。");
        return session with { Cookies = cookies.ToArray(), Proxy = session.Proxy.Trim() };
    }

    private static bool IsSafe(string? value, int maximumLength) => value is not null && value.Length <= maximumLength && !value.Any(char.IsControl);
    private static InvalidOperationException InvalidCookies() => new("网站验证会话格式无效或过大，请重新打开网站验证并保存会话。");
}
