using System.Globalization;
using System.Net;
using System.Text.Json;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.OnlineVideo;

/// <summary>Downloads one online video into an isolated local job before entering the existing media pipeline.</summary>
public sealed class YtDlpOnlineVideoService : IOnlineVideoService
{
    private readonly IMediaService _media;
    private readonly IOnlineVideoProcessRunner _runner;
    private readonly IWebsiteSessionStore? _websiteSessions;
    private const string OutputPrefix = "LF_OUTPUT:";
    private const string ProgressPrefix = "LF_PROGRESS:";

    public YtDlpOnlineVideoService(IMediaService mediaService) : this(mediaService, new OnlineVideoProcessRunner()) { }

    public YtDlpOnlineVideoService(IMediaService mediaService, IOnlineVideoProcessRunner runner, IWebsiteSessionStore? websiteSessions = null)
    {
        _media = mediaService;
        _runner = runner;
        _websiteSessions = websiteSessions;
    }

    public async Task<string> CheckAvailableAsync(OnlineVideoSettings settings, CancellationToken cancellationToken)
    {
        var job = CreateJob(TemporaryRoot());
        try
        {
            var downloader = await ExecuteAsync(settings.DownloaderPath,
                ["--ignore-config", "--no-config-locations", "--no-plugin-dirs", "--no-remote-components", "--version"],
                job, cancellationToken, outputLimit: 4096).ConfigureAwait(false);
            var runtime = await ExecuteAsync(settings.JavaScriptRuntimePath, ["--version"], job,
                cancellationToken, outputLimit: 4096).ConfigureAwait(false);
            var ytVersion = downloader.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            var denoVersion = runtime.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(ytVersion) || string.IsNullOrWhiteSpace(denoVersion) ||
                !denoVersion.StartsWith("deno ", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工具版本响应无效，请选择官方 yt-dlp 和 Deno 可执行文件。");
            var versionText = denoVersion[5..].Split(' ', '-')[0];
            if (!Version.TryParse(versionText, out var version) || version < new Version(2, 3))
                throw new InvalidOperationException("YouTube 支持需要 Deno 2.3 或更新版本，请更新在线视频工具。");
            return $"yt-dlp {CleanText(ytVersion, 80)} · {CleanText(denoVersion, 80)}";
        }
        finally { DeleteJob(job); }
    }

    public async Task<OnlineVideoInfo> InspectAsync(OnlineVideoRequest request, OnlineVideoSettings settings,
        CancellationToken cancellationToken)
    {
        var url = ValidateRequest(request);
        var job = CreateJob(TemporaryRoot());
        try
        {
            var arguments = await CommonArgumentsAsync(settings, url, job, cancellationToken).ConfigureAwait(false);
            return await InspectCoreAsync(url, settings, arguments, job, cancellationToken).ConfigureAwait(false);
        }
        finally { DeleteJob(job); }
    }

    public async Task<OnlineVideoDownload> DownloadAsync(OnlineVideoRequest request, OnlineVideoSettings settings,
        MediaSettings mediaSettings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        var url = ValidateRequest(request);
        var root = string.IsNullOrWhiteSpace(settings.DownloadDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinguaForge-X", "downloads")
            : settings.DownloadDirectory;
        var job = CreateJob(root);
        var completed = false;
        try
        {
            Report(progress, "解析在线视频", 0, "正在读取视频信息");
            var common = await CommonArgumentsAsync(settings, url, job, cancellationToken).ConfigureAwait(false);
            var info = await InspectCoreAsync(url, settings, common, job, cancellationToken).ConfigureAwait(false);
            await _media.CheckAvailableAsync(mediaSettings, cancellationToken).ConfigureAwait(false);
            var arguments = new List<string>(common)
            {
                "--no-simulate", "--no-flat-playlist", "--max-downloads", "1",
                "--match-filter", "!is_live & live_status !=? is_upcoming & live_status !=? post_live",
                "--format", FormatSelector(request.MaxHeight),
                "--format-sort", "vcodec:h264,acodec:aac", "--merge-output-format", "mp4/mkv",
                "--ffmpeg-location", ResolveToolLocation(mediaSettings.FfmpegPath),
                "--paths", job, "--output", "media.%(ext)s",
                "--no-overwrites", "--no-continue", "--no-write-info-json", "--no-write-thumbnail",
                "--no-write-subs", "--no-write-auto-subs", "--no-embed-metadata",
                "--newline", "--progress", "--progress-delta", "0.5",
                "--progress-template", "download:" + ProgressPrefix + "%(progress._percent_str)s",
                "--progress-template", "postprocess:LF_POSTPROCESS",
                "--print", "after_move:" + OutputPrefix + "%(filepath)j", "--", url.AbsoluteUri
            };
            string? downloadedPath = null;
            Report(progress, "下载在线视频", 2, "正在下载画面与声音，随后自动合并");
            var result = await _runner.RunAsync(new(RequireTool(settings.DownloaderPath), arguments, job,
                CaptureOutput: false, OutputLimit: 16_384), line =>
            {
                if (line.StartsWith(OutputPrefix, StringComparison.Ordinal))
                {
                    if (downloadedPath is not null) throw new InvalidOperationException("链接返回了多个视频，请使用具体视频或分 P 链接。");
                    try { downloadedPath = JsonSerializer.Deserialize<string>(line[OutputPrefix.Length..]); }
                    catch (JsonException) { throw new InvalidOperationException("下载工具返回了无效的文件位置。"); }
                }
                else if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal) &&
                         double.TryParse(line[ProgressPrefix.Length..].Trim().TrimEnd('%'), NumberStyles.Float,
                             CultureInfo.InvariantCulture, out var percent) && double.IsFinite(percent))
                    Report(progress, "下载在线视频", 2 + Math.Clamp(percent, 0, 100) * .83,
                        $"下载当前音视频流 {Math.Clamp(percent, 0, 100):F0}%");
                else if (line == "LF_POSTPROCESS")
                    Report(progress, "合并在线视频", 87, "正在合并画面与声音");
            }, cancellationToken).ConfigureAwait(false);
            // yt-dlp returns 101 after the first completed video when --max-downloads is reached.
            if (result.ExitCode != 0 && !(result.ExitCode == 101 && downloadedPath is not null)) ThrowFailure(result.Error);
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ValidateDownloadedFile(downloadedPath, job);
            var sourceInfo = await _media.ProbeAsync(sourcePath, mediaSettings, cancellationToken).ConfigureAwait(false);
            if (sourceInfo.AudioTracks.Count == 0)
                throw new InvalidOperationException("该视频没有可用音轨，无法生成语音字幕或中文配音。");
            Report(progress, "整理在线视频", 90, "正在检查本地视频格式");
            var codecs = await ReadCodecsAsync(sourcePath, mediaSettings, job, cancellationToken).ConfigureAwait(false);
            var output = Path.Combine(job, "video.mp4");
            var compatible = Path.GetExtension(sourcePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
                             codecs.Video == "h264" && codecs.Audio == "aac" &&
                             codecs.PixelFormat is "yuv420p" or "yuvj420p";
            if (compatible) File.Move(sourcePath, output);
            else
            {
                Report(progress, "转为 MP4", 92, "正在生成可预览的 MP4，较长视频可能需要一些时间");
                await ExecuteAsync(mediaSettings.FfmpegPath,
                    ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", sourcePath,
                     "-map", "0:v:0", "-map", "0:a:0", "-c:v", "libx264", "-preset", "fast",
                     "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
                     "-movflags", "+faststart", "-map_metadata", "-1", "-map_chapters", "-1", output],
                    job, cancellationToken, outputLimit: 32_768).ConfigureAwait(false);
                ValidateDownloadedFile(output, job, final: true);
                var finalInfo = await _media.ProbeAsync(output, mediaSettings, cancellationToken).ConfigureAwait(false);
                if (finalInfo.AudioTracks.Count == 0) throw new InvalidOperationException("MP4 转换后缺少音轨，请重试。");
                File.Delete(sourcePath);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Do not retain cookies, partial streams, or site metadata beside the imported source.
            foreach (var file in Directory.EnumerateFiles(job))
                if (!Path.GetFullPath(file).Equals(output, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
            Report(progress, "在线视频已导入", 100, "已下载为本地 MP4，可以生成字幕和中文配音");
            completed = true;
            return new(info with { DurationSeconds = sourceInfo.DurationSeconds }, output);
        }
        finally
        {
            if (!completed) DeleteJob(job);
            else DeleteCookieCopy(job);
        }
    }

    private async Task<OnlineVideoInfo> InspectCoreAsync(Uri url, OnlineVideoSettings settings,
        List<string> common, string job, CancellationToken cancellationToken)
    {
        var arguments = new List<string>(common)
        { "--skip-download", "--flat-playlist", "--playlist-items", "1", "--dump-single-json", "--", url.AbsoluteUri };
        var result = await ExecuteAsync(settings.DownloaderPath, arguments, job, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(result.Output, new JsonDocumentOptions { MaxDepth = 48 });
            var item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (ReadString(item, "_type") is "playlist" or "multi_video" || item.TryGetProperty("entries", out _))
                throw new InvalidOperationException("请粘贴单个视频链接；暂不支持播放列表、频道或合集批量导入。B 站分 P 视频请打开所需分 P 后复制链接。");
            if (ReadBoolean(item, "is_live") || ReadString(item, "live_status") is "is_live" or "is_upcoming" or "post_live")
                throw new InvalidOperationException("暂不支持直播、预约视频或尚未处理完成的直播回放，请在普通视频发布后重试。");
            if (ReadBoolean(item, "has_drm")) throw new InvalidOperationException("该视频受 DRM 保护，无法导入，请选择可正常下载的素材。");
            if (item.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array && formats.GetArrayLength() > 0)
            {
                var all = formats.EnumerateArray().ToArray();
                if (all.All(f => ReadBoolean(f, "has_drm"))) throw new InvalidOperationException("该视频仅提供 DRM 保护的格式，无法导入。");
                if (all.All(f => ReadString(f, "vcodec") == "none")) throw new InvalidOperationException("该链接仅提供音频，请选择包含画面的视频。");
            }
            var id = CleanText(ReadString(item, "id"), 200);
            var title = CleanText(ReadString(item, "title"), 500);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                throw new InvalidOperationException("无法读取视频信息，请确认这是可访问的单个视频链接。");
            var pageUrl = ReadString(item, "webpage_url");
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(page.UserInfo))
                pageUrl = url.AbsoluteUri;
            var duration = item.TryGetProperty("duration", out var durationValue) && durationValue.ValueKind == JsonValueKind.Number &&
                           durationValue.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
            return new(id, title, CleanText(ReadString(item, "extractor_key"), 80) is { Length: > 0 } platform ? platform : url.Host,
                pageUrl, duration);
        }
        catch (JsonException) { throw new InvalidOperationException("下载工具返回了无效的视频信息，请更新 yt-dlp 后重试。"); }
    }

    private async Task<List<string>> CommonArgumentsAsync(OnlineVideoSettings settings, Uri url, string job, CancellationToken cancellationToken)
    {
        var proxy = settings.Proxy.Trim();
        if (proxy.Length > 0 && (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri) ||
            proxyUri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5" or "socks5h") ||
            string.IsNullOrWhiteSpace(proxyUri.Host) || proxy.Any(char.IsControl)))
            throw new InvalidOperationException("代理地址无效，请使用 http://、https:// 或 socks5:// 等完整地址。");
        var arguments = new List<string>
        {
            "--ignore-config", "--no-config-locations", "--no-plugin-dirs", "--no-exec", "--no-remote-components",
            "--no-cache-dir", "--no-playlist", "--no-mark-watched", "--no-wait-for-video", "--no-update",
            "--no-js-runtimes", "--js-runtimes", "deno:" + RequireTool(settings.JavaScriptRuntimePath),
            "--no-colors", "--encoding", "utf-8", "--socket-timeout", "30", "--retries", "2",
            "--fragment-retries", "2", "--extractor-retries", "1", "--concurrent-fragments", "1",
            "--abort-on-unavailable-fragments"
        };
        // An empty --proxy explicitly disables yt-dlp's environment/Windows proxy discovery.
        // Leave it unspecified unless the user has chosen an override.
        if (proxy.Length > 0) arguments.AddRange(["--proxy", proxy]);
        if (!string.IsNullOrWhiteSpace(settings.CookiesFilePath))
        {
            try
            {
                var source = new FileInfo(settings.CookiesFilePath);
                if (!source.Exists || source.Length is <= 0 or > 5_242_880)
                    throw new InvalidOperationException("Cookie 文件不存在、为空或超过 5 MB，请重新选择 Netscape 格式文件。");
                await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                using (var reader = new StreamReader(input, leaveOpen: true))
                {
                    var header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (header is null || !(header.StartsWith("# Netscape HTTP Cookie File", StringComparison.Ordinal) ||
                                            header.StartsWith("# HTTP Cookie File", StringComparison.Ordinal)))
                        throw new InvalidOperationException("Cookie 文件格式无效，请选择 Netscape 格式的 cookies.txt 文件。");
                }
                input.Position = 0;
                var cookieCopy = Path.Combine(job, "session-cookies.txt");
                await using var output = new FileStream(cookieCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                arguments.AddRange(["--cookies", cookieCopy]);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { throw new InvalidOperationException("无法读取所选 Cookie 文件，请检查文件位置和读取权限。"); }
        }
        else if (_websiteSessions is not null && VideoWebsites.FromUrl(url.AbsoluteUri) is { } website)
        {
            var session = await _websiteSessions.LoadAsync(website, cancellationToken).ConfigureAwait(false);
            if (session is not null)
            {
                session = NetscapeCookieFormatter.ValidateAndSnapshot(session, DateTimeOffset.UtcNow);
                if (session.Website != website)
                    throw new InvalidOperationException("保存的网站验证会话不匹配，请重新打开当前网站验证并保存会话。");
                if (!string.Equals(session.Proxy.Trim(), proxy, StringComparison.Ordinal))
                    throw new InvalidOperationException("当前代理与网站验证时的代理不同，请使用当前代理重新打开网站验证并保存会话。");
                var cookieContent = NetscapeCookieFormatter.Format(session);
                var cookieCopy = Path.Combine(job, "session-cookies.txt");
                try
                {
                    await using var output = new FileStream(cookieCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(cookieContent);
                    try { await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new InvalidOperationException("无法为下载任务准备网站验证会话，请检查下载目录的写入权限。"); }
                arguments.AddRange(["--cookies", cookieCopy, "--user-agent", session.UserAgent]);
            }
        }
        return arguments;
    }

    private async Task<(string Video, string Audio, string PixelFormat)> ReadCodecsAsync(string path, MediaSettings settings,
        string job, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(settings.FfprobePath,
            ["-v", "error", "-show_entries", "stream=codec_type,codec_name,pix_fmt", "-of", "json", path],
            job, cancellationToken, outputLimit: 65_536).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.FirstOrDefault(s => ReadString(s, "codec_type") == "video");
            var audio = streams.FirstOrDefault(s => ReadString(s, "codec_type") == "audio");
            return (ReadString(video, "codec_name"), ReadString(audio, "codec_name"), ReadString(video, "pix_fmt"));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidOperationException("无法检查下载视频的编码，请检查 FFprobe 配置后重试。"); }
    }

    private async Task<OnlineVideoProcessResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments,
        string job, CancellationToken cancellationToken, int outputLimit = 4_194_304)
    {
        var result = await _runner.RunAsync(new(RequireTool(executable), arguments, job, OutputLimit: outputLimit), null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) ThrowFailure(result.Error);
        return result;
    }

    private static Uri ValidateRequest(OnlineVideoRequest request)
    {
        if (request.MaxHeight < 0 || request.MaxHeight > 4320) throw new InvalidOperationException("视频清晰度应为自动或不超过 4320p。");
        var text = request.Url.Trim();
        if (text.Length > 4096 || text.Any(char.IsControl) || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IsPrivateAddress(ip))
            throw new InvalidOperationException("请粘贴视频网站的完整 http:// 或 https:// 链接。");
        var host = uri.IdnHost.ToLowerInvariant();
        if ((host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal)) &&
            !(uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase) && uri.Query.Contains("v=", StringComparison.Ordinal) ||
              new[] { "/shorts/", "/live/", "/embed/" }.Any(p => uri.AbsolutePath.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("请使用 YouTube 单个视频链接，暂不支持频道、搜索结果或播放列表。");
        if (host == "live.bilibili.com" || host == "space.bilibili.com")
            throw new InvalidOperationException("请使用 B 站单个视频链接，暂不支持直播间或 UP 主空间。");
        return uri;
    }

    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] is 0 or 10 or 127 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] >= 224
            : ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal || (bytes[0] & 0xfe) == 0xfc;
    }

    private static string FormatSelector(int height)
    {
        var limit = height == 0 ? "" : $"[height<=?{height.ToString(CultureInfo.InvariantCulture)}]";
        return $"bv{limit}+ba/b{limit}";
    }

    private static string ValidateDownloadedFile(string? path, string job, bool final = false)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("下载未产生完整视频，请检查登录状态、清晰度和网站访问情况。");
        string full;
        try { full = Path.GetFullPath(path, job); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidOperationException("下载工具返回了无效的文件位置。"); }
        var filename = Path.GetFileName(full);
        if (!Path.GetDirectoryName(full)!.Equals(job, StringComparison.OrdinalIgnoreCase) ||
            !(final ? filename == "video.mp4" : filename.StartsWith("media.", StringComparison.Ordinal) && filename.Count(c => c == '.') == 1) ||
            !File.Exists(full) || new FileInfo(full).Length < 32 ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("下载文件缺失或位置异常，已停止导入。");
        return full;
    }

    private static void ThrowFailure(string diagnostic)
    {
        throw new InvalidOperationException(OnlineVideoErrors.Describe(diagnostic));
    }

    private static string RequireTool(string path) => !string.IsNullOrWhiteSpace(path) && !path.Any(char.IsControl)
        ? path.Trim() : throw new InvalidOperationException("请先在设置中配置 yt-dlp、Deno 和 FFmpeg 的可执行文件路径。");
    private static string ResolveToolLocation(string path)
    {
        path = RequireTool(path);
        if (Path.IsPathRooted(path) || path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(path);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            foreach (var name in OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                         ? new[] { path + ".exe", path } : new[] { path })
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (ArgumentException) { }
            }
        }
        return path; // The media availability check provides the actionable error for an absent tool.
    }
    private static string ReadString(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool ReadBoolean(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static string CleanText(string text, int length) => new string(text.Where(c => !char.IsControl(c)).Take(length).ToArray());
    private static void Report(IProgress<ProcessingProgress>? progress, string stage, double percent, string message) => progress?.Report(new(stage, percent, message));
    private static string TemporaryRoot() => Path.Combine(Path.GetTempPath(), "LinguaForge-X", "online-inspect");
    private static string CreateJob(string root)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new InvalidOperationException("无法创建在线视频下载目录，请在设置中选择可写入的本地文件夹。"); }
    }

    private static void DeleteCookieCopy(string job)
    {
        try { File.Delete(Path.Combine(job, "session-cookies.txt")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteJob(string job)
    {
        // Only service-created GUID directories are traversed. Never recurse through junctions or symlinks.
        if (!Guid.TryParseExact(Path.GetFileName(job), "N", out _) || !Directory.Exists(job)) return;
        try { DeleteSafeDirectory(job); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { DeleteCookieCopy(job); }
    }

    private static void DeleteSafeDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                if (Directory.Exists(entry)) DeleteSafeDirectory(entry);
                else File.Delete(entry);
        Directory.Delete(path);
    }
}
