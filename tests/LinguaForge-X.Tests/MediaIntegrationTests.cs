using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Media;
using LinguaForge_X.Infrastructure.OnlineVideo;
using Xunit;

namespace LinguaForge_X.Tests;

/// <summary>Real local-media tests. No Azure account or network access is used.</summary>
public sealed class MediaIntegrationTests
{
    [MediaFact]
    public async Task OnlineWebmNormalizesToPlayableMp4AndEntersSubtitleExport()
    {
        using var fixture = new MediaFixture();
        var source = fixture.PathFor("下载器素材.webm");
        await fixture.FfmpegAsync("-f", "lavfi", "-i", "color=c=blue:size=64x64:rate=5:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
            "-c:v", "libvpx-vp9", "-c:a", "libopus", source);
        var runner = new DownloadFixtureRunner(source);
        var service = new YtDlpOnlineVideoService(fixture.Service, runner);
        var result = await service.DownloadAsync(new() { Url = "https://www.youtube.com/watch?v=fixture" },
            new() { DownloaderPath = "fixture-downloader", DownloadDirectory = fixture.Root }, fixture.Settings, null, default);
        Assert.True(File.Exists(source));
        var video = await fixture.Service.ProbeAsync(result.FilePath, fixture.Settings, default);
        Assert.InRange(video.DurationSeconds, 1.9, 2.2);
        Assert.Single(video.AudioTracks);
        var probe = await new OnlineVideoProcessRunner().RunAsync(new(fixture.Settings.FfprobePath,
            ["-v", "error", "-show_entries", "stream=codec_name", "-of", "json", result.FilePath], fixture.Root), null, default);
        Assert.Contains("h264", probe.Output);
        Assert.Contains("aac", probe.Output);
        var subtitle = fixture.PathFor("下载后字幕.srt");
        await File.WriteAllTextAsync(subtitle, "1\n00:00:00,000 --> 00:00:01,800\n在线视频字幕\n\n");
        var output = fixture.PathFor("下载后导出.mp4");
        await fixture.Service.RenderAsync(video, new() { AudioStreamIndex = video.AudioTracks[0].StreamIndex },
            subtitle, null, output, fixture.Settings, null, default);
        Assert.True(new FileInfo(output).Length > 100);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(result.FilePath)!));
    }

    private sealed class DownloadFixtureRunner(string source) : IOnlineVideoProcessRunner
    {
        private readonly OnlineVideoProcessRunner _real = new();
        public Task<OnlineVideoProcessResult> RunAsync(OnlineVideoProcessRequest request, Action<string>? outputLine, CancellationToken token)
        {
            if (request.Executable != "fixture-downloader") return _real.RunAsync(request, outputLine, token);
            if (request.Arguments.Contains("--dump-single-json"))
                return Task.FromResult(new OnlineVideoProcessResult(0,
                    """{"id":"fixture","title":"下载器素材","duration":2,"extractor_key":"Youtube"}""", ""));
            var downloaded = Path.Combine(request.WorkingDirectory, "media.webm");
            File.Copy(source, downloaded);
            outputLine!("LF_OUTPUT:" + JsonSerializer.Serialize(downloaded));
            return Task.FromResult(new OnlineVideoProcessResult(0, "", ""));
        }
    }

    [MediaFact]
    public async Task ProbeHandlesChineseAndApostrophePathsWithoutChangingSource()
    {
        using var fixture = new MediaFixture();
        var input = await fixture.VideoAsync();
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(input));
        await fixture.Service.CheckAvailableAsync(fixture.Settings, default);
        var video = await fixture.Service.ProbeAsync(input, fixture.Settings, default);
        Assert.Equal(320, video.Width);
        Assert.Equal(180, video.Height);
        Assert.InRange(video.DurationSeconds, 7.99, 8.02);
        Assert.Single(video.AudioTracks);
        var chunks = await fixture.Service.ExtractAudioChunksAsync(video, video.AudioTracks[0].StreamIndex,
            fixture.Root, fixture.Settings, null, default);
        Assert.Single(chunks);
        Assert.Equal(0, chunks[0].OffsetSeconds);
        Assert.InRange(new FileInfo(chunks[0].FilePath).Length, 100, 25_000_000);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(input)));
    }

    [MediaFact]
    public async Task ExtractionPreservesDelayedAndShortAudioOnVideoTimeline()
    {
        using var fixture = new MediaFixture();
        var input = fixture.PathFor("延迟音轨.mp4");
        await fixture.FfmpegAsync("-f", "lavfi", "-i", "color=c=blue:size=64x64:rate=5:duration=8",
            "-itsoffset", "2", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", input);
        var video = await fixture.Service.ProbeAsync(input, fixture.Settings, default);
        var chunks = await fixture.Service.ExtractAudioChunksAsync(video, video.AudioTracks[0].StreamIndex,
            fixture.Root, fixture.Settings, null, default);
        var bytes = await fixture.DecodePcmAsync(chunks[0].FilePath, 16000);
        Assert.Equal(8 * 16000 * 2, bytes.Length);
        Assert.All(bytes.Take(16000 * 2), b => Assert.Equal(0, b));
        Assert.Contains(bytes.Skip(3 * 16000 * 2).Take(16000 * 2), b => b != 0);
        Assert.All(bytes.Skip(6 * 16000 * 2), b => Assert.Equal(0, b));
    }

    [MediaFact]
    public async Task LongVideoChunksKeepExactOffsetsContextAndAudioContent()
    {
        using var fixture = new MediaFixture();
        var input = fixture.PathFor("长视频.mp4");
        await fixture.FfmpegAsync("-f", "lavfi", "-i", "color=c=black:size=32x32:rate=1:duration=605",
            "-itsoffset", "300", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000:duration=3",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", input);
        var video = await fixture.Service.ProbeAsync(input, fixture.Settings, default);
        var chunks = await fixture.Service.ExtractAudioChunksAsync(video, video.AudioTracks[0].StreamIndex,
            fixture.Root, fixture.Settings, null, default);
        Assert.Equal([0d, 299d, 599d], chunks.Select(c => c.OffsetSeconds).ToArray());
        Assert.Equal([301d, 302d, 6d], chunks.Select(c => c.DurationSeconds).ToArray());
        foreach (var chunk in chunks)
        {
            Assert.InRange(new FileInfo(chunk.FilePath).Length, 100, 25_000_000);
            Assert.Equal((long)(chunk.DurationSeconds * 16000 * 2), (await fixture.DecodePcmAsync(chunk.FilePath, 16000)).LongLength);
        }
        // The tone at absolute 300s must be at 1s in the chunk starting at 299s.
        var second = await fixture.DecodePcmAsync(chunks[1].FilePath, 16000);
        Assert.All(second.Take(8000 * 2), b => Assert.Equal(0, b));
        Assert.Contains(second.Skip(2 * 16000 * 2).Take(16000 * 2), b => b != 0);
        Assert.All(second.Skip(5 * 16000 * 2), b => Assert.Equal(0, b));
    }

    [MediaFact]
    public async Task DubbedAudioFitsSlotsAndPreservesExactDurationAndSilence()
    {
        using var fixture = new MediaFixture();
        var clip = await fixture.SpeechAsync(2.3);
        var output = fixture.PathFor("中文配音.wav");
        await fixture.Service.BuildDubbedAudioAsync([new(clip, 1, 3), new(clip, 4, 7)], 8, 1.35,
            output, fixture.Settings, null, default);
        var bytes = await fixture.DecodePcmAsync(output, 48000);
        Assert.Equal(8 * 48000 * 2, bytes.Length);
        Assert.All(bytes.Take(48000 * 2), b => Assert.Equal(0, b));
        Assert.Contains(bytes.Skip(48000 * 2).Take(48000 * 2), b => b != 0);
        Assert.All(bytes.Skip(3 * 48000 * 2).Take(48000 * 2), b => Assert.Equal(0, b));
        Assert.All(bytes.Skip(7 * 48000 * 2), b => Assert.Equal(0, b));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "dub-*"));
    }

    [MediaFact]
    public async Task ReadsActualSynthesizedAudioDuration()
    {
        using var fixture = new MediaFixture();
        var clip = await fixture.SpeechAsync(5.38);
        var duration = await fixture.Service.GetAudioDurationAsync(clip, fixture.Settings, default);
        Assert.InRange(duration, 5.379, 5.381);
    }

    [MediaFact]
    public async Task AudioDurationRejectsMediaWithoutAudio()
    {
        using var fixture = new MediaFixture();
        var video = fixture.PathFor("无音轨.mp4");
        await fixture.FfmpegAsync("-f", "lavfi", "-i", "color=c=blue:size=64x64:rate=5:duration=2",
            "-c:v", "libx264", "-an", video);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetAudioDurationAsync(video, fixture.Settings, default));
        Assert.Contains("没有可读取的音轨", exception.Message);
    }

    [MediaFact]
    public async Task SpeechTooLongForMaximumSpeedFailsWithoutTruncationOrOutput()
    {
        using var fixture = new MediaFixture();
        var clip = await fixture.SpeechAsync(2.3);
        var output = fixture.PathFor("too-long.wav");
        var exception = await Assert.ThrowsAsync<SpeechDurationExceededException>(() => fixture.Service.BuildDubbedAudioAsync(
            [new(clip, 1, 1.5)], 8, 1.35, output, fixture.Settings, null, default));
        Assert.Equal(0, exception.ClipIndex);
        Assert.InRange(exception.DurationSeconds, 2.299, 2.301);
        Assert.Equal(.5, exception.WindowSeconds);
        Assert.Equal(1.35, exception.MaxSpeechSpeed);
        Assert.Contains("第 1 段", exception.Message);
        Assert.Contains("不会截断配音", exception.Message);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "dub-*"));
    }

    [MediaFact]
    public async Task BurnsChineseSubtitleFromQuotedPathWithReplacementAudio()
    {
        using var fixture = new MediaFixture();
        var input = await fixture.VideoAsync();
        var video = await fixture.Service.ProbeAsync(input, fixture.Settings, default);
        var clip = await fixture.SpeechAsync(2);
        var dubbed = fixture.PathFor("配音.wav");
        await fixture.Service.BuildDubbedAudioAsync([new(clip, 1, 4)], 8, 1.35, dubbed, fixture.Settings, null, default);
        var subtitles = await fixture.SubtitlesAsync();
        var options = new ConversionOptions { AudioStreamIndex = video.AudioTracks[0].StreamIndex, GenerateSpeech = true };
        var sourceHash = await fixture.VideoHashAsync(input);
        foreach (var encoding in Enum.GetValues<VideoEncodingMode>())
        {
            fixture.Settings.VideoEncoding = encoding;
            var output = fixture.PathFor("字幕与中文配音-" + encoding + ".mp4");
            await fixture.Service.RenderAsync(video, options, subtitles, dubbed, output, fixture.Settings, null, default);
            var rendered = await fixture.Service.ProbeAsync(output, fixture.Settings, default);
            Assert.InRange(rendered.DurationSeconds, 7.9, 8.1);
            Assert.Single(rendered.AudioTracks);
            Assert.NotEqual(sourceHash, await fixture.VideoHashAsync(output));
            Assert.Empty(Directory.GetFiles(fixture.Root, "subtitle-*.srt"));
        }
        Assert.Equal(sourceHash, await fixture.VideoHashAsync(input));
        Assert.True(File.Exists(subtitles));
    }

    [MediaFact]
    public async Task QuietMixKeepsVideoFramesAndAddsSynthesizedAudio()
    {
        using var fixture = new MediaFixture();
        var input = await fixture.VideoAsync();
        var video = await fixture.Service.ProbeAsync(input, fixture.Settings, default);
        var clip = await fixture.SpeechAsync(2);
        var dubbed = fixture.PathFor("配音.wav");
        await fixture.Service.BuildDubbedAudioAsync([new(clip, 1, 4)], 8, 1.35, dubbed, fixture.Settings, null, default);
        var output = fixture.PathFor("中文混音.mp4");
        var options = new ConversionOptions { AudioStreamIndex = video.AudioTracks[0].StreamIndex,
            GenerateSubtitles = false, GenerateSpeech = true, OriginalAudio = OriginalAudioMode.QuietMix };
        await fixture.Service.RenderAsync(video, options, null, dubbed, output, fixture.Settings, null, default);
        Assert.Equal(await fixture.VideoHashAsync(input), await fixture.VideoHashAsync(output));
        var originalPcm = await fixture.DecodePcmAsync(input, 48000);
        var mixedPcm = await fixture.DecodePcmAsync(output, 48000);
        Assert.False(originalPcm.SequenceEqual(mixedPcm));
        Assert.Contains(mixedPcm.Skip(2 * 48000 * 2).Take(48000 * 2), b => b != 0);
    }

    [MediaFact]
    public async Task IncompatibleCodecAndOddDimensionsConvertAndMissingAudioIsActionable()
    {
        using var fixture = new MediaFixture();
        var input = fixture.PathFor("无音轨奇数尺寸.mkv");
        await fixture.FfmpegAsync("-f", "lavfi", "-i", "testsrc=size=321x181:rate=5:duration=2", "-c:v", "ffv1", "-pix_fmt", "bgr0", input);
        var video = await fixture.Service.ProbeAsync(input, fixture.Settings, default);
        Assert.Empty(video.AudioTracks);
        var output = fixture.PathFor("兼容转换.mp4");
        await fixture.Service.RenderAsync(video, new() { GenerateSubtitles = false }, null, null, output, fixture.Settings, null, default);
        var rendered = await fixture.Service.ProbeAsync(output, fixture.Settings, default);
        Assert.Equal(322, rendered.Width);
        Assert.Equal(182, rendered.Height);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExtractAudioChunksAsync(
            video, 0, fixture.Root, fixture.Settings, null, default));
        Assert.Contains("无音轨", exception.Message);
    }

    [MediaFact]
    public async Task Hd720HandlesLandscapePortraitSmallAndOddSourcesWithoutUpscaling()
    {
        using var fixture = new MediaFixture();
        fixture.Settings.VideoEncoding = VideoEncodingMode.CpuFast;
        fixture.Settings.OutputQuality = OutputVideoQuality.Hd720;
        var cases = new[]
        {
            (Name: "横屏", Width: 1920, Height: 1080, ExpectedWidth: 1280, ExpectedHeight: 720, Odd: false),
            (Name: "竖屏", Width: 1080, Height: 1920, ExpectedWidth: 720, ExpectedHeight: 1280, Odd: false),
            (Name: "低清", Width: 320, Height: 180, ExpectedWidth: 320, ExpectedHeight: 180, Odd: false),
            (Name: "奇数", Width: 901, Height: 507, ExpectedWidth: 900, ExpectedHeight: 506, Odd: true)
        };
        foreach (var item in cases)
        {
            var input = fixture.PathFor(item.Name + (item.Odd ? ".mkv" : ".mp4"));
            await fixture.FfmpegAsync("-f", "lavfi", "-i",
                $"{(item.Odd ? "testsrc" : "testsrc2")}=size={item.Width}x{item.Height}:rate=5:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
                "-c:v", item.Odd ? "ffv1" : "libx264", "-pix_fmt", item.Odd ? "bgr0" : "yuv420p",
                "-c:a", "aac", input);
            var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
            var video = await fixture.Service.ProbeAsync(input, fixture.Settings, default);
            var output = fixture.PathFor(item.Name + "-720p.mp4");
            await fixture.Service.RenderAsync(video,
                new() { GenerateSubtitles = false, AudioStreamIndex = video.AudioTracks[0].StreamIndex },
                null, null, output, fixture.Settings, null, default);
            var rendered = await fixture.Service.ProbeAsync(output, fixture.Settings, default);
            Assert.Equal(item.ExpectedWidth, rendered.Width);
            Assert.Equal(item.ExpectedHeight, rendered.Height);
            Assert.InRange(rendered.DurationSeconds, 1.9, 2.2);
            Assert.Single(rendered.AudioTracks);
            Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(input)));
        }
    }

    [MediaFact]
    public async Task Compact480KeepsAudioDurationAndDrawsChineseSubtitlesAtFinalSize()
    {
        using var fixture = new MediaFixture();
        fixture.Settings.VideoEncoding = VideoEncodingMode.CpuFast;
        fixture.Settings.OutputQuality = OutputVideoQuality.Compact480;
        var input = fixture.PathFor("纯黑原视频.mp4");
        await fixture.FfmpegAsync("-f", "lavfi", "-i", "color=c=black:size=960x540:rate=5:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", input);
        var video = await fixture.Service.ProbeAsync(input, fixture.Settings, default);
        var options = new ConversionOptions { GenerateSubtitles = false, AudioStreamIndex = video.AudioTracks[0].StreamIndex };
        var plain = fixture.PathFor("480p-无字幕.mp4");
        await fixture.Service.RenderAsync(video, options, null, null, plain, fixture.Settings, null, default);
        options.GenerateSubtitles = true;
        var subtitles = await fixture.SubtitlesAsync();
        var output = fixture.PathFor("480p-中文字幕.mp4");
        await fixture.Service.RenderAsync(video, options, subtitles, null, output, fixture.Settings, null, default);
        var rendered = await fixture.Service.ProbeAsync(output, fixture.Settings, default);
        Assert.InRange(rendered.Width, 850, 854);
        Assert.Equal(480, rendered.Height);
        Assert.InRange(rendered.DurationSeconds, 1.9, 2.2);
        Assert.Single(rendered.AudioTracks);
        var audio = await fixture.DecodePcmAsync(output, 16000);
        Assert.InRange(audio.Length, 2 * 16000 * 2 - 2048, 2 * 16000 * 2 + 2048);
        Assert.Contains(audio, sample => sample != 0);
        var plainFrame = await fixture.GrayFrameAsync(plain, 1.2);
        var subtitleFrame = await fixture.GrayFrameAsync(output, 1.2);
        Assert.Equal(plainFrame.Length, subtitleFrame.Length);
        Assert.All(plainFrame, sample => Assert.InRange(sample, (byte)0, (byte)5));
        Assert.True(subtitleFrame.Count(sample => sample > 64) > 30, "The rendered frame must contain visible subtitle pixels.");
        Assert.Empty(Directory.GetFiles(fixture.Root, "subtitle-*.srt"));
        Assert.True(File.Exists(subtitles));
    }

    [MediaFact]
    public async Task Hd720UsesRotatedFrameOrientationRatherThanStoredSourceDimensions()
    {
        using var fixture = new MediaFixture();
        fixture.Settings.VideoEncoding = VideoEncodingMode.CpuFast;
        fixture.Settings.OutputQuality = OutputVideoQuality.Hd720;
        var unrotated = fixture.PathFor("横屏拍摄.mp4");
        await fixture.FfmpegAsync("-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=5:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", unrotated);
        var rotated = fixture.PathFor("竖屏旋转元数据.mp4");
        await fixture.FfmpegAsync("-display_rotation", "90", "-i", unrotated, "-map", "0", "-c", "copy", rotated);
        using (var sourceProbe = await fixture.ProbeStreamsAsync(rotated))
        {
            var videoStream = sourceProbe.RootElement.GetProperty("streams").EnumerateArray()
                .First(stream => stream.GetProperty("codec_type").GetString() == "video");
            Assert.Contains(videoStream.GetProperty("side_data_list").EnumerateArray(), sideData =>
                sideData.GetProperty("side_data_type").GetString() == "Display Matrix" &&
                sideData.GetProperty("rotation").GetInt32() == 90);
        }
        var video = await fixture.Service.ProbeAsync(rotated, fixture.Settings, default);
        Assert.Equal(1920, video.Width);
        Assert.Equal(1080, video.Height);
        var output = fixture.PathFor("旋转后720p.mp4");
        await fixture.Service.RenderAsync(video,
            new() { GenerateSubtitles = false, AudioStreamIndex = video.AudioTracks[0].StreamIndex },
            null, null, output, fixture.Settings, null, default);
        var rendered = await fixture.Service.ProbeAsync(output, fixture.Settings, default);
        Assert.Equal(720, rendered.Width);
        Assert.Equal(1280, rendered.Height);
        Assert.InRange(rendered.DurationSeconds, 1.9, 2.2);
        Assert.Single(rendered.AudioTracks);
    }

    [MediaFact]
    public async Task Hd720PreservesDisplayAspectRatioForAnamorphicVideo()
    {
        using var fixture = new MediaFixture();
        fixture.Settings.VideoEncoding = VideoEncodingMode.CpuFast;
        fixture.Settings.OutputQuality = OutputVideoQuality.Hd720;
        var input = fixture.PathFor("非方形像素.mp4");
        await fixture.FfmpegAsync("-f", "lavfi", "-i", "testsrc2=size=1440x1080:rate=5:duration=2",
            "-vf", "setsar=4/3", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", input);
        var video = await fixture.Service.ProbeAsync(input, fixture.Settings, default);
        var output = fixture.PathFor("非方形像素720p.mp4");
        await fixture.Service.RenderAsync(video, new() { GenerateSubtitles = false }, null, null,
            output, fixture.Settings, null, default);
        var rendered = await fixture.Service.ProbeAsync(output, fixture.Settings, default);
        Assert.Equal(960, rendered.Width);
        Assert.Equal(720, rendered.Height);
        using var probe = await fixture.ProbeStreamsAsync(output);
        var stream = probe.RootElement.GetProperty("streams").EnumerateArray()
            .First(item => item.GetProperty("codec_type").GetString() == "video");
        var sar = stream.GetProperty("sample_aspect_ratio").GetString()!.Split(':');
        var displayAspect = rendered.Width / (double)rendered.Height * int.Parse(sar[0]) / int.Parse(sar[1]);
        Assert.InRange(displayAspect, 16d / 9 - .005, 16d / 9 + .005);
    }

    [MediaFact]
    public async Task ActiveRenderCancellationKillsProcessAndCleansSubtitleCopy()
    {
        using var fixture = new MediaFixture();
        var input = await fixture.VideoAsync();
        var video = await fixture.Service.ProbeAsync(input, fixture.Settings, default);
        var subtitles = await fixture.SubtitlesAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = fixture.PathFor("canceled.mp4");
        var progressReceived = false;
        var progress = new ImmediateProgress<ProcessingProgress>(_ => { progressReceived = true; cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RenderAsync(video,
            new() { AudioStreamIndex = video.AudioTracks[0].StreamIndex }, subtitles, null, output, fixture.Settings, progress, cancellation.Token));
        Assert.True(progressReceived);
        Assert.Empty(Directory.GetFiles(fixture.Root, "subtitle-*.srt"));
        // A leaked FFmpeg process would still hold this file open on Windows.
        if (File.Exists(output))
        {
            using var exclusive = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    [MediaFact]
    public async Task CancellationCompletesWhenCallingSynchronizationContextDoesNotPump()
    {
        using var fixture = new MediaFixture();
        var input = fixture.PathFor("dispatcher-shutdown.mp4");
        await fixture.FfmpegAsync("-f", "lavfi", "-i", "color=c=blue:size=1280x720:rate=30:duration=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", input);
        var video = await fixture.Service.ProbeAsync(input, fixture.Settings, default);
        var subtitles = await fixture.SubtitlesAsync();
        var output = fixture.PathFor("canceled-without-dispatcher.mp4");
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ImmediateProgress<ProcessingProgress>(_ => started.TrySetResult());
        var context = new NonPumpingSynchronizationContext();
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                fixture.Service.RenderAsync(video, new(), subtitles, null, output, fixture.Settings, progress, cancellation.Token)
                    .GetAwaiter().GetResult();
                completed.TrySetResult(null);
            }
            catch (Exception exception) { completed.TrySetResult(exception); }
        }) { IsBackground = true, Name = "Media cancellation without dispatcher" };
        thread.Start();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            cancellation.Cancel();
            var exception = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
            Assert.Equal(0, context.PostCount);
            Assert.Empty(Directory.GetFiles(fixture.Root, "subtitle-*.srt"));
            if (File.Exists(output))
            {
                using var exclusive = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        finally { cancellation.Cancel(); }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;
        public int PostCount => Volatile.Read(ref _postCount);
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref _postCount);
    }

    private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class MediaFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "linguaforge-media-" + Guid.NewGuid().ToString("N"), "视频 O'Brien [测试]");
        public MediaSettings Settings { get; } = MediaTools.Find() ?? throw new InvalidOperationException("FFmpeg integration test prerequisites are missing.");
        public FfmpegMediaService Service { get; } = new();

        public MediaFixture() => Directory.CreateDirectory(Root);
        public string PathFor(string name) => Path.Combine(Root, name);

        public async Task<string> VideoAsync()
        {
            var path = PathFor("原始 input.mp4");
            await FfmpegAsync("-f", "lavfi", "-i", "color=c=blue:size=320x180:rate=15:duration=8", "-f", "lavfi", "-i",
                "sine=frequency=440:sample_rate=48000:duration=8", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", path);
            return path;
        }

        public async Task<string> SpeechAsync(double seconds)
        {
            var path = PathFor("中文片段.wav");
            await FfmpegAsync("-f", "lavfi", "-i", $"sine=frequency=700:sample_rate=24000:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}", path);
            return path;
        }

        public async Task<string> SubtitlesAsync()
        {
            var path = PathFor("字幕 O'Brien.srt");
            await File.WriteAllTextAsync(path, "1\n00:00:01,000 --> 00:00:04,000\n你好，世界！\n\n", new UTF8Encoding(false));
            return path;
        }

        public async Task<byte[]> DecodePcmAsync(string input, int rate)
        {
            var path = PathFor("verify-" + Guid.NewGuid().ToString("N") + ".pcm");
            await FfmpegAsync("-i", input, "-vn", "-ac", "1", "-ar", rate.ToString(), "-c:a", "pcm_s16le", "-f", "s16le", path);
            return await File.ReadAllBytesAsync(path);
        }

        public async Task<byte[]> GrayFrameAsync(string input, double seconds)
        {
            var path = PathFor("frame-" + Guid.NewGuid().ToString("N") + ".gray");
            await FfmpegAsync("-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", input,
                "-map", "0:v:0", "-frames:v", "1", "-vf", "format=gray", "-f", "rawvideo", path);
            return await File.ReadAllBytesAsync(path);
        }

        public async Task<JsonDocument> ProbeStreamsAsync(string input)
        {
            var result = await new OnlineVideoProcessRunner().RunAsync(new(Settings.FfprobePath,
                ["-v", "error", "-show_streams", "-of", "json", input], Root), null, default);
            Assert.Equal(0, result.ExitCode);
            return JsonDocument.Parse(result.Output);
        }

        public async Task<string> VideoHashAsync(string input)
        {
            var path = PathFor("hash-" + Guid.NewGuid().ToString("N") + ".txt");
            await FfmpegAsync("-i", input, "-map", "0:v:0", "-f", "hash", "-hash", "sha256", path);
            return await File.ReadAllTextAsync(path);
        }

        public async Task FfmpegAsync(params string[] arguments)
        {
            var start = new ProcessStartInfo(Settings.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(arguments)) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            await output;
            Assert.True(process.ExitCode == 0, await error);
        }

        public void Dispose()
        {
            var ownedDirectory = Path.GetFullPath(Path.GetDirectoryName(Root)!);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!ownedDirectory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(ownedDirectory).StartsWith("linguaforge-media-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a path outside the owned test directory.");
            if (Directory.Exists(ownedDirectory)) Directory.Delete(ownedDirectory, recursive: true);
        }
    }
}

public sealed class MediaFactAttribute : FactAttribute
{
    public MediaFactAttribute()
    {
        if (MediaTools.Find() is null)
            Skip = "Real media integration test requires LINGUAFORGE_FFMPEG and LINGUAFORGE_FFPROBE executable paths, or portable binaries under artifacts/media-tools. No media assertions ran.";
    }
}

internal static class MediaTools
{
    public static MediaSettings? Find()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("LINGUAFORGE_FFMPEG");
        var ffprobe = Environment.GetEnvironmentVariable("LINGUAFORGE_FFPROBE");
        if (ffmpeg is not null || ffprobe is not null)
            return File.Exists(ffmpeg) && File.Exists(ffprobe) ? new() { FfmpegPath = Path.GetFullPath(ffmpeg), FfprobePath = Path.GetFullPath(ffprobe) } : null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var portable = Path.Combine(directory.FullName, "artifacts", "media-tools");
            if (!Directory.Exists(portable)) continue;
            var executableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            foreach (var binary in Directory.EnumerateFiles(portable, executableName, SearchOption.AllDirectories))
            {
                var probe = Path.Combine(Path.GetDirectoryName(binary)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
                if (File.Exists(probe)) return new() { FfmpegPath = binary, FfprobePath = probe };
            }
        }
        return null;
    }
}
