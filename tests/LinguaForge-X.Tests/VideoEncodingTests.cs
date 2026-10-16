using System.Text.Json;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Media;
using Xunit;

namespace LinguaForge_X.Tests;

/// <summary>Offline coverage of encoder selection, retry boundaries, and stream mapping.</summary>
public sealed class VideoEncodingTests
{
    [Fact]
    public void NewAndPreviouslySavedSettingsDefaultToAutomaticEncoding()
    {
        Assert.Equal(VideoEncodingMode.Auto, new MediaSettings().VideoEncoding);
        Assert.Equal(VideoEncodingMode.Auto, JsonSerializer.Deserialize<MediaSettings>(
            """{"FfmpegPath":"existing-ffmpeg","FfprobePath":"existing-ffprobe","OutputDirectory":"existing"}""")!.VideoEncoding);
        Assert.Equal(OutputVideoQuality.Original, new MediaSettings().OutputQuality);
        Assert.Equal(OutputVideoQuality.Original, JsonSerializer.Deserialize<MediaSettings>(
            """{"FfmpegPath":"existing-ffmpeg","VideoEncoding":1}""")!.OutputQuality);
        foreach (var mode in Enum.GetValues<VideoEncodingMode>())
            Assert.Equal(mode, JsonSerializer.Deserialize<MediaSettings>(JsonSerializer.Serialize(
                new MediaSettings { VideoEncoding = mode }))!.VideoEncoding);
        foreach (var quality in Enum.GetValues<OutputVideoQuality>())
            Assert.Equal(quality, JsonSerializer.Deserialize<MediaSettings>(JsonSerializer.Serialize(
                new MediaSettings { OutputQuality = quality }))!.OutputQuality);
    }

    [Theory]
    [InlineData(VideoEncodingMode.CpuFast, "veryfast")]
    [InlineData(VideoEncodingMode.CpuQuality, "medium")]
    public async Task CpuModeUsesChosenPresetAndRetainsCrfWithoutHardwareDiscovery(VideoEncodingMode mode, string preset)
    {
        using var fixture = new Fixture();
        fixture.Settings.VideoEncoding = mode;
        await fixture.RenderAsync();
        var render = Assert.Single(fixture.Runner.RenderCalls);
        Assert.Equal("libx264", render.Value("-c:v"));
        Assert.Equal(preset, render.Value("-preset"));
        Assert.Equal("20", render.Value("-crf"));
        Assert.Equal("yuv420p", render.Value("-pix_fmt"));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Arguments.Contains("-encoders"));
        Assert.Empty(fixture.Runner.InitializationCalls);
        Assert.True(new FileInfo(fixture.Output).Length > 0);
    }

    [Theory]
    [InlineData("h264_nvenc", "Cannot load nvcuda.dll")]
    [InlineData("h264_qsv", "Error initializing an MFX session: unsupported (-3)")]
    [InlineData("h264_amf", "Cannot load amfrt64.dll")]
    public async Task CompiledButUnavailableHardwareFallsBackToCpu(string encoder, string diagnostic)
    {
        using var fixture = new Fixture();
        fixture.Runner.Encoders = Encoders(encoder);
        fixture.Runner.Initialize = (_, _) => throw Failure(diagnostic);
        await fixture.RenderAsync();
        Assert.Equal(encoder, Assert.Single(fixture.Runner.InitializationCalls).Value("-c:v"));
        var render = Assert.Single(fixture.Runner.RenderCalls);
        Assert.Equal("libx264", render.Value("-c:v"));
        Assert.Equal("veryfast", render.Value("-preset"));
    }

    [Fact]
    public async Task AutomaticModeSkipsUncompiledHardwareWithoutInitialization()
    {
        using var fixture = new Fixture();
        fixture.Runner.Encoders = "Encoders:\n V....D libx264 H.264 encoder\n A..... aac AAC encoder\n";
        await fixture.RenderAsync();
        Assert.Empty(fixture.Runner.InitializationCalls);
        Assert.Equal("libx264", Assert.Single(fixture.Runner.RenderCalls).Value("-c:v"));
    }

    [Fact]
    public async Task AutomaticModeTriesNextCompiledEncoderAndStopsAtFirstWorkingDevice()
    {
        using var fixture = new Fixture();
        fixture.Runner.Encoders = Encoders("h264_nvenc", "h264_qsv", "h264_amf");
        fixture.Runner.Initialize = (call, _) => call.Value("-c:v") == "h264_nvenc"
            ? throw Failure("Cannot load nvEncodeAPI64.dll") : Task.FromResult("");
        await fixture.RenderAsync();
        Assert.Equal(["h264_nvenc", "h264_qsv"], fixture.Runner.InitializationCalls.Select(call => call.Value("-c:v")));
        var render = Assert.Single(fixture.Runner.RenderCalls);
        Assert.Equal("h264_qsv", render.Value("-c:v"));
        Assert.Equal("nv12", render.Value("-pix_fmt"));
        Assert.Equal("20", render.Value("-global_quality"));
    }

    [Theory]
    [InlineData("h264_nvenc", "yuv420p")]
    [InlineData("h264_qsv", "nv12")]
    [InlineData("h264_amf", "nv12")]
    public async Task InitializationUsesActualVideoStreamAndCompleteSubtitleFilterWithoutGpuDecoding(string encoder, string pixelFormat)
    {
        using var fixture = new Fixture();
        fixture.Runner.Encoders = Encoders(encoder);
        await fixture.RenderAsync();
        var initialization = Assert.Single(fixture.Runner.InitializationCalls);
        var render = Assert.Single(fixture.Runner.RenderCalls);
        Assert.Equal(fixture.Input, initialization.Value("-i"));
        Assert.Equal("0:2", initialization.Value("-map"));
        Assert.Equal("3", initialization.Value("-frames:v"));
        Assert.Equal("null", initialization.Value("-f"));
        Assert.Equal("-", initialization.Arguments[^1]);
        Assert.Equal(render.Value("-vf"), initialization.Value("-vf"));
        Assert.Contains("subtitles=filename=subtitle-", initialization.Value("-vf"));
        Assert.Contains("pad=ceil(iw/2)*2:ceil(ih/2)*2", initialization.Value("-vf"));
        Assert.Contains("-an", initialization.Arguments);
        Assert.Contains("-sn", initialization.Arguments);
        Assert.Equal(fixture.Root, initialization.WorkingDirectory);
        Assert.Equal(encoder, render.Value("-c:v"));
        Assert.Equal(pixelFormat, render.Value("-pix_fmt"));
        Assert.DoesNotContain("-hwaccel", initialization.Arguments);
        Assert.DoesNotContain("-hwaccel", render.Arguments);
        Assert.DoesNotContain("-crf", render.Arguments);
        if (encoder == "h264_nvenc")
        {
            Assert.Equal("vbr", render.Value("-rc"));
            Assert.Equal("20", render.Value("-cq"));
            Assert.Equal("0", render.Value("-b:v"));
        }
        if (encoder == "h264_amf")
        {
            Assert.Equal("cqp", render.Value("-rc"));
            Assert.Equal("20", render.Value("-qp_i"));
            Assert.Equal("20", render.Value("-qp_p"));
            Assert.Equal("20", render.Value("-qp_b"));
        }
        Assert.Empty(Directory.GetFiles(fixture.Root, "subtitle-*.srt"));
        Assert.True(File.Exists(fixture.Subtitles));
    }

    [Fact]
    public async Task HardwareStartupFailureDeletesPartialOutputAndRetriesOnceWithCpu()
    {
        using var fixture = new Fixture();
        fixture.Runner.Encoders = Encoders("h264_nvenc");
        fixture.Runner.Render = async (call, token) =>
        {
            if (call.Value("-c:v") == "h264_nvenc")
            {
                await File.WriteAllBytesAsync(fixture.Output, [9, 9, 9], token);
                throw Failure("NVENC initialization failed", 0);
            }
            Assert.False(File.Exists(fixture.Output));
            await File.WriteAllBytesAsync(fixture.Output, [1, 2, 3], token);
            return "";
        };
        await fixture.RenderAsync();
        Assert.Equal(["h264_nvenc", "libx264"], fixture.Runner.RenderCalls.Select(call => call.Value("-c:v")));
        Assert.Contains(fixture.Progress.Items, item => item.Message.Contains("已切换到 CPU 快速编码"));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(fixture.Output));
    }

    [Fact]
    public async Task HardwareFailureAfterProcessingDoesNotRestartAnAlreadyRunningExport()
    {
        using var fixture = new Fixture();
        fixture.Runner.Encoders = Encoders("h264_nvenc");
        var failure = Failure("NVENC encoding failed", 2);
        fixture.Runner.Render = (call, _) =>
        {
            call.OnProgress?.Invoke(2);
            throw failure;
        };
        Assert.Same(failure, await Assert.ThrowsAsync<MediaProcessException>(() => fixture.RenderAsync()));
        Assert.Single(fixture.Runner.RenderCalls);
        Assert.DoesNotContain(fixture.Progress.Items, item => item.Message.Contains("已切换"));
        Assert.Empty(Directory.GetFiles(fixture.Root, "subtitle-*.srt"));
    }

    [Theory]
    [InlineData("No space left on device\nError initializing output stream 0:0")]
    [InlineData("Permission denied\nError while opening encoder")]
    [InlineData("Unable to open subtitles.srt\nError initializing output stream 0:0")]
    [InlineData("Error initializing filters\nError while opening encoder")]
    [InlineData("Invalid data found when processing input")]
    public async Task InputSubtitleOrDiskFailureDoesNotTriggerCpuRetry(string diagnostic)
    {
        using var fixture = new Fixture();
        fixture.Runner.Encoders = Encoders("h264_nvenc");
        var failure = Failure(diagnostic);
        fixture.Runner.Render = (_, _) => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<MediaProcessException>(() => fixture.RenderAsync()));
        Assert.Single(fixture.Runner.RenderCalls);
        Assert.Empty(Directory.GetFiles(fixture.Root, "subtitle-*.srt"));
    }

    [Fact]
    public async Task InvalidSubtitleDuringInitializationIsReportedInsteadOfTryingAnotherEncoder()
    {
        using var fixture = new Fixture();
        fixture.Runner.Encoders = Encoders("h264_nvenc", "h264_qsv", "h264_amf");
        var failure = Failure("Unable to open subtitles.srt\nError initializing output stream 0:0");
        fixture.Runner.Initialize = (_, _) => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<MediaProcessException>(() => fixture.RenderAsync()));
        Assert.Single(fixture.Runner.InitializationCalls);
        Assert.Empty(fixture.Runner.RenderCalls);
        Assert.Empty(Directory.GetFiles(fixture.Root, "subtitle-*.srt"));
    }

    [Fact]
    public async Task ExternalCancellationDuringEncoderDiscoveryIsNotSwallowed()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Runner.Discover = token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult("");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RenderAsync(cancellation.Token));
        Assert.Empty(fixture.Runner.InitializationCalls);
        Assert.Empty(fixture.Runner.RenderCalls);
    }

    [Fact]
    public async Task ExternalCancellationDuringInitializationIsNotSwallowed()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Runner.Encoders = Encoders("h264_nvenc", "h264_qsv");
        fixture.Runner.Initialize = (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult("");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RenderAsync(cancellation.Token));
        Assert.Single(fixture.Runner.InitializationCalls);
        Assert.Empty(fixture.Runner.RenderCalls);
        Assert.Empty(Directory.GetFiles(fixture.Root, "subtitle-*.srt"));
    }

    [Fact]
    public async Task CancellationDuringHardwareRenderDoesNotFallBack()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Runner.Encoders = Encoders("h264_nvenc");
        fixture.Runner.Render = (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult("");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RenderAsync(cancellation.Token));
        Assert.Single(fixture.Runner.RenderCalls);
        Assert.DoesNotContain(fixture.Progress.Items, item => item.Message.Contains("已切换"));
        Assert.Empty(Directory.GetFiles(fixture.Root, "subtitle-*.srt"));
    }

    [Fact]
    public async Task BlockedHardwareInitializationIsCanceledByTimeoutAndFallsBack()
    {
        using var fixture = new Fixture();
        fixture.Runner.Encoders = Encoders("h264_nvenc");
        var probeWasCanceled = false;
        fixture.Runner.Initialize = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { probeWasCanceled = token.IsCancellationRequested; throw; }
            return "";
        };
        await fixture.RenderAsync();
        Assert.True(probeWasCanceled);
        Assert.Single(fixture.Runner.InitializationCalls);
        Assert.Equal("libx264", Assert.Single(fixture.Runner.RenderCalls).Value("-c:v"));
    }

    [Theory]
    [InlineData("h264")]
    [InlineData("hevc")]
    [InlineData("mpeg4")]
    [InlineData("av1")]
    public async Task NoBurnCompatibleVideoCopiesStreamsAndDoesNotProbeHardware(string videoCodec)
    {
        using var fixture = new Fixture();
        fixture.Runner.VideoCodec = videoCodec;
        fixture.Options.GenerateSubtitles = false;
        await fixture.RenderAsync();
        var render = Assert.Single(fixture.Runner.RenderCalls);
        Assert.Equal("copy", render.Value("-c:v"));
        Assert.Equal("copy", render.Value("-c:a"));
        Assert.DoesNotContain("-vf", render.Arguments);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Arguments.Contains("-encoders"));
        Assert.Empty(fixture.Runner.InitializationCalls);
    }

    [Fact]
    public async Task BurningSubtitlesCanStillCopySelectedAacAudioWithoutLoss()
    {
        using var fixture = new Fixture();
        fixture.Settings.VideoEncoding = VideoEncodingMode.CpuFast;
        await fixture.RenderAsync();
        var render = Assert.Single(fixture.Runner.RenderCalls);
        Assert.Equal("libx264", render.Value("-c:v"));
        Assert.Equal("copy", render.Value("-c:a"));
        Assert.Contains("0:5", render.Arguments);
        Assert.DoesNotContain("-b:a", render.Arguments);
    }

    [Fact]
    public async Task NonAacSourceAudioIsEncodedForMp4Compatibility()
    {
        using var fixture = new Fixture();
        fixture.Runner.AudioCodec = "opus";
        fixture.Options.GenerateSubtitles = false;
        await fixture.RenderAsync();
        var render = Assert.Single(fixture.Runner.RenderCalls);
        Assert.Equal("copy", render.Value("-c:v"));
        Assert.Equal("aac", render.Value("-c:a"));
        Assert.Equal("192k", render.Value("-b:a"));
    }

    [Theory]
    [InlineData(OriginalAudioMode.Replace)]
    [InlineData(OriginalAudioMode.QuietMix)]
    public async Task DubbedAudioIsEncodedAndUsesCorrectReplacementOrMixMapping(OriginalAudioMode audioMode)
    {
        using var fixture = new Fixture();
        fixture.Options.GenerateSubtitles = false;
        fixture.Options.GenerateSpeech = true;
        fixture.Options.OriginalAudio = audioMode;
        await fixture.RenderAsync();
        var render = Assert.Single(fixture.Runner.RenderCalls);
        Assert.Equal("copy", render.Value("-c:v"));
        Assert.Equal("aac", render.Value("-c:a"));
        Assert.Equal("192k", render.Value("-b:a"));
        Assert.Contains(fixture.DubbedAudio, render.Arguments);
        if (audioMode == OriginalAudioMode.QuietMix)
        {
            Assert.Contains("[0:5]volume=0.15[original]", render.Value("-filter_complex"));
            Assert.Contains("[original][1:a:0]amix=", render.Value("-filter_complex"));
            Assert.Contains("[mixed]", render.Arguments);
        }
        else
        {
            Assert.DoesNotContain("-filter_complex", render.Arguments);
            Assert.Contains("1:a:0", render.Arguments);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalAudioSelectionGuardIsPreserved(bool quietMix)
    {
        using var fixture = new Fixture();
        fixture.Options.GenerateSubtitles = false;
        fixture.Options.AudioStreamIndex = 99;
        fixture.Options.GenerateSpeech = quietMix;
        fixture.Options.OriginalAudio = OriginalAudioMode.QuietMix;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RenderAsync());
        Assert.Contains("所选原音轨不存在", exception.Message);
        Assert.Empty(fixture.Runner.RenderCalls);
    }

    [Fact]
    public async Task ProgressNamesEncoderAndShowsFiniteSpeedAndRemainingTime()
    {
        using var fixture = new Fixture();
        fixture.Runner.Encoders = Encoders("h264_nvenc");
        fixture.Runner.Render = async (call, token) =>
        {
            call.OnProgress?.Invoke(0);
            await Task.Delay(1100, token);
            call.OnProgress?.Invoke(30);
            await File.WriteAllBytesAsync(fixture.Output, [1, 2, 3], token);
            return "";
        };
        await fixture.RenderAsync();
        Assert.Contains(fixture.Progress.Items, item => item.Message.StartsWith("NVIDIA 显卡编码", StringComparison.Ordinal));
        var timed = Assert.Single(fixture.Progress.Items, item => item.Message.Contains("预计剩余"));
        Assert.Equal(25, timed.Percent);
        Assert.Contains("NVIDIA 显卡编码", timed.Message);
        Assert.Contains("倍速", timed.Message);
        Assert.DoesNotContain("NaN", timed.Message);
        Assert.DoesNotContain("Infinity", timed.Message);
        Assert.Equal(100, fixture.Progress.Items[^1].Percent);
    }

    [Fact]
    public async Task InvalidEncodingModeIsRejectedBeforeAnyToolRuns()
    {
        using var fixture = new Fixture();
        fixture.Settings.VideoEncoding = (VideoEncodingMode)99;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RenderAsync());
        Assert.Contains("编码方式", exception.Message);
        Assert.Empty(fixture.Runner.Calls);
    }

    public static IEnumerable<object[]> CpuOutputQualityCases()
    {
        foreach (var (quality, quantizer) in QualityCases())
            foreach (var mode in new[] { VideoEncodingMode.CpuFast, VideoEncodingMode.CpuQuality })
                yield return [mode, quality, quantizer];
    }

    public static IEnumerable<object[]> HardwareOutputQualityCases()
    {
        foreach (var (quality, quantizer) in QualityCases())
            foreach (var encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
                yield return [encoder, quality, quantizer];
    }

    private static (OutputVideoQuality Quality, string Quantizer)[] QualityCases() =>
    [
        (OutputVideoQuality.Original, "20"), (OutputVideoQuality.UltraHd2160, "18"),
        (OutputVideoQuality.FullHd1080, "20"), (OutputVideoQuality.Hd720, "23"),
        (OutputVideoQuality.Compact480, "26")
    ];

    [Theory]
    [MemberData(nameof(CpuOutputQualityCases))]
    public async Task EveryOutputQualityUsesItsOwnCpuQuantizerAndKeepsChosenPreset(
        VideoEncodingMode mode, OutputVideoQuality quality, string quantizer)
    {
        using var fixture = new Fixture();
        fixture.Settings.VideoEncoding = mode;
        fixture.Settings.OutputQuality = quality;
        await fixture.RenderAsync();
        var render = Assert.Single(fixture.Runner.RenderCalls);
        Assert.Equal("libx264", render.Value("-c:v"));
        Assert.Equal(quantizer, render.Value("-crf"));
        Assert.Equal(mode == VideoEncodingMode.CpuQuality ? "medium" : "veryfast", render.Value("-preset"));
        Assert.Contains(fixture.Progress.Items, item => item.Message.Contains(VideoQualityProfile.For(quality).Name));
    }

    [Theory]
    [MemberData(nameof(HardwareOutputQualityCases))]
    public async Task EveryOutputQualityReachesInitializationAndHardwareEncoderWithoutMutatingDefaults(
        string encoder, OutputVideoQuality quality, string quantizer)
    {
        using var fixture = new Fixture();
        fixture.Settings.OutputQuality = quality;
        fixture.Runner.Encoders = Encoders(encoder);
        await fixture.RenderAsync();
        var initialize = Assert.Single(fixture.Runner.InitializationCalls);
        var render = Assert.Single(fixture.Runner.RenderCalls);
        var quantizerOptions = encoder switch
        {
            "h264_nvenc" => new[] { "-cq" }, "h264_qsv" => ["-global_quality"],
            _ => ["-qp_i", "-qp_p", "-qp_b"]
        };
        foreach (var option in quantizerOptions)
        {
            Assert.Equal(quantizer, initialize.Value(option));
            Assert.Equal(quantizer, render.Value(option));
            var defaultPlan = Assert.Single(VideoEncoderSelector.HardwarePlans, plan => plan.Encoder == encoder);
            var defaultOptionIndex = defaultPlan.Arguments.ToList().IndexOf(option);
            Assert.Equal("20", defaultPlan.Arguments[defaultOptionIndex + 1]);
        }
        Assert.Equal(initialize.Value("-vf"), render.Value("-vf"));
        Assert.Equal(encoder, render.Value("-c:v"));
    }

    [Theory]
    [InlineData(OutputVideoQuality.UltraHd2160, "18")]
    [InlineData(OutputVideoQuality.FullHd1080, "20")]
    [InlineData(OutputVideoQuality.Hd720, "23")]
    [InlineData(OutputVideoQuality.Compact480, "26")]
    public async Task ResolutionLimitsRequireReencodingEvenWithoutBurningAndWithCompatibleSource(
        OutputVideoQuality quality, string quantizer)
    {
        using var fixture = new Fixture();
        fixture.Options.GenerateSubtitles = false;
        fixture.Settings.VideoEncoding = VideoEncodingMode.CpuFast;
        fixture.Settings.OutputQuality = quality;
        await fixture.RenderAsync();
        var render = Assert.Single(fixture.Runner.RenderCalls);
        Assert.Equal("libx264", render.Value("-c:v"));
        Assert.Equal(quantizer, render.Value("-crf"));
        Assert.Equal("copy", render.Value("-c:a"));
        Assert.Contains("scale=", render.Value("-vf"));
        Assert.DoesNotContain("subtitles=", render.Value("-vf"));
    }

    [Theory]
    [InlineData(OutputVideoQuality.UltraHd2160, "18")]
    [InlineData(OutputVideoQuality.Hd720, "23")]
    [InlineData(OutputVideoQuality.Compact480, "26")]
    public async Task RuntimeHardwareFallbackRetainsSelectedQualityAndFilter(OutputVideoQuality quality, string quantizer)
    {
        using var fixture = new Fixture();
        fixture.Settings.OutputQuality = quality;
        fixture.Runner.Encoders = Encoders("h264_nvenc");
        fixture.Runner.Render = async (call, token) =>
        {
            if (call.Value("-c:v") == "h264_nvenc") throw Failure("NVENC initialization failed");
            await File.WriteAllBytesAsync(fixture.Output, [1], token);
            return "";
        };
        await fixture.RenderAsync();
        var calls = fixture.Runner.RenderCalls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(quantizer, calls[0].Value("-cq"));
        Assert.Equal(quantizer, calls[1].Value("-crf"));
        Assert.Equal(calls[0].Value("-vf"), calls[1].Value("-vf"));
    }

    [Theory]
    [InlineData(OutputVideoQuality.UltraHd2160, "3840", "2160")]
    [InlineData(OutputVideoQuality.FullHd1080, "1920", "1080")]
    [InlineData(OutputVideoQuality.Hd720, "1280", "720")]
    [InlineData(OutputVideoQuality.Compact480, "854", "480")]
    public async Task OutputFilterResizesAndPadsBeforeDrawingSubtitlesAndLimitsActualFrameOrientation(
        OutputVideoQuality quality, string landscapeWidth, string landscapeHeight)
    {
        using var fixture = new Fixture();
        fixture.Settings.OutputQuality = quality;
        fixture.Settings.VideoEncoding = VideoEncodingMode.CpuFast;
        await fixture.RenderAsync();
        var filter = Assert.Single(fixture.Runner.RenderCalls).Value("-vf");
        var scaleIndex = filter.IndexOf("scale=", StringComparison.Ordinal);
        var padIndex = filter.IndexOf("pad=", StringComparison.Ordinal);
        var subtitleIndex = filter.IndexOf("subtitles=", StringComparison.Ordinal);
        Assert.True(scaleIndex >= 0 && scaleIndex < padIndex && padIndex < subtitleIndex);
        Assert.Contains($"if(gte(iw,ih),{landscapeWidth},{landscapeHeight})", filter);
        Assert.Contains($"if(gte(iw,ih),{landscapeHeight},{landscapeWidth})", filter);
        Assert.Contains("min(iw", filter);
        Assert.Contains("min(ih", filter);
        Assert.Contains("force_original_aspect_ratio=decrease", filter);
        Assert.DoesNotContain("setsar=1", filter);
    }

    [Fact]
    public async Task OriginalResolutionStillPadsBeforeSubtitlesWithoutAddingScale()
    {
        using var fixture = new Fixture();
        fixture.Settings.VideoEncoding = VideoEncodingMode.CpuFast;
        await fixture.RenderAsync();
        var filter = Assert.Single(fixture.Runner.RenderCalls).Value("-vf");
        Assert.StartsWith("pad=", filter);
        Assert.DoesNotContain("scale=", filter);
        Assert.Contains(",subtitles=", filter);
    }

    [Fact]
    public async Task InvalidOutputQualityIsRejectedBeforeAnyToolRuns()
    {
        using var fixture = new Fixture();
        fixture.Settings.OutputQuality = (OutputVideoQuality)99;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RenderAsync());
        Assert.Contains("输出画质", exception.Message);
        Assert.Empty(fixture.Runner.Calls);
    }

    private static string Encoders(params string[] names) => "Encoders:\n" +
        string.Join("\n", names.Select(name => $" V....D {name} hardware encoder")) + "\n";

    private static MediaProcessException Failure(string diagnostic, double processedSeconds = 0) =>
        new("fake process failure", diagnostic, processedSeconds);

    private sealed record MediaCall(string Executable, string[] Arguments, Action<double>? OnProgress, string? WorkingDirectory)
    {
        internal string Value(string option)
        {
            var index = Array.IndexOf(Arguments, option);
            Assert.InRange(index, 0, Arguments.Length - 2);
            return Arguments[index + 1];
        }
    }

    private sealed class FakeRunner
    {
        internal List<MediaCall> Calls { get; } = [];
        internal string Encoders { get; set; } = "";
        internal string VideoCodec { get; set; } = "h264";
        internal string AudioCodec { get; set; } = "aac";
        internal Func<CancellationToken, Task<string>>? Discover { get; set; }
        internal Func<MediaCall, CancellationToken, Task<string>>? Initialize { get; set; }
        internal Func<MediaCall, CancellationToken, Task<string>>? Render { get; set; }
        internal IEnumerable<MediaCall> InitializationCalls => Calls.Where(call => call.Arguments.Contains("-frames:v"));
        internal IEnumerable<MediaCall> RenderCalls => Calls.Where(call => call.Arguments.Contains("-progress"));

        internal async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token,
            Action<double>? onProgress = null, string? workingDirectory = null)
        {
            token.ThrowIfCancellationRequested();
            var call = new MediaCall(executable, arguments.ToArray(), onProgress, workingDirectory);
            Calls.Add(call);
            if (executable == "ffprobe-test")
                return JsonSerializer.Serialize(new
                {
                    streams = new object[]
                    {
                        new { index = 0, codec_type = "video", codec_name = "mjpeg", disposition = new { attached_pic = 1 } },
                        new { index = 2, codec_type = "video", codec_name = VideoCodec, width = 321, height = 181 },
                        new { index = 5, codec_type = "audio", codec_name = AudioCodec }
                    },
                    format = new { duration = "120" }
                });
            if (call.Arguments.Contains("-filters")) return " ... subtitles V->V Render subtitles using libass.\n";
            if (call.Arguments.Contains("-encoders")) return Discover is null ? Encoders : await Discover(token);
            if (call.Arguments.Contains("-frames:v")) return Initialize is null ? "" : await Initialize(call, token);
            if (call.Arguments.Contains("-progress"))
            {
                if (Render is not null) return await Render(call, token);
                onProgress?.Invoke(0);
                await File.WriteAllBytesAsync(call.Arguments[^1], [1, 2, 3], token);
                onProgress?.Invoke(120);
                return "";
            }
            throw new InvalidOperationException("Unexpected fake media command.");
        }
    }

    private sealed class ProgressRecorder : IProgress<ProcessingProgress>
    {
        internal List<ProcessingProgress> Items { get; } = [];
        public void Report(ProcessingProgress value) => Items.Add(value);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "linguaforge-encoder-" + Guid.NewGuid().ToString("N"));
        internal string Input => Path.Combine(Root, "原视频 O'Brien.mp4");
        internal string Subtitles => Path.Combine(Root, "字幕 O'Brien.srt");
        internal string DubbedAudio => Path.Combine(Root, "中文配音.wav");
        internal string Output => Path.Combine(Root, "output.mp4");
        internal FakeRunner Runner { get; } = new();
        internal MediaSettings Settings { get; } = new() { FfmpegPath = "ffmpeg-test", FfprobePath = "ffprobe-test" };
        internal ConversionOptions Options { get; } = new() { AudioStreamIndex = 5 };
        internal ProgressRecorder Progress { get; } = new();

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Input, [1]);
            File.WriteAllBytes(DubbedAudio, [1]);
            File.WriteAllText(Subtitles, "1\n00:00:01,000 --> 00:00:04,000\n中文字幕\n\n");
        }

        internal Task RenderAsync(CancellationToken token = default) =>
            new FfmpegMediaService(Runner.RunAsync).RenderAsync(new(Input, 120, 321, 181, [new(5, "AAC")]), Options,
                Options.GenerateSubtitles ? Subtitles : null, Options.GenerateSpeech ? DubbedAudio : null,
                Output, Settings, Progress, token);

        public void Dispose()
        {
            var ownedPath = Path.GetFullPath(Root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!ownedPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(ownedPath).StartsWith("linguaforge-encoder-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a directory not owned by this fixture.");
            if (Directory.Exists(ownedPath)) Directory.Delete(ownedPath, recursive: true);
        }
    }
}
