using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.Media;

/// <summary>Runs locally installed FFmpeg tools without a command shell.</summary>
public sealed class FfmpegMediaService : IMediaService
{
    private readonly MediaCommand _renderCommand;
    public FfmpegMediaService() : this(RunAsync) { }
    internal FfmpegMediaService(MediaCommand renderCommand) => _renderCommand = renderCommand;

    private const int SampleRate = 48000;
    private const int BytesPerSample = 2;
    private const double ChunkSeconds = 300;
    private const double ChunkContextSeconds = 1;

    public async Task CheckAvailableAsync(MediaSettings settings, CancellationToken cancellationToken)
    {
        await RunAsync(settings.FfmpegPath, ["-hide_banner", "-version"], cancellationToken).ConfigureAwait(false);
        await RunAsync(settings.FfprobePath, ["-hide_banner", "-version"], cancellationToken).ConfigureAwait(false);
    }

    public async Task<VideoInfo> ProbeAsync(string path, MediaSettings settings, CancellationToken cancellationToken)
    {
        path = RequireFile(path);
        using var document = await ProbeDocumentAsync(path, settings, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => ReadString(s, "codec_type") == "video" &&
            !(s.TryGetProperty("disposition", out var d) && ReadInt(d, "attached_pic") == 1));
        if (video.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("所选文件没有可用的视频画面，请选择视频文件。");
        var duration = ReadDuration(root, video);
        if (!double.IsFinite(duration) || duration <= 0)
            throw new InvalidOperationException("无法读取视频时长。请检查文件是否完整，或先转为 MP4 后重试。");
        var tracks = streams.Where(s => ReadString(s, "codec_type") == "audio").Select(s =>
        {
            var index = ReadInt(s, "index");
            var tags = s.TryGetProperty("tags", out var t) ? t : default;
            var language = ReadString(tags, "language");
            var title = ReadString(tags, "title");
            var details = string.Join(" · ", new[] { language, title, ReadString(s, "codec_name") }.Where(v => !string.IsNullOrWhiteSpace(v)));
            return new AudioTrack(index, $"音轨 {index} · {details}");
        }).ToArray();
        return new VideoInfo(path, duration, ReadInt(video, "width"), ReadInt(video, "height"), tracks);
    }

    public async Task<double> GetAudioDurationAsync(string path, MediaSettings settings, CancellationToken cancellationToken)
    {
        using var document = await ProbeDocumentAsync(RequireFile(path), settings, cancellationToken).ConfigureAwait(false);
        var audio = document.RootElement.GetProperty("streams").EnumerateArray()
            .FirstOrDefault(stream => ReadString(stream, "codec_type") == "audio");
        if (audio.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("合成语音文件没有可读取的音轨，请重新合成。");
        var duration = ReadDuration(document.RootElement, audio);
        if (!double.IsFinite(duration) || duration <= 0)
            throw new InvalidOperationException("合成语音时长无效，请重新合成。");
        return duration;
    }

    public async Task<IReadOnlyList<AudioChunk>> ExtractAudioChunksAsync(VideoInfo video, int audioStreamIndex,
        string workDirectory, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        var input = RequireFile(video.FilePath);
        if (!video.AudioTracks.Any(t => t.StreamIndex == audioStreamIndex))
            throw new InvalidOperationException("请选择视频中存在的音轨；无音轨的视频无法进行语音转写。");
        ValidateDuration(video.DurationSeconds);
        Directory.CreateDirectory(workDirectory);
        var count = checked((int)Math.Ceiling(video.DurationSeconds / ChunkSeconds));
        var chunks = new List<AudioChunk>(count);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = Math.Max(0, index * ChunkSeconds - ChunkContextSeconds);
            var end = Math.Min(video.DurationSeconds, (index + 1) * ChunkSeconds + ChunkContextSeconds);
            var duration = end - offset;
            var output = Path.GetFullPath(Path.Combine(workDirectory, $"audio-{index:D5}.wav"));
            EnsureDifferentPaths(output, input);
            var chunkIndex = index;
            await RunAsync(settings.FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", Number(offset), "-i", input,
                 "-map", $"0:{audioStreamIndex}", "-vn", "-t", Number(duration), "-af", "aresample=16000:async=1:first_pts=0,apad",
                 "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
                 "-progress", "pipe:1", "-nostats", "-f", "wav", output], cancellationToken,
                seconds => Report(progress, "提取音频", (chunkIndex + Math.Min(1, seconds / duration)) / count * 100,
                    $"提取第 {chunkIndex + 1}/{count} 段音频")).ConfigureAwait(false);
            // Five-minute PCM chunks are about 9.7 MB, comfortably below the 25 MB upload limit.
            if (!File.Exists(output) || new FileInfo(output).Length is <= 44 or > 25_000_000)
                throw new InvalidOperationException($"第 {index + 1} 段音频为空或超过上传限制，请检查原视频音轨。");
            chunks.Add(new AudioChunk(output, offset, duration));
            Report(progress, "提取音频", (index + 1d) / count * 100, $"已提取 {index + 1}/{count} 段音频");
        }
        return chunks;
    }

    public async Task BuildDubbedAudioAsync(IReadOnlyList<SpeechClip> clips, double videoDurationSeconds, double maxSpeechSpeed,
        string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        ValidateDuration(videoDurationSeconds);
        if (!double.IsFinite(maxSpeechSpeed) || maxSpeechSpeed < 1 || maxSpeechSpeed > 2)
            throw new InvalidOperationException("最大配音语速应在 1.0 至 2.0 倍之间。");
        outputPath = PrepareOutputPath(outputPath);
        foreach (var clip in clips) EnsureDifferentPaths(outputPath, RequireFile(clip.FilePath));
        var tempDirectory = Path.Combine(Path.GetDirectoryName(outputPath)!, "dub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var timelinePath = Path.Combine(tempDirectory, "timeline.pcm");
        var clipPath = Path.Combine(tempDirectory, "clip.pcm");
        try
        {
            var totalSamples = checked((long)Math.Round(videoDurationSeconds * SampleRate));
            await using (var timeline = new FileStream(timelinePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                timeline.SetLength(checked(totalSamples * BytesPerSample));
                for (var index = 0; index < clips.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var clip = clips[index];
                    if (!double.IsFinite(clip.StartSeconds) || !double.IsFinite(clip.EndSeconds) || clip.StartSeconds < 0 ||
                        clip.EndSeconds <= clip.StartSeconds || clip.EndSeconds > videoDurationSeconds + .001)
                        throw new InvalidOperationException($"第 {index + 1} 段配音时间无效，请先修正字幕时间轴。");
                    var startSample = checked((long)Math.Round(clip.StartSeconds * SampleRate));
                    var endSample = Math.Min(totalSamples, checked((long)Math.Round(clip.EndSeconds * SampleRate)));
                    var allowedSamples = endSample - startSample;
                    var allowedSeconds = allowedSamples / (double)SampleRate;
                    using var probe = await ProbeDocumentAsync(RequireFile(clip.FilePath), settings, cancellationToken).ConfigureAwait(false);
                    var duration = ReadDuration(probe.RootElement);
                    if (!double.IsFinite(duration) || duration <= 0)
                        throw new InvalidOperationException($"第 {index + 1} 段合成语音为空或不可读取，请重新合成。");
                    var requiredSpeed = duration / allowedSeconds;
                    if (requiredSpeed > maxSpeechSpeed + .001)
                        throw new SpeechDurationExceededException(index, duration, allowedSeconds, maxSpeechSpeed);
                    var speed = Math.Min(maxSpeechSpeed, Math.Max(1, requiredSpeed * (requiredSpeed > 1 ? 1.005 : 1)));
                    long producedSamples = 0;
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        var filter = $"aresample={SampleRate},atempo={Number(speed)}";
                        await RunAsync(settings.FfmpegPath,
                            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", Path.GetFullPath(clip.FilePath),
                             "-map", "0:a:0", "-vn", "-af", filter, "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
                             "-c:a", "pcm_s16le", "-f", "s16le", clipPath], cancellationToken).ConfigureAwait(false);
                        producedSamples = new FileInfo(clipPath).Length / BytesPerSample;
                        if (producedSamples <= allowedSamples) break;
                        var correctedSpeed = speed * producedSamples / allowedSamples * 1.008;
                        if (speed >= maxSpeechSpeed || correctedSpeed > maxSpeechSpeed && attempt == 2) break;
                        speed = Math.Min(maxSpeechSpeed, correctedSpeed);
                    }
                    if (producedSamples <= 0)
                        throw new InvalidOperationException($"第 {index + 1} 段配音解码后没有声音，请重新合成。");
                    if (producedSamples > allowedSamples)
                        throw new SpeechDurationExceededException(index,
                            Math.Max(duration, producedSamples / (double)SampleRate * speed), allowedSeconds, maxSpeechSpeed);
                    await MixPcmAsync(timeline, clipPath, startSample * BytesPerSample, cancellationToken).ConfigureAwait(false);
                    Report(progress, "对齐配音", (index + 1d) / Math.Max(1, clips.Count) * 95,
                        $"已对齐 {index + 1}/{clips.Count} 段配音（{speed:F2} 倍语速）");
                }
            }
            await RunAsync(settings.FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "s16le", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
                 "-ac", "1", "-i", timelinePath, "-c:a", "pcm_s16le", "-rf64", "auto", "-f", "wav", outputPath], cancellationToken).ConfigureAwait(false);
            Report(progress, "对齐配音", 100, "配音时间轴已完成，句间空白保留为静音");
        }
        finally
        {
            DeleteTemporaryFile(clipPath);
            DeleteTemporaryFile(timelinePath);
            DeleteEmptyDirectory(tempDirectory);
        }
    }

    public async Task RenderAsync(VideoInfo video, ConversionOptions options, string? subtitlePath, string? dubbedAudioPath,
        string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        var input = RequireFile(video.FilePath);
        outputPath = PrepareOutputPath(outputPath);
        EnsureDifferentPaths(input, outputPath);
        ValidateDuration(video.DurationSeconds);
        if (!Enum.IsDefined(settings.VideoEncoding)) throw new InvalidOperationException("请选择有效的导出编码方式。");
        var quality = VideoQualityProfile.For(settings.OutputQuality);
        var burn = options.GenerateSubtitles && options.BurnSubtitles;
        if (burn && string.IsNullOrWhiteSpace(subtitlePath))
            throw new InvalidOperationException("没有生成字幕文件，无法烧录字幕。");
        if (options.GenerateSpeech && string.IsNullOrWhiteSpace(dubbedAudioPath))
            throw new InvalidOperationException("没有生成配音音轨，无法导出配音视频。");
        if (dubbedAudioPath is not null)
        {
            dubbedAudioPath = RequireFile(dubbedAudioPath);
            EnsureDifferentPaths(outputPath, dubbedAudioPath);
        }
        string? subtitleCopy = null;
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        try
        {
            string? subtitleFilter = null;
            if (burn)
            {
                var filters = await _renderCommand(settings.FfmpegPath, ["-hide_banner", "-filters"], cancellationToken).ConfigureAwait(false);
                if (!Regex.IsMatch(filters, @"(?m)^\s*\S+\s+subtitles\s"))
                    throw new InvalidOperationException("当前 FFmpeg 不包含 subtitles/libass 字幕滤镜。请在设置中选择支持 libass 的完整 FFmpeg 版本。");
                subtitlePath = RequireFile(subtitlePath!);
                EnsureDifferentPaths(outputPath, subtitlePath);
                // A simple relative ASCII file name avoids two levels of FFmpeg filter escaping,
                // including Windows drive colons, apostrophes and Chinese directory names.
                var name = "subtitle-" + Guid.NewGuid().ToString("N") + ".srt";
                subtitleCopy = Path.Combine(outputDirectory, name);
                File.Copy(subtitlePath, subtitleCopy, false);
                var fontSize = Math.Clamp(options.SubtitleFontSize, 10, 96);
                subtitleFilter = $"subtitles=filename={name}:charenc=UTF-8:force_style='FontName=Microsoft YaHei,FontSize={fontSize},Outline=1,Shadow=0,MarginV=18'";
            }
            using var probe = await ProbeDocumentAsync(input, settings, cancellationToken, _renderCommand).ConfigureAwait(false);
            var codec = probe.RootElement.GetProperty("streams").EnumerateArray()
                .FirstOrDefault(s => ReadString(s, "codec_type") == "video" &&
                    !(s.TryGetProperty("disposition", out var d) && ReadInt(d, "attached_pic") == 1));
            var videoIndex = ReadInt(codec, "index");
            var codecName = ReadString(codec, "codec_name");
            var canCopyVideo = !burn && settings.OutputQuality == OutputVideoQuality.Original
                && codecName is "h264" or "hevc" or "mpeg4" or "av1";
            var sourceAudio = probe.RootElement.GetProperty("streams").EnumerateArray()
                .FirstOrDefault(s => ReadString(s, "codec_type") == "audio" && ReadInt(s, "index") == options.AudioStreamIndex);
            var canCopyAudio = dubbedAudioPath is null && ReadString(sourceAudio, "codec_name") == "aac";
            // libass processes ordinary CPU frames. Hardware encoding is applied after these filters.
            var videoFilter = quality.BuildFilter(subtitleFilter);
            var encoding = canCopyVideo ? null : await new VideoEncoderSelector(_renderCommand).SelectAsync(
                settings.FfmpegPath, settings.VideoEncoding, input, videoIndex, videoFilter, outputDirectory, cancellationToken, quality).ConfigureAwait(false);
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", input };
            if (dubbedAudioPath is not null) args.AddRange(["-i", dubbedAudioPath]);
            args.AddRange(["-map", $"0:{videoIndex}"]);
            if (dubbedAudioPath is not null && options.OriginalAudio == OriginalAudioMode.QuietMix)
            {
                RequireAudioTrack(video, options.AudioStreamIndex);
                args.AddRange(["-filter_complex", $"[0:{options.AudioStreamIndex}]volume=0.15[original];[original][1:a:0]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.95[mixed]", "-map", "[mixed]"]);
            }
            else if (dubbedAudioPath is not null)
                args.AddRange(["-map", "1:a:0"]);
            else if (video.AudioTracks.Count > 0)
            {
                RequireAudioTrack(video, options.AudioStreamIndex);
                args.AddRange(["-map", $"0:{options.AudioStreamIndex}"]);
            }
            if (!canCopyVideo) args.AddRange(["-vf", videoFilter]);
            var encodingIndex = args.Count;
            var audioArgs = canCopyAudio ? new[] { "-c:a", "copy" } : ["-c:a", "aac", "-b:a", "192k"];
            var outputArgs = new List<string>(audioArgs);
            outputArgs.AddRange(["-t", Number(video.DurationSeconds), "-map_metadata", "-1", "-map_chapters", "-1",
                "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", "-f", "mp4", outputPath]);
            void SetEncodingArguments()
            {
                args.RemoveRange(encodingIndex, args.Count - encodingIndex);
                args.AddRange(canCopyVideo ? new[] { "-c:v", "copy" } : encoding!.Arguments);
                args.AddRange(outputArgs);
            }
            async Task EncodeAsync()
            {
                var watch = Stopwatch.StartNew();
                var name = encoding is null ? "直接复制原视频" : encoding.Name + " · " + quality.Name;
                await _renderCommand(settings.FfmpegPath, args, cancellationToken, seconds =>
                {
                    var message = name;
                    if (seconds > 0 && watch.Elapsed.TotalSeconds >= 1)
                    {
                        var speed = seconds / watch.Elapsed.TotalSeconds;
                        var remaining = Math.Max(0, video.DurationSeconds - seconds) / speed;
                        if (double.IsFinite(speed) && double.IsFinite(remaining))
                            message += $" · {speed:F2} 倍速 · 预计剩余 {FormatRemaining(remaining)}";
                    }
                    Report(progress, "合成视频", Math.Min(99, seconds / video.DurationSeconds * 100), message);
                }, outputDirectory).ConfigureAwait(false);
            }
            SetEncodingArguments();
            try { await EncodeAsync().ConfigureAwait(false); }
            catch (MediaProcessException ex) when (encoding?.Hardware == true && ex.ProcessedSeconds <= .001
                && VideoEncoderSelector.IsHardwareFailure(ex.Diagnostic))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Only a hardware startup failure can retry. Disk, subtitle and input errors are reported as-is.
                DeleteTemporaryFile(outputPath);
                encoding = VideoEncodingPlan.Software(false, quality.ConstantQuality);
                Report(progress, "合成视频", 0, "显卡编码未能启动，已切换到 CPU 快速编码。");
                SetEncodingArguments();
                await EncodeAsync().ConfigureAwait(false);
            }
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException("FFmpeg 未生成有效的视频文件。");
            Report(progress, "合成视频", 100, "视频合成完成");
        }
        finally
        {
            if (subtitleCopy is not null) DeleteTemporaryFile(subtitleCopy);
        }
    }

    private static async Task MixPcmAsync(FileStream timeline, string clipPath, long startByte, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(clipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        var source = new byte[65536];
        var existing = new byte[source.Length];
        long offset = startByte;
        while (true)
        {
            var read = await input.ReadAsync(source, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (read % BytesPerSample != 0) throw new InvalidOperationException("合成语音的 PCM 数据损坏。");
            timeline.Position = offset;
            await timeline.ReadExactlyAsync(existing.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            MixSamples(source, existing, read);
            timeline.Position = offset;
            await timeline.WriteAsync(existing.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            offset += read;
        }
    }

    private static void MixSamples(byte[] source, byte[] destination, int length)
    {
        for (var i = 0; i < length; i += BytesPerSample)
        {
            var mixed = BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(i, 2)) +
                BinaryPrimitives.ReadInt16LittleEndian(destination.AsSpan(i, 2));
            BinaryPrimitives.WriteInt16LittleEndian(destination.AsSpan(i, 2), (short)Math.Clamp(mixed, short.MinValue, short.MaxValue));
        }
    }

    private static async Task<JsonDocument> ProbeDocumentAsync(string path, MediaSettings settings, CancellationToken cancellationToken,
        MediaCommand? command = null)
    {
        command ??= RunAsync;
        var json = await command(settings.FfprobePath,
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", path], cancellationToken).ConfigureAwait(false);
        try { return JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidOperationException("ffprobe 返回了无效的媒体信息，请检查工具版本和输入文件。", ex); }
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken,
        Action<double>? onProgress = null, string? workingDirectory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("请在设置中配置 FFmpeg 和 ffprobe 路径。");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable.Trim().Trim('"'),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (workingDirectory is not null) startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动媒体处理工具。");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"无法启动 {Path.GetFileName(startInfo.FileName)}。请先安装 FFmpeg，并在设置中选择 ffmpeg.exe 与 ffprobe.exe 的完整路径。", ex);
        }
        // Cancellation must kill the child even if the WPF dispatcher has stopped pumping.
        // Registration also handles a token canceled between process.Start and this line.
        using var cancellationRegistration = cancellationToken.Register(static state => KillProcess((Process)state!), process);
        double processedSeconds = 0;
        var outputTask = ReadOutputAsync(process.StandardOutput, seconds =>
        {
            processedSeconds = Math.Max(processedSeconds, seconds);
            onProgress?.Invoke(seconds);
        });
        var errorTask = ReadErrorAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new MediaProcessException($"{Path.GetFileName(startInfo.FileName)} 处理失败（退出码 {process.ExitCode}）：{SanitizeError(error)}", error, processedSeconds);
        return output;
    }

    private static string FormatRemaining(double seconds)
    {
        var total = (long)Math.Clamp(Math.Ceiling(seconds), 0, 315360000);
        return total >= 3600 ? $"{total / 3600} 小时 {total % 3600 / 60} 分"
            : total >= 60 ? $"{total / 60} 分 {total % 60} 秒" : $"{total} 秒";
    }

    private static void KillProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // Already exited or disposed.
        catch (Win32Exception) { } // The process may exit concurrently with Kill.
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, Action<double>? onProgress)
    {
        var result = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (onProgress is not null && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                double.TryParse(line.AsSpan(12), NumberStyles.Float, CultureInfo.InvariantCulture, out var microseconds))
                onProgress(Math.Max(0, microseconds / 1_000_000));
            if (result.Length < 1_048_576)
                result.AppendLine(line.Length > 16384 ? line[..16384] : line);
        }
        return result.ToString();
    }

    private static async Task<string> ReadErrorAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            result.Append(buffer, 0, read);
            if (result.Length > 12000) result.Remove(0, result.Length - 12000);
        }
        return result.ToString();
    }

    private static string SanitizeError(string value)
    {
        value = Regex.Replace(value, @"https?://\S+", "[远程地址已隐藏]", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"(?i)(api[-_]?key|access[-_]?key|authorization|token|sig)\s*[:=]\s*\S+", "$1=[已隐藏]");
        return string.IsNullOrWhiteSpace(value) ? "工具没有返回错误详情，请检查输入文件、工具配置和输出目录权限。" : value.Trim();
    }

    private static string ReadString(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) ? value.ToString() : "";
    private static int ReadInt(JsonElement element, string property) => int.TryParse(ReadString(element, property), out var value) ? value : 0;
    private static double ReadDuration(JsonElement root, JsonElement stream = default)
    {
        var format = root.TryGetProperty("format", out var f) ? f : default;
        if (double.TryParse(ReadString(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) && duration > 0)
            return duration;
        return double.TryParse(ReadString(stream, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out duration) ? duration : 0;
    }
    private static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    private static string RequireFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("所需的本地媒体文件不存在，请重新选择文件。", path);
        return Path.GetFullPath(path);
    }
    private static string PrepareOutputPath(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
    private static void EnsureDifferentPaths(string first, string second)
    {
        if (string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("输出路径不能覆盖输入文件，请选择其他文件名。");
    }
    private static void ValidateDuration(double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidOperationException("媒体时长必须大于零。");
    }
    private static void RequireAudioTrack(VideoInfo video, int index)
    {
        if (!video.AudioTracks.Any(track => track.StreamIndex == index)) throw new InvalidOperationException("所选原音轨不存在，请重新选择音轨。");
    }
    private static void Report(IProgress<ProcessingProgress>? progress, string stage, double percent, string message) =>
        progress?.Report(new ProcessingProgress(stage, Math.Clamp(percent, 0, 100), message));
    private static void DeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static void DeleteEmptyDirectory(string path)
    {
        try { Directory.Delete(path, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
