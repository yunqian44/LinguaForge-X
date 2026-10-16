using System.Diagnostics;
using System.Text.Json;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.OnlineVideo;
using Xunit;

namespace LinguaForge_X.Tests;

public sealed class OnlineVideoServiceTests
{
    [Fact]
    public async Task InspectionPreservesBilibiliPartAndIsolatesConfiguration()
    {
        using var fixture = new Fixture();
        var request = new OnlineVideoRequest { Url = "https://www.bilibili.com/video/BV1234567890?p=3" };
        var info = await fixture.Service.InspectAsync(request, fixture.Settings, default);
        Assert.Equal("测试素材", info.Title);
        var invocation = Assert.Single(fixture.Runner.Requests);
        Assert.Equal(request.Url, invocation.Arguments.Last());
        foreach (var option in new[] { "--ignore-config", "--no-config-locations", "--no-plugin-dirs", "--no-exec", "--no-remote-components", "--no-cache-dir", "--no-playlist", "--flat-playlist" })
            Assert.Contains(option, invocation.Arguments);
        Assert.DoesNotContain("--cookies-from-browser", invocation.Arguments);
        Assert.Equal("deno:deno-test", Value(invocation, "--js-runtimes"));
        Assert.False(Directory.Exists(invocation.WorkingDirectory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankProxyPreservesSystemAndEnvironmentDiscovery(string proxy)
    {
        using var fixture = new Fixture();
        fixture.Settings.Proxy = proxy;
        await fixture.Service.InspectAsync(fixture.Request, fixture.Settings, default);
        Assert.DoesNotContain("--proxy", Assert.Single(fixture.Runner.Requests).Arguments);
    }

    [Fact]
    public async Task YoutubeWatchLinkWithPlaylistStillTargetsOneVideo()
    {
        using var fixture = new Fixture();
        fixture.Request.Url = "https://www.youtube.com/watch?v=zIwLWfaAg-8&list=PLI7EV3IpiQieV_FwI0-KaiEDhJYseeQTv";
        await fixture.Service.InspectAsync(fixture.Request, fixture.Settings, default);
        var request = Assert.Single(fixture.Runner.Requests);
        Assert.Contains("--no-playlist", request.Arguments);
        Assert.Equal(fixture.Request.Url, request.Arguments.Last());
    }

    [Theory]
    [InlineData("ERROR: [youtube] id: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for authentication.", "不是机器人")]
    [InlineData("ERROR: [youtube] id: Sign in to confirm you're not a bot.", "不是机器人")]
    [InlineData("ERROR: [youtube] id: YouTube is requiring a captcha challenge before playback", "不是机器人")]
    [InlineData("ERROR: [youtube] id: This is a private video. Sign in if you've been granted access.", "私有视频")]
    [InlineData("ERROR: [youtube] id: This video is available to this channel's members-only content.", "会员")]
    [InlineData("ERROR: [youtube] id: Sign in to confirm your age. This video may be inappropriate for some users.", "年龄限制")]
    [InlineData("ERROR: The provided YouTube account cookies are no longer valid", "已失效")]
    [InlineData("WARNING: Pass --cookies to authenticate\nERROR: Requested format is not available. Use --cookies for authentication.", "音视频格式")]
    [InlineData("WARNING: Sign in with cookies for more formats\nERROR: HTTP Error 403: Forbidden. See https://example.org/cookies", "请求被限制")]
    [InlineData("WARNING: Some formats contain DRM\nERROR: HTTP Error 429: Too Many Requests", "请求频率")]
    [InlineData("ERROR: [youtube] Your current session has been rate-limited", "请求频率")]
    [InlineData("ERROR: [youtube] This content isn't available, try again later", "请求频率")]
    [InlineData("WARNING: Cookies have expired\nERROR: [youtube] This video is unavailable", "视频不可用")]
    [InlineData("ERROR: Unable to connect to proxy http://user:SECRET@127.0.0.1:7890", "代理连接失败")]
    [InlineData("ERROR: [youtube] Sign in to watch this video. Use --cookies", "网站要求登录")]
    [InlineData("ERROR: [youtube] This video is not available in your country", "地区访问限制")]
    [InlineData("ERROR: [youtube] PO Token is required. Use --cookies", "播放验证令牌")]
    [InlineData("ERROR: [youtube] JavaScript challenge solving failed. Use --cookies", "脚本解析失败")]
    public async Task FailureReportsActualCauseWithoutCookieHelpMisclassification(string diagnostic, string expected)
    {
        using var fixture = new Fixture();
        fixture.Runner.DownloadBehavior = (_, _) => new(1, "", diagnostic);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DownloadAsync(fixture.Request, fixture.Settings, fixture.MediaSettings, null, default));
        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("SECRET", exception.Message);
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    [Theory]
    [InlineData("file:///C:/secret.mp4")]
    [InlineData("https://user:secret@www.youtube.com/watch?v=abcdefghijk")]
    [InlineData("http://127.0.0.1/test")]
    [InlineData("http://192.168.1.5/test")]
    [InlineData("https://www.youtube.com/playlist?list=abc")]
    [InlineData("https://www.youtube.com/@channel")]
    [InlineData("https://space.bilibili.com/123")]
    [InlineData("https://live.bilibili.com/123")]
    public async Task UnsupportedInputsAreRejectedBeforeStartingProcess(string url)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.InspectAsync(new() { Url = url }, fixture.Settings, default));
        Assert.Empty(fixture.Runner.Requests);
    }

    [Theory]
    [InlineData("{\"_type\":\"playlist\",\"entries\":[]}", "单个视频")]
    [InlineData("{\"is_live\":true}", "直播")]
    [InlineData("{\"live_status\":\"is_upcoming\"}", "预约")]
    [InlineData("{\"live_status\":\"post_live\"}", "直播")]
    [InlineData("{\"has_drm\":true}", "DRM")]
    [InlineData("{\"formats\":[{\"has_drm\":true}]}", "DRM")]
    [InlineData("{\"formats\":[{\"vcodec\":\"none\"}]}", "音频")]
    [InlineData("not json", "无效")]
    public async Task InspectionRejectsNonVideoResults(string json, string message)
    {
        using var fixture = new Fixture();
        fixture.Runner.Inspection = json;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.InspectAsync(fixture.Request, fixture.Settings, default));
        Assert.Contains(message, error.Message);
        Assert.All(fixture.Runner.Requests, r => Assert.False(Directory.Exists(r.WorkingDirectory)));
    }

    [Fact]
    public async Task InspectionHandlesUnknownDurationAndUntrustedWebpageUrl()
    {
        using var fixture = new Fixture();
        fixture.Runner.Inspection = "{\"id\":\"v\",\"title\":\"title\",\"duration\":null,\"webpage_url\":\"javascript:alert(1)\"}";
        var info = await fixture.Service.InspectAsync(fixture.Request, fixture.Settings, default);
        Assert.Equal(0, info.DurationSeconds);
        Assert.Equal(fixture.Request.Url, info.WebpageUrl);
    }

    [Theory]
    [InlineData(0, "bv+ba/b")]
    [InlineData(720, "bv[height<=?720]+ba/b[height<=?720]")]
    [InlineData(1080, "bv[height<=?1080]+ba/b[height<=?1080]")]
    [InlineData(2160, "bv[height<=?2160]+ba/b[height<=?2160]")]
    public async Task DownloadSelectsQualityAndKeepsOnlyValidatedMp4(int height, string selector)
    {
        using var fixture = new Fixture();
        fixture.Request.MaxHeight = height;
        var progress = new ProgressCollector();
        var downloaded = await fixture.Service.DownloadAsync(fixture.Request, fixture.Settings, fixture.MediaSettings, progress, default);
        Assert.Equal("video.mp4", Path.GetFileName(downloaded.FilePath));
        Assert.True(File.Exists(downloaded.FilePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(downloaded.FilePath)!));
        Assert.Equal(12, downloaded.Video.DurationSeconds);
        var invocation = Assert.Single(fixture.Runner.Requests, r => r.Arguments.Contains("--format"));
        Assert.Equal(selector, Value(invocation, "--format"));
        Assert.Equal("media.%(ext)s", Value(invocation, "--output"));
        Assert.Contains("--no-simulate", invocation.Arguments);
        Assert.Contains("--abort-on-unavailable-fragments", invocation.Arguments);
        Assert.Equal("1", Value(invocation, "--max-downloads"));
        Assert.Contains(progress.Items, p => p.Stage == "下载在线视频" && p.Percent > 2);
        Assert.Equal(100, progress.Items.Last().Percent);
        Assert.DoesNotContain(fixture.Runner.Requests, r => r.Executable == "ffmpeg-test");
    }

    [Fact]
    public async Task NonCompatibleCodecGetsConvertedAndProbedAgain()
    {
        using var fixture = new Fixture();
        fixture.Runner.Codecs = "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"av1\",\"pix_fmt\":\"yuv420p10le\"},{\"codec_type\":\"audio\",\"codec_name\":\"opus\"}]}";
        var downloaded = await fixture.Service.DownloadAsync(fixture.Request, fixture.Settings, fixture.MediaSettings, null, default);
        var conversion = Assert.Single(fixture.Runner.Requests, r => r.Executable == "ffmpeg-test");
        Assert.Equal("libx264", Value(conversion, "-c:v"));
        Assert.Equal("aac", Value(conversion, "-c:a"));
        Assert.Equal("yuv420p", Value(conversion, "-pix_fmt"));
        Assert.Equal(2, fixture.Media.Probes);
        Assert.True(File.Exists(downloaded.FilePath));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("missing")]
    [InlineData("wrong_name")]
    [InlineData("duplicate")]
    public async Task DownloadCannotImportMissingOrUnconfinedOutput(string scenario)
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside.mp4");
        await File.WriteAllBytesAsync(outside, new byte[64]);
        fixture.Runner.DownloadBehavior = (request, callback) =>
        {
            var path = scenario == "outside" ? outside : Path.Combine(request.WorkingDirectory, scenario == "wrong_name" ? "unsafe.mp4" : "media.mp4");
            if (scenario != "missing") File.WriteAllBytes(path, new byte[64]);
            callback!("LF_OUTPUT:" + JsonSerializer.Serialize(path));
            if (scenario == "duplicate") callback("LF_OUTPUT:" + JsonSerializer.Serialize(path));
            return new(0, "", "");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DownloadAsync(fixture.Request, fixture.Settings, fixture.MediaSettings, null, default));
        Assert.True(File.Exists(outside));
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task CookiesAreCopiedWithoutMutatingOriginalAndRemovedAfterSuccess(int exitCode)
    {
        using var fixture = new Fixture();
        var cookie = Path.Combine(fixture.Root, "original-cookies.txt");
        const string content = "# Netscape HTTP Cookie File\n.example.org\tTRUE\t/\tFALSE\t0\tsession\tSECRET_VALUE\n";
        await File.WriteAllTextAsync(cookie, content);
        fixture.Settings.CookiesFilePath = cookie;
        fixture.Settings.Proxy = "socks5://user:PROXY_SECRET@127.0.0.1:7890";
        fixture.Runner.DownloadExitCode = exitCode;
        var downloaded = await fixture.Service.DownloadAsync(fixture.Request, fixture.Settings, fixture.MediaSettings, null, default);
        foreach (var request in fixture.Runner.Requests.Where(r => r.Arguments.Contains("--cookies")))
        {
            var copy = Value(request, "--cookies");
            Assert.NotEqual(cookie, copy);
            Assert.False(File.Exists(copy));
            Assert.Equal(fixture.Settings.Proxy, Value(request, "--proxy"));
        }
        Assert.Equal(content, await File.ReadAllTextAsync(cookie));
        Assert.True(File.Exists(downloaded.FilePath));
    }

    [Fact]
    public async Task FailedDownloadRemovesCookieCopyAndNeverShowsRawDiagnostics()
    {
        using var fixture = new Fixture();
        var cookie = Path.Combine(fixture.Root, "cookies.txt");
        await File.WriteAllTextAsync(cookie, "# Netscape HTTP Cookie File\n");
        fixture.Settings.CookiesFilePath = cookie;
        fixture.Runner.DownloadBehavior = (_, _) => new(1, "", "Proxy connection failed https://user:SUPERSECRET@example.org/watch?token=HIDDEN");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DownloadAsync(fixture.Request, fixture.Settings, fixture.MediaSettings, null, default));
        Assert.DoesNotContain("SUPERSECRET", error.Message);
        Assert.DoesNotContain("HIDDEN", error.Message);
        Assert.Contains("代理连接失败", error.Message);
        Assert.Empty(Directory.GetDirectories(fixture.Root));
        Assert.True(File.Exists(cookie));
    }

    [Fact]
    public async Task CanceledDownloadRemovesPartialFilesAndCookieCopy()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Runner.DownloadBehavior = (request, _) =>
        {
            File.WriteAllBytes(Path.Combine(request.WorkingDirectory, "media.mp4.part"), new byte[64]);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.DownloadAsync(fixture.Request, fixture.Settings, fixture.MediaSettings, null, cancellation.Token));
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    [Fact]
    public async Task ToolsCheckReportsBothVersionsAndRejectsOldDeno()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service.CheckAvailableAsync(fixture.Settings, default);
        Assert.Contains("yt-dlp 2026.09.23", result);
        Assert.Contains("deno 2.7.0", result);
        fixture.Runner.DenoVersion = "deno 2.2.0";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CheckAvailableAsync(fixture.Settings, default));
        Assert.Contains("2.3", error.Message);
    }

    [Theory]
    [InlineData("ftp://proxy.example.org")]
    [InlineData("proxy.example.org:8080")]
    [InlineData("http://user:secret@proxy.example.org\n--exec evil")]
    public async Task InvalidProxyNeverReachesProcess(string proxy)
    {
        using var fixture = new Fixture();
        fixture.Settings.Proxy = proxy;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.InspectAsync(fixture.Request, fixture.Settings, default));
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task InvalidCookiesNeverReachProcessOrChangeUserFile()
    {
        using var fixture = new Fixture();
        var cookie = Path.Combine(fixture.Root, "invalid.txt");
        const string original = "[{\"name\":\"session\",\"value\":\"SECRET\"}]";
        await File.WriteAllTextAsync(cookie, original);
        fixture.Settings.CookiesFilePath = cookie;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.InspectAsync(fixture.Request, fixture.Settings, default));
        Assert.DoesNotContain("SECRET", error.Message);
        Assert.Contains("Netscape", error.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(cookie));
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task ProcessRunnerBoundsOutputAndCancelsWithoutPumpingSynchronizationContext()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var runner = new OnlineVideoProcessRunner();
        var excessive = new OnlineVideoProcessRequest(powershell,
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine(('x' * 10000))"], fixture.Root, OutputLimit: 1024);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(excessive, null, default));
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NeverPumpingContext());
            try
            {
                runner.RunAsync(new(powershell, ["-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine('started'); Start-Sleep -Seconds 30"], fixture.Root),
                    line => { if (line == "started") started.TrySetResult(); }, cancellation.Token).GetAwaiter().GetResult();
                finished.TrySetResult(null);
            }
            catch (Exception error) { finished.TrySetResult(error); }
        }) { IsBackground = true };
        thread.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();
        var failure = await finished.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8));
    }

    private static string Value(OnlineVideoProcessRequest request, string name) => request.Arguments[request.Arguments.ToList().IndexOf(name) + 1];
    private sealed class NeverPumpingContext : SynchronizationContext { public override void Post(SendOrPostCallback d, object? state) { } }
    private sealed class ProgressCollector : IProgress<ProcessingProgress>
    {
        public List<ProcessingProgress> Items { get; } = [];
        public void Report(ProcessingProgress value) => Items.Add(value);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "linguaforge-online-test-" + Guid.NewGuid().ToString("N"));
        public OnlineVideoRequest Request { get; } = new() { Url = "https://www.youtube.com/watch?v=abcdefghijk" };
        public OnlineVideoSettings Settings { get; }
        public MediaSettings MediaSettings { get; } = new() { FfmpegPath = "ffmpeg-test", FfprobePath = "ffprobe-test" };
        public FakeRunner Runner { get; } = new();
        public FakeMedia Media { get; } = new();
        public YtDlpOnlineVideoService Service { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Settings = new() { DownloaderPath = "yt-dlp-test", JavaScriptRuntimePath = "deno-test", DownloadDirectory = Root };
            Service = new(Media, Runner);
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("linguaforge-online-test-", StringComparison.Ordinal)) throw new InvalidOperationException();
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private sealed class FakeRunner : IOnlineVideoProcessRunner
    {
        public List<OnlineVideoProcessRequest> Requests { get; } = [];
        public string Inspection { get; set; } = "{\"id\":\"v\",\"title\":\"测试素材\",\"extractor_key\":\"YouTube\",\"duration\":11.5,\"formats\":[{\"vcodec\":\"h264\",\"acodec\":\"aac\"}]}";
        public string Codecs { get; set; } = "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"pix_fmt\":\"yuv420p\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}]}";
        public string DenoVersion { get; set; } = "deno 2.7.0";
        public int DownloadExitCode { get; set; }
        public Func<OnlineVideoProcessRequest, Action<string>?, OnlineVideoProcessResult>? DownloadBehavior { get; set; }
        public Task<OnlineVideoProcessResult> RunAsync(OnlineVideoProcessRequest request, Action<string>? outputLine, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Executable == "deno-test") return Task.FromResult(new OnlineVideoProcessResult(0, DenoVersion, ""));
            if (request.Arguments.Contains("--version")) return Task.FromResult(new OnlineVideoProcessResult(0, "2026.09.23", ""));
            if (request.Arguments.Contains("--dump-single-json")) return Task.FromResult(new OnlineVideoProcessResult(0, Inspection, ""));
            if (request.Executable == "ffprobe-test") return Task.FromResult(new OnlineVideoProcessResult(0, Codecs, ""));
            if (request.Executable == "ffmpeg-test")
            {
                File.WriteAllBytes(request.Arguments.Last(), new byte[64]);
                return Task.FromResult(new OnlineVideoProcessResult(0, "", ""));
            }
            if (DownloadBehavior is not null) return Task.FromResult(DownloadBehavior(request, outputLine));
            var path = Path.Combine(request.WorkingDirectory, "media.mp4");
            File.WriteAllBytes(path, new byte[64]);
            outputLine!("LF_PROGRESS: 45.2%");
            outputLine("LF_POSTPROCESS");
            outputLine("LF_OUTPUT:" + JsonSerializer.Serialize(path));
            return Task.FromResult(new OnlineVideoProcessResult(DownloadExitCode, "", ""));
        }
    }

    private sealed class FakeMedia : IMediaService
    {
        public Task<double> GetAudioDurationAsync(string path, MediaSettings settings, CancellationToken token) => Task.FromResult(1d);
        public int Probes { get; private set; }
        public Task CheckAvailableAsync(MediaSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<VideoInfo> ProbeAsync(string path, MediaSettings settings, CancellationToken cancellationToken)
        {
            Probes++;
            return Task.FromResult(new VideoInfo(path, 12, 1920, 1080, [new AudioTrack(1, "audio")]));
        }
        public Task<IReadOnlyList<AudioChunk>> ExtractAudioChunksAsync(VideoInfo video, int audioStreamIndex, string workDirectory, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task BuildDubbedAudioAsync(IReadOnlyList<SpeechClip> clips, double videoDurationSeconds, double maxSpeechSpeed, string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RenderAsync(VideoInfo video, ConversionOptions options, string? subtitlePath, string? dubbedAudioPath, string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

}
