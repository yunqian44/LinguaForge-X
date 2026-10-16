using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.Persistence;

public static class AppDataPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinguaForge-X");
}

internal static class AtomicJson
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    internal static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

/// <summary>Stores configuration beside the executable. DPAPI binds encrypted keys to the current Windows user.</summary>
public sealed class LocalSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LinguaForge-X/settings/v1");

    public LocalSettingsStore(string? directory = null) => _path = Path.Combine(directory ?? AppContext.BaseDirectory, "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return ApplyBundledTools(new AppSettings());
            var envelope = JsonSerializer.Deserialize<SettingsEnvelope>(await File.ReadAllTextAsync(_path, cancellationToken))
                ?? throw new InvalidDataException("设置文件为空。请备份并检查本地设置文件。");
            if (envelope.Version != 1) throw new InvalidDataException("当前版本不支持此设置文件，请升级应用。");
            var settings = envelope.Settings;
            if (settings?.Transcription is null || settings.Translation is null || settings.Synthesis is null || settings.Media is null)
                throw new InvalidDataException("设置文件不完整，请备份并检查设置文件。");
            foreach (var service in new[] { settings.Transcription, settings.Translation, settings.Synthesis })
            {
                service.ApiKey = Unprotect(service.ApiKey);
                service.LocalVoice ??= new LocalVoiceSettings();
            }
            settings.OnlineVideo ??= new OnlineVideoSettings();
            settings.OnlineVideo.Proxy = Unprotect(settings.OnlineVideo.Proxy);
            return ApplyBundledTools(settings);
        }
        catch (JsonException ex) { throw new InvalidDataException("设置文件格式损坏，请备份并检查设置文件。", ex); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        // Copy first so encrypting persisted values never mutates the live UI/service configuration.
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        foreach (var service in new[] { copy.Transcription, copy.Translation, copy.Synthesis })
            service.ApiKey = Protect(service.ApiKey);
        copy.OnlineVideo ??= new OnlineVideoSettings();
        // Proxy URLs can contain a username/password; protect the whole value like service keys.
        copy.OnlineVideo.Proxy = Protect(copy.OnlineVideo.Proxy);
        await _gate.WaitAsync(cancellationToken);
        try { await AtomicJson.WriteAsync(_path, new SettingsEnvelope(1, copy), cancellationToken); }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("无法保存设置文件，请确保程序所在目录可写：" + _path, ex);
        }
        finally { _gate.Release(); }
    }

    private static string Protect(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密钥存储需要 Windows。");
        var bytes = Encoding.UTF8.GetBytes(key);
        try { return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException ex) { throw new InvalidOperationException("无法加密保存密钥，请在正常的 Windows 用户会话中运行应用。", ex); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string Unprotect(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密钥存储需要 Windows。");
        if (!key.StartsWith("dpapi:", StringComparison.Ordinal)) throw new InvalidDataException("设置文件中存在未加密密钥，请在设置页重新配置。");
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(key[6..]), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidDataException("无法解密密钥。设置文件必须由保存它的 Windows 用户在原环境中使用。", ex);
        }
    }

    private sealed record SettingsEnvelope(int Version, AppSettings Settings);

    private static AppSettings ApplyBundledTools(AppSettings settings)
    {
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        var ffprobe = Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe");
        if (settings.Media.FfmpegPath is "ffmpeg" or "ffmpeg.exe" && File.Exists(ffmpeg)) settings.Media.FfmpegPath = ffmpeg;
        if (settings.Media.FfprobePath is "ffprobe" or "ffprobe.exe" && File.Exists(ffprobe)) settings.Media.FfprobePath = ffprobe;
        var downloader = Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe");
        var runtime = Path.Combine(AppContext.BaseDirectory, "tools", "deno.exe");
        if (settings.OnlineVideo.DownloaderPath is "yt-dlp" or "yt-dlp.exe" && File.Exists(downloader)) settings.OnlineVideo.DownloaderPath = downloader;
        if (settings.OnlineVideo.JavaScriptRuntimePath is "deno" or "deno.exe" && File.Exists(runtime)) settings.OnlineVideo.JavaScriptRuntimePath = runtime;
        return settings;
    }
}

public sealed class LocalProjectStore : IProjectStore
{
    private readonly string _root;
    public LocalProjectStore(string? directory = null) => _root = Path.GetFullPath(Path.Combine(directory ?? AppDataPaths.Root, "jobs"));

    public string CreateWorkDirectory(string id)
    {
        if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)) throw new ArgumentException("任务编号无效。", nameof(id));
        var path = Path.Combine(_root, id);
        Directory.CreateDirectory(path);
        return path;
    }

    public Task SaveAsync(ConversionProject project, CancellationToken cancellationToken = default)
    {
        var expected = CreateWorkDirectory(project.Id);
        if (!string.Equals(expected, Path.GetFullPath(project.WorkDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("任务目录无效。");
        return AtomicJson.WriteAsync(Path.Combine(expected, "project.json"), project, cancellationToken);
    }

    public async Task<IReadOnlyList<ConversionProject>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root)) return [];
        var projects = new List<ConversionProject>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, "project.json");
            if (!File.Exists(path)) continue;
            try
            {
                var project = JsonSerializer.Deserialize<ConversionProject>(await File.ReadAllTextAsync(path, cancellationToken));
                if (project is null || project.Id != Path.GetFileName(directory) || project.Options is null || project.Video is null || project.Segments is null) continue;
                project.WorkDirectory = directory;
                projects.Add(project);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A damaged job must not prevent opening other projects. Never overwrite the damaged file.
            }
        }
        return projects.OrderByDescending(p => p.CreatedAt).ToArray();
    }
}
