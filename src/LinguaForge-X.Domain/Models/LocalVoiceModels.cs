namespace LinguaForge_X.Domain.Models;

public static class LocalVoiceLanguages
{
    public static IReadOnlyList<string> Codes { get; } = Array.AsReadOnly(new[]
        { "zh-CN", "zh-TW", "en-US", "en-GB", "ja-JP", "ko-KR", "yue-CN" });
}

public sealed class LocalVoiceSettings
{
    public string RootDirectory { get; set; } = "";
    public string PythonPath { get; set; } = "python";
    public string ApiEndpoint { get; set; } = "http://127.0.0.1:9880";
    public string Device { get; set; } = "auto";
}

public sealed class LocalVoiceDraft
{
    public string Name { get; set; } = "";
    public string ReferenceAudioPath { get; set; } = "";
    public string ReferenceText { get; set; } = "";
    public string Language { get; set; } = "zh-CN";
    public string GptWeightsPath { get; set; } = "";
    public string SoVitsWeightsPath { get; set; } = "";
}

public sealed class LocalVoiceProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ReferenceAudioPath { get; set; } = "";
    public string ReferenceText { get; set; } = "";
    public string Language { get; set; } = "zh-CN";
    public string GptWeightsPath { get; set; } = "";
    public string SoVitsWeightsPath { get; set; } = "";
    public string Revision { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsTrained => !string.IsNullOrWhiteSpace(GptWeightsPath) && !string.IsNullOrWhiteSpace(SoVitsWeightsPath);
    public string Description => IsTrained ? "本地模型音色" : "参考录音音色（未微调）";
    public override string ToString() => Name;
}

public sealed class LocalVoiceTrainingRequest
{
    public LocalVoiceDraft Voice { get; set; } = new();
    public string DatasetListPath { get; set; } = "";
    public int GptEpochs { get; set; } = 5;
    public int SoVitsEpochs { get; set; } = 8;
}
