using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Application.Abstractions;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IProjectStore
{
    string CreateWorkDirectory(string id);
    Task SaveAsync(ConversionProject project, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversionProject>> ListAsync(CancellationToken cancellationToken = default);
}

public interface ITranscriptionService
{
    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string audioPath, string sourceLanguage,
        ServiceSettings settings, CancellationToken cancellationToken);
}

public interface ITranslationService
{
    Task<IReadOnlyList<TranslationItem>> TranslateAsync(IReadOnlyList<TranslationItem> items,
        string sourceLanguage, string targetLanguage, ServiceSettings settings, CancellationToken cancellationToken);

    Task<string> ShortenForSpeechAsync(SpeechShorteningRequest request, ServiceSettings settings,
        CancellationToken cancellationToken) => throw new NotSupportedException("当前翻译服务不支持自动精简配音稿，请配置 Azure OpenAI 文本服务。");
}

public interface ISynthesisService
{
    Task SynthesizeAsync(string text, string language, string voiceId, string outputPath,
        ServiceSettings settings, CancellationToken cancellationToken);

    Task<string?> GetCacheIdentityAsync(string voiceId, ServiceSettings settings, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}

public interface IServiceCatalog
{
    Task<ServiceCapabilities> GetCapabilitiesAsync(AppSettings settings, CancellationToken cancellationToken);
    Task<ConnectionTestResult> TestConnectionAsync(ServiceSettings settings, ServiceKind kind, CancellationToken cancellationToken);
}

public interface IMediaService
{
    Task CheckAvailableAsync(MediaSettings settings, CancellationToken cancellationToken);
    Task<VideoInfo> ProbeAsync(string path, MediaSettings settings, CancellationToken cancellationToken);
    Task<double> GetAudioDurationAsync(string path, MediaSettings settings, CancellationToken cancellationToken);
    Task<IReadOnlyList<AudioChunk>> ExtractAudioChunksAsync(VideoInfo video, int audioStreamIndex,
        string workDirectory, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken);
    Task BuildDubbedAudioAsync(IReadOnlyList<SpeechClip> clips, double videoDurationSeconds, double maxSpeechSpeed,
        string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken);
    Task RenderAsync(VideoInfo video, ConversionOptions options, string? subtitlePath, string? dubbedAudioPath,
        string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken);
}

public interface IConversionService
{
    Task<ConversionProject> PrepareAsync(ConversionOptions options, AppSettings settings,
        IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken);
    Task<ConversionResult> ExportAsync(ConversionProject project, AppSettings settings,
        IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken);
}
