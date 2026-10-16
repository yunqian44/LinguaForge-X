using System.Text.Json;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Application.UseCases;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Persistence;
using Xunit;

namespace LinguaForge_X.Tests;

public sealed class SpeechFittingTests
{
    private const string LongText = "首先请选择所需要的字幕和配音语言，然后再开始导出视频文件。";
    private const string ShortText = "选好语言后导出视频。";

    [Fact]
    public async Task OverlongSpeechIsShortenedAndBothSubtitleOutputsUseTheAcceptedText()
    {
        using var fixture = new Fixture();
        var result = await fixture.ExportAsync();
        var saved = await fixture.SavedAsync();
        var request = Assert.Single(fixture.Cloud.Requests);
        Assert.Equal(LongText, request.OriginalText);
        Assert.Equal(LongText, request.CurrentText);
        Assert.Equal(fixture.Project.Segments[0].SourceText, request.SourceText);
        Assert.InRange(request.TargetDurationSeconds, .01, 3.52 * 1.35);
        Assert.InRange(request.MaxCharacters, 1, LongText.Length - 1);
        Assert.Equal(ShortText, saved.Segments[0].SpeechText);
        Assert.Equal(ShortText, saved.Segments[0].SubtitleText);
        Assert.Equal(fixture.Project.Segments[0].SourceText, saved.Segments[0].SourceText);
        Assert.Equal(0, saved.Segments[0].StartSeconds);
        Assert.Equal(3.52, saved.Segments[0].EndSeconds);
        Assert.Equal(1.35, fixture.Media.LastMaxSpeed);
        Assert.Equal(1.35, saved.Options.MaxSpeechSpeed);
        var srt = await File.ReadAllTextAsync(result.SubtitlePath!);
        Assert.Contains(ShortText, srt);
        Assert.DoesNotContain(LongText, srt);
        Assert.Equal(srt, fixture.Media.RenderedSubtitle);
        Assert.Equal([LongText, ShortText], fixture.Cloud.SynthesizedTexts);
    }

    [Fact]
    public async Task SpeechThatAlreadyFitsDoesNotCallShortening()
    {
        using var fixture = new Fixture();
        fixture.Cloud.Duration = _ => 3;
        await fixture.ExportAsync();
        Assert.Empty(fixture.Cloud.Requests);
        Assert.Equal([LongText], fixture.Cloud.SynthesizedTexts);
        Assert.Equal(LongText, (await fixture.SavedAsync()).Segments[0].SpeechText);
    }

    [Fact]
    public void OlderProjectJsonEnablesAutomaticShorteningByDefault()
    {
        var project = JsonSerializer.Deserialize<ConversionProject>("""{"Options":{"GenerateSpeech":true}}""")!;
        Assert.True(project.Options.AutoShortenSpeech);
        Assert.True(new ConversionOptions().AutoShortenSpeech);
    }

    [Fact]
    public async Task DisabledShorteningPreservesTextAndRejectsOverlongSpeech()
    {
        using var fixture = new Fixture();
        fixture.Project.Options.AutoShortenSpeech = false;
        await Assert.ThrowsAsync<SpeechDurationExceededException>(() => fixture.ExportAsync());
        Assert.Empty(fixture.Cloud.Requests);
        Assert.Equal(LongText, (await fixture.SavedAsync()).Segments[0].SpeechText);
        fixture.AssertNoPublishedOutput();
    }

    [Fact]
    public async Task ThreeUnsuccessfulAttemptsStopWithoutSavingUnfittedTextOrPublishing()
    {
        using var fixture = new Fixture();
        fixture.Project.Segments[0].SpeechText = fixture.Project.Segments[0].SubtitleText = new string('长', 100);
        fixture.Cloud.Shorten = request => request.CurrentText[..Math.Min(request.MaxCharacters, request.CurrentText.Length - 1)];
        fixture.Cloud.Duration = _ => 6;
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.ExportAsync()));
        Assert.Equal(3, fixture.Cloud.Requests.Count);
        Assert.Equal(4, fixture.Cloud.SynthesizedTexts.Count);
        var saved = await fixture.SavedAsync();
        Assert.Equal(new string('长', 100), saved.Segments[0].SpeechText);
        Assert.Equal(new string('长', 100), saved.Segments[0].SubtitleText);
        fixture.AssertNoPublishedOutput();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringShorteningOrResynthesisKeepsOriginalText(bool duringSynthesis)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        if (duringSynthesis)
            fixture.Cloud.BeforeSynthesis = text => { if (text == ShortText) cancellation.Cancel(); };
        else
            fixture.Cloud.BeforeShortening = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ExportAsync(cancellation.Token));
        var saved = await fixture.SavedAsync();
        Assert.Equal(LongText, saved.Segments[0].SpeechText);
        Assert.Equal(LongText, saved.Segments[0].SubtitleText);
        fixture.AssertNoPublishedOutput();
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.partial.wav", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(LongText)]
    [InlineData(LongText + LongText)]
    public async Task InvalidShorteningResultDoesNotOverwriteTheDraft(string candidate)
    {
        using var fixture = new Fixture();
        fixture.Cloud.Shorten = _ => candidate;
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.ExportAsync()));
        var saved = await fixture.SavedAsync();
        Assert.Equal(LongText, saved.Segments[0].SpeechText);
        Assert.Equal(LongText, saved.Segments[0].SubtitleText);
        Assert.Equal([LongText], fixture.Cloud.SynthesizedTexts);
        fixture.AssertNoPublishedOutput();
    }

    [Fact]
    public async Task IndependentlyEditedSubtitleIsPreserved()
    {
        using var fixture = new Fixture();
        const string edited = "用户单独校对的完整字幕";
        fixture.Project.Segments[0].SubtitleText = edited;
        var result = await fixture.ExportAsync();
        var saved = await fixture.SavedAsync();
        Assert.Equal(ShortText, saved.Segments[0].SpeechText);
        Assert.Equal(edited, saved.Segments[0].SubtitleText);
        Assert.Contains(edited, await File.ReadAllTextAsync(result.SubtitlePath!));
    }

    [Fact]
    public async Task SubtitleInAnotherLanguageIsPreservedEvenWhenTextMatches()
    {
        using var fixture = new Fixture();
        fixture.Project.Options.SubtitleLanguage = "en-US";
        await fixture.ExportAsync();
        var saved = await fixture.SavedAsync();
        Assert.Equal(ShortText, saved.Segments[0].SpeechText);
        Assert.Equal(LongText, saved.Segments[0].SubtitleText);
    }

    [Fact]
    public async Task OriginalLanguageSubtitleCanSyncWhenSourceAndSpeechLanguagesMatch()
    {
        using var fixture = new Fixture();
        fixture.Project.Options.SubtitleLanguage = "source";
        fixture.Project.Options.SourceLanguage = "zh-CN";
        fixture.Project.Segments[0].SourceText = LongText;
        await fixture.ExportAsync();
        var saved = await fixture.SavedAsync();
        Assert.Equal(ShortText, saved.Segments[0].SpeechText);
        Assert.Equal(ShortText, saved.Segments[0].SubtitleText);
        Assert.Equal(LongText, saved.Segments[0].SourceText);
    }

    [Fact]
    public async Task RestoringAfterRenderFailureRetainsShortenedDraftAndReusesVoiceCache()
    {
        using var fixture = new Fixture();
        fixture.Media.FailRender = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.ExportAsync());
        fixture.AssertNoPublishedOutput();
        var restored = await fixture.SavedAsync();
        Assert.Equal(ShortText, restored.Segments[0].SpeechText);
        Assert.Equal(ShortText, restored.Segments[0].SubtitleText);
        Assert.Single(fixture.Cloud.Requests);
        Assert.Equal(2, fixture.Cloud.SynthesizedTexts.Count);
        fixture.Media.FailRender = false;
        await fixture.Service.ExportAsync(restored, fixture.Settings, null, default);
        Assert.Single(fixture.Cloud.Requests);
        Assert.Equal(2, fixture.Cloud.SynthesizedTexts.Count);
        Assert.True(File.Exists(restored.Options.OutputPath));
    }

    [Fact]
    public async Task FailedCandidateSynthesisCanRetryWithoutPayingForShorteningAgain()
    {
        using var fixture = new Fixture();
        fixture.Cloud.BeforeSynthesis = text =>
        {
            if (text == ShortText) throw new IOException("Simulated synthesis interruption.");
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.ExportAsync());
        var restored = await fixture.SavedAsync();
        Assert.Equal(LongText, restored.Segments[0].SpeechText);
        Assert.Single(fixture.Cloud.Requests);
        fixture.AssertNoPublishedOutput();
        fixture.Cloud.BeforeSynthesis = null;
        await fixture.Service.ExportAsync(restored, fixture.Settings, null, default);
        Assert.Single(fixture.Cloud.Requests);
        Assert.Equal(1, fixture.Cloud.SynthesizedTexts.Count(text => text == LongText));
        Assert.Equal(2, fixture.Cloud.SynthesizedTexts.Count(text => text == ShortText));
        Assert.Equal(ShortText, (await fixture.SavedAsync()).Segments[0].SpeechText);
    }

    [Fact]
    public async Task OnlyOverlongSegmentsAreRewrittenAndResynthesized()
    {
        using var fixture = new Fixture();
        const string fittingText = "正常片段";
        fixture.Project.Segments.Add(new()
        {
            Id = 2, StartSeconds = 4, EndSeconds = 7, SourceText = "Next line",
            SpeechText = fittingText, SubtitleText = fittingText
        });
        fixture.Cloud.Duration = text => text == LongText ? 5.38 : text == ShortText ? 3 : 1;
        await fixture.ExportAsync();
        Assert.Equal(1, Assert.Single(fixture.Cloud.Requests).SegmentId);
        Assert.Equal(1, fixture.Cloud.SynthesizedTexts.Count(text => text == LongText));
        Assert.Equal(1, fixture.Cloud.SynthesizedTexts.Count(text => text == ShortText));
        Assert.Equal(1, fixture.Cloud.SynthesizedTexts.Count(text => text == fittingText));
        Assert.Equal(fittingText, (await fixture.SavedAsync()).Segments[1].SpeechText);
    }

    [Fact]
    public async Task DecodedSampleOverflowAlsoTriggersShortening()
    {
        using var fixture = new Fixture();
        fixture.Cloud.Duration = text => text == LongText ? 4 : 3;
        fixture.Media.AlignmentFailures.Enqueue(new(0, 5.38, 3.52, 1.35));
        await fixture.ExportAsync();
        Assert.Single(fixture.Cloud.Requests);
        Assert.Equal(2, fixture.Media.AlignmentCalls);
        Assert.Equal(ShortText, (await fixture.SavedAsync()).Segments[0].SpeechText);
    }

    [Fact]
    public async Task DecodedOverflowSharesTheSameThreeAttemptBudget()
    {
        using var fixture = new Fixture();
        fixture.Project.Segments[0].SpeechText = fixture.Project.Segments[0].SubtitleText = new string('长', 100);
        fixture.Cloud.Shorten = request => request.CurrentText[..Math.Min(request.MaxCharacters, request.CurrentText.Length - 1)];
        fixture.Cloud.Duration = _ => fixture.Cloud.SynthesizedTexts.Count switch { 1 => 6, 2 => 5.5, _ => 3 };
        fixture.Media.AlignmentFailures.Enqueue(new(0, 5.38, 3.52, 1.35));
        fixture.Media.AlignmentFailures.Enqueue(new(0, 5.38, 3.52, 1.35));
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.ExportAsync()));
        Assert.Equal(3, fixture.Cloud.Requests.Count);
        Assert.Equal(4, fixture.Cloud.SynthesizedTexts.Count);
        fixture.AssertNoPublishedOutput();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "linguaforge-fitting-" + Guid.NewGuid().ToString("N"));
        public FakeCloud Cloud { get; } = new();
        public FakeMedia Media { get; } = new();
        public LocalProjectStore Store { get; }
        public ConversionService Service { get; }
        public ConversionProject Project { get; }
        public AppSettings Settings { get; } = new()
        {
            Translation = new() { Provider = ServiceProviderKind.AzureOpenAI, Endpoint = "https://fixture.openai.azure.com/", Deployment = "text", ApiKey = "test-only" }
        };

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var input = Path.Combine(Root, "source.mp4");
            File.WriteAllText(input, "unchanged source fixture");
            var info = new FileInfo(input);
            Store = new(Root);
            Service = new(Media, Cloud, Cloud, Cloud, Store);
            Project = new()
            {
                PreparationComplete = true, SourceFileLength = info.Length, SourceLastWriteTimeUtc = info.LastWriteTimeUtc,
                Video = new(input, 10, 640, 360, [new(0, "English")]),
                Options = new()
                {
                    InputPath = input, OutputPath = Path.Combine(Root, "result.mp4"), GenerateSpeech = true,
                    VoiceId = "zh-CN-XiaoxiaoNeural", MaxSpeechSpeed = 1.35
                },
                Segments = [new() { Id = 1, StartSeconds = 0, EndSeconds = 3.52, SourceText = "Select languages, then export the video.", SpeechText = LongText, SubtitleText = LongText }]
            };
            Project.WorkDirectory = Store.CreateWorkDirectory(Project.Id);
        }

        public Task<ConversionResult> ExportAsync(CancellationToken token = default)
            => Service.ExportAsync(Project, Settings, null, token);

        public async Task<ConversionProject> SavedAsync() => Assert.Single(await Store.ListAsync());

        public void AssertNoPublishedOutput()
        {
            Assert.False(File.Exists(Project.Options.OutputPath));
            Assert.False(File.Exists(Path.ChangeExtension(Project.Options.OutputPath, ".srt")));
            Assert.Empty(Directory.EnumerateFiles(Root, ".linguaforge-*"));
        }

        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("linguaforge-fitting-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a path outside the fitting test directory.");
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FakeCloud : ITranscriptionService, ITranslationService, ISynthesisService
    {
        public List<SpeechShorteningRequest> Requests { get; } = [];
        public List<string> SynthesizedTexts { get; } = [];
        public Func<SpeechShorteningRequest, string> Shorten { get; set; } = _ => ShortText;
        public Func<string, double> Duration { get; set; } = text => text == LongText ? 5.38 : 3;
        public Action? BeforeShortening { get; set; }
        public Action<string>? BeforeSynthesis { get; set; }

        public Task<string> ShortenForSpeechAsync(SpeechShorteningRequest request, ServiceSettings settings, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            BeforeShortening?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Shorten(request));
        }

        public async Task SynthesizeAsync(string text, string language, string voiceId, string outputPath, ServiceSettings settings, CancellationToken cancellationToken)
        {
            SynthesizedTexts.Add(text);
            BeforeSynthesis?.Invoke(text);
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = new byte[100];
            BitConverter.GetBytes(Duration(text)).CopyTo(bytes, 0);
            await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
        }

        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string audioPath, string sourceLanguage, ServiceSettings settings, CancellationToken cancellationToken)
            => throw new NotSupportedException("Prepared fixture must not be transcribed again.");

        public Task<IReadOnlyList<TranslationItem>> TranslateAsync(IReadOnlyList<TranslationItem> items, string sourceLanguage, string targetLanguage, ServiceSettings settings, CancellationToken cancellationToken)
            => throw new NotSupportedException("Shortening must not redo translation.");
    }

    private sealed class FakeMedia : IMediaService
    {
        public bool FailRender { get; set; }
        public string? RenderedSubtitle { get; private set; }
        public double LastMaxSpeed { get; private set; }
        public int AlignmentCalls { get; private set; }
        public Queue<SpeechDurationExceededException> AlignmentFailures { get; } = new();

        public Task CheckAvailableAsync(MediaSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task<double> GetAudioDurationAsync(string path, MediaSettings settings, CancellationToken cancellationToken)
            => BitConverter.ToDouble(await File.ReadAllBytesAsync(path, cancellationToken));

        public async Task BuildDubbedAudioAsync(IReadOnlyList<SpeechClip> clips, double videoDurationSeconds, double maxSpeechSpeed, string outputPath,
            MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
        {
            AlignmentCalls++;
            LastMaxSpeed = maxSpeechSpeed;
            cancellationToken.ThrowIfCancellationRequested();
            if (AlignmentFailures.TryDequeue(out var exception)) throw exception;
            for (var index = 0; index < clips.Count; index++)
            {
                var clip = clips[index];
                var duration = await GetAudioDurationAsync(clip.FilePath, settings, cancellationToken);
                var window = clip.EndSeconds - clip.StartSeconds;
                if (duration > window * maxSpeechSpeed + .001)
                    throw new SpeechDurationExceededException(index, duration, window, maxSpeechSpeed);
            }
            await File.WriteAllBytesAsync(outputPath, new byte[100], cancellationToken);
        }

        public async Task RenderAsync(VideoInfo video, ConversionOptions options, string? subtitlePath, string? dubbedAudioPath, string outputPath,
            MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
        {
            RenderedSubtitle = subtitlePath is null ? null : await File.ReadAllTextAsync(subtitlePath, cancellationToken);
            await File.WriteAllTextAsync(outputPath, "rendered fixture", cancellationToken);
            if (FailRender) throw new IOException("Simulated render failure.");
        }

        public Task<VideoInfo> ProbeAsync(string path, MediaSettings settings, CancellationToken cancellationToken)
            => throw new NotSupportedException("Prepared fixture already has video metadata.");

        public Task<IReadOnlyList<AudioChunk>> ExtractAudioChunksAsync(VideoInfo video, int audioStreamIndex, string workDirectory, MediaSettings settings,
            IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException("Prepared fixture must not be extracted again.");
    }
}
