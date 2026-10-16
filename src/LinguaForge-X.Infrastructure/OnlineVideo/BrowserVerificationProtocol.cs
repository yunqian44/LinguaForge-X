using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.OnlineVideo;

/// <summary>Only talks to the explicitly owned browser on loopback. Never logs browser responses.</summary>
internal sealed class BrowserVerificationProtocol : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 1_048_576 };
    private readonly int _port;
    private int _commandId;

    internal BrowserVerificationProtocol(int port) => _port = port;

    internal async Task<JsonElement> GetAsync(string resource, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"http://127.0.0.1:{_port}/{resource}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("浏览器连接尚未就绪。");
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false),
            new JsonDocumentOptions { MaxDepth = 32 });
        return document.RootElement.Clone();
    }

    internal async Task<JsonElement> CommandAsync(string socketUrl, string method, object? parameters,
        CancellationToken cancellationToken)
    {
        var uri = ValidateSocket(socketUrl, _port);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _commandId);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
        await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        var buffer = new byte[16_384];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (received.MessageType != WebSocketMessageType.Text)
                    throw new InvalidOperationException("验证窗口连接已关闭，请重新打开验证窗口。");
                if (message.Length + received.Count > 1_048_576)
                    throw new InvalidOperationException("验证窗口返回的数据过大，请关闭窗口后重试。");
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            using var document = JsonDocument.Parse(message.ToArray(), new JsonDocumentOptions { MaxDepth = 48 });
            var reply = document.RootElement;
            if (!reply.TryGetProperty("id", out var replyId) || replyId.ValueKind != JsonValueKind.Number ||
                !replyId.TryGetInt32(out var replyNumber) || replyNumber != id) continue;
            if (reply.TryGetProperty("error", out _) || !reply.TryGetProperty("result", out var result))
                throw new InvalidOperationException("浏览器未能读取当前验证会话，请返回视频页面后重试。");
            return result.Clone();
        }
    }

    internal static Uri ValidateSocket(string url, int port)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "ws" ||
            uri.Host != "127.0.0.1" || uri.Port != port || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !(uri.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal) ||
              uri.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal)))
            throw new InvalidOperationException("无法确认验证窗口的本机连接，请关闭窗口后重试。");
        return uri;
    }

    internal static string? ReadString(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static bool IsVideoPage(string? url, VideoWebsite website) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        string.IsNullOrEmpty(uri.UserInfo) && VideoWebsites.IsDomain(uri.IdnHost, VideoWebsites.CookieDomain(website));

    internal static string[] CookieUrls(string currentUrl, VideoWebsite website)
    {
        if (!IsVideoPage(currentUrl, website)) throw new InvalidOperationException("请在验证窗口完成登录或验证，并返回该网站的视频页面。");
        var domain = VideoWebsites.CookieDomain(website);
        return [currentUrl, $"https://{domain}/", $"https://www.{domain}/"];
    }

    internal static IReadOnlyList<WebsiteCookie> ReadCookies(JsonElement response, VideoWebsite website, DateTimeOffset now)
    {
        var output = new Dictionary<(string Domain, string Path, string Name), WebsiteCookie>();
        if (!response.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("未读取到网站会话，请在验证窗口完成验证后重试。");
        foreach (var item in cookies.EnumerateArray())
        {
            var domain = ReadString(item, "domain");
            if (domain is null || domain.StartsWith("..", StringComparison.Ordinal) ||
                !VideoWebsites.IsDomain(domain.TrimStart('.'), VideoWebsites.CookieDomain(website))) continue;
            // Netscape cookie files cannot retain partition keys. Do not broaden their scope on export.
            if (item.TryGetProperty("partitionKey", out _) ||
                (item.TryGetProperty("partitionKeyOpaque", out var opaque) && opaque.ValueKind == JsonValueKind.True)) continue;
            var name = ReadString(item, "name");
            var value = ReadString(item, "value");
            var path = ReadString(item, "path");
            if (string.IsNullOrEmpty(name) || name.Length > 4096 || value is null || value.Length > 32_768 ||
                string.IsNullOrEmpty(path) || !path.StartsWith('/') || domain.Length > 253 || path.Length > 4096 ||
                domain.Any(char.IsControl) || name.Any(char.IsControl) || value.Any(char.IsControl) || path.Any(char.IsControl)) continue;
            var session = item.TryGetProperty("session", out var sessionValue) && sessionValue.ValueKind == JsonValueKind.True;
            var expires = 0d;
            if (!session && (!item.TryGetProperty("expires", out var expiresValue) || expiresValue.ValueKind != JsonValueKind.Number || !expiresValue.TryGetDouble(out expires) ||
                             !double.IsFinite(expires) || expires <= now.ToUnixTimeSeconds())) continue;
            var secure = item.TryGetProperty("secure", out var secureValue) && secureValue.ValueKind == JsonValueKind.True;
            var httpOnly = item.TryGetProperty("httpOnly", out var httpOnlyValue) && httpOnlyValue.ValueKind == JsonValueKind.True;
            output[(domain, path, name)] = new(domain, path, name, value, secure, httpOnly, session ? 0 : expires, session);
        }
        return output.Values.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
