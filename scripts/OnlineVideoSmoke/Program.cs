using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Media;
using LinguaForge_X.Infrastructure.OnlineVideo;

// Explicit opt-in live diagnostic; no saved Azure settings or browser credentials are loaded.
if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--download"))
{
    Console.WriteLine("Run from repository root: dotnet run --project scripts/OnlineVideoSmoke -- <video-url> [--download]");
    return 2;
}
var root = Directory.GetCurrentDirectory();
var tools = Path.Combine(root, "artifacts", "online-tools");
var ffmpegRoot = Path.Combine(root, "artifacts", "media-tools");
string FindTool(string name) => Directory.EnumerateFiles(ffmpegRoot, name, SearchOption.AllDirectories).First();
var settings = new OnlineVideoSettings
{
    DownloaderPath = Path.Combine(tools, "yt-dlp.exe"),
    JavaScriptRuntimePath = Path.Combine(tools, "deno.exe"),
    DownloadDirectory = Path.Combine(root, "artifacts", "online-smoke")
};
var mediaSettings = new MediaSettings { FfmpegPath = FindTool("ffmpeg.exe"), FfprobePath = FindTool("ffprobe.exe") };
var media = new FfmpegMediaService();
var service = new YtDlpOnlineVideoService(media);
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(args.Length == 2 ? 10 : 2));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    Console.WriteLine(await service.CheckAvailableAsync(settings, cancellation.Token));
    var request = new OnlineVideoRequest { Url = args[0], MaxHeight = 720 };
    var info = await service.InspectAsync(request, settings, cancellation.Token);
    Console.WriteLine($"Parsed: {info.Platform} | {info.Title} | {info.DurationSeconds:F1} seconds");
    if (args.Length == 2)
    {
        var downloaded = await service.DownloadAsync(request, settings, mediaSettings,
            new ConsoleProgress(), cancellation.Token);
        var video = await media.ProbeAsync(downloaded.FilePath, mediaSettings, cancellation.Token);
        Console.WriteLine($"Downloaded: {downloaded.FilePath}");
        Console.WriteLine($"Verified: {video.Width}x{video.Height}; {video.DurationSeconds:F1} seconds; {video.AudioTracks.Count} audio tracks");
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled or diagnostic time limit reached.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

sealed class ConsoleProgress : IProgress<ProcessingProgress>
{
    private int _lastPercent = -1;
    private string _lastStage = "";
    public void Report(ProcessingProgress value)
    {
        var percent = (int)(value.Percent / 10) * 10;
        if (percent == _lastPercent && _lastStage == value.Stage) return;
        _lastPercent = percent;
        _lastStage = value.Stage;
        Console.WriteLine($"{value.Stage}: {value.Percent:F0}% {value.Message}");
    }
}
