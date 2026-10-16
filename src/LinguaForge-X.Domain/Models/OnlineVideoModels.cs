namespace LinguaForge_X.Domain.Models;

public sealed class OnlineVideoSettings
{
    public string DownloaderPath { get; set; } = "yt-dlp";
    public string JavaScriptRuntimePath { get; set; } = "deno";
    public string DownloadDirectory { get; set; } = "";
    public string Proxy { get; set; } = "";
    public string CookiesFilePath { get; set; } = "";
}

public sealed class OnlineVideoRequest
{
    public string Url { get; set; } = "";
    public int MaxHeight { get; set; } = 1080;
}

public sealed record OnlineVideoInfo(string Id, string Title, string Platform,
    string WebpageUrl, double DurationSeconds);

public sealed record OnlineVideoDownload(OnlineVideoInfo Video, string FilePath);
