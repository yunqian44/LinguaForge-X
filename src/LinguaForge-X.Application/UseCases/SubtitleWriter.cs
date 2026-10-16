using System.Globalization;
using System.Text;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Application.UseCases;

public static class SubtitleWriter
{
    public static void Validate(IReadOnlyList<TranscriptSegment> segments, double durationSeconds)
    {
        if (segments.Count == 0) throw new InvalidOperationException("没有识别到语音，请检查所选音轨和原音语言。");
        var ids = new HashSet<int>();
        var previousEnd = 0d;
        foreach (var segment in segments)
        {
            if (!ids.Add(segment.Id)) throw new InvalidOperationException("字幕片段编号重复，请重新生成字幕。");
            if (!double.IsFinite(segment.StartSeconds) || !double.IsFinite(segment.EndSeconds)
                || segment.StartSeconds < 0 || segment.EndSeconds <= segment.StartSeconds
                || Math.Round(segment.EndSeconds * 1000) <= Math.Round(segment.StartSeconds * 1000)
                || segment.EndSeconds > durationSeconds + 0.05)
                throw new InvalidOperationException($"第 {segment.Id} 段的起止时间无效，必须位于视频时长内。");
            if (segment.StartSeconds < previousEnd - 0.001)
                throw new InvalidOperationException($"第 {segment.Id} 段与上一段重叠，请调整时间轴。");
            previousEnd = segment.EndSeconds;
        }
    }

    public static string BuildSrt(ConversionProject project)
    {
        Validate(project.Segments, project.Video.DurationSeconds);
        var builder = new StringBuilder();
        var index = 1;
        foreach (var segment in project.Segments)
        {
            var text = segment.SubtitleText;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException($"第 {segment.Id} 段字幕为空。");
            builder.Append(index++.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append(FormatTime(segment.StartSeconds)).Append(" --> ").Append(FormatTime(segment.EndSeconds)).Append('\n');
            if (project.Options.BilingualSubtitles && !string.Equals(text.Trim(), segment.SourceText.Trim(), StringComparison.Ordinal))
                builder.Append(CleanText(segment.SourceText)).Append('\n');
            builder.Append(CleanText(text)).Append("\n\n");
        }
        return builder.ToString();
    }

    private static string CleanText(string text) => string.Join('\n', text.Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    private static string FormatTime(double seconds)
    {
        var milliseconds = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00},{milliseconds % 1000:000}");
    }
}
