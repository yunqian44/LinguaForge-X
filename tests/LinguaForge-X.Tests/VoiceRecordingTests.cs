using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Recording;
using Xunit;

namespace LinguaForge_X.Tests;

public sealed class VoiceRecordingTests
{
    [Fact]
    public async Task RecordingRejectsConcurrentStartsAndStopReturnsAnOwnedTake()
    {
        using var files = new Files();
        using var started = new ManualResetEventSlim();
        using var service = new VoiceRecordingService(files.Library, (_, stop, _, token) =>
        {
            started.Set();
            if (WaitHandle.WaitAny([stop, token.WaitHandle], 5000) != 0) token.ThrowIfCancellationRequested();
            return RecordingAudio.Read(files.Audio(3));
        });
        var recording = service.RecordAsync(42);
        Assert.True(started.Wait(5000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync(42));
        await service.StopAsync();
        var take = await recording;
        Assert.True(take.Quality.CanUseAsReference);
        Assert.True(File.Exists(take.AudioPath));
    }

    [Fact]
    public async Task CancellingActiveCaptureLeavesNoTakeAndReleasesTheCaptureLock()
    {
        using var files = new Files();
        using var started = new ManualResetEventSlim();
        using var service = new VoiceRecordingService(files.Library, (_, stop, _, token) =>
        {
            started.Set();
            WaitHandle.WaitAny([stop, token.WaitHandle], 5000);
            token.ThrowIfCancellationRequested();
            return RecordingAudio.Read(files.Audio(3));
        });
        using var cancelled = new CancellationTokenSource();
        var recording = service.RecordAsync(42, cancellationToken: cancelled.Token);
        Assert.True(started.Wait(5000));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recording);
        Assert.False(Directory.Exists(files.Library));
        started.Reset();
        var retry = service.RecordAsync(42);
        Assert.True(started.Wait(5000));
        await service.StopAsync();
        Assert.True((await retry).Quality.CanUseAsReference);
    }

    [Fact]
    public async Task ImportKeepsAnIndependentOriginalAndReportsUsableReference()
    {
        using var files = new Files();
        var source = files.Audio(5);
        var original = await File.ReadAllBytesAsync(source);
        var take = await files.Service.ImportAsync(source);
        File.Delete(source);
        Assert.Equal(original, await File.ReadAllBytesAsync(take.OriginalAudioPath));
        Assert.True(take.Quality.CanUseForTraining);
        Assert.True(take.Quality.CanUseAsReference);
        Assert.Equal(5, take.Quality.DurationSeconds, 2);
        Assert.InRange(take.Quality.RmsDbfs, -20, -15);
        Assert.NotEqual(take.AudioPath, take.OriginalAudioPath);
    }

    [Theory]
    [InlineData(0.5, false, false)]
    [InlineData(2, true, false)]
    [InlineData(3, true, true)]
    [InlineData(10, true, true)]
    [InlineData(11, true, false)]
    [InlineData(45, true, false)]
    public async Task DurationRulesDistinguishTrainingFromReference(double duration, bool training, bool reference)
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(duration));
        Assert.Equal(training, take.Quality.CanUseForTraining);
        Assert.Equal(reference, take.Quality.CanUseAsReference);
    }

    [Fact]
    public async Task LongAndMalformedInputsAreRejectedWithoutPartialTakes()
    {
        using var files = new Files();
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.ImportAsync(files.Audio(45.01)));
        var wav = files.Audio(3);
        var bytes = await File.ReadAllBytesAsync(wav);
        await File.WriteAllBytesAsync(wav, bytes[..^2]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.ImportAsync(wav));
        Assert.False(Directory.Exists(Path.Combine(files.Library, "takes")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.001)]
    public async Task SilenceAndVeryQuietAudioCannotBeSavedAsTraining(double amplitude)
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(5, amplitude));
        Assert.False(take.Quality.CanUseForTraining);
        Assert.Contains("静音", take.Quality.Summary);
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.SaveSampleAsync(files.Draft(take)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.CalibrateAsync(take, new() { Normalize = true }));
    }

    [Fact]
    public async Task ClippingCannotBeHiddenByTurningTheFileVolumeDown()
    {
        using var files = new Files();
        var path = files.Audio(5);
        new RecordingAudio(16000, Enumerable.Range(0, 80000).Select(i => i % 2 == 0 ? 1f : -1f).ToArray()).Write(path + ".wav");
        var take = await files.Service.ImportAsync(path + ".wav");
        Assert.False(take.Quality.CanUseForTraining);
        var lowered = await files.Service.CalibrateAsync(take, new() { GainDb = -12 });
        Assert.False(lowered.Quality.CanUseForTraining);
        Assert.True(lowered.Quality.ClippedFraction >= .001);
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.SaveSampleAsync(files.Draft(lowered)));
    }

    [Fact]
    public async Task CalibrationCreatesANewVersionAndPreservesOriginalAcrossRepeatedEdits()
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(8, .03));
        var original = await File.ReadAllBytesAsync(take.OriginalAudioPath);
        var edited = await files.Service.CalibrateAsync(take, new() { StartSeconds = 1, EndSeconds = 6, Normalize = true });
        var editedAgain = await files.Service.CalibrateAsync(edited, new() { StartSeconds = .5, EndSeconds = 4.5, GainDb = -3 });
        Assert.Equal(5, edited.Quality.DurationSeconds);
        Assert.Equal(4, editedAgain.Quality.DurationSeconds);
        Assert.InRange(edited.Quality.RmsDbfs, -20.1, -19.9);
        Assert.Equal(original, await File.ReadAllBytesAsync(editedAgain.OriginalAudioPath));
        Assert.NotEqual(take.AudioPath, edited.AudioPath);
        Assert.NotEqual(edited.AudioPath, editedAgain.AudioPath);
        Assert.Equal(8, RecordingAudio.Read(take.AudioPath).Duration);
    }

    [Theory]
    [InlineData(-1, 3, 0)]
    [InlineData(3, 2, 0)]
    [InlineData(0, 10, 0)]
    [InlineData(0, .2, 0)]
    [InlineData(0, 3, 19)]
    [InlineData(0, 3, -25)]
    [InlineData(double.NaN, 3, 0)]
    public async Task InvalidCropAndGainAreRejected(double start, double end, double gain)
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.CalibrateAsync(take, new() { StartSeconds = start, EndSeconds = end, GainDb = gain }));
        Assert.Single(Directory.GetDirectories(Path.Combine(files.Library, "takes")));
    }

    [Fact]
    public async Task ExcessiveGainThatWouldClipIsRejected()
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(5, .5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.CalibrateAsync(take, new() { GainDb = 12 }));
    }

    [Fact]
    public async Task ConstantDcSignalIsNotMistakenForUsableSpeech()
    {
        using var files = new Files();
        var path = Path.Combine(files.Root, "dc.wav");
        new RecordingAudio(16000, Enumerable.Repeat(.3f, 80000).ToArray()).Write(path);
        var take = await files.Service.ImportAsync(path);
        Assert.False(take.Quality.CanUseForTraining);
        Assert.Contains("直流", take.Quality.Summary);
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.CalibrateAsync(take, new() { GainDb = -12 }));
    }

    [Fact]
    public void StereoDownmixDoesNotConcealAClippedChannel()
    {
        using var files = new Files();
        var path = Path.Combine(files.Root, "stereo.wav");
        var bytes = new byte[44 + 16000 * 4];
        using (var stream = new MemoryStream(bytes))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x46464952); writer.Write(bytes.Length - 8); writer.Write(0x45564157);
            writer.Write(0x20746d66); writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)2);
            writer.Write(16000); writer.Write(64000); writer.Write((ushort)4); writer.Write((ushort)16);
            writer.Write(0x61746164); writer.Write(16000 * 4);
            for (var i = 0; i < 16000; i++) { writer.Write(i % 2 == 0 ? short.MaxValue : short.MinValue); writer.Write((short)0); }
        }
        File.WriteAllBytes(path, bytes);
        var quality = RecordingAudio.Read(path).Analyze();
        Assert.True(quality.PeakDbfs < -5);
        Assert.True(quality.ClippedFraction >= .5);
        Assert.False(quality.CanUseForTraining);
    }

    [Fact]
    public async Task SavedSampleRetainsAudioAndTranscriptAfterDraftFilesAreRemoved()
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(5));
        var sample = await files.Service.SaveSampleAsync(files.Draft(take));
        File.Delete(take.AudioPath); File.Delete(take.OriginalAudioPath);
        var restored = Assert.Single(await files.Service.ListSamplesAsync());
        Assert.Equal(sample.Id, restored.Id);
        Assert.Equal("这是准确的逐字文稿。", restored.Text);
        Assert.True(File.Exists(restored.AudioPath));
        Assert.True(File.Exists(restored.OriginalAudioPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello|injection")]
    [InlineData("hello\nworld")]
    [InlineData("hello\tworld")]
    [InlineData("hello\0world")]
    public async Task TranscriptMustBeSafeForTrainingList(string text)
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(5));
        var draft = files.Draft(take); draft.Text = text;
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.SaveSampleAsync(draft));
        Assert.Empty(await files.Service.ListSamplesAsync());
    }

    [Fact]
    public async Task UnsupportedLanguageAndExcessiveMetadataAreRejected()
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(5));
        var draft = files.Draft(take); draft.Language = "fr-FR";
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.SaveSampleAsync(draft));
        draft.Language = "zh-CN"; draft.Text = new string('字', 1001);
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.SaveSampleAsync(draft));
        draft.Text = "文字"; draft.Name = new string('a', 81);
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.SaveSampleAsync(draft));
    }

    [Fact]
    public async Task DatasetHasUniqueNamesCorrectLanguageAbsolutePathsAndNoBom()
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(5));
        var first = await files.Service.SaveSampleAsync(files.Draft(take));
        var draft = files.Draft(take); draft.Language = "en-US"; draft.Text = "A second sentence.";
        var second = await files.Service.SaveSampleAsync(draft);
        var list = await files.Service.ExportDatasetAsync([first.Id, second.Id]);
        var bytes = await File.ReadAllBytesAsync(list);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }));
        var rows = (await File.ReadAllLinesAsync(list)).Select(row => row.Split('|')).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => { Assert.Equal(4, row.Length); Assert.True(Path.IsPathFullyQualified(row[0])); Assert.True(File.Exists(row[0])); });
        Assert.Equal("ZH", rows[0][2]); Assert.Equal("EN", rows[1][2]);
        Assert.NotEqual(Path.GetFileName(rows[0][0]), Path.GetFileName(rows[1][0]));
        Assert.Contains("本人", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task DatasetRejectsDifferentSpeakersDuplicateIdsMissingFilesAndTraversal()
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(5));
        var first = await files.Service.SaveSampleAsync(files.Draft(take));
        var draft = files.Draft(take); draft.Speaker = "另一人";
        var second = await files.Service.SaveSampleAsync(draft);
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.ExportDatasetAsync([first.Id, second.Id]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.ExportDatasetAsync([first.Id, first.Id]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.ExportDatasetAsync(["../source"]));
        File.Delete(first.AudioPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.ExportDatasetAsync([first.Id]));
        Assert.False(Directory.Exists(Path.Combine(files.Library, "datasets")));
    }

    [Fact]
    public async Task DatasetRevalidatesChangedAudioInsteadOfTrustingCachedQuality()
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(5));
        var sample = await files.Service.SaveSampleAsync(files.Draft(take));
        File.Delete(sample.AudioPath);
        new RecordingAudio(16000, new float[80000]).Write(sample.AudioPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.ExportDatasetAsync([sample.Id]));
    }

    [Fact]
    public async Task CancelBeforeImportSaveExportOrRecordCreatesNothingAndNeverOpensMicrophone()
    {
        using var files = new Files();
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.Service.ImportAsync(files.Audio(5), source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.Service.SaveSampleAsync(new(), source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.Service.ExportDatasetAsync([], source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.Service.RecordAsync(0, null, source.Token));
        Assert.False(Directory.Exists(files.Library));
    }

    [Fact]
    public async Task CorruptOrRedirectedLibraryMetadataIsNotLoaded()
    {
        using var files = new Files();
        var take = await files.Service.ImportAsync(files.Audio(5));
        var sample = await files.Service.SaveSampleAsync(files.Draft(take));
        var manifest = Path.Combine(Path.GetDirectoryName(sample.AudioPath)!, "sample.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
        node["AudioPath"] = take.AudioPath;
        await File.WriteAllTextAsync(manifest, node.ToJsonString());
        Assert.Empty(await files.Service.ListSamplesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.Service.ExportDatasetAsync([sample.Id]));
    }

    [Fact]
    public void WaveReaderRejectsDuplicateDataChunksAndNonFiniteFloatSamples()
    {
        using var files = new Files();
        var path = files.Audio(3);
        var bytes = File.ReadAllBytes(path);
        var duplicate = bytes.Concat(bytes[36..]).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(duplicate.AsSpan(4), (uint)duplicate.Length - 8);
        File.WriteAllBytes(path, duplicate);
        Assert.Throws<InvalidOperationException>(() => RecordingAudio.Read(path));
        var floatWav = new byte[48];
        bytes.AsSpan(0, 44).CopyTo(floatWav);
        BinaryPrimitives.WriteUInt32LittleEndian(floatWav.AsSpan(4), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(floatWav.AsSpan(20), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(floatWav.AsSpan(28), 64000);
        BinaryPrimitives.WriteUInt16LittleEndian(floatWav.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(floatWav.AsSpan(34), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(floatWav.AsSpan(40), 4);
        BinaryPrimitives.WriteInt32LittleEndian(floatWav.AsSpan(44), BitConverter.SingleToInt32Bits(float.NaN));
        File.WriteAllBytes(path, floatWav);
        Assert.Throws<InvalidOperationException>(() => RecordingAudio.Read(path));
    }

    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "lfx-recording-tests-" + Guid.NewGuid().ToString("N"));
        internal string Library => Path.Combine(Root, "library");
        internal VoiceRecordingService Service { get; }
        internal Files() { Directory.CreateDirectory(Root); Service = new(Library); }
        internal string Audio(double seconds, double amplitude = .2)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".wav");
            var samples = Enumerable.Range(0, (int)(16000 * seconds)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 220 * i / 16000))).ToArray();
            new RecordingAudio(16000, samples).Write(path);
            return path;
        }
        internal VoiceRecordingSampleDraft Draft(VoiceRecordingTake take) => new() { Take = take, Name = "录音样本", Text = "这是准确的逐字文稿。", Speaker = "本人", Language = "zh-CN" };
        public void Dispose() { Service.Dispose(); Directory.Delete(Root, true); }
    }
}
