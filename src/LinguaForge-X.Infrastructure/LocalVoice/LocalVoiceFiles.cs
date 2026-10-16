using System.Buffers.Binary;
using System.Net;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.LocalVoice;

internal static class LocalVoiceFiles
{
    internal const string DefaultGpt = "GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt";
    internal const string DefaultSoVits = "GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s2G2333k.pth";

    internal static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                 || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)))
            throw new InvalidOperationException("本地配音地址必须是本机 HTTP 地址，例如 http://127.0.0.1:9880；不能填写远程地址、路径或密钥。");
        // Do not resolve localhost through a proxy or external DNS configuration.
        return new UriBuilder(uri) { Host = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : uri.Host }.Uri;
    }

    internal static string Language(string value) => value?.Trim().ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "zh-tw" or "zh-hk" => "zh",
        "en" or "en-us" or "en-gb" => "en",
        "ja" or "ja-jp" => "ja",
        "ko" or "ko-kr" => "ko",
        "yue" or "yue-cn" or "yue-hk" => "yue",
        _ => throw new InvalidOperationException("本地 GPT-SoVITS 配音支持中文、英语、日语、韩语和粤语，请选择支持的语言。")
    };

    internal static string ExistingFile(string value, string description, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"请选择{description}。");
        var path = Path.GetFullPath(value.Trim());
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"{description}不存在或为空：{path}");
        if (extension is not null && !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{description}需要 {extension} 文件。");
        return path;
    }

    internal static string RuntimeRoot(LocalVoiceSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.RootDirectory))
            throw new InvalidOperationException("请先安装 GPT-SoVITS，并在本地音色设置中选择包含 api_v2.py 的运行目录。");
        var root = Path.GetFullPath(settings.RootDirectory.Trim());
        ExistingFile(Path.Combine(root, "api_v2.py"), "GPT-SoVITS api_v2.py");
        if (string.IsNullOrWhiteSpace(settings.PythonPath)) throw new InvalidOperationException("请选择 GPT-SoVITS 环境的 Python 解释器。");
        if (settings.Device is not ("auto" or "cpu" or "cuda")) throw new InvalidOperationException("运行设备只能选择 auto、cpu 或 cuda。");
        return root;
    }

    internal static (string Gpt, string SoVits) BaseWeights(string root) =>
        (ExistingFile(Path.Combine(root, DefaultGpt), "v2 预训练 GPT 模型", ".ckpt"),
         ExistingFile(Path.Combine(root, DefaultSoVits), "v2 预训练 SoVITS 模型", ".pth"));

    internal static void ValidateDraft(LocalVoiceDraft voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        if (string.IsNullOrWhiteSpace(voice.Name) || voice.Name.Trim().Length > 80)
            throw new InvalidOperationException("音色名称不能为空，且不能超过 80 个字符。");
        if (string.IsNullOrWhiteSpace(voice.ReferenceText) || voice.ReferenceText.Trim().Length > 1000)
            throw new InvalidOperationException("请填写参考录音对应的准确文字（最多 1000 个字符）。");
        Language(voice.Language);
        var reference = ExistingFile(voice.ReferenceAudioPath, "参考录音", ".wav");
        using (var file = File.OpenRead(reference))
        {
            var duration = ValidateWave(file);
            if (duration is < 3 or > 10)
                throw new InvalidOperationException("参考录音需要为 3～10 秒的 WAV 文件，请选取一段清晰、单人且无伴奏的录音。");
        }
        if (string.IsNullOrWhiteSpace(voice.GptWeightsPath) != string.IsNullOrWhiteSpace(voice.SoVitsWeightsPath))
            throw new InvalidOperationException("导入训练结果时，需要同时选择 GPT .ckpt 和 SoVITS .pth 两个模型文件；参考录音模式则都留空。");
        if (!string.IsNullOrWhiteSpace(voice.GptWeightsPath))
        {
            ExistingFile(voice.GptWeightsPath, "GPT 模型", ".ckpt");
            ExistingFile(voice.SoVitsWeightsPath, "SoVITS 模型", ".pth");
        }
    }

    /// <summary>Validate the actual PCM/float RIFF structure, including data bounds, instead of trusting a MIME type.</summary>
    internal static double ValidateWave(Stream stream)
    {
        const string error = "音频不是完整、有效的 PCM/浮点 WAV 文件。";
        if (!stream.CanSeek || stream.Length < 44) throw new InvalidOperationException(error);
        stream.Position = 0;
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8)) throw new InvalidOperationException(error);
        var declaredEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        if (declaredEnd != stream.Length) throw new InvalidOperationException(error);
        uint byteRate = 0;
        ushort blockAlign = 0;
        long dataLength = 0;
        var foundFormat = false;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> format = stackalloc byte[16];
        while (stream.Position < declaredEnd)
        {
            if (declaredEnd - stream.Position < 8) throw new InvalidOperationException(error);
            stream.ReadExactly(chunk);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var next = stream.Position + length + (length % 2);
            if (next > declaredEnd) throw new InvalidOperationException(error);
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (foundFormat || length < 16) throw new InvalidOperationException(error);
                stream.ReadExactly(format);
                var encoding = BinaryPrimitives.ReadUInt16LittleEndian(format);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
                if (encoding is not (1 or 3) || channels is < 1 or > 8 || sampleRate is < 8000 or > 192000
                    || bits is not (8 or 16 or 24 or 32 or 64) || encoding == 3 && bits is not (32 or 64)
                    || blockAlign != channels * (bits / 8) || byteRate != sampleRate * blockAlign)
                    throw new InvalidOperationException(error);
                foundFormat = true;
            }
            else if (chunk[..4].SequenceEqual("data"u8)) dataLength += length;
            stream.Position = next;
        }
        if (!foundFormat || dataLength <= 0 || dataLength % blockAlign != 0) throw new InvalidOperationException(error);
        return dataLength / (double)byteRate;
    }
}
