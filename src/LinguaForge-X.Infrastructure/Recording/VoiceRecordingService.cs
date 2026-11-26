using System.Text;
using System.Text.Json;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Persistence;

namespace LinguaForge_X.Infrastructure.Recording;

/// <summary>Local-only recording library. No cloud requests and no microphone access before RecordAsync.</summary>
public sealed class VoiceRecordingService : IVoiceRecordingService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _root;
    private readonly Func<int, ManualResetEvent, IProgress<VoiceRecordingProgress>?, CancellationToken, RecordingAudio> _capture;
    private readonly object _captureLock = new();
    private ManualResetEvent? _stop;
    private bool _disposed;

    public VoiceRecordingService() : this(Path.Combine(AppDataPaths.Root, "recordings")) { }
    public VoiceRecordingService(string libraryRoot) : this(libraryRoot, WindowsMicrophone.Capture) { }
    internal VoiceRecordingService(string libraryRoot, Func<int, ManualResetEvent, IProgress<VoiceRecordingProgress>?, CancellationToken, RecordingAudio> capture)
    {
        _root = Path.GetFullPath(libraryRoot);
        _capture = capture;
    }

    public Task<IReadOnlyList<VoiceRecordingDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return Task.FromResult(WindowsMicrophone.Devices());
    }

    public async Task<VoiceRecordingTake> RecordAsync(int deviceId, IProgress<VoiceRecordingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ManualResetEvent stop;
        lock (_captureLock)
        {
            ThrowIfDisposed();
            if (_stop is not null) throw new InvalidOperationException("正在录音，请先停止或取消当前录音。");
            _stop = stop = new(false);
        }
        try
        {
            var audio = await Task.Run(() => _capture(deviceId, stop, progress, cancellationToken), CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return await StoreTakeAsync(audio, null, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_captureLock) { _stop = null; stop.Dispose(); }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_captureLock) { _stop?.Set(); }
        return Task.CompletedTask;
    }

    public async Task<VoiceRecordingTake> ImportAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!Path.GetExtension(audioPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择 WAV 录音；录音只复制到本机音频库，不会上传云端。");
        var audio = await Task.Run(() => RecordingAudio.Read(audioPath), cancellationToken).ConfigureAwait(false);
        return await StoreTakeAsync(audio, audioPath, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoiceRecordingTake> CalibrateAsync(VoiceRecordingTake take, VoiceRecordingCalibration calibration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(take); ArgumentNullException.ThrowIfNull(calibration);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Task.Run(() => RecordingAudio.Read(take.AudioPath).Calibrate(calibration), cancellationToken).ConfigureAwait(false);
        var original = string.IsNullOrWhiteSpace(take.OriginalAudioPath) ? take.AudioPath : take.OriginalAudioPath;
        return await StoreTakeAsync(result.Audio, original, result.WasClipped || take.Quality.ClippedFraction >= .001, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoiceRecordingSample> SaveSampleAsync(VoiceRecordingSampleDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(draft.Take);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var name = Field(draft.Name, "录音名称", 80);
        var speaker = Field(draft.Speaker, "说话人", 80);
        var text = Field(draft.Text, "逐字文稿", 1000);
        Language(draft.Language);
        var audio = await Task.Run(() => RecordingAudio.Read(draft.Take.AudioPath), cancellationToken).ConfigureAwait(false);
        var quality = audio.Analyze(draft.Take.Quality.ClippedFraction >= .001);
        if (!quality.CanUseForTraining) throw new InvalidOperationException("录音未通过保存校验：" + quality.Summary);
        var id = Guid.NewGuid().ToString("N");
        var final = Path.Combine(_root, "samples", id);
        var pending = Path.Combine(_root, "samples", ".pending-" + id);
        Directory.CreateDirectory(pending);
        try
        {
            await Task.Run(() => audio.Write(Path.Combine(pending, id + ".wav")), cancellationToken).ConfigureAwait(false);
            var original = string.IsNullOrWhiteSpace(draft.Take.OriginalAudioPath) ? draft.Take.AudioPath : draft.Take.OriginalAudioPath;
            await CopyOriginalAsync(original, Path.Combine(pending, "original.wav"), cancellationToken).ConfigureAwait(false);
            var sample = new VoiceRecordingSample
            {
                Id = id, Name = name, Speaker = speaker, Text = text, Language = draft.Language, Quality = quality,
                AudioPath = Path.Combine(final, id + ".wav"), OriginalAudioPath = Path.Combine(final, "original.wav")
            };
            await File.WriteAllTextAsync(Path.Combine(pending, "sample.json"), JsonSerializer.Serialize(sample, Json), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(pending, final);
            return sample;
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }

    public async Task<IReadOnlyList<VoiceRecordingSample>> ListSamplesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(_root, "samples");
        var result = new List<VoiceRecordingSample>();
        if (!Directory.Exists(path)) return result;
        foreach (var folder in Directory.EnumerateDirectories(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) continue;
            try { result.Add(await ReadSampleAsync(Path.GetFileName(folder), cancellationToken).ConfigureAwait(false)); }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { }
        }
        return result.OrderByDescending(sample => sample.CreatedAt).ToArray();
    }

    public async Task<string> ExportDatasetAsync(IReadOnlyList<string> sampleIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampleIds); ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
        if (sampleIds.Count is < 1 or > 2000 || sampleIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sampleIds.Count)
            throw new InvalidOperationException("请选择 1～2000 段不重复的录音导出训练标注。");
        var samples = new List<VoiceRecordingSample>();
        foreach (var id in sampleIds) samples.Add(await ReadSampleAsync(id, cancellationToken).ConfigureAwait(false));
        if (samples.Select(sample => sample.Speaker).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidOperationException("一个个人音色训练集只能包含同一位说话人，请检查录音的说话人字段。");
        var lines = new List<string>();
        foreach (var sample in samples)
        {
            var quality = (await Task.Run(() => RecordingAudio.Read(sample.AudioPath), cancellationToken).ConfigureAwait(false)).Analyze(sample.Quality.ClippedFraction >= .001);
            if (!quality.CanUseForTraining) throw new InvalidOperationException($"录音“{sample.Name}”未通过训练校验：{quality.Summary}");
            lines.Add($"{sample.AudioPath}|{Field(sample.Speaker, "说话人", 80)}|{Language(sample.Language)}|{Field(sample.Text, "逐字文稿", 1000)}");
        }
        var directory = Path.Combine(_root, "datasets");
        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, "training-" + Guid.NewGuid().ToString("N") + ".list");
        var pending = final + ".pending";
        try
        {
            await File.WriteAllTextAsync(pending, string.Join('\n', lines) + "\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, final);
            return final;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private async Task<VoiceRecordingSample> ReadSampleAsync(string id, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("录音标识无效，请刷新录音库。");
        var folder = Path.Combine(_root, "samples", id);
        var manifest = Path.Combine(folder, "sample.json");
        if (new FileInfo(manifest).Length > 256 * 1024) throw new InvalidOperationException("录音元数据过大。");
        var sample = JsonSerializer.Deserialize<VoiceRecordingSample>(await File.ReadAllTextAsync(manifest, token).ConfigureAwait(false))
                     ?? throw new InvalidOperationException("录音元数据无效。");
        if (sample.Quality is null || sample.Id != id || Path.GetFullPath(sample.AudioPath) != Path.Combine(folder, id + ".wav")
            || Path.GetFullPath(sample.OriginalAudioPath) != Path.Combine(folder, "original.wav")
            || !File.Exists(sample.AudioPath) || !File.Exists(sample.OriginalAudioPath))
            throw new InvalidOperationException("录音库文件缺失或路径不匹配，请重新导入录音。");
        Field(sample.Name, "录音名称", 80); Field(sample.Speaker, "说话人", 80); Field(sample.Text, "逐字文稿", 1000); Language(sample.Language);
        return sample;
    }

    private async Task<VoiceRecordingTake> StoreTakeAsync(RecordingAudio audio, string? originalPath, bool clipped, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var final = Path.Combine(_root, "takes", id);
        var pending = Path.Combine(_root, "takes", ".pending-" + id);
        Directory.CreateDirectory(pending);
        try
        {
            await Task.Run(() => audio.Write(Path.Combine(pending, "audio.wav")), token).ConfigureAwait(false);
            await CopyOriginalAsync(originalPath ?? Path.Combine(pending, "audio.wav"), Path.Combine(pending, "original.wav"), token).ConfigureAwait(false);
            var take = new VoiceRecordingTake { Id = id, AudioPath = Path.Combine(final, "audio.wav"), OriginalAudioPath = Path.Combine(final, "original.wav"), Quality = audio.Analyze(clipped) };
            await File.WriteAllTextAsync(Path.Combine(pending, "take.json"), JsonSerializer.Serialize(take, Json), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Move(pending, final);
            return take;
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }

    private static async Task CopyOriginalAsync(string source, string destination, CancellationToken token)
    {
        if (new FileInfo(source).Length > RecordingAudio.MaximumBytes) throw new InvalidOperationException("原始录音文件过大。");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Keep a read-only handle during validation and copy so a concurrent editor cannot replace the original samples.
        await Task.Run(() => RecordingAudio.Read(source), token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, token).ConfigureAwait(false);
    }

    private static string Field(string value, string label, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum || value.Any(character => character == '|' || char.IsControl(character)))
            throw new InvalidOperationException($"{label}不能为空、不能超过 {maximum} 字符，且不能含竖线、换行或控制字符。");
        return value.Trim();
    }

    private static string Language(string value) => value switch
    {
        "zh-CN" or "zh-TW" => "ZH", "en-US" or "en-GB" => "EN", "ja-JP" => "JA", "ko-KR" => "KO", "yue-CN" => "YUE",
        _ => throw new InvalidOperationException("请选择支持的录音语言。")
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() { lock (_captureLock) { _disposed = true; _stop?.Set(); } }
}
