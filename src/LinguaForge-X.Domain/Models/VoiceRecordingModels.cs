namespace LinguaForge_X.Domain.Models;

public sealed record VoiceRecordingDevice(int Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class VoiceRecordingQuality
{
    public double DurationSeconds { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public double PeakDbfs { get; init; }
    public double RmsDbfs { get; init; }
    public double ClippedFraction { get; init; }
    public double SilentFraction { get; init; }
    public bool CanUseForTraining { get; init; }
    public bool CanUseAsReference { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = [];
    public string Summary => Issues.Count == 0 ? "音量与时长检查通过；请试听并校对逐字文稿。" : string.Join("；", Issues);
}

public sealed class VoiceRecordingTake
{
    public string Id { get; init; } = "";
    public string AudioPath { get; init; } = "";
    public string OriginalAudioPath { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public VoiceRecordingQuality Quality { get; init; } = new();
}

public sealed record VoiceRecordingProgress(double DurationSeconds, double PeakDbfs, double RmsDbfs, double Level, int MaximumSeconds = 45);

public sealed class VoiceRecordingCalibration
{
    public double StartSeconds { get; set; }
    public double? EndSeconds { get; set; }
    public double GainDb { get; set; }
    public bool Normalize { get; set; }
}

public sealed class VoiceRecordingSampleDraft
{
    public VoiceRecordingTake Take { get; set; } = new();
    public string Name { get; set; } = "";
    public string Speaker { get; set; } = "本人";
    public string Text { get; set; } = "";
    public string Language { get; set; } = "zh-CN";
}

public sealed class VoiceRecordingSample
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Speaker { get; init; } = "";
    public string Text { get; init; } = "";
    public string Language { get; init; } = "zh-CN";
    public string AudioPath { get; init; } = "";
    public string OriginalAudioPath { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public VoiceRecordingQuality Quality { get; init; } = new();
    public override string ToString() => $"{Name} · {Quality.DurationSeconds:F1} 秒 · {Language}";
}
