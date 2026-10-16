using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.OnlineVideo;
using Xunit;

namespace LinguaForge_X.Tests;

public sealed class WebsiteSessionTests
{
    private static readonly DateTimeOffset SampleNow = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static WebsiteCookie Cookie(string domain = ".youtube.com", string value = "SECRET_SESSION") =>
        new(domain, "/", "session", value, true, true, 0, true);
    private static WebsiteSession Session(VideoWebsite website = VideoWebsite.YouTube, string proxy = "") =>
        new(website, [Cookie(website == VideoWebsite.YouTube ? ".youtube.com" : ".bilibili.com")], "Test Browser/1.0", proxy, DateTimeOffset.UtcNow);

    [Fact]
    public void CookieExportScopesDomainsAndPreservesNetscapeSessionSemantics()
    {
        var persistentExpiry = SampleNow.AddDays(1).ToUnixTimeSeconds();
        var session = Session() with
        {
            CapturedAt = SampleNow,
            Cookies = [Cookie(), Cookie("www.youtube.com", "HOST_ONLY") with { HttpOnly = false, Secure = false },
                Cookie() with { Name = "persistent", Session = false, ExpiresUnixSeconds = persistentExpiry + .5 },
                Cookie() with { Name = "expired", Session = false, ExpiresUnixSeconds = SampleNow.AddSeconds(-1).ToUnixTimeSeconds() },
                Cookie(".google.com", "OTHER_ACCOUNT"), Cookie("notyoutube.com", "BAD_SUFFIX"),
                Cookie("youtube.com.evil.example", "BAD_PREFIX"), Cookie(".bilibili.com", "OTHER_SITE")]
        };
        var formatted = NetscapeCookieFormatter.Format(session, SampleNow);
        Assert.StartsWith("# Netscape HTTP Cookie File\r\n", formatted);
        Assert.Contains("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t0\tsession\tSECRET_SESSION\r\n", formatted);
        Assert.Contains("www.youtube.com\tFALSE\t/\tFALSE\t0\tsession\tHOST_ONLY\r\n", formatted);
        Assert.Contains($"\t{persistentExpiry}\tpersistent\t", formatted);
        Assert.DoesNotContain("expired", formatted);
        Assert.DoesNotContain("OTHER_", formatted);
        Assert.DoesNotContain("BAD_", formatted);
        Assert.Equal(3, formatted.Split("\r\n").Count(line => line.Contains('\t')));
        Assert.DoesNotContain('\n', formatted.Replace("\r\n", ""));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("value")]
    [InlineData("path")]
    [InlineData("domain")]
    [InlineData("agent")]
    [InlineData("proxy")]
    public void CookieExportRejectsFieldInjectionWithoutLeakingValues(string field)
    {
        var cookie = Cookie();
        var session = Session();
        const string injected = "PRIVATE_SECRET\tINJECTED\r\n";
        cookie = field switch
        {
            "name" => cookie with { Name = injected },
            "value" => cookie with { Value = injected },
            "path" => cookie with { Path = "/" + injected },
            "domain" => cookie with { Domain = ".youtube.com" + injected },
            _ => cookie
        };
        session = session with
        {
            Cookies = [cookie],
            UserAgent = field == "agent" ? injected : session.UserAgent,
            Proxy = field == "proxy" ? injected : session.Proxy
        };
        var error = Assert.Throws<InvalidOperationException>(() => NetscapeCookieFormatter.Format(session));
        Assert.DoesNotContain("PRIVATE_SECRET", error.ToString());
    }

    [Fact]
    public void CookieExportRejectsExpiredEmptyOversizedAndInvalidSessions()
    {
        Assert.Throws<InvalidOperationException>(() => NetscapeCookieFormatter.Format(Session() with { CapturedAt = SampleNow.AddHours(-25) }, SampleNow));
        Assert.Throws<InvalidOperationException>(() => NetscapeCookieFormatter.Format(Session() with { CapturedAt = SampleNow.AddMinutes(6) }, SampleNow));
        Assert.Throws<InvalidOperationException>(() => NetscapeCookieFormatter.Format(Session() with { Cookies = [] }));
        Assert.Throws<InvalidOperationException>(() => NetscapeCookieFormatter.Format(Session() with { Cookies = [Cookie(".google.com")] }));
        Assert.Throws<InvalidOperationException>(() => NetscapeCookieFormatter.Format(Session() with { Cookies = Enumerable.Repeat(Cookie(), 3001).ToArray() }));
        Assert.Throws<InvalidOperationException>(() => NetscapeCookieFormatter.Format(Session() with { Cookies = [Cookie(value: new string('a', 16_385))] }));
        Assert.Throws<InvalidOperationException>(() => NetscapeCookieFormatter.Format(Session() with { Cookies = [Cookie() with { ExpiresUnixSeconds = double.NaN }] }));
        Assert.Throws<InvalidOperationException>(() => NetscapeCookieFormatter.Format(Session() with { Cookies = [Cookie() with { Session = false, ExpiresUnixSeconds = 0 }] }));
    }

    [Fact]
    public async Task ProtectedStoreEncryptsEntireSessionAndSeparatesWebsites()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TemporaryDirectory();
        var store = new ProtectedWebsiteSessionStore(fixture.Path);
        var original = Session(proxy: "http://user:PROXY_SECRET@example.org:8080") with { UserAgent = "PRIVATE_USER_AGENT" };
        var inputCookies = original.Cookies.ToArray();
        await store.SaveAsync(original with { Cookies = inputCookies }, default);
        inputCookies[0] = Cookie(value: "MUTATED");
        await store.SaveAsync(Session(VideoWebsite.Bilibili), default);
        var ciphertext = await File.ReadAllBytesAsync(System.IO.Path.Combine(fixture.Path, "youtube.bin"));
        Assert.DoesNotContain("SECRET_SESSION", Encoding.UTF8.GetString(ciphertext));
        Assert.DoesNotContain("PROXY_SECRET", Encoding.UTF8.GetString(ciphertext));
        Assert.DoesNotContain("PRIVATE_USER_AGENT", Encoding.UTF8.GetString(ciphertext));
        var loaded = Assert.IsType<WebsiteSession>(await store.LoadAsync(VideoWebsite.YouTube, default));
        Assert.Equal(original.UserAgent, loaded.UserAgent);
        Assert.Equal(original.Proxy, loaded.Proxy);
        Assert.Equal(original.Cookies, loaded.Cookies);
        Assert.Equal(VideoWebsite.Bilibili, (await store.LoadAsync(VideoWebsite.Bilibili, default))!.Website);
        await store.DeleteAsync(VideoWebsite.YouTube, default);
        Assert.Null(await store.LoadAsync(VideoWebsite.YouTube, default));
        Assert.NotNull(await store.LoadAsync(VideoWebsite.Bilibili, default));
        Assert.Single(Directory.GetFiles(fixture.Path));
    }

    [Fact]
    public async Task ProtectedStoreRejectsCorruptOversizedAndCrossSitePayloads()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TemporaryDirectory();
        var store = new ProtectedWebsiteSessionStore(fixture.Path);
        var youtube = System.IO.Path.Combine(fixture.Path, "youtube.bin");
        await File.WriteAllTextAsync(youtube, "BROKEN_SECRET");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync(VideoWebsite.YouTube, default));
        Assert.DoesNotContain("BROKEN_SECRET", error.ToString());
        await using (var file = File.Create(youtube)) file.SetLength(6 * 1024 * 1024);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync(VideoWebsite.YouTube, default));
        await store.SaveAsync(Session(VideoWebsite.Bilibili), default);
        File.Copy(System.IO.Path.Combine(fixture.Path, "bilibili.bin"), youtube, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync(VideoWebsite.YouTube, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.LoadAsync((VideoWebsite)789, default));
    }

    [Fact]
    public async Task ProtectedStoreRejectsExpiredEncryptedSessionAndKeepsPreviousOnCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TemporaryDirectory();
        var store = new ProtectedWebsiteSessionStore(fixture.Path);
        await store.SaveAsync(Session(), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Session() with { UserAgent = "changed" }, cancellation.Token));
        Assert.Equal("Test Browser/1.0", (await store.LoadAsync(VideoWebsite.YouTube, default))!.UserAgent);
        Assert.Single(Directory.GetFiles(fixture.Path));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Session = Session() with { CapturedAt = DateTimeOffset.UtcNow.AddDays(-2) } });
        var encrypted = ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes("LinguaForge-X/website-sessions/v1"), DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(fixture.Path, "youtube.bin"), encrypted);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync(VideoWebsite.YouTube, default));
        Assert.Contains("过期", error.Message);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk", VideoWebsite.YouTube)]
    [InlineData("https://youtu.be/abcdefghijk", VideoWebsite.YouTube)]
    [InlineData("https://www.bilibili.com/video/BVabcdefghij", VideoWebsite.Bilibili)]
    [InlineData("https://b23.tv/abcdef", VideoWebsite.Bilibili)]
    public async Task MatchingSessionIsAppliedAndPlaintextRemovedAfterInspect(string url, VideoWebsite website)
    {
        var store = new FakeStore { Session = Session(website) };
        var runner = new RecordingRunner();
        var service = new YtDlpOnlineVideoService(new FakeMedia(), runner, store);
        await service.InspectAsync(new() { Url = url }, new(), default);
        Assert.Equal(website, Assert.Single(store.Loaded));
        Assert.Contains("SECRET_SESSION", Assert.Single(runner.CookieContents));
        var request = Assert.Single(runner.Requests);
        Assert.Equal(store.Session.UserAgent, Argument(request, "--user-agent"));
        Assert.False(Directory.Exists(request.WorkingDirectory));
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task OtherSiteAndMissingSessionContinueWithoutSessionArguments()
    {
        var store = new FakeStore();
        var runner = new RecordingRunner();
        var service = new YtDlpOnlineVideoService(new FakeMedia(), runner, store);
        await service.InspectAsync(new() { Url = "https://youtube.com.example.org/video.mp4" }, new(), default);
        Assert.Empty(store.Loaded);
        await service.InspectAsync(new() { Url = "https://www.youtube.com/watch?v=abcdefghijk" }, new(), default);
        Assert.Single(store.Loaded);
        Assert.All(runner.Requests, r => { Assert.DoesNotContain("--cookies", r.Arguments); Assert.DoesNotContain("--user-agent", r.Arguments); });
    }

    [Fact]
    public async Task ExplicitCookieFileTakesPrecedenceOverSavedSession()
    {
        using var fixture = new TemporaryDirectory();
        var file = System.IO.Path.Combine(fixture.Path, "cookies.txt");
        const string original = "# Netscape HTTP Cookie File\r\n.youtube.com\tTRUE\t/\tTRUE\t0\tsession\tEXPLICIT_COOKIE\r\n";
        await File.WriteAllTextAsync(file, original);
        var store = new FakeStore { Session = Session() with { CapturedAt = DateTimeOffset.UtcNow.AddDays(-1) } };
        var runner = new RecordingRunner();
        await new YtDlpOnlineVideoService(new FakeMedia(), runner, store).InspectAsync(
            new() { Url = "https://www.youtube.com/watch?v=abcdefghijk" }, new() { CookiesFilePath = file }, default);
        Assert.Empty(store.Loaded);
        Assert.Equal(original, Assert.Single(runner.CookieContents));
        Assert.Equal(original, await File.ReadAllTextAsync(file));
        Assert.DoesNotContain("--user-agent", Assert.Single(runner.Requests).Arguments);
    }

    [Theory]
    [InlineData("proxy", "代理")]
    [InlineData("site", "不匹配")]
    [InlineData("expired", "过期")]
    public async Task UnsuitableSessionFailsBeforeProcessWithoutSensitiveDiagnostics(string scenario, string message)
    {
        var session = Session();
        session = scenario switch
        {
            "proxy" => session with { Proxy = "http://user:PROXY_SECRET@127.0.0.1:8080" },
            "site" => Session(VideoWebsite.Bilibili),
            _ => session with { CapturedAt = DateTimeOffset.UtcNow.AddHours(-25) }
        };
        var runner = new RecordingRunner();
        var service = new YtDlpOnlineVideoService(new FakeMedia(), runner, new FakeStore { Session = session });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InspectAsync(
            new() { Url = "https://www.youtube.com/watch?v=abcdefghijk" }, new(), default));
        Assert.Contains(message, error.Message);
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task DownloadRemovesSessionCopiesOnAllExitPaths(string outcome)
    {
        using var fixture = new TemporaryDirectory();
        var runner = new RecordingRunner { Outcome = outcome };
        var service = new YtDlpOnlineVideoService(new FakeMedia(), runner, new FakeStore { Session = Session() });
        var task = service.DownloadAsync(new() { Url = "https://www.youtube.com/watch?v=abcdefghijk" },
            new() { DownloadDirectory = fixture.Path }, new(), null, default);
        if (outcome == "success") Assert.True(File.Exists((await task).FilePath));
        else if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.NotEmpty(runner.CookieContents);
        Assert.All(runner.Requests, r => Assert.False(File.Exists(System.IO.Path.Combine(r.WorkingDirectory, "session-cookies.txt"))));
        if (outcome == "success") Assert.Single(Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories));
        else Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
    }

    private static string Argument(OnlineVideoProcessRequest request, string flag) => request.Arguments[request.Arguments.ToList().IndexOf(flag) + 1];
    private sealed class FakeStore : IWebsiteSessionStore
    {
        public WebsiteSession? Session { get; set; }
        public List<VideoWebsite> Loaded { get; } = [];
        public int Saves { get; private set; }
        public Task<WebsiteSession?> LoadAsync(VideoWebsite website, CancellationToken cancellationToken) { Loaded.Add(website); return Task.FromResult(Session); }
        public Task SaveAsync(WebsiteSession session, CancellationToken cancellationToken) { Saves++; return Task.CompletedTask; }
        public Task DeleteAsync(VideoWebsite website, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingRunner : IOnlineVideoProcessRunner
    {
        public List<OnlineVideoProcessRequest> Requests { get; } = [];
        public List<string> CookieContents { get; } = [];
        public string Outcome { get; set; } = "success";
        public Task<OnlineVideoProcessResult> RunAsync(OnlineVideoProcessRequest request, Action<string>? outputLine, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Arguments.Contains("--cookies")) CookieContents.Add(File.ReadAllText(Argument(request, "--cookies")));
            if (request.Arguments.Contains("--dump-single-json"))
                return Task.FromResult(new OnlineVideoProcessResult(0, "{\"id\":\"video\",\"title\":\"sample\"}", ""));
            if (request.Executable == "ffprobe")
                return Task.FromResult(new OnlineVideoProcessResult(0, "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"pix_fmt\":\"yuv420p\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}]}", ""));
            if (Outcome == "cancel") throw new OperationCanceledException();
            if (Outcome == "failure") return Task.FromResult(new OnlineVideoProcessResult(1, "", "ERROR: Sign in to confirm you're not a bot"));
            var path = System.IO.Path.Combine(request.WorkingDirectory, "media.mp4");
            File.WriteAllBytes(path, new byte[64]);
            outputLine!("LF_OUTPUT:" + JsonSerializer.Serialize(path));
            return Task.FromResult(new OnlineVideoProcessResult(0, "", ""));
        }
    }

    private sealed class FakeMedia : IMediaService
    {
        public Task<double> GetAudioDurationAsync(string path, MediaSettings settings, CancellationToken token) => Task.FromResult(1d);
        public Task CheckAvailableAsync(MediaSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<VideoInfo> ProbeAsync(string path, MediaSettings settings, CancellationToken cancellationToken) => Task.FromResult(new VideoInfo(path, 10, 1920, 1080, [new AudioTrack(1, "audio")]));
        public Task<IReadOnlyList<AudioChunk>> ExtractAudioChunksAsync(VideoInfo video, int audioStreamIndex, string workDirectory, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task BuildDubbedAudioAsync(IReadOnlyList<SpeechClip> clips, double videoDurationSeconds, double maxSpeechSpeed, string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RenderAsync(VideoInfo video, ConversionOptions options, string? subtitlePath, string? dubbedAudioPath, string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "linguaforge-session-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(resolved).StartsWith("linguaforge-session-test-", StringComparison.Ordinal)) throw new InvalidOperationException();
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
