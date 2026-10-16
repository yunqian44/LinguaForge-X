namespace LinguaForge_X.Infrastructure.OnlineVideo;

internal static class OnlineVideoErrors
{
    internal static string Describe(string diagnostic)
    {
        // Warnings and appended cookie help links are not evidence of an authentication failure.
        // Return only fixed messages: remote diagnostics may contain account data or signed URLs.
        var lines = diagnostic.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var errors = lines.Select(line => line.Trim()).Where(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)).ToArray();
        var error = (errors.Length > 0 ? string.Join('\n', errors) : diagnostic).ToLowerInvariant();
        bool Has(params string[] patterns) => patterns.Any(error.Contains);

        if (Has("not a bot", "confirm you're human", "confirm you’re human", "captcha", "unusual traffic"))
            return "网站要求确认不是机器人。YouTube / B 站可点击「网页登录／验证」，手动完成网站操作后点击「使用会话并重试」；也可在设置中选择该站的 Cookie 文件。";
        if (Has("drm")) return "视频受 DRM 保护，无法导入。";
        if (Has("private video", "video is private"))
            return "这是私有视频。请点击「网页登录／验证」并使用可播放该视频的账号登录，或在设置中选择该账号的 Cookie 文件。";
        if (Has("members-only", "members only", "join this channel", "premium-only", "requires payment", "purchase this video"))
            return "该视频需要会员或购买权限。请确认当前账号有权限，再选择对应账号的 Cookie 文件；软件无法获得账号没有的权限。";
        if (Has("age-restricted", "age restricted", "confirm your age", "age verification"))
            return "该视频有年龄限制。请在「网页登录／验证」窗口完成网站要求的账号年龄验证并确认可播放，再回到软件使用该会话。";
        if (Has("cookies are no longer valid", "cookies have expired", "expired cookies", "cookies are invalid", "does not look like a netscape", "invalid cookie file", "failed to load cookies"))
            return "Cookie 文件无效或登录状态已失效。请重新进行「网页登录／验证」，或重新导出该站的 Netscape 格式 Cookie 文件并在设置中选择。";
        if (Has("http error 429", "status code 429", "too many requests", "rate limit", "rate-limit", "this content isn't available, try again later"))
            return "网站暂时限制了请求频率。请停止连续重试，稍后再试；若浏览器要求验证，请先在同一网络下完成验证。";
        if (Has("geo-restricted", "geographic restriction", "not available in your country", "not available in your region", "blocked in your country"))
            return "该视频存在地区访问限制，请检查当前网络是否可正常播放。";
        if (Has("unable to connect to proxy", "proxy connection", "proxyerror", "tunnel connection failed", "http error 407"))
            return "代理连接失败。请检查代理软件已运行、地址和端口正确；软件应使用能在浏览器中播放该视频的同一代理。";
        if (Has("timed out", "connection", "resolve", "http error 403", "http error 412", "status code 403", "status code 412"))
            return "无法访问视频网站或请求被限制。请确认浏览器可播放，并在在线视频下载设置中检查代理；若浏览器要求验证，先完成验证后再试。";
        if (Has("po token", "po_token"))
            return "YouTube 要求额外的播放验证令牌。请先更新官方 yt-dlp；当前版本未配置该令牌，仅提供 Cookie 可能仍无法下载。";
        if (Has("js runtime", "javascript", "ejs", "signature extraction failed", "nsig extraction failed"))
            return "YouTube 脚本解析失败，请检查 Deno 路径，并更新官方 yt-dlp 与 Deno。";
        if (Has("unsupported url")) return "当前 yt-dlp 不支持此链接，请使用具体视频链接或更新下载工具。";
        if (Has("requested format", "no video formats"))
            return "没有可下载的音视频格式，请尝试其他清晰度、确认视频可播放，或更新下载工具。";
        if (Has("video is unavailable", "video unavailable", "video has been removed", "video has been deleted"))
            return "该视频不可用、已删除或无法访问。请先在浏览器检查同一链接是否能正常播放。";
        if (Has("sign in", "log in", "login required", "authentication required", "requires authentication"))
            return "网站要求登录。YouTube / B 站可点击「网页登录／验证」登录后使用会话重试；其他站点可在设置中选择对应账号的 Netscape Cookie 文件。";
        return "在线视频处理失败，请检查视频是否可播放、工具设置是否正确，或更新 yt-dlp 后重试。";
    }
}
