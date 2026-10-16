using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Application.UseCases;

public sealed class ConversionService(IMediaService media, ITranscriptionService transcription,
    ITranslationService translation, ISynthesisService synthesis, IProjectStore projects) : IConversionService
{
    public async Task<ConversionProject> PrepareAsync(ConversionOptions options, AppSettings settings,
        IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        options = Copy(options);
        settings = Copy(settings);
        ValidateOptions(options);
        var source = new FileInfo(options.InputPath);
        if (!source.Exists) throw new FileNotFoundException("找不到输入视频。", options.InputPath);
        options.InputPath = source.FullName;
        await media.CheckAvailableAsync(settings.Media, cancellationToken);
        Report(progress, "读取视频", 0, "检查视频与音轨…");
        var video = await media.ProbeAsync(source.FullName, settings.Media, cancellationToken);
        if (!double.IsFinite(video.DurationSeconds) || video.DurationSeconds <= 0) throw new InvalidDataException("无法确定视频时长。");
        if (!video.AudioTracks.Any(t => t.StreamIndex == options.AudioStreamIndex)) throw new InvalidOperationException("请选择有效的音轨。");
        // Each preparation preserves earlier edited jobs; paid requests use independent shared caches below.
        var id = Guid.NewGuid().ToString("N");
        var project = new ConversionProject
        {
            Id = id, Options = options, Video = video, WorkDirectory = projects.CreateWorkDirectory(id),
            SourceFileLength = source.Length, SourceLastWriteTimeUtc = source.LastWriteTimeUtc
        };
        await projects.SaveAsync(project, cancellationToken);
        try
        {
            var transcriptionCache = projects.CreateWorkDirectory("cache-stt-" + Hash(new
            {
                CacheVersion = 1, source.FullName, source.Length, source.LastWriteTimeUtc,
                options.AudioStreamIndex, options.SourceLanguage, Service = Identity(settings.Transcription)
            })[..32]);
            var translationCache = projects.CreateWorkDirectory("cache-translation");
            var chunks = await media.ExtractAudioChunksAsync(video, options.AudioStreamIndex, project.WorkDirectory, settings.Media,
                MapProgress(progress, "提取音频", 2, 12), cancellationToken);
            var assembled = new List<TranscriptSegment>();
            for (var i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = chunks[i];
                Report(progress, "语音转写", 15 + 45d * i / chunks.Count, $"转写音频 {i + 1}/{chunks.Count}；已完成片段会缓存。");
                var cache = Path.Combine(transcriptionCache, "chunk-" + Hash(new { i, chunk.OffsetSeconds, chunk.DurationSeconds }) + ".json");
                var segments = await ReadCacheAsync<List<TranscriptSegment>>(cache, cancellationToken);
                if (segments is null)
                {
                    segments = (await transcription.TranscribeAsync(chunk.FilePath, options.SourceLanguage, settings.Transcription, cancellationToken)).ToList();
                    ValidateTranscription(segments, chunk.DurationSeconds);
                    await WriteJsonAsync(cache, segments, cancellationToken);
                }
                else ValidateTranscription(segments, chunk.DurationSeconds);
                var leftBoundary = i == 0 ? 0 : (chunks[i - 1].OffsetSeconds + chunks[i - 1].DurationSeconds + chunk.OffsetSeconds) / 2;
                var rightBoundary = i == chunks.Count - 1 ? video.DurationSeconds : (chunk.OffsetSeconds + chunk.DurationSeconds + chunks[i + 1].OffsetSeconds) / 2;
                foreach (var raw in segments)
                {
                    var start = chunk.OffsetSeconds + raw.StartSeconds;
                    var end = Math.Min(video.DurationSeconds, chunk.OffsetSeconds + raw.EndSeconds);
                    var middle = (start + end) / 2;
                    if (middle < leftBoundary || (i < chunks.Count - 1 && middle >= rightBoundary)) continue;
                    assembled.Add(new TranscriptSegment { StartSeconds = Math.Max(0, start), EndSeconds = end, SourceText = raw.SourceText.Trim() });
                }
            }
            foreach (var segment in assembled.OrderBy(s => s.StartSeconds))
            {
                if (project.Segments.Count > 0)
                {
                    var previous = project.Segments[^1];
                    // Reconcile overlapping boundary hypotheses; retain the original service timestamps otherwise.
                    if (segment.StartSeconds < previous.EndSeconds && segment.SourceText == previous.SourceText) continue;
                    segment.StartSeconds = Math.Max(segment.StartSeconds, previous.EndSeconds);
                }
                if (segment.EndSeconds - segment.StartSeconds < 0.02) continue;
                segment.Id = project.Segments.Count + 1;
                project.Segments.Add(segment);
            }
            SubtitleWriter.Validate(project.Segments, video.DurationSeconds);
            await projects.SaveAsync(project, cancellationToken);
            var translations = new Dictionary<string, IReadOnlyList<TranslationItem>>(StringComparer.OrdinalIgnoreCase);
            async Task<IReadOnlyList<TranslationItem>> GetTextAsync(string language)
            {
                if (translations.TryGetValue(language, out var result)) return result;
                var inputs = project.Segments.Select(s => new TranslationItem(s.Id, s.SourceText)).ToArray();
                if (language == "source" || SameLanguage(options.SourceLanguage, language)) result = inputs;
                else
                {
                    var cache = Path.Combine(translationCache, "translation-" + Hash(new { options.SourceLanguage, language, inputs, Service = Identity(settings.Translation) }) + ".json");
                    result = await ReadCacheAsync<List<TranslationItem>>(cache, cancellationToken);
                    if (result is null)
                    {
                        var collected = new List<TranslationItem>();
                        var batches = inputs.Chunk(30).ToArray();
                        for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
                        {
                            Report(progress, "翻译", 62 + 32d * batchIndex / batches.Length, $"翻译为 {language}：{batchIndex + 1}/{batches.Length}");
                            var batch = batches[batchIndex];
                            var batchPath = Path.Combine(translationCache, "translation-batch-" + Hash(new { options.SourceLanguage, language, batch, Service = Identity(settings.Translation) }) + ".json");
                            var translated = await ReadCacheAsync<List<TranslationItem>>(batchPath, cancellationToken);
                            if (translated is null)
                            {
                                translated = (await translation.TranslateAsync(batch, options.SourceLanguage, language, settings.Translation, cancellationToken)).ToList();
                                ValidateTranslation(batch, translated);
                                await WriteJsonAsync(batchPath, translated, cancellationToken);
                            }
                            ValidateTranslation(batch, translated);
                            collected.AddRange(translated);
                        }
                        result = collected;
                        await WriteJsonAsync(cache, result, cancellationToken);
                    }
                    ValidateTranslation(inputs, result);
                }
                translations[language] = result;
                return result;
            }
            if (options.GenerateSubtitles)
            {
                var subtitle = (await GetTextAsync(options.SubtitleLanguage)).ToDictionary(i => i.Id, i => i.Text);
                foreach (var segment in project.Segments) segment.SubtitleText = subtitle[segment.Id];
            }
            if (options.GenerateSpeech)
            {
                var speech = (await GetTextAsync(options.SpeechLanguage)).ToDictionary(i => i.Id, i => i.Text);
                foreach (var segment in project.Segments) segment.SpeechText = speech[segment.Id];
            }
            project.PreparationComplete = true;
            project.Status = "待校对 / 可导出";
            await projects.SaveAsync(project, cancellationToken);
            Report(progress, "待校对", 100, "请检查文字和起止时间，再开始配音与导出。");
            return project;
        }
        catch (Exception ex)
        {
            project.Status = ex is OperationCanceledException ? "已取消准备（可重试）" : "准备失败（可重试）";
            await SaveStatusBestEffortAsync(project);
            throw;
        }
    }

    public async Task<ConversionResult> ExportAsync(ConversionProject project, AppSettings settings,
        IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        project = Copy(project);
        settings = Copy(settings);
        if (!project.PreparationComplete) throw new InvalidOperationException("任务尚未完成转写和翻译，请先恢复生成步骤，再进行导出。");
        ValidateOptions(project.Options);
        SubtitleWriter.Validate(project.Segments, project.Video.DurationSeconds);
        if (project.Options.GenerateSubtitles && project.Segments.Any(s => string.IsNullOrWhiteSpace(s.SubtitleText)))
            throw new InvalidOperationException("存在空白目标字幕，请完成翻译或校对后再导出。");
        if (project.Options.GenerateSpeech && project.Segments.Any(s => string.IsNullOrWhiteSpace(s.SpeechText)))
            throw new InvalidOperationException("存在空白配音稿，请完成翻译或校对后再导出。");
        var input = new FileInfo(project.Options.InputPath);
        if (!input.Exists || input.Length != project.SourceFileLength || input.LastWriteTimeUtc != project.SourceLastWriteTimeUtc)
            throw new InvalidOperationException("原视频已移动或发生变化，请重新导入并生成字幕。");
        var output = Path.GetFullPath(project.Options.OutputPath);
        if (!Path.GetExtension(output).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择 .mp4 格式的输出文件。");
        if (string.Equals(output, input.FullName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("输出文件不能覆盖原视频。");
        var srtOutput = project.Options.GenerateSubtitles && project.Options.ExportSrt ? Path.ChangeExtension(output, ".srt") : null;
        if (File.Exists(output) || (srtOutput is not null && File.Exists(srtOutput)))
            throw new IOException("输出视频或同名字幕已存在，请选择新的文件名。");
        if (project.Options.GenerateSpeech && string.IsNullOrWhiteSpace(project.Options.VoiceId)) throw new InvalidOperationException("请选择配音音色。");
        await media.CheckAvailableAsync(settings.Media, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var staging = Path.Combine(Path.GetDirectoryName(output)!, ".linguaforge-" + Guid.NewGuid().ToString("N") + ".mp4");
        var stagedSrt = Path.ChangeExtension(staging, ".srt");
        var srtPublished = false;
        var videoPublished = false;
        try
        {
            project.Status = "正在导出";
            await projects.SaveAsync(project, cancellationToken);
            string? subtitlePath = null;
            string? dubbedPath = null;
            if (project.Options.GenerateSpeech)
            {
                dubbedPath = await BuildFittedSpeechAsync(project, settings, progress, cancellationToken);
            }
            // Both burned subtitles and the sidecar must use the final, fitted wording.
            if (project.Options.GenerateSubtitles)
            {
                subtitlePath = Path.Combine(project.WorkDirectory, "subtitles.srt");
                await File.WriteAllTextAsync(subtitlePath, SubtitleWriter.BuildSrt(project), new UTF8Encoding(false), cancellationToken);
            }
            await media.RenderAsync(project.Video, project.Options, subtitlePath, dubbedPath, staging, settings.Media,
                MapProgress(progress, "合成视频", 70, 28), cancellationToken);
            if (!File.Exists(staging) || new FileInfo(staging).Length == 0) throw new InvalidDataException("未生成有效视频。");
            if (srtOutput is not null)
            {
                await File.WriteAllTextAsync(stagedSrt, SubtitleWriter.BuildSrt(project), new UTF8Encoding(false), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (srtOutput is not null) { File.Move(stagedSrt, srtOutput, false); srtPublished = true; }
            File.Move(staging, output, false);
            videoPublished = true;
            project.ResultPath = output;
            project.Status = "已完成";
            // Publishing has succeeded even if the history disk becomes unavailable afterwards.
            await SaveStatusBestEffortAsync(project);
            Report(progress, "已完成", 100, "视频已保存到本地。");
            return new ConversionResult(output, srtOutput);
        }
        catch (Exception ex)
        {
            if (srtPublished && !videoPublished && srtOutput is not null) TryDelete(srtOutput);
            project.Status = ex is OperationCanceledException ? "已取消导出（可重试）" : "导出失败（可重试）";
            await SaveStatusBestEffortAsync(project);
            throw;
        }
        finally { TryDelete(staging); TryDelete(stagedSrt); }
    }

    private async Task<string> BuildFittedSpeechAsync(ConversionProject project, AppSettings settings,
        IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        var options = project.Options;
        var voiceCache = projects.CreateWorkDirectory("cache-voice");
        var shorteningCache = projects.CreateWorkDirectory("cache-speech-shortening");
        var originalTexts = project.Segments.Select(s => s.SpeechText).ToArray();
        var attempts = new int[project.Segments.Count];
        var clips = new List<SpeechClip>();
        var modelIdentity = await synthesis.GetCacheIdentityAsync(options.VoiceId, settings.Synthesis, cancellationToken);

        async Task<string> SynthesizeCachedAsync(string text, int segmentId)
        {
            // Keep the existing voice-cache identity so previous successful exports remain reusable.
            var hash = modelIdentity is null
                ? Hash(new { SpeechText = text, options.SpeechLanguage, options.VoiceId, Service = Identity(settings.Synthesis) })
                : Hash(new { SpeechText = text, options.SpeechLanguage, options.VoiceId, Service = Identity(settings.Synthesis), ModelIdentity = modelIdentity });
            var audio = Path.Combine(voiceCache, "voice-" + hash + ".wav");
            if (!File.Exists(audio) || new FileInfo(audio).Length <= 44)
            {
                var temporary = audio + "." + Guid.NewGuid().ToString("N") + ".partial.wav";
                try
                {
                    await synthesis.SynthesizeAsync(text, options.SpeechLanguage, options.VoiceId, temporary, settings.Synthesis, cancellationToken);
                    if (!File.Exists(temporary) || new FileInfo(temporary).Length <= 44)
                        throw new InvalidDataException($"第 {segmentId} 段未生成有效音频。");
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, audio, true);
                }
                finally { TryDelete(temporary); }
            }
            return audio;
        }

        async Task<double> DurationAsync(string audio)
        {
            var duration = await media.GetAudioDurationAsync(audio, settings.Media, cancellationToken);
            if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("合成配音的时长无效，请重新生成配音。");
            return duration;
        }

        async Task<SpeechClip> FitAsync(int index, string audio, double duration, double window, bool forceShorten = false)
        {
            var segment = project.Segments[index];
            var allowedDuration = window * options.MaxSpeechSpeed;
            if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(allowedDuration) || allowedDuration <= 0)
                throw new InvalidDataException($"第 {segment.Id} 段配音时长或时间窗口无效。");
            var currentText = segment.SpeechText;
            var subtitleLanguage = options.SubtitleLanguage == "source" ? options.SourceLanguage : options.SubtitleLanguage;
            var syncSubtitle = options.GenerateSubtitles && SameLanguage(subtitleLanguage, options.SpeechLanguage)
                && string.Equals(segment.SubtitleText.Trim(), currentText.Trim(), StringComparison.Ordinal);
            while (forceShorten || duration > allowedDuration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!options.AutoShortenSpeech)
                    throw new SpeechDurationExceededException(index, duration, window, options.MaxSpeechSpeed);
                var characters = currentText.Trim().EnumerateRunes().Count();
                if (attempts[index] >= maxAttempts || characters < 2)
                    throw new InvalidOperationException($"第 {segment.Id} 段自动精简后仍无法适配 {window:F2} 秒时间窗口（最多 {maxAttempts} 次）。请校对该段配音稿或调整时间；未截断配音，也未提高最大语速。");
                attempts[index]++;
                // Leave room for pauses and the small sample-count changes introduced by atempo.
                var targetDuration = allowedDuration * .94;
                var maxCharacters = Math.Max(1, Math.Min(characters - 1,
                    (int)Math.Floor(characters * Math.Min(.9, targetDuration / duration))));
                var request = new SpeechShorteningRequest(segment.Id, segment.SourceText, originalTexts[index], currentText,
                    options.SpeechLanguage, maxCharacters, duration, targetDuration,
                    index > 0 ? originalTexts[index - 1] : "", index + 1 < originalTexts.Length ? originalTexts[index + 1] : "");
                Report(progress, "自动精简", 50d * index / project.Segments.Count,
                    $"第 {segment.Id} 段配音 {duration:F2} 秒，正在保留原意精简并重新配音（{attempts[index]}/{maxAttempts}）…");
                var cache = Path.Combine(shorteningCache, "shortening-" + Hash(new
                { Version = 1, request, options.MaxSpeechSpeed, Service = Identity(settings.Translation) }) + ".json");
                var shortened = await ReadCacheAsync<string>(cache, cancellationToken);
                if (!IsValidShortening(shortened, characters, maxCharacters))
                {
                    shortened = (await translation.ShortenForSpeechAsync(request, settings.Translation, cancellationToken))?.Trim();
                    if (!IsValidShortening(shortened, characters, maxCharacters))
                        throw new InvalidOperationException($"第 {segment.Id} 段自动精简未返回有效的更短文案，已保留原稿。请校对后重试。");
                    await WriteJsonAsync(cache, shortened, cancellationToken);
                }
                // A candidate is only accepted into the project after synthesizing and measuring it.
                audio = await SynthesizeCachedAsync(shortened!, segment.Id);
                duration = await DurationAsync(audio);
                currentText = shortened!;
                forceShorten = false;
            }
            if (!string.Equals(segment.SpeechText, currentText, StringComparison.Ordinal))
            {
                segment.SpeechText = currentText;
                if (syncSubtitle) segment.SubtitleText = currentText;
                await projects.SaveAsync(project, cancellationToken);
                Report(progress, "自动精简", 50d * (index + 1) / project.Segments.Count,
                    $"第 {segment.Id} 段已精简并重新配音，时长 {duration:F2} 秒；已保存适配文案。");
            }
            return new SpeechClip(audio, segment.StartSeconds, segment.EndSeconds);
        }

        for (var i = 0; i < project.Segments.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = project.Segments[i];
            Report(progress, "生成配音", 50d * i / project.Segments.Count, $"合成配音 {i + 1}/{project.Segments.Count}");
            var audio = await SynthesizeCachedAsync(segment.SpeechText, segment.Id);
            clips.Add(options.AutoShortenSpeech
                ? await FitAsync(i, audio, await DurationAsync(audio), segment.EndSeconds - segment.StartSeconds)
                : new SpeechClip(audio, segment.StartSeconds, segment.EndSeconds));
        }
        var dubbedPath = Path.Combine(project.WorkDirectory, "dubbed.wav");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await media.BuildDubbedAudioAsync(clips, project.Video.DurationSeconds, options.MaxSpeechSpeed, dubbedPath,
                    settings.Media, MapProgress(progress, "配音对齐", 50, 20), cancellationToken);
                return dubbedPath;
            }
            catch (SpeechDurationExceededException ex) when (options.AutoShortenSpeech
                && ex.ClipIndex >= 0 && ex.ClipIndex < clips.Count)
            {
                // ffprobe preflight avoids repeatedly building the entire timeline. The decoder remains
                // the final authority in the rare case where atempo produces extra samples.
                clips[ex.ClipIndex] = await FitAsync(ex.ClipIndex, clips[ex.ClipIndex].FilePath,
                    ex.DurationSeconds, ex.WindowSeconds, forceShorten: true);
            }
        }
    }

    private static bool IsValidShortening(string? text, int previousCharacters, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var length = text.Trim().EnumerateRunes().Count();
        return length > 0 && length < previousCharacters && length <= maximumCharacters;
    }

    public static bool SameLanguage(string source, string target)
    {
        if (source == "auto" || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return false;
        static string Normalize(string code) => code.ToLowerInvariant() switch
        {
            "zh-cn" or "zh-hans" or "zh-sg" => "zh-hans",
            "zh-tw" or "zh-hant" or "zh-hk" => "zh-hant",
            var other when other.StartsWith("zh") => other,
            var other => other.Split('-')[0]
        };
        return Normalize(source) == Normalize(target);
    }

    private static void ValidateOptions(ConversionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.InputPath)) throw new InvalidOperationException("请先选择视频。");
        if (!options.GenerateSubtitles && !options.GenerateSpeech) throw new InvalidOperationException("请至少选择生成字幕或配音。");
        if (options.GenerateSubtitles && !options.BurnSubtitles && !options.ExportSrt) throw new InvalidOperationException("请选择烧录字幕或导出 SRT。");
        if (options.SubtitleFontSize is < 12 or > 72) throw new InvalidOperationException("字幕字号必须在 12 到 72 之间。");
        if (!double.IsFinite(options.MaxSpeechSpeed) || options.MaxSpeechSpeed is < 1 or > 2) throw new InvalidOperationException("最大配音加速必须在 1.0 到 2.0 之间。");
        if (string.IsNullOrWhiteSpace(options.SourceLanguage)) throw new InvalidOperationException("请选择原音语言。");
        if (options.GenerateSubtitles && string.IsNullOrWhiteSpace(options.SubtitleLanguage)) throw new InvalidOperationException("请选择字幕语言。");
        if (options.GenerateSpeech && (string.IsNullOrWhiteSpace(options.SpeechLanguage) || options.SpeechLanguage == "source" || options.SpeechLanguage == "auto"))
            throw new InvalidOperationException("请选择明确的配音语言。");
    }

    private static void ValidateTranscription(IReadOnlyList<TranscriptSegment> segments, double duration)
    {
        foreach (var segment in segments)
            if (!double.IsFinite(segment.StartSeconds) || !double.IsFinite(segment.EndSeconds) || segment.StartSeconds < 0
                || segment.EndSeconds <= segment.StartSeconds || segment.EndSeconds > duration + 1 || string.IsNullOrWhiteSpace(segment.SourceText))
                throw new InvalidDataException("转写服务未返回有效的文字时间戳，请检查模型是否支持带时间戳转写。");
    }

    private static void ValidateTranslation(IReadOnlyList<TranslationItem> source, IReadOnlyList<TranslationItem> translated)
    {
        if (source.Count != translated.Count || translated.Select(t => t.Id).Distinct().Count() != source.Count
            || !source.Select(t => t.Id).Order().SequenceEqual(translated.Select(t => t.Id).Order()) || translated.Any(t => string.IsNullOrWhiteSpace(t.Text)))
            throw new InvalidDataException("翻译结果缺少片段或编号不匹配，请重试。");
    }

    private static object Identity(ServiceSettings settings) => settings.Provider == ServiceProviderKind.LocalGptSoVits
        ? new { settings.Provider, settings.LocalVoice.ApiEndpoint, settings.LocalVoice.RootDirectory, Engine = "GPT-SoVITS-v2" }
        : new { settings.Provider, settings.Endpoint, settings.Region, settings.Deployment, settings.ApiVersion, settings.Model };
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static void Report(IProgress<ProcessingProgress>? progress, string stage, double percent, string message) => progress?.Report(new(stage, percent, message));
    private static IProgress<ProcessingProgress> MapProgress(IProgress<ProcessingProgress>? target, string stage, double start, double range)
        => new InlineProgress(p => target?.Report(new(stage, start + Math.Clamp(p.Percent, 0, 100) / 100 * range, p.Message)));
    private sealed class InlineProgress(Action<ProcessingProgress> report) : IProgress<ProcessingProgress>
    {
        public void Report(ProcessingProgress value) => report(value);
    }
    private async Task SaveStatusBestEffortAsync(ConversionProject project)
    {
        try { await projects.SaveAsync(project, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static async Task<T?> ReadCacheAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, cancellationToken)); }
        catch (JsonException) { return default; }
    }
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { TryDelete(temporary); }
    }
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
