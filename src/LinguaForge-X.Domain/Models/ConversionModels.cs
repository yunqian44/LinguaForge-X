namespace LinguaForge_X.Domain.Models;

public enum ServiceProviderKind { AzureOpenAI, AzureSpeech, LocalGptSoVits }
public enum ServiceKind { Transcription, Translation, Synthesis }
public enum OriginalAudioMode { Replace, QuietMix }
public enum VideoEncodingMode { Auto, CpuFast, CpuQuality }
public enum OutputVideoQuality { Original, UltraHd2160, FullHd1080, Hd720, Compact480 }

public sealed class ServiceSettings
{
    public ServiceProviderKind Provider { get; set; } = ServiceProviderKind.AzureSpeech;
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Region { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string ApiVersion { get; set; } = "2024-10-21";
    public string Model { get; set; } = "whisper";
    public LocalVoiceSettings LocalVoice { get; set; } = new();
}

public sealed class MediaSettings
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string OutputDirectory { get; set; } = "";
    public VideoEncodingMode VideoEncoding { get; set; } = VideoEncodingMode.Auto;
    public OutputVideoQuality OutputQuality { get; set; } = OutputVideoQuality.Original;
}

public sealed class AppSettings
{
    public ServiceSettings Transcription { get; set; } = new();
    public ServiceSettings Translation { get; set; } = new() { Provider = ServiceProviderKind.AzureOpenAI, Model = "text" };
    public ServiceSettings Synthesis { get; set; } = new() { Model = "tts-1" };
    public MediaSettings Media { get; set; } = new();
    public OnlineVideoSettings OnlineVideo { get; set; } = new();
}

public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

public sealed record VoiceOption(string Id, string Name, string Locale, IReadOnlyList<string> SupportedLocales)
{
    public override string ToString() => Name;
}

public sealed record ServiceCapabilities(IReadOnlyList<LanguageOption> SourceLanguages,
    IReadOnlyList<LanguageOption> TranslationLanguages, IReadOnlyList<VoiceOption> Voices, string Description);
public sealed record ConnectionTestResult(bool Success, string Message);
public sealed record AudioTrack(int StreamIndex, string Name)
{
    public override string ToString() => Name;
}
public sealed record VideoInfo(string FilePath, double DurationSeconds, int Width, int Height,
    IReadOnlyList<AudioTrack> AudioTracks);
public sealed record AudioChunk(string FilePath, double OffsetSeconds, double DurationSeconds);
public sealed record TranslationItem(int Id, string Text);
public sealed record SpeechClip(string FilePath, double StartSeconds, double EndSeconds);
public sealed record ProcessingProgress(string Stage, double Percent, string Message);

public sealed class TranscriptSegment
{
    public int Id { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string SourceText { get; set; } = "";
    public string SubtitleText { get; set; } = "";
    public string SpeechText { get; set; } = "";
}

public sealed class ConversionOptions
{
    public string InputPath { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string SourceTitle { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public int AudioStreamIndex { get; set; }
    public string SourceLanguage { get; set; } = "en-US";
    public string SubtitleLanguage { get; set; } = "zh-CN";
    public string SpeechLanguage { get; set; } = "zh-CN";
    public string VoiceId { get; set; } = "";
    public bool GenerateSubtitles { get; set; } = true;
    public bool GenerateSpeech { get; set; }
    public bool BurnSubtitles { get; set; } = true;
    public bool ExportSrt { get; set; } = true;
    public bool BilingualSubtitles { get; set; }
    public int SubtitleFontSize { get; set; } = 24;
    public OriginalAudioMode OriginalAudio { get; set; } = OriginalAudioMode.Replace;
    public double MaxSpeechSpeed { get; set; } = 1.35;
    public bool AutoShortenSpeech { get; set; } = true;
}

public sealed class ConversionProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string WorkDirectory { get; set; } = "";
    public ConversionOptions Options { get; set; } = new();
    public VideoInfo Video { get; set; } = new("", 0, 0, 0, []);
    public List<TranscriptSegment> Segments { get; set; } = [];
    public string Status { get; set; } = "准备中";
    public bool PreparationComplete { get; set; }
    public string? ResultPath { get; set; }
    public long SourceFileLength { get; set; }
    public DateTime SourceLastWriteTimeUtc { get; set; }
}

public sealed record ConversionResult(string VideoPath, string? SubtitlePath);
