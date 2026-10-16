using System.Text.RegularExpressions;
using System.Globalization;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.Media;

internal delegate Task<string> MediaCommand(string executable, IEnumerable<string> arguments,
    CancellationToken cancellationToken, Action<double>? onProgress = null, string? workingDirectory = null);

internal sealed record VideoEncodingPlan(string Name, string Encoder, bool Hardware, IReadOnlyList<string> Arguments)
{
    internal static VideoEncodingPlan Software(bool quality, int constantQuality = 20) => new(
        quality ? "CPU 画质与体积优先" : "CPU 快速编码", "libx264", false,
        ["-c:v", "libx264", "-preset", quality ? "medium" : "veryfast", "-crf", constantQuality.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"]);

    internal VideoEncodingPlan WithQuality(int constantQuality)
    {
        var args = Arguments.ToArray();
        var value = constantQuality.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] is "-crf" or "-cq" or "-global_quality" or "-qp_i" or "-qp_p" or "-qp_b") args[++i] = value;
        return this with { Arguments = args };
    }
}

/// <summary>Compiled-in encoders are only candidates; a short encode of the actual filtered input proves availability.</summary>
internal sealed class VideoEncoderSelector(MediaCommand command)
{
    internal static readonly VideoEncodingPlan[] HardwarePlans =
    [
        new("NVIDIA 显卡编码", "h264_nvenc", true,
            ["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "20", "-b:v", "0", "-pix_fmt", "yuv420p"]),
        new("Intel 显卡编码", "h264_qsv", true,
            ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "20", "-pix_fmt", "nv12"]),
        new("AMD 显卡编码", "h264_amf", true,
            ["-c:v", "h264_amf", "-usage", "transcoding", "-quality", "balanced", "-rc", "cqp",
             "-qp_i", "20", "-qp_p", "20", "-qp_b", "20", "-pix_fmt", "nv12"])
    ];

    internal async Task<VideoEncodingPlan> SelectAsync(string executable, VideoEncodingMode mode,
        string input, int videoIndex, string videoFilter, string workingDirectory, CancellationToken cancellationToken,
        VideoQualityProfile? quality = null)
    {
        quality ??= VideoQualityProfile.For(OutputVideoQuality.Original);
        if (!Enum.IsDefined(mode)) throw new InvalidOperationException("请选择有效的导出编码方式。");
        if (mode != VideoEncodingMode.Auto) return VideoEncodingPlan.Software(mode == VideoEncodingMode.CpuQuality, quality.ConstantQuality);
        var software = VideoEncodingPlan.Software(false, quality.ConstantQuality);
        string encoders;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { encoders = await command(executable, ["-hide_banner", "-encoders"], timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return software; }
        }
        foreach (var candidate in HardwarePlans)
        {
            var plan = candidate.WithQuality(quality.ConstantQuality);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Regex.IsMatch(encoders, @"(?m)^\s*V\S*\s+" + Regex.Escape(plan.Encoder) + @"\s")) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", input,
                "-map", $"0:{videoIndex}", "-an", "-sn", "-vf", videoFilter };
            args.AddRange(plan.Arguments);
            args.AddRange(["-frames:v", "3", "-f", "null", "-"]);
            try
            {
                await command(executable, args, timeout.Token, null, workingDirectory).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return plan;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (MediaProcessException ex) when (IsHardwareFailure(ex.Diagnostic)) { }
        }
        return software;
    }

    internal static bool IsHardwareFailure(string diagnostic)
    {
        const RegexOptions flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        // FFmpeg can append a generic encoder/output error after an unrelated failure.
        if (Regex.IsMatch(diagnostic, @"No space left|Permission denied|Access is denied|Read-only file system|" +
            @"Error opening (?:input|output) files?|Unable to open.*(?:subtitle|\.srt)|" +
            @"Error initializing filters|Error reinitializing filters|Invalid data found|" +
            @"(?:Parsed_subtitles|libass).*(?:error|failed|unable|cannot)|Could not create a libass track", flags)) return false;
        return Regex.IsMatch(diagnostic,
            @"Cannot load (?:nvcuda|nvEncodeAPI|amfrt)|No (?:NVENC|capable) devices|(?:NVENC|CUDA|AMF|MFX|QSV).*(?:failed|failure|error|unsupported|not supported|not available)|" +
            @"Driver.*(?:version|support)|Error while opening encoder|Error initializing (?:output|encoder)|Unknown encoder|" +
            @"unsupported (?:device|GPU)|No device available|Cannot init (?:CUDA|MFX|AMF)|device setup failed", flags);
    }
}

internal sealed class MediaProcessException(string message, string diagnostic, double processedSeconds)
    : InvalidOperationException(message)
{
    internal string Diagnostic { get; } = diagnostic;
    internal double ProcessedSeconds { get; } = processedSeconds;
}
