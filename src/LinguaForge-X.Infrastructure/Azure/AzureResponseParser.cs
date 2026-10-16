using System.Text.Json;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.Azure;

/// <summary>Strict parsing deliberately rejects text-only transcripts instead of inventing a timeline.</summary>
public static class AzureResponseParser
{
    public static IReadOnlyList<TranscriptSegment> ParseTranscript(JsonElement root, bool speech)
    {
        var result = new List<TranscriptSegment>();
        var arrayName = speech ? "phrases" : "segments";
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("转写结果格式不正确。");
        if (!root.TryGetProperty(arrayName, out var segments) || segments.ValueKind != JsonValueKind.Array)
        {
            if (!speech && root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                && string.IsNullOrWhiteSpace(text.GetString())) return result;
            throw new InvalidOperationException("转写接口没有返回分段时间戳。Azure OpenAI 请使用支持 verbose_json 的 Whisper 部署。");
        }
        foreach (var segment in segments.EnumerateArray())
        {
            var text = String(segment, "text")?.Trim() ?? "";
            if (text.Length == 0) continue;
            var start = Number(segment, speech ? "offsetMilliseconds" : "start");
            var end = speech ? start + Number(segment, "durationMilliseconds") : Number(segment, "end");
            if (speech) { start /= 1000; end /= 1000; }
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
                throw new InvalidOperationException("转写服务返回了无效时间戳，请检查模型或重试此音频片段。");
            result.Add(new TranscriptSegment { Id = result.Count + 1, StartSeconds = start, EndSeconds = end,
                SourceText = text, SubtitleText = text, SpeechText = text });
        }
        if (result.Count == 0)
        {
            var hasText = !string.IsNullOrWhiteSpace(String(root, "text"));
            if (speech && root.TryGetProperty("combinedPhrases", out var combined) && combined.ValueKind == JsonValueKind.Array)
                hasText |= combined.EnumerateArray().Any(p => !string.IsNullOrWhiteSpace(String(p, "text")));
            if (hasText) throw new InvalidOperationException("服务返回了文字但没有有效的字幕时间戳，不能自动生成字幕。");
        }
        var ordered = result.OrderBy(s => s.StartSeconds).ToArray();
        for (var i = 0; i < ordered.Length; i++) ordered[i].Id = i + 1;
        return ordered;
    }

    public static IReadOnlyList<TranslationItem> ParseTranslation(JsonElement root, IReadOnlyList<TranslationItem> expected)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("翻译结果格式不正确，预期为包含 items 的 JSON 对象。");
        var sourceIds = expected.Select(i => i.Id).ToHashSet();
        var translated = new Dictionary<int, string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idValue)
                || idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt32(out var id) || !sourceIds.Contains(id)
                || String(item, "text") is not { } text || string.IsNullOrWhiteSpace(text)
                || !translated.TryAdd(id, text.Trim()))
                throw new InvalidOperationException("翻译结果含有缺失、重复或无效片段。已停止处理以防止字幕错位，请重试。");
        }
        if (translated.Count != expected.Count)
            throw new InvalidOperationException("翻译结果遗漏了字幕片段。已停止处理，请重试。");
        return expected.Select(i => new TranslationItem(i.Id, translated[i.Id])).ToArray();
    }

    public static IReadOnlyList<VoiceOption> ParseVoices(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Speech 音色目录响应格式不正确。");
        var result = new List<VoiceOption>();
        foreach (var item in root.EnumerateArray())
        {
            var id = String(item, "ShortName");
            var locale = String(item, "Locale");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(locale)) continue;
            var locales = new List<string> { locale };
            if (item.TryGetProperty("SecondaryLocaleList", out var secondary) && secondary.ValueKind == JsonValueKind.Array)
                locales.AddRange(secondary.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)));
            var name = String(item, "LocalName") ?? String(item, "DisplayName") ?? id;
            result.Add(new VoiceOption(id, $"{name} · {locale}", locale, locales.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        if (result.Count == 0) throw new InvalidOperationException("当前 Speech 资源没有返回可用音色。");
        return result.DistinctBy(v => v.Id).OrderBy(v => v.Locale).ThenBy(v => v.Name).ToArray();
    }

    internal static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static double Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result))
            throw new InvalidOperationException("转写结果缺少有效的分段时间戳。");
        return result;
    }
}
