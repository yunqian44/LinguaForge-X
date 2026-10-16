using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Application.UseCases;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Persistence;
using Xunit;

namespace LinguaForge_X.Tests;

public sealed class ConversionReviewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "linguaforge-review-" + Guid.NewGuid().ToString("N"));
    private readonly ReviewCloud _cloud = new();
    private readonly ConversionService _service;
    private readonly LocalProjectStore _projects;
    private readonly ConversionOptions _options;
    private readonly AppSettings _settings = new();

    public ConversionReviewTests()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "source.mp4");
        File.WriteAllText(source, "video fixture");
        _options = new ConversionOptions { InputPath = source, OutputPath = Path.Combine(_directory, "result.mp4"), AudioStreamIndex = 1 };
        _projects = new LocalProjectStore(_directory);
        _service = new ConversionService(new ReviewMedia(), _cloud, _cloud, _cloud, _projects);
    }

    [Fact]
    public async Task RestoredFailedTranslationCannotSilentlyExportSourceLanguage()
    {
        _cloud.FailTranslationCall = 1;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.PrepareAsync(_options, _settings, null, default));
        var interrupted = Assert.Single(await _projects.ListAsync());
        Assert.NotEmpty(interrupted.Segments);
        Assert.All(interrupted.Segments, s => Assert.True(string.IsNullOrEmpty(s.SubtitleText)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ExportAsync(interrupted, _settings, null, default));
        Assert.False(File.Exists(_options.OutputPath));
    }

    [Fact]
    public async Task ChangingOnlyOutputPresentationDoesNotRepeatCloudPreparation()
    {
        await _service.PrepareAsync(_options, _settings, null, default);
        _options.OutputPath = Path.Combine(_directory, "different-name.mp4");
        _options.SubtitleFontSize = 36;
        _options.BurnSubtitles = false;
        _options.BilingualSubtitles = true;
        await _service.PrepareAsync(_options, _settings, null, default);
        Assert.Equal(1, _cloud.TranscriptionCalls);
        Assert.Equal(1, _cloud.TranslationCalls);
    }

    [Fact]
    public async Task CancellationAfterFirstTranslationBatchReusesCompletedRequests()
    {
        _cloud.SegmentCount = 35;
        _cloud.FailTranslationCall = 2;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.PrepareAsync(_options, _settings, null, default));
        var resumed = await _service.PrepareAsync(_options, _settings, null, default);
        Assert.Equal(35, resumed.Segments.Count);
        Assert.Equal(1, _cloud.TranscriptionCalls);
        Assert.Equal(3, _cloud.TranslationCalls);
        Assert.All(resumed.Segments, s => Assert.StartsWith("译文", s.SubtitleText));
    }

    [Theory]
    [InlineData("A\n\n\nB")]
    [InlineData("A\n \t\nB")]
    [InlineData("A\rB")]
    public void SrtContentCannotContainInternalCueSeparators(string text)
    {
        var project = new ConversionProject
        {
            Video = new("input.mp4", 5, 640, 360, []),
            Segments = [new() { Id = 1, StartSeconds = 0, EndSeconds = 1, SourceText = "source", SubtitleText = text }]
        };
        Assert.Equal("1\n00:00:00,000 --> 00:00:01,000\nA\nB\n\n", SubtitleWriter.BuildSrt(project));
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class ReviewCloud : ITranscriptionService, ITranslationService, ISynthesisService
    {
        public int TranscriptionCalls, TranslationCalls, FailTranslationCall, SegmentCount = 1;
        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string audioPath, string sourceLanguage, ServiceSettings settings, CancellationToken cancellationToken)
        {
            TranscriptionCalls++;
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>(Enumerable.Range(1, SegmentCount).Select(id => new TranscriptSegment
            { Id = id, StartSeconds = (id - 1) * .1, EndSeconds = (id - 1) * .1 + .05, SourceText = "source " + id }).ToArray());
        }
        public Task<IReadOnlyList<TranslationItem>> TranslateAsync(IReadOnlyList<TranslationItem> items, string sourceLanguage, string targetLanguage, ServiceSettings settings, CancellationToken cancellationToken)
        {
            TranslationCalls++;
            if (TranslationCalls == FailTranslationCall) throw new OperationCanceledException();
            return Task.FromResult<IReadOnlyList<TranslationItem>>(items.Select(i => new TranslationItem(i.Id, "译文 " + i.Id)).ToArray());
        }
        public Task SynthesizeAsync(string text, string language, string voiceId, string outputPath, ServiceSettings settings, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
    private sealed class ReviewMedia : IMediaService
    {
        public Task<double> GetAudioDurationAsync(string path, MediaSettings settings, CancellationToken token) => Task.FromResult(1d);
        public Task CheckAvailableAsync(MediaSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<VideoInfo> ProbeAsync(string path, MediaSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(new VideoInfo(path, 10, 640, 360, [new(1, "English")]));
        public Task<IReadOnlyList<AudioChunk>> ExtractAudioChunksAsync(VideoInfo video, int audioStreamIndex, string workDirectory, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AudioChunk>>([new("part.wav", 0, 10)]);
        public Task BuildDubbedAudioAsync(IReadOnlyList<SpeechClip> clips, double videoDurationSeconds, double maxSpeechSpeed, string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task RenderAsync(VideoInfo video, ConversionOptions options, string? subtitlePath, string? dubbedAudioPath, string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
            => File.WriteAllTextAsync(outputPath, "rendered", cancellationToken);
    }
}
