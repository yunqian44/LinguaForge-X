using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Persistence;

namespace LinguaForge_X.Infrastructure.LocalVoice;

/// <summary>Local-only adapter for the official GPT-SoVITS api_v2.py server and durable voice profiles.</summary>
public sealed partial class LocalVoiceService : ILocalVoiceService, ISynthesisService, IDisposable
{
    // api_v2 has one mutable global model. Switching both models and synthesis must be one transaction.
    private static readonly SemaphoreSlim ApiGate = new(1, 1);
    private static readonly Dictionary<string, string> LoadedModels = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _trainingGate = new(1, 1);
    private readonly HttpClient _client;
    private readonly string _profilesDirectory;
    private Process? _ownedProcess;
    private string? _ownedEndpoint;
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public LocalVoiceService(string? profilesDirectory = null, HttpMessageHandler? handler = null)
    {
        _profilesDirectory = Path.GetFullPath(profilesDirectory ?? Path.Combine(AppDataPaths.Root, "local-voices"));
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<IReadOnlyList<LocalVoiceProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_profilesDirectory)) return [];
        var profiles = new List<LocalVoiceProfile>();
        foreach (var directory in Directory.EnumerateDirectories(_profilesDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            var metadata = Path.Combine(directory, "voice.json");
            if (!File.Exists(metadata)) continue;
            try
            {
                var profile = JsonSerializer.Deserialize<LocalVoiceProfile>(await File.ReadAllTextAsync(metadata, cancellationToken), JsonOptions);
                if (profile is null || profile.Id != Path.GetFileName(directory) || !Guid.TryParseExact(profile.Revision, "N", out _)
                    || !StoredFile(profile.ReferenceAudioPath, directory) || string.IsNullOrWhiteSpace(profile.Name)
                    || string.IsNullOrWhiteSpace(profile.ReferenceText)) continue;
                if (string.IsNullOrWhiteSpace(profile.GptWeightsPath) != string.IsNullOrWhiteSpace(profile.SoVitsWeightsPath)) continue;
                if (profile.IsTrained && (!StoredFile(profile.GptWeightsPath, directory) || !StoredFile(profile.SoVitsWeightsPath, directory))) continue;
                profiles.Add(profile);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
            {
                // Leave damaged profiles intact; another profile can still be used.
            }
        }
        return profiles.OrderByDescending(p => p.CreatedAt).ToArray();
    }

    public async Task<LocalVoiceProfile> SaveAsync(LocalVoiceDraft voice, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        LocalVoiceFiles.ValidateDraft(voice);
        var id = Guid.NewGuid().ToString("N");
        var finalDirectory = Path.Combine(_profilesDirectory, id);
        var staging = Path.Combine(_profilesDirectory, ".pending-" + id);
        Directory.CreateDirectory(staging);
        try
        {
            await CopyAsync(voice.ReferenceAudioPath, Path.Combine(staging, "reference.wav"), cancellationToken);
            using (var file = File.OpenRead(Path.Combine(staging, "reference.wav"))) LocalVoiceFiles.ValidateWave(file);
            var trained = !string.IsNullOrWhiteSpace(voice.GptWeightsPath);
            if (trained)
            {
                await CopyAsync(voice.GptWeightsPath, Path.Combine(staging, "gpt.ckpt"), cancellationToken);
                await CopyAsync(voice.SoVitsWeightsPath, Path.Combine(staging, "sovits.pth"), cancellationToken);
            }
            var profile = new LocalVoiceProfile
            {
                Id = id, Revision = Guid.NewGuid().ToString("N"), Name = voice.Name.Trim(),
                ReferenceAudioPath = Path.Combine(finalDirectory, "reference.wav"), ReferenceText = voice.ReferenceText.Trim(),
                Language = voice.Language.Trim(), CreatedAt = DateTimeOffset.UtcNow,
                GptWeightsPath = trained ? Path.Combine(finalDirectory, "gpt.ckpt") : "",
                SoVitsWeightsPath = trained ? Path.Combine(finalDirectory, "sovits.pth") : ""
            };
            await AtomicJson.WriteAsync(Path.Combine(staging, "voice.json"), profile, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, finalDirectory);
            return profile;
        }
        finally { RemoveOwnedTemporaryDirectory(staging); }
    }

    public async Task SynthesizeAsync(string text, string language, string voiceId, string outputPath,
        ServiceSettings settings, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Provider != ServiceProviderKind.LocalGptSoVits) throw new InvalidOperationException("请选择本地 GPT-SoVITS 配音服务。");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) throw new InvalidOperationException("配音文字不能为空，且单条不能超过 4000 个字符。");
        var endpoint = LocalVoiceFiles.Endpoint(settings.LocalVoice.ApiEndpoint);
        var textLanguage = LocalVoiceFiles.Language(language);
        var profile = (await ListAsync(cancellationToken)).SingleOrDefault(v => v.Id == voiceId)
            ?? throw new InvalidOperationException("找不到已保存的本地音色，请先创建音色并重新选择。");
        var promptLanguage = LocalVoiceFiles.Language(profile.Language);
        var weights = profile.IsTrained ? (Gpt: profile.GptWeightsPath, SoVits: profile.SoVitsWeightsPath)
            : LocalVoiceFiles.BaseWeights(LocalVoiceFiles.RuntimeRoot(settings.LocalVoice));
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        await ApiGate.WaitAsync(cancellationToken);
        try
        {
            var fingerprint = WeightIdentity(weights.Gpt) + "|" + WeightIdentity(weights.SoVits);
            // Only reuse state when this app owns the running server. An externally managed server may restart independently.
            var canReuse = _ownedProcess is { HasExited: false } && _ownedEndpoint == endpoint.AbsoluteUri;
            if (!canReuse || !LoadedModels.TryGetValue(endpoint.AbsoluteUri, out var loaded) || loaded != fingerprint)
            {
                LoadedModels.Remove(endpoint.AbsoluteUri);
                await SetWeightsAsync(endpoint, "set_gpt_weights", weights.Gpt, cancellationToken);
                await SetWeightsAsync(endpoint, "set_sovits_weights", weights.SoVits, cancellationToken);
                LoadedModels[endpoint.AbsoluteUri] = fingerprint;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "tts"))
            {
                Content = JsonContent.Create(new
                {
                    text, text_lang = textLanguage, ref_audio_path = profile.ReferenceAudioPath,
                    prompt_text = profile.ReferenceText, prompt_lang = promptLanguage,
                    media_type = "wav", streaming_mode = false, text_split_method = "cut5",
                    batch_size = 1, speed_factor = 1.0, seed = 42
                })
            };
            // This client is completely separate from Azure. No credentials are ever copied onto this request.
            using var response = await SendAsync(request, cancellationToken);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await CopyBoundedAsync(input, destination, 128L * 1024 * 1024, cancellationToken);
            using (var result = File.OpenRead(temporary)) LocalVoiceFiles.ValidateWave(result);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, true);
        }
        catch (HttpRequestException ex)
        {
            LoadedModels.Remove(endpoint.AbsoluteUri);
            throw new InvalidOperationException("无法连接本地 GPT-SoVITS。请先在本地音色页启动服务或检查本机 API 地址。", ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoadedModels.Remove(endpoint.AbsoluteUri);
            // Cancelling an HTTP request does not cancel Python inference. Stop our own child before allowing
            // another request to change its global models, but never terminate an externally managed server.
            if (_ownedProcess is { HasExited: false } && _ownedEndpoint == endpoint.AbsoluteUri)
            {
                Kill(_ownedProcess);
                try
                {
                    await Task.WhenAll(_serverReaders.Append(_ownedProcess.WaitForExitAsync(CancellationToken.None)))
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException) { }
            }
            throw;
        }
        catch
        {
            LoadedModels.Remove(endpoint.AbsoluteUri);
            throw;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { ApiGate.Release(); }
        }
    }

    public async Task<string?> GetCacheIdentityAsync(string voiceId, ServiceSettings settings, CancellationToken cancellationToken)
    {
        var profile = (await ListAsync(cancellationToken)).SingleOrDefault(v => v.Id == voiceId)
            ?? throw new InvalidOperationException("找不到已保存的本地音色，请重新创建或选择音色。");
        var weights = profile.IsTrained ? (Gpt: profile.GptWeightsPath, SoVits: profile.SoVitsWeightsPath)
            : LocalVoiceFiles.BaseWeights(LocalVoiceFiles.RuntimeRoot(settings.LocalVoice));
        return string.Join("|", "GPT-SoVITS-v2", profile.Id, profile.Revision, profile.Language, profile.ReferenceText,
            WeightIdentity(profile.ReferenceAudioPath), WeightIdentity(weights.Gpt), WeightIdentity(weights.SoVits));
    }

    public async Task<ConnectionTestResult> CheckAsync(LocalVoiceSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var endpoint = LocalVoiceFiles.Endpoint(settings.ApiEndpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "openapi.json"));
            using var response = await SendAsync(request, timeout.Token);
            using var buffer = new MemoryStream();
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
                await CopyBoundedAsync(input, buffer, 2 * 1024 * 1024, timeout.Token);
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (!document.RootElement.TryGetProperty("paths", out var paths)
                || !HasRoute(paths, "/tts", "post") || !HasRoute(paths, "/set_gpt_weights", "get") || !HasRoute(paths, "/set_sovits_weights", "get"))
                return new(false, "该地址没有提供所需的 GPT-SoVITS api_v2 接口，请启动 api_v2.py。");
            return new(true, "本地 GPT-SoVITS api_v2 已连接；请生成试听确认模型与音色可用。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "本地配音服务尚未响应，首次加载模型可能较慢。请稍后再检查连接。"); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        { return new(false, "本地 GPT-SoVITS 服务未就绪。请检查运行目录和 Python 环境，并启动 api_v2.py。"); }
    }

    private async Task SetWeightsAsync(Uri endpoint, string route, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, route + "?weights_path=" + Uri.EscapeDataString(path)));
        using var response = await SendAsync(request, cancellationToken);
        var bytes = await ReadBoundedAsync(response.Content, 1024 * 1024, cancellationToken);
        try
        {
            using var result = JsonDocument.Parse(bytes);
            if (!result.RootElement.TryGetProperty("message", out var message) || message.GetString() != "success")
                throw new InvalidOperationException("本地模型切换未得到成功确认，未开始配音。请检查 GPT/SoVITS 模型版本是否匹配。");
        }
        catch (JsonException ex) { throw new InvalidOperationException("本地模型切换返回了无效结果，未开始配音。", ex); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400) throw new InvalidOperationException("本地配音服务返回了重定向，已拒绝请求。请填写直接提供 api_v2 的本机地址。");
            var details = "";
            try
            {
                using var error = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 8192, cancellationToken));
                if (error.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var messages = new List<string>();
                    foreach (var field in new[] { "message", "Exception", "detail" })
                        if (error.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                            && value.GetString() is { Length: > 0 } message)
                            messages.Add(new string(message.Take(600).Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()));
                    if (messages.Count > 0) details = "\n" + string.Join("\n", messages);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            throw new InvalidOperationException($"GPT-SoVITS 返回 HTTP {status}。请检查模型版本与参考录音；本次未保存配音结果。" + details);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await CopyBoundedAsync(input, buffer, limit, cancellationToken);
        return buffer.ToArray();
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += count;
            if (total > limit) throw new InvalidOperationException("本地配音服务返回的数据超过允许大小，已停止保存。");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken);
        if (output.Length == 0) throw new InvalidOperationException("音色文件为空，未保存档案。");
    }

    private static bool HasRoute(JsonElement paths, string route, string method) =>
        paths.ValueKind == JsonValueKind.Object && paths.TryGetProperty(route, out var item) && item.TryGetProperty(method, out _);

    private static bool StoredFile(string path, string directory) => !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), directory, StringComparison.OrdinalIgnoreCase)
        && File.Exists(path) && new FileInfo(path).Length > 0;

    private static string WeightIdentity(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) throw new InvalidOperationException("本地音色模型已丢失，请重新导入训练结果。");
        return $"{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    private void RemoveOwnedTemporaryDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), _profilesDirectory, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith(".pending-", StringComparison.Ordinal))
            throw new InvalidOperationException("临时音色目录无效。");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill(_ownedProcess);
        _ownedProcess?.Dispose();
        _client.Dispose();
        // Gates are not disposed: an in-flight cancellation may still need to release them.
    }
}
