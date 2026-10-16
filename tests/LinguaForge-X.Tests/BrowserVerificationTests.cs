using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.OnlineVideo;
using Xunit;

namespace LinguaForge_X.Tests;

public sealed class BrowserVerificationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("C:\\managed-browser-profile", true)]
    [InlineData("${local_app_data}\\ManagedEdge", true)]
    [InlineData(1, true)]
    public void MandatoryProfilePolicyIsRecognizedWithoutExpandingOrReadingItsDirectory(object? value, bool blocked) =>
        Assert.Equal(blocked, BrowserProfilePolicies.OverridesDirectory(value));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedOrUnreadablePolicyStopsBeforeCreatingProfileOrStartingBrowser(bool unreadable)
    {
        var root = Path.Combine(Path.GetTempPath(), "LinguaForge-X-browser-tests", Guid.NewGuid().ToString("N"));
        var service = new BrowserVerificationService("nonexistent-msedge.exe", root,
            _ => unreadable ? throw new UnauthorizedAccessException("PRIVATE_POLICY_PATH") : true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.OpenAsync("https://www.youtube.com/watch?v=local-test", "", CancellationToken.None));
        Assert.Contains("组织策略", error.Message);
        Assert.DoesNotContain("PRIVATE_POLICY_PATH", error.ToString());
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void BrowserArgumentsPreserveNormalBrowserAndSystemProxy()
    {
        var arguments = BrowserVerificationService.BrowserArguments("C:\\owned profile", 32123, "about:blank#unique", null);
        Assert.Contains("--user-data-dir=C:\\owned profile", arguments);
        Assert.Contains("--remote-debugging-port=32123", arguments);
        Assert.Contains("--remote-debugging-address=127.0.0.1", arguments);
        Assert.DoesNotContain(arguments, value => value.Contains("proxy") || value.Contains("headless") ||
            value.Contains("automation") || value.Contains("disable-") || value.Contains("remote-allow-origins"));
        Assert.Equal("about:blank#unique", arguments[^1]);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("https://proxy.example:8443", "https://proxy.example:8443")]
    [InlineData("socks5h://127.0.0.1:1080", "socks5://127.0.0.1:1080")]
    [InlineData("socks5://[::1]:1080", "socks5://[::1]:1080")]
    public void BrowserProxyUsesExplicitMatchingRoute(string input, string? expected) =>
        Assert.Equal(expected, BrowserVerificationService.ValidateProxy(input));

    [Theory]
    [InlineData("http://name:password@proxy:8080")]
    [InlineData("http://proxy:8080/path")]
    [InlineData("http://proxy:8080/?query=x")]
    [InlineData("http://proxy:8080/#fragment")]
    [InlineData("socks4a://proxy:1080")]
    [InlineData("socks5://proxy")]
    [InlineData("file:///tmp")]
    [InlineData("http://proxy:0")]
    public void UnsupportedProxyNeverFallsBackToADifferentRoute(string input) =>
        Assert.Throws<InvalidOperationException>(() => BrowserVerificationService.ValidateProxy(input));

    [Theory]
    [InlineData("ws://example.com:12345/devtools/page/id")]
    [InlineData("ws://127.0.0.1:12346/devtools/page/id")]
    [InlineData("wss://127.0.0.1:12345/devtools/page/id")]
    [InlineData("ws://user@127.0.0.1:12345/devtools/page/id")]
    [InlineData("ws://127.0.0.1:12345/devtools/page/id?secret=1")]
    [InlineData("ws://127.0.0.1:12345/arbitrary")]
    public void DebugEndpointCannotLeaveOwnedLoopbackPort(string url) =>
        Assert.Throws<InvalidOperationException>(() => BrowserVerificationProtocol.ValidateSocket(url, 12345));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=123", true)]
    [InlineData("https://m.youtube.com/watch?v=123", true)]
    [InlineData("https://accounts.google.com/", false)]
    [InlineData("https://youtube.com.evil.example/", false)]
    [InlineData("https://evilyoutube.com/", false)]
    [InlineData("http://youtube.com/", false)]
    [InlineData("https://user@youtube.com/", false)]
    [InlineData("https://youtu.be/123", false)]
    public void CaptureRequiresReturnedVideoSite(string url, bool expected) =>
        Assert.Equal(expected, BrowserVerificationProtocol.IsVideoPage(url, VideoWebsite.YouTube));

    [Fact]
    public void CookiesAreScopedExpireCheckedAndPartitionKeysNeverFlattened()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        using var payload = JsonDocument.Parse("""
            {"cookies":[
             {"domain":".youtube.com","path":"/","name":"SESSION","value":"safe","session":true,"secure":true,"httpOnly":true,"expires":-1},
             {"domain":"www.youtube.com","path":"/","name":"VALID","value":"ok","session":false,"expires":1800000300},
             {"domain":".google.com","path":"/","name":"LOGIN","value":"other-site","session":true},
             {"domain":"evilyoutube.com","path":"/","name":"SPOOF","value":"bad","session":true},
             {"domain":"..youtube.com","path":"/","name":"BAD_DOMAIN","value":"bad","session":true},
             {"domain":".youtube.com","path":"/","name":"EXPIRED","value":"old","expires":1799999999},
             {"domain":".youtube.com","path":"/","name":"BAD_EXPIRY","value":"bad","expires":"1800000300"},
             {"domain":".youtube.com","path":"/","name":"NULL_EXPIRY","value":"bad","expires":null},
             {"domain":".youtube.com","path":"/","name":"PARTITIONED","value":"bad","session":true,"partitionKey":{"topLevelSite":"https://google.com"}},
             {"domain":".youtube.com","path":"/","name":"OPAQUE","value":"bad","session":true,"partitionKeyOpaque":true},
             {"domain":".youtube.com","path":"/","name":"CONTROL","value":"bad\nvalue","session":true}
            ]}
            """);
        var cookies = BrowserVerificationProtocol.ReadCookies(payload.RootElement, VideoWebsite.YouTube, now);
        Assert.Equal(2, cookies.Count);
        var session = Assert.Single(cookies, cookie => cookie.Name == "SESSION");
        Assert.True(session.Session);
        Assert.True(session.HttpOnly);
        Assert.Equal(0, session.ExpiresUnixSeconds);
        Assert.Contains(cookies, cookie => cookie.Name == "VALID" && !cookie.Session);
    }

    [Fact]
    public async Task LocalWebSocketRoundTripOnlyRequestsTargetSiteUrls()
    {
        using var server = new LocalDebugServer();
        using var protocol = new BrowserVerificationProtocol(server.Port);
        var responseTask = server.WebSocketAsync(request =>
        {
            Assert.Equal("Network.getCookies", request.GetProperty("method").GetString());
            var urls = request.GetProperty("params").GetProperty("urls").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Assert.All(urls, url => Assert.True(BrowserVerificationProtocol.IsVideoPage(url, VideoWebsite.Bilibili)));
            Assert.DoesNotContain(urls, url => url.Contains("youtube") || url.Contains("google"));
            return new { id = request.GetProperty("id").GetInt32(), result = new { cookies = new[] {
                new { domain = ".bilibili.com", path = "/", name = "LOCAL_TEST", value = "local-value", session = true } } } };
        });
        var result = await protocol.CommandAsync(server.SocketUrl, "Network.getCookies",
            new { urls = BrowserVerificationProtocol.CookieUrls("https://www.bilibili.com/video/BVtest", VideoWebsite.Bilibili) }, CancellationToken.None);
        Assert.Single(BrowserVerificationProtocol.ReadCookies(result, VideoWebsite.Bilibili, DateTimeOffset.UtcNow));
        await responseTask;
    }

    [Fact]
    public async Task BrowserProtocolErrorsNeverExposeRemotePayload()
    {
        using var server = new LocalDebugServer();
        using var protocol = new BrowserVerificationProtocol(server.Port);
        var responseTask = server.WebSocketAsync(request => new { id = request.GetProperty("id").GetInt32(),
            error = new { message = "COOKIE_SECRET_DO_NOT_SHOW" } });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.CommandAsync(server.SocketUrl,
            "Browser.getVersion", null, CancellationToken.None));
        Assert.DoesNotContain("COOKIE_SECRET", error.ToString());
        await responseTask;
    }

    [Fact]
    public async Task DebugHttpRedirectIsNotFollowed()
    {
        using var server = new LocalDebugServer();
        using var protocol = new BrowserVerificationProtocol(server.Port);
        var responseTask = server.RedirectAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => protocol.GetAsync("json/list", CancellationToken.None));
        await responseTask;
    }

    [Fact]
    public void CleanupDeletesOnlyTheCreatedProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "LinguaForge-X-browser-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new BrowserVerificationProfile(root);
            var second = new BrowserVerificationProfile(root);
            File.WriteAllText(Path.Combine(second.DirectoryPath, "keep.txt"), "untouched");
            Directory.CreateDirectory(Path.Combine(first.DirectoryPath, "Default"));
            File.WriteAllText(Path.Combine(first.DirectoryPath, "Default", "cookie-test"), "test");
            first.Delete();
            Assert.False(Directory.Exists(first.DirectoryPath));
            Assert.True(File.Exists(Path.Combine(second.DirectoryPath, "keep.txt")));
            second.Delete();
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CleanupRefusesDirectoryWhoseOwnershipMarkerWasChanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "LinguaForge-X-browser-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new BrowserVerificationProfile(root);
            File.WriteAllText(Path.Combine(profile.DirectoryPath, ".linguaforge-verification"), "unowned");
            profile.Delete();
            Assert.True(Directory.Exists(profile.DirectoryPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CleanupRetainsOwnershipMarkerUntilLockedFileCanBeDeleted()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix permits unlinking files with open handles.
        var root = Path.Combine(Path.GetTempPath(), "LinguaForge-X-browser-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new BrowserVerificationProfile(root);
            using (var file = new FileStream(Path.Combine(profile.DirectoryPath, "locked-cookie-test"),
                       FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                profile.Delete();
                Assert.True(File.Exists(Path.Combine(profile.DirectoryPath, ".linguaforge-verification")));
                Assert.True(Directory.Exists(profile.DirectoryPath));
            }
            profile.Delete();
            Assert.False(Directory.Exists(profile.DirectoryPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("msedge")]
    [InlineData("chrome")]
    [Trait("Category", "LiveBrowser")]
    public async Task RealBrowserBlankStartupAndOwnedProfileCleanup(string browser)
    {
        // Explicit opt-in: no website navigation, login, or cookie capture occurs in this smoke check.
        if (Environment.GetEnvironmentVariable("LINGUAFORGE_BROWSER_SMOKE") != "1") return;
        Assert.True(OperatingSystem.IsWindows());
        var executable = browser == "msedge" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "Edge", "Application", "msedge.exe") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Google", "Chrome", "Application", "chrome.exe");
        Assert.True(File.Exists(executable));
        var root = Path.Combine(Path.GetTempPath(), "LinguaForge-X-browser-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var session = await BrowserVerificationService.StartBrowserAsync(executable, root, VideoWebsite.YouTube, "", null, timeout.Token);
            Assert.Equal(browser == "msedge" ? "Microsoft Edge" : "Google Chrome", session.BrowserName);
            Assert.Single(Directory.EnumerateDirectories(root));
            await session.DisposeAsync();
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); }
    }

    /// <summary>One-request local CDP stand-in; no HTTP.sys registration, remote network, or browser is needed.</summary>
    private sealed class LocalDebugServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));
        internal int Port { get; }
        internal string SocketUrl => $"ws://127.0.0.1:{Port}/devtools/page/local-test";

        internal LocalDebugServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        internal async Task WebSocketAsync(Func<JsonElement, object> response)
        {
            using var client = await _listener.AcceptTcpClientAsync(_timeout.Token);
            await using var stream = client.GetStream();
            var headers = await ReadHeadersAsync(stream);
            var key = headers.Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _timeout.Token);
            using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(5));
            var buffer = new byte[4096];
            var request = await socket.ReceiveAsync(buffer.AsMemory(), _timeout.Token);
            Assert.True(request.EndOfMessage);
            using var document = JsonDocument.Parse(buffer.AsMemory(0, request.Count));
            // An unrelated event and nonnumeric id must be ignored safely before the actual response.
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"id\":\"event\",\"method\":\"Page.event\"}").AsMemory(), WebSocketMessageType.Text, true, _timeout.Token);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(response(document.RootElement)).AsMemory(), WebSocketMessageType.Text, true, _timeout.Token);
        }

        internal async Task RedirectAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(_timeout.Token);
            await using var stream = client.GetStream();
            await ReadHeadersAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 307 Temporary Redirect\r\nLocation: http://not-a-browser.invalid/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _timeout.Token);
        }

        private async Task<string> ReadHeadersAsync(NetworkStream stream)
        {
            var output = new StringBuilder();
            var buffer = new byte[1];
            while (output.Length < 8192)
            {
                if (await stream.ReadAsync(buffer, _timeout.Token) == 0) throw new EndOfStreamException();
                output.Append((char)buffer[0]);
                if (output.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) return output.ToString();
            }
            throw new InvalidOperationException("Test request headers too large.");
        }

        public void Dispose()
        {
            _timeout.Cancel();
            _timeout.Dispose();
            _listener.Stop();
        }
    }
}
