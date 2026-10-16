using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Application.UseCases;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Persistence;
using Xunit;

namespace LinguaForge_X.Tests;

public sealed class ConversionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "linguaforge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMedia _media = new();
    private readonly FakeCloud _cloud = new();
    private readonly ConversionService _service;
    private readonly LocalProjectStore _store;
    private readonly AppSettings _settings = new();
    private readonly ConversionOptions _options;

    public ConversionTests()
    {
        Directory.CreateDirectory(_directory);
        var input = Path.Combine(_directory, "原视频.mp4");
        File.WriteAllText(input, "original video fixture");
        _options = new ConversionOptions { InputPath = input, OutputPath = Path.Combine(_directory, "成品.mp4"), AudioStreamIndex = 1 };
        _store = new LocalProjectStore(_directory);
        _service = new ConversionService(_media, _cloud, _cloud, _cloud, _store);
    }

    [Fact]
    public async Task PrepareReusesTranscriptionAndTranslationWithoutPersistingKeys()
    {
        _settings.Transcription.ApiKey = "super-secret-transcription";
        _settings.Translation.ApiKey = "super-secret-translation";
        var first = await _service.PrepareAsync(_options, _settings, null, default);
        var second = await _service.PrepareAsync(_options, _settings, null, default);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(1, _cloud.TranscriptionCalls);
        Assert.Equal(1, _cloud.TranslationCalls);
        Assert.Equal("译文 1", second.Segments[0].SubtitleText);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_directory, "jobs"), "*.json", SearchOption.AllDirectories))
            Assert.DoesNotContain("super-secret", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task PreparingAgainPreservesPreviouslyEditedHistory()
    {
        var first = await _service.PrepareAsync(_options, _settings, null, default);
        first.Segments[0].SubtitleText = "手工校对不能丢失";
        await _store.SaveAsync(first);
        await _service.PrepareAsync(_options, _settings, null, default);
        var history = await _store.ListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal("手工校对不能丢失", history.Single(p => p.Id == first.Id).Segments[0].SubtitleText);
        Assert.Equal(1, _cloud.TranscriptionCalls);
    }

    [Fact]
    public async Task OriginalLanguageSubtitlesDoNotRequireTranslation()
    {
        _options.SubtitleLanguage = "source";
        var project = await _service.PrepareAsync(_options, _settings, null, default);
        Assert.Equal(0, _cloud.TranslationCalls);
        Assert.Equal(project.Segments[0].SourceText, project.Segments[0].SubtitleText);
    }

    [Fact]
    public async Task SharedSubtitleAndSpeechLanguageTranslatesOnce()
    {
        _options.GenerateSpeech = true;
        _options.VoiceId = "zh-CN-XiaoxiaoNeural";
        var project = await _service.PrepareAsync(_options, _settings, null, default);
        Assert.Equal(1, _cloud.TranslationCalls);
        Assert.Equal(project.Segments[0].SubtitleText, project.Segments[0].SpeechText);
    }

    [Fact]
    public async Task MissingTranslationSegmentStopsPreparation()
    {
        _cloud.BadTranslation = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PrepareAsync(_options, _settings, null, default));
        Assert.Contains("失败", Assert.Single(await _store.ListAsync()).Status);
        Assert.False(File.Exists(_options.OutputPath));
    }

    [Fact]
    public async Task InvalidTimestampIsNotInventedOrAccepted()
    {
        _cloud.InvalidTimestamp = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PrepareAsync(_options, _settings, null, default));
    }

    [Fact]
    public async Task OverlappingChunkContextIsOwnedByOnlyOneChunk()
    {
        _media.LongVideo = true;
        _cloud.LongVideo = true;
        var project = await _service.PrepareAsync(_options, _settings, null, default);
        Assert.Equal(2, project.Segments.Count);
        Assert.Equal("boundary", project.Segments[0].SourceText);
        Assert.Equal(298, project.Segments[0].StartSeconds);
        Assert.Equal(300, project.Segments[0].EndSeconds);
        Assert.Equal(301, project.Segments[1].StartSeconds);
        Assert.Equal(303, project.Segments[1].EndSeconds);
    }

    [Fact]
    public async Task ExportUsesEditedTextAndPreservesOriginal()
    {
        var original = await File.ReadAllTextAsync(_options.InputPath);
        var project = await _service.PrepareAsync(_options, _settings, null, default);
        project.Segments[0].SubtitleText = "校对后的中文";
        var result = await _service.ExportAsync(project, _settings, null, default);
        Assert.True(File.Exists(result.VideoPath));
        Assert.Contains("校对后的中文", await File.ReadAllTextAsync(result.SubtitlePath!));
        Assert.Equal(original, await File.ReadAllTextAsync(_options.InputPath));
        Assert.Equal("已完成", Assert.Single(await _store.ListAsync()).Status);
    }

    [Fact]
    public async Task DownloadedVideoRetainsSourceAndSupportsSubtitlesDubbingAndHistory()
    {
        _options.SourceUrl = "https://www.youtube.com/watch?v=source-video";
        _options.SourceTitle = "在线素材：英文课程";
        _options.GenerateSpeech = true;
        _options.VoiceId = "zh-CN-XiaoxiaoNeural";
        _settings.OnlineVideo.Proxy = "http://user:private-password@localhost:7890";
        _settings.OnlineVideo.CookiesFilePath = "private-cookies.txt";
        var project = await _service.PrepareAsync(_options, _settings, null, default);
        var result = await _service.ExportAsync(project, _settings, null, default);
        var restored = Assert.Single(await _store.ListAsync());
        Assert.Equal(_options.SourceUrl, restored.Options.SourceUrl);
        Assert.Equal(_options.SourceTitle, restored.Options.SourceTitle);
        Assert.Equal(_options.InputPath, restored.Options.InputPath);
        Assert.Equal(result.VideoPath, restored.ResultPath);
        Assert.True(File.Exists(result.SubtitlePath));
        Assert.Equal(1, _cloud.SynthesisCalls);
        var persisted = await File.ReadAllTextAsync(Path.Combine(restored.WorkDirectory, "project.json"));
        Assert.DoesNotContain("private-password", persisted);
        Assert.DoesNotContain("private-cookies", persisted);
    }

    [Fact]
    public async Task ExistingOutputIsNeverOverwritten()
    {
        var project = await _service.PrepareAsync(_options, _settings, null, default);
        await File.WriteAllTextAsync(_options.OutputPath, "user content");
        await Assert.ThrowsAsync<IOException>(() => _service.ExportAsync(project, _settings, null, default));
        Assert.Equal("user content", await File.ReadAllTextAsync(_options.OutputPath));
    }

    [Fact]
    public async Task ChangedSourceRequiresNewPreparation()
    {
        var project = await _service.PrepareAsync(_options, _settings, null, default);
        await File.AppendAllTextAsync(_options.InputPath, "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ExportAsync(project, _settings, null, default));
    }

    [Fact]
    public async Task ExportFailureKeepsSpeechCacheForRetry()
    {
        _options.GenerateSpeech = true;
        _options.VoiceId = "test-voice";
        var project = await _service.PrepareAsync(_options, _settings, null, default);
        _media.FailRender = true;
        await Assert.ThrowsAsync<IOException>(() => _service.ExportAsync(project, _settings, null, default));
        Assert.False(File.Exists(_options.OutputPath));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".linguaforge-*"));
        _media.FailRender = false;
        await _service.ExportAsync(project, _settings, null, default);
        Assert.Equal(1, _cloud.SynthesisCalls);
    }

    [Fact]
    public async Task CancellationProducesNoFinalOutput()
    {
        var project = await _service.PrepareAsync(_options, _settings, null, default);
        _media.CancelRender = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => _service.ExportAsync(project, _settings, null, default));
        Assert.False(File.Exists(_options.OutputPath));
        Assert.False(File.Exists(Path.ChangeExtension(_options.OutputPath, ".srt")));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".linguaforge-*"));
    }

    [Fact]
    public async Task ChangedLocalModelInvalidatesSpeechCacheWithoutRepeatingPreparation()
    {
        _options.GenerateSpeech = true;
        _options.VoiceId = "local-voice";
        _settings.Synthesis.Provider = ServiceProviderKind.LocalGptSoVits;
        _cloud.ModelIdentity = "weights-revision-one";
        var project = await _service.PrepareAsync(_options, _settings, null, default);
        _media.FailRender = true;
        await Assert.ThrowsAsync<IOException>(() => _service.ExportAsync(project, _settings, null, default));
        await Assert.ThrowsAsync<IOException>(() => _service.ExportAsync(project, _settings, null, default));
        Assert.Equal(1, _cloud.SynthesisCalls);
        _cloud.ModelIdentity = "weights-revision-two";
        _media.FailRender = false;
        await _service.ExportAsync(project, _settings, null, default);
        Assert.Equal(2, _cloud.SynthesisCalls);
        Assert.Equal(1, _cloud.TranscriptionCalls);
        Assert.Equal(1, _cloud.TranslationCalls);
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans", true)]
    [InlineData("zh-CN", "zh-TW", false)]
    [InlineData("en-US", "en-GB", true)]
    [InlineData("auto", "en-US", false)]
    public void LanguageMatchingPreservesChineseScript(string source, string target, bool expected)
        => Assert.Equal(expected, ConversionService.SameLanguage(source, target));

    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class FakeCloud : ITranscriptionService, ITranslationService, ISynthesisService
    {
        public int TranscriptionCalls, TranslationCalls, SynthesisCalls;
        public bool BadTranslation, InvalidTimestamp, LongVideo;
        public string? ModelIdentity;
        public Task<string?> GetCacheIdentityAsync(string voiceId, ServiceSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(ModelIdentity);
        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string path, string language, ServiceSettings settings, CancellationToken token)
        {
            TranscriptionCalls++;
            IReadOnlyList<TranscriptSegment> result = LongVideo
                ? TranscriptionCalls == 1
                    ? [new() { StartSeconds = 298, EndSeconds = 300, SourceText = "boundary" }]
                    : [new() { StartSeconds = 0, EndSeconds = .8, SourceText = "duplicate context" }, new() { StartSeconds = 2, EndSeconds = 4, SourceText = "after boundary" }]
                : [new() { StartSeconds = 1, EndSeconds = InvalidTimestamp ? double.NaN : 3, SourceText = "Hello world" }];
            return Task.FromResult(result);
        }
        public Task<IReadOnlyList<TranslationItem>> TranslateAsync(IReadOnlyList<TranslationItem> items, string source, string target, ServiceSettings settings, CancellationToken token)
        {
            TranslationCalls++;
            return Task.FromResult<IReadOnlyList<TranslationItem>>(BadTranslation ? [] : items.Select(i => new TranslationItem(i.Id, "译文 " + i.Id)).ToArray());
        }
        public async Task SynthesizeAsync(string text, string language, string voice, string path, ServiceSettings settings, CancellationToken token)
        {
            SynthesisCalls++;
            await File.WriteAllBytesAsync(path, new byte[100], token);
        }
    }

    private sealed class FakeMedia : IMediaService
    {
        public Task<double> GetAudioDurationAsync(string path, MediaSettings settings, CancellationToken token) => Task.FromResult(1d);
        public bool LongVideo, FailRender, CancelRender;
        public Task CheckAvailableAsync(MediaSettings settings, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<VideoInfo> ProbeAsync(string path, MediaSettings settings, CancellationToken token)
            => Task.FromResult(new VideoInfo(path, LongVideo ? 600 : 10, 640, 360, [new(1, "English")]));
        public Task<IReadOnlyList<AudioChunk>> ExtractAudioChunksAsync(VideoInfo video, int stream, string directory, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken token)
            => Task.FromResult<IReadOnlyList<AudioChunk>>(LongVideo ? [new("a.wav", 0, 301), new("b.wav", 299, 301)] : [new("a.wav", 0, 10)]);
        public Task BuildDubbedAudioAsync(IReadOnlyList<SpeechClip> clips, double duration, double speed, string output, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken token)
            => File.WriteAllBytesAsync(output, new byte[100], token);
        public async Task RenderAsync(VideoInfo video, ConversionOptions options, string? srt, string? dubbed, string output, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken token)
        {
            await File.WriteAllTextAsync(output, "rendered video", token);
            if (CancelRender) throw new OperationCanceledException();
            if (FailRender) throw new IOException("simulated render failure");
        }
    }
}
