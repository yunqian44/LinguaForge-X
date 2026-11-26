using System.Buffers.Binary;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.Recording;

/// <summary>Bounded, deterministic PCM processing. Quality metrics are signal checks, never speaker or transcript verification.</summary>
internal sealed record RecordingAudio(int SampleRate, float[] Samples, double SourceClippedFraction = 0)
{
    internal const int MaximumSeconds = 45;
    internal const int MaximumBytes = 64 * 1024 * 1024;
    internal double Duration => Samples.Length / (double)SampleRate;

    internal static RecordingAudio Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is < 44 or > MaximumBytes)
            throw new InvalidOperationException("录音文件为空、过大或格式无效；请导入不超过 45 秒的 WAV 片段。");
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x46464952 || reader.ReadUInt32() + 8L != stream.Length || reader.ReadUInt32() != 0x45564157)
            throw new InvalidOperationException("录音必须是完整的 PCM 或浮点 WAV 文件。");
        ushort encoding = 0, channels = 0, bits = 0, align = 0;
        int rate = 0;
        byte[]? data = null;
        var formatFound = false;
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 8) throw InvalidWave();
            var id = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            var next = stream.Position + length + length % 2;
            if (next > stream.Length) throw InvalidWave();
            if (id == 0x20746d66)
            {
                if (formatFound || length < 16) throw InvalidWave();
                encoding = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                var bytesPerSecond = reader.ReadUInt32(); align = reader.ReadUInt16(); bits = reader.ReadUInt16();
                if (channels is < 1 or > 2 || rate is < 16000 or > 96000 || encoding is not (1 or 3)
                    || bits is not (8 or 16 or 24 or 32 or 64) || encoding == 3 && bits is not (32 or 64)
                    || encoding == 1 && bits == 64 || align != channels * (bits / 8) || bytesPerSecond != rate * align)
                    throw new InvalidOperationException("录音需为 16～96 kHz 单声道/双声道 PCM 或浮点 WAV；请转换后导入。");
                formatFound = true;
            }
            else if (id == 0x61746164)
            {
                if (data is not null || length == 0 || length > MaximumBytes) throw InvalidWave();
                data = reader.ReadBytes(checked((int)length));
            }
            stream.Position = next;
        }
        if (!formatFound || data is null || data.Length % align != 0) throw InvalidWave();
        var count = data.Length / align;
        if (count > (long)rate * MaximumSeconds) throw new InvalidOperationException("单段录音不能超过 45 秒，请先按句切片。");
        var samples = new float[count];
        var step = bits / 8;
        var sourceClipped = 0;
        for (var i = 0; i < count; i++)
        {
            double sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var span = data.AsSpan(i * align + channel * step, step);
                var sample = (encoding, bits) switch
                {
                    (3, 32) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span)),
                    (3, 64) => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(span)),
                    (1, 8) => (span[0] - 128) / 128d,
                    (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(span) / 32768d,
                    (1, 24) => ((span[0] | span[1] << 8 | span[2] << 16) << 8 >> 8) / 8388608d,
                    (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(span) / 2147483648d,
                    _ => throw InvalidWave()
                };
                if (!double.IsFinite(sample) || Math.Abs(sample) > 1.01) throw new InvalidOperationException("浮点录音含无效或越界采样，请重新导出 WAV。");
                if (Math.Abs(sample) >= .999) sourceClipped++;
                sum += sample;
            }
            samples[i] = (float)Math.Clamp(sum / channels, -1, 1);
        }
        return new(rate, samples, sourceClipped / (double)Math.Max(1, count * channels));
    }

    internal VoiceRecordingQuality Analyze(bool sourceWasClipped = false)
    {
        double square = 0, peak = 0, sum = 0;
        var clipped = 0;
        foreach (var value in Samples)
        {
            square += value * value;
            sum += value;
            peak = Math.Max(peak, Math.Abs(value));
            if (Math.Abs(value) >= .999) clipped++;
        }
        var rms = Math.Sqrt(square / Math.Max(1, Samples.Length));
        // Use 20 ms windows, not zero crossings, to distinguish silence from speech.
        var window = Math.Max(1, SampleRate / 50);
        var silent = 0;
        var windows = 0;
        for (var i = 0; i < Samples.Length; i += window)
        {
            double energy = 0;
            var end = Math.Min(i + window, Samples.Length);
            for (var j = i; j < end; j++) energy += Samples[j] * Samples[j];
            if (Math.Sqrt(energy / (end - i)) < .00316227766) silent++;
            windows++;
        }
        var clippedFraction = Math.Max(SourceClippedFraction, clipped / (double)Math.Max(1, Samples.Length));
        var dcOffset = Math.Abs(sum / Math.Max(1, Samples.Length));
        var silentFraction = silent / (double)Math.Max(1, windows);
        var issues = new List<string>();
        if (Duration < 1) issues.Add("片段不足 1 秒，请录制完整短句");
        if (Duration > MaximumSeconds) issues.Add("片段超过 45 秒，请按句切片");
        if (Db(rms) < -40 || silentFraction >= .9) issues.Add("录音接近静音或有效声音太少，请检查麦克风并重新录制");
        else if (Db(rms) < -30) issues.Add("音量偏低，可先校准增益后试听");
        if (clippedFraction >= .001 || sourceWasClipped) issues.Add("检测到削波失真，请降低麦克风音量重新录制；降低文件增益不能修复失真");
        if (silentFraction is > .5 and < .9) issues.Add("静音占比较高，可裁剪首尾空白并校对文稿");
        if (dcOffset > .15) issues.Add("录音存在异常直流偏移，请检查麦克风或重新导出音频");
        var eligible = Duration >= 1 && Duration <= MaximumSeconds && Db(rms) >= -40 && silentFraction < .9 && clippedFraction < .001 && !sourceWasClipped && dcOffset <= .15;
        return new()
        {
            DurationSeconds = Duration, SampleRate = SampleRate, Channels = 1, PeakDbfs = Db(peak), RmsDbfs = Db(rms),
            ClippedFraction = sourceWasClipped ? Math.Max(.001, clippedFraction) : clippedFraction, SilentFraction = silentFraction, CanUseForTraining = eligible,
            CanUseAsReference = eligible && Duration is >= 3 and <= 10, Issues = issues
        };
    }

    internal (RecordingAudio Audio, bool WasClipped) Calibrate(VoiceRecordingCalibration options)
    {
        var end = options.EndSeconds ?? Duration;
        if (!double.IsFinite(options.StartSeconds) || !double.IsFinite(end) || !double.IsFinite(options.GainDb)
            || options.StartSeconds < 0 || end > Duration + .001 || end <= options.StartSeconds || options.GainDb is < -24 or > 18)
            throw new InvalidOperationException("裁剪区间必须位于录音内且结束晚于开始；手动增益范围为 -24～18 dB。");
        var startSample = (int)Math.Round(options.StartSeconds * SampleRate);
        var endSample = Math.Min(Samples.Length, (int)Math.Round(end * SampleRate));
        if (endSample - startSample < SampleRate)
            throw new InvalidOperationException("裁剪后需至少保留 1 秒完整语音，请同步校对文稿。");
        var output = Samples[startSample..endSample];
        var cropped = new RecordingAudio(SampleRate, output);
        var quality = cropped.Analyze();
        if (Math.Abs(output.Average(value => (double)value)) > .15)
            throw new InvalidOperationException("录音存在异常直流偏移，音量校准无法修复，请检查设备后重新录制。");
        var gainDb = options.GainDb;
        if (options.Normalize)
        {
            if (quality.RmsDbfs < -40 || quality.SilentFraction >= .9)
                throw new InvalidOperationException("静音或极低音量不能通过自动增益恢复，请调整麦克风重新录制。");
            gainDb = Math.Clamp(Math.Min(-3 - quality.PeakDbfs, -20 - quality.RmsDbfs), -24, 18);
        }
        var gain = Math.Pow(10, gainDb / 20);
        if (output.Any(sample => Math.Abs(sample * gain) > .98))
            throw new InvalidOperationException("该增益会造成削波，请降低增益或选择自动校准。");
        for (var i = 0; i < output.Length; i++) output[i] = (float)(output[i] * gain);
        return (new(SampleRate, output), quality.ClippedFraction >= .001);
    }

    internal void Write(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46464952); writer.Write(36 + Samples.Length * 2); writer.Write(0x45564157);
        writer.Write(0x20746d66); writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(SampleRate); writer.Write(SampleRate * 2); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(0x61746164); writer.Write(Samples.Length * 2);
        foreach (var sample in Samples) writer.Write((short)Math.Clamp(Math.Round(sample * 32768d), short.MinValue, short.MaxValue));
        stream.Flush(true);
    }

    internal static double Db(double amplitude) => amplitude <= .000001 ? -120 : Math.Max(-120, 20 * Math.Log10(amplitude));
    private static InvalidOperationException InvalidWave() => new("录音 WAV 结构无效、重复或截断，请重新录制或转换后导入。");
}
