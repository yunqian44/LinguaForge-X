using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.OnlineVideo;

/// <summary>Opens a separate, visible browser. The user alone handles website login and verification.</summary>
public sealed class BrowserVerificationService : IWebsiteVerificationService
{
    private readonly string? _browserExecutablePath;
    private readonly string _profileRoot;
    private readonly Func<string, bool> _profilePolicy;

    public BrowserVerificationService() : this(null, null) { }

    /// <summary>Explicit paths are available for isolated integration tests and managed installations.</summary>
    public BrowserVerificationService(string? browserExecutablePath, string? profileRoot)
        : this(browserExecutablePath, profileRoot, BrowserProfilePolicies.PreventsIsolation) { }

    internal BrowserVerificationService(string? browserExecutablePath, string? profileRoot, Func<string, bool> profilePolicy)
    {
        _browserExecutablePath = browserExecutablePath;
        _profilePolicy = profilePolicy;
        _profileRoot = profileRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinguaForge-X", "verification-browser");
    }

    public async Task<IWebsiteVerificationSession> OpenAsync(string url, string proxy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var website = VideoWebsites.FromUrl(url) ?? throw new InvalidOperationException("验证窗口目前支持 YouTube 和 B 站的 HTTPS 视频链接。");
        if (url.Length > 8192 || url.Any(char.IsControl)) throw new InvalidOperationException("视频链接格式无效。");
        var proxyArgument = ValidateProxy(proxy);
        proxy = proxy.Trim();
        var browsers = _browserExecutablePath is null ? FindBrowsers() : [_browserExecutablePath];
        if (browsers.Count == 0) throw new InvalidOperationException("未找到 Microsoft Edge 或 Google Chrome，请安装其中一个浏览器，或在设置中选择手动导出的 Cookie 文件。");
        var policyBlocked = 0;
        foreach (var executable in browsers)
        {
            OwnedBrowserSession? session = null;
            try
            {
                session = await StartBrowserAsync(executable, _profileRoot, website, proxy, proxyArgument, cancellationToken, _profilePolicy).ConfigureAwait(false);
                await session.NavigateAsync(url, cancellationToken).ConfigureAwait(false);
                return session;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (BrowserProfilePolicyException) { policyBlocked++; }
            catch (Exception error) when (IsBrowserError(error))
            {
                if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        if (policyBlocked == browsers.Count)
            throw new InvalidOperationException("浏览器组织策略不允许使用独立验证目录，或无法读取该策略。为保护已有浏览器资料，未打开验证窗口；请在设置中选择手动导出的 Cookie 文件。");
        throw new InvalidOperationException("无法启动独立验证窗口。浏览器可能受组织策略限制；请更新 Edge / Chrome，或在设置中选择手动导出的 Cookie 文件。");
    }

    internal static async Task<OwnedBrowserSession> StartBrowserAsync(string executable, string profileRoot,
        VideoWebsite website, string proxy, string? proxyArgument, CancellationToken cancellationToken,
        Func<string, bool>? profilePolicy = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BrowserProfilePolicies.RequireIsolation(executable, profilePolicy ?? BrowserProfilePolicies.PreventsIsolation);
        var profile = new BrowserVerificationProfile(profileRoot);
        BrowserVerificationProtocol? protocol = null;
        Process? process = null;
        try
        {
            // A nonzero port keeps the normal browser's automation flags unchanged. It is never exposed off loopback.
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            // Chromium command-line URL handling drops about:blank fragments. A local, script-free data page
            // retains the exact nonce while showing a readable transition page instead of a technical identifier.
            var nonceUrl = "data:text/html," + Uri.EscapeDataString("<!doctype html><meta charset=\"utf-8\"><title>LinguaForge-X</title>" +
                "<p>正在打开验证页面…</p><!--linguaforge-" + Guid.NewGuid().ToString("N") + "-->");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = profile.DirectoryPath
            };
            foreach (var argument in BrowserArguments(profile.DirectoryPath, port, nonceUrl, proxyArgument)) start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new InvalidOperationException("无法打开验证浏览器。");
            protocol = new(port);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("验证浏览器已退出。");
                try
                {
                    var pages = await protocol.GetAsync("json/list", timeout.Token).ConfigureAwait(false);
                    if (pages.ValueKind == JsonValueKind.Array)
                    {
                        var page = pages.EnumerateArray().FirstOrDefault(item =>
                            BrowserVerificationProtocol.ReadString(item, "type") == "page" &&
                            BrowserVerificationProtocol.ReadString(item, "url") == nonceUrl);
                        var pageSocket = BrowserVerificationProtocol.ReadString(page, "webSocketDebuggerUrl");
                        var pageId = BrowserVerificationProtocol.ReadString(page, "id");
                        if (!string.IsNullOrEmpty(pageSocket) && !string.IsNullOrEmpty(pageId))
                        {
                            BrowserVerificationProtocol.ValidateSocket(pageSocket, port);
                            var version = await protocol.GetAsync("json/version", timeout.Token).ConfigureAwait(false);
                            var browserSocket = BrowserVerificationProtocol.ReadString(version, "webSocketDebuggerUrl") ?? "";
                            BrowserVerificationProtocol.ValidateSocket(browserSocket, port);
                            return new(process, profile, protocol, browserSocket, pageSocket, pageId, website,
                                Path.GetFileNameWithoutExtension(executable).Equals("msedge", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge" : "Google Chrome", proxy);
                        }
                    }
                }
                catch (Exception error) when (error is HttpRequestException or JsonException or IOException ||
                                             (error is OperationCanceledException && !timeout.IsCancellationRequested))
                { /* Browser startup is asynchronous. Never attach until the unique blank page proves ownership. */ }
                await Task.Delay(150, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            if (process is not null) await StopOwnedProcessAsync(process).ConfigureAwait(false);
            protocol?.Dispose();
            await profile.DeleteAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static IReadOnlyList<string> BrowserArguments(string profileDirectory, int port, string nonceUrl, string? proxyArgument)
    {
        var arguments = new List<string>
        {
            "--user-data-dir=" + profileDirectory, "--remote-debugging-address=127.0.0.1", $"--remote-debugging-port={port}",
            "--no-first-run", "--no-default-browser-check", "--new-window"
        };
        if (proxyArgument is not null) arguments.Add("--proxy-server=" + proxyArgument);
        arguments.Add(nonceUrl);
        return arguments;
    }

    internal static string? ValidateProxy(string proxy)
    {
        proxy = proxy.Trim();
        if (proxy.Length == 0) return null;
        if (proxy.Length > 4096 || proxy.Any(char.IsControl) || !Uri.TryCreate(proxy, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks5" or "socks5h") ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.AbsolutePath != "/" && uri.AbsolutePath != "" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.Port == 0)
            throw new InvalidOperationException("验证窗口的代理需要 http://、https://、socks4:// 或 socks5:// 地址。其他代理请使用手动 Cookie 文件方式。");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("独立验证窗口暂不支持地址中含用户名或密码的代理，请使用无账号本机代理，或手动导出 Cookie 文件。");
        if (uri.Port < 0) throw new InvalidOperationException("请为 SOCKS 代理填写完整端口，或使用手动 Cookie 文件方式。");
        // Chromium's SOCKS5 already resolves destination names through the proxy.
        return (uri.Scheme == "socks5h" ? "socks5" : uri.Scheme) + "://" + uri.Authority;
    }

    private static List<string> FindBrowsers()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return FindWindowsBrowsers();
    }

    [SupportedOSPlatform("windows")]
    private static List<string> FindWindowsBrowsers()
    {
        var output = new List<string>();
        foreach (var browser in new[] { (Name: "msedge.exe", Folder: "Microsoft/Edge/Application"), (Name: "chrome.exe", Folder: "Google/Chrome/Application") })
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var path = Path.Combine(root, browser.Folder.Replace('/', Path.DirectorySeparatorChar), browser.Name);
                if (File.Exists(path) && !output.Contains(path, StringComparer.OrdinalIgnoreCase)) output.Add(path);
            }
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var key = RegistryKey.OpenBaseKey(hive, view);
                    using var app = key.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + browser.Name);
                    if (app?.GetValue(null) is string candidate && File.Exists(candidate) &&
                        Path.GetFileName(candidate).Equals(browser.Name, StringComparison.OrdinalIgnoreCase) &&
                        !output.Contains(candidate, StringComparer.OrdinalIgnoreCase)) output.Add(candidate);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            }
        }
        return output;
    }

    private static bool IsBrowserError(Exception error) => error is InvalidOperationException or IOException or
        UnauthorizedAccessException or Win32Exception or HttpRequestException or WebSocketException or JsonException or OperationCanceledException;

    internal static async Task StopOwnedProcessAsync(Process process, bool allowGracefulExit = false)
    {
        try
        {
            if (allowGracefulExit && !process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException or TimeoutException) { }
        finally { process.Dispose(); }
    }

    internal sealed class OwnedBrowserSession(Process process, BrowserVerificationProfile profile,
        BrowserVerificationProtocol protocol, string browserSocket, string pageSocket, string pageId,
        VideoWebsite website, string browserName, string proxy) : IWebsiteVerificationSession
    {
        private readonly SemaphoreSlim _operation = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private int _disposed;
        public VideoWebsite Website => website;
        public string BrowserName => browserName;

        internal async Task NavigateAsync(string url, CancellationToken cancellationToken)
        {
            var result = await protocol.CommandAsync(pageSocket, "Page.navigate", new { url }, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(BrowserVerificationProtocol.ReadString(result, "errorText")))
                throw new InvalidOperationException("验证窗口无法打开视频，请检查网络或代理设置。");
        }

        public async Task<WebsiteSession> CaptureAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _operation.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (process.HasExited) throw new InvalidOperationException("验证窗口已关闭，请重新打开验证窗口。");
                var pages = await protocol.GetAsync("json/list", linked.Token).ConfigureAwait(false);
                // Only the original app-opened tab is read. Other tabs, even same-site tabs, are not selected automatically.
                var page = pages.ValueKind == JsonValueKind.Array ? pages.EnumerateArray().FirstOrDefault(item =>
                    BrowserVerificationProtocol.ReadString(item, "id") == pageId) : default;
                if (!BrowserVerificationProtocol.IsVideoPage(BrowserVerificationProtocol.ReadString(page, "url"), website))
                    throw new InvalidOperationException("请在原验证标签页完成验证，并返回该网站的视频页面，再点击“使用会话并重试”。");
                var currentUrl = await CurrentPageUrlAsync(linked.Token).ConfigureAwait(false);
                var urls = BrowserVerificationProtocol.CookieUrls(currentUrl, website);
                var response = await protocol.CommandAsync(pageSocket, "Network.getCookies", new { urls }, linked.Token).ConfigureAwait(false);
                // A navigation to a login site during capture must not be reported as completed.
                if (!BrowserVerificationProtocol.IsVideoPage(await CurrentPageUrlAsync(linked.Token).ConfigureAwait(false), website))
                    throw new InvalidOperationException("页面仍在登录或验证跳转中，请返回视频页面后重试。");
                var cookies = BrowserVerificationProtocol.ReadCookies(response, website, DateTimeOffset.UtcNow);
                if (cookies.Count == 0) throw new InvalidOperationException("尚未取得可用的网站会话，请在视频页面完成验证或播放后再点击“使用会话并重试”。");
                var version = await protocol.CommandAsync(browserSocket, "Browser.getVersion", null, linked.Token).ConfigureAwait(false);
                var userAgent = BrowserVerificationProtocol.ReadString(version, "userAgent");
                if (string.IsNullOrWhiteSpace(userAgent) || userAgent.Length > 2048 || userAgent.Any(char.IsControl))
                    throw new InvalidOperationException("未能读取验证窗口的信息，请关闭窗口后重试。");
                return new(website, cookies, userAgent, proxy, DateTimeOffset.UtcNow);
            }
            catch (Exception error) when (error is HttpRequestException or WebSocketException or JsonException or IOException ||
                                         (error is OperationCanceledException && !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested))
            { throw new InvalidOperationException("验证窗口连接中断，请保持窗口打开并返回视频页面后重试。"); }
            finally { _operation.Release(); }
        }

        private async Task<string> CurrentPageUrlAsync(CancellationToken cancellationToken)
        {
            var frames = await protocol.CommandAsync(pageSocket, "Page.getFrameTree", null, cancellationToken).ConfigureAwait(false);
            return frames.TryGetProperty("frameTree", out var tree) && tree.TryGetProperty("frame", out var frame)
                ? BrowserVerificationProtocol.ReadString(frame, "url") ?? "" : "";
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _operation.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await protocol.CommandAsync(browserSocket, "Browser.close", null, timeout.Token).ConfigureAwait(false); }
                catch (Exception error) when (IsBrowserError(error)) { }
                await StopOwnedProcessAsync(process, allowGracefulExit: true).ConfigureAwait(false);
                protocol.Dispose();
                await profile.DeleteAsync().ConfigureAwait(false);
            }
            finally
            {
                _operation.Release();
                _lifetime.Dispose();
            }
        }
    }
}
