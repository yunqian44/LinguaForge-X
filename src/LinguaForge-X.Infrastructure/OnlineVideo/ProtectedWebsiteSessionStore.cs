using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Persistence;

namespace LinguaForge_X.Infrastructure.OnlineVideo;

/// <summary>Stores the entire website session encrypted for the current Windows user.</summary>
public sealed class ProtectedWebsiteSessionStore : IWebsiteSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LinguaForge-X/website-sessions/v1");
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProtectedWebsiteSessionStore(string? directory = null) =>
        _root = Path.GetFullPath(directory ?? Path.Combine(AppDataPaths.Root, "website-sessions"));

    public async Task SaveAsync(WebsiteSession session, CancellationToken cancellationToken = default)
    {
        var snapshot = NetscapeCookieFormatter.ValidateAndSnapshot(session, DateTimeOffset.UtcNow);
        var path = PathFor(snapshot.Website);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("网站会话的加密存储需要 Windows。");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new SessionEnvelope(1, snapshot));
        byte[] encrypted;
        try
        {
            if (payload.Length > NetscapeCookieFormatter.MaximumPayloadBytes) throw new InvalidOperationException("网站验证会话过大，请重新打开网站验证并保存会话。");
            encrypted = ProtectedData.Protect(payload, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        { throw new InvalidOperationException("无法加密保存网站会话，请在正常的 Windows 用户会话中运行应用后重试。"); }
        finally { CryptographicOperations.ZeroMemory(payload); }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_root);
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("无法保存网站验证会话，请检查本地应用数据目录的写入权限。"); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            _gate.Release();
        }
    }

    public async Task<WebsiteSession?> LoadAsync(VideoWebsite website, CancellationToken cancellationToken = default)
    {
        var path = PathFor(website);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return null;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("网站会话的加密存储需要 Windows。");
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > NetscapeCookieFormatter.MaximumPayloadBytes + 4096) throw InvalidSession();
            var encrypted = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(encrypted, cancellationToken).ConfigureAwait(false);
            var payload = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                if (payload.Length > NetscapeCookieFormatter.MaximumPayloadBytes) throw InvalidSession();
                var envelope = JsonSerializer.Deserialize<SessionEnvelope>(payload, new JsonSerializerOptions { MaxDepth = 16 });
                if (envelope?.Version != 1 || envelope.Session is null || envelope.Session.Website != website) throw InvalidSession();
                return NetscapeCookieFormatter.ValidateAndSnapshot(envelope.Session, DateTimeOffset.UtcNow);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { throw InvalidSession(); }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(VideoWebsite website, CancellationToken cancellationToken = default)
    {
        var path = PathFor(website);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("无法清除网站验证会话，请检查本地应用数据目录的写入权限。"); }
        finally { _gate.Release(); }
    }

    private string PathFor(VideoWebsite website) => Path.Combine(_root, website switch
    {
        VideoWebsite.YouTube => "youtube.bin",
        VideoWebsite.Bilibili => "bilibili.bin",
        _ => throw new ArgumentOutOfRangeException(nameof(website), "不支持的网站。")
    });

    private static InvalidOperationException InvalidSession() => new("无法读取保存的网站验证会话，请重新打开网站验证并保存会话。");
    private sealed record SessionEnvelope(int Version, WebsiteSession Session);
}
