using System.Globalization;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.Media;

internal sealed record VideoQualityProfile(string Name, int? MaximumWidth, int? MaximumHeight, int ConstantQuality)
{
    internal static VideoQualityProfile For(OutputVideoQuality quality) => quality switch
    {
        OutputVideoQuality.Original => new("保持源分辨率", null, null, 20),
        OutputVideoQuality.UltraHd2160 => new("4K", 3840, 2160, 18),
        OutputVideoQuality.FullHd1080 => new("1080p", 1920, 1080, 20),
        OutputVideoQuality.Hd720 => new("720p", 1280, 720, 23),
        OutputVideoQuality.Compact480 => new("480p", 854, 480, 26),
        _ => throw new InvalidOperationException("请选择有效的输出画质。")
    };

    internal string BuildFilter(string? subtitleFilter)
    {
        var filters = new List<string>();
        if (MaximumWidth is { } width && MaximumHeight is { } height)
        {
            // FFmpeg rotates camera footage before these filters, so inspect the actual frame rather
            // than ffprobe's stored dimensions. min keeps low-resolution inputs from being enlarged.
            var landscapeWidth = width.ToString(CultureInfo.InvariantCulture);
            var landscapeHeight = height.ToString(CultureInfo.InvariantCulture);
            filters.Add($"scale=w='max(2,min(iw,if(gte(iw,ih),{landscapeWidth},{landscapeHeight})))':" +
                $"h='max(2,min(ih,if(gte(iw,ih),{landscapeHeight},{landscapeWidth})))':" +
                "force_original_aspect_ratio=decrease:force_divisible_by=2");
        }
        filters.Add("pad=ceil(iw/2)*2:ceil(ih/2)*2");
        // Render text at the final output size, rather than drawing and then shrinking it.
        if (!string.IsNullOrWhiteSpace(subtitleFilter)) filters.Add(subtitleFilter);
        return string.Join(',', filters);
    }
}
