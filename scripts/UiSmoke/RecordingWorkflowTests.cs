using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Presentation.Wpf;
using LinguaForge_X.Presentation.Wpf.Controls;
using LinguaForge_X.Presentation.Wpf.ViewModels;

internal static partial class Program
{
    private static async Task VerifyRecordingWorkflowAsync(string output)
    {
        var stub = new StubServices(output);
        var recording = new RecordingStub(output);
        var vm = new MainViewModel(stub, stub, stub, stub, stub, stub, stub, stub, stub, recording: recording);
        await vm.LoadAsync();
        Assert(recording.RecordCalls == 0 && vm.RecordingDevices.Count == 1 && vm.RecordingDevice is not null,
            "loading microphone metadata never starts recording");
        Assert(vm.StartRecordingCommand.CanExecute(null) && !vm.StopRecordingCommand.CanExecute(null) && !vm.SaveRecordingCommand.CanExecute(null),
            "recording commands begin in a safe idle state");
        vm.StartRecordingCommand.Execute(null);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert(vm.IsBusy && vm.IsRecording && vm.StopRecordingCommand.CanExecute(null) && vm.CancelRecordingCommand.CanExecute(null),
            "recording holds the global busy state while stop and cancel stay available");
        Assert(!vm.ImportCommand.CanExecute(null) && !vm.ImportRecordingCommand.CanExecute(null) && !vm.SaveSettingsCommand.CanExecute(null),
            "other processing and configuration cannot race with microphone capture");
        Assert(vm.RecordingMeter.Contains("45") && vm.RecordingLevel > 0, "microphone duration and level progress reaches the UI");
        vm.CancelRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(!vm.HasRecording && !vm.IsRecording && recording.Cancellations == 1, "first recording cancellation releases capture without registering a partial take");

        vm.StartRecordingCommand.Execute(null);
        vm.StopRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.HasRecording && vm.RecordingQuality.Contains("6.00") && !vm.RecordingReviewed,
            "explicit stop publishes the completed quality-checked take and requires review");
        var firstQuality = vm.RecordingQuality;
        recording.FailCapture = true;
        vm.StartRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.RecordingQuality == firstQuality && vm.RecordingStatus.Contains("模拟设备断开"), "capture failure preserves the last good take with an actionable error");
        recording.FailCapture = false;
        vm.StartRecordingCommand.Execute(null);
        vm.CancelRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.RecordingQuality == firstQuality && recording.Cancellations == 2, "retake cancellation preserves the previous complete audio");

        vm.RecordingName = "本人中文录音";
        vm.RecordingSpeaker = "本人";
        vm.RecordingText = "你好，这是经过校对的本人录音。";
        vm.RecordingReviewed = true;
        Assert(vm.SaveRecordingCommand.CanExecute(null) && vm.UseRecordingAsReferenceCommand.CanExecute(null), "reviewed valid 6 second audio supports saving and reference use");
        vm.UseRecordingAsReferenceCommand.Execute(null);
        Assert(vm.LocalVoiceDraft.Name == vm.RecordingName && vm.LocalVoiceDraft.ReferenceText == vm.RecordingText && vm.LocalVoiceDraft.ReferenceAudioPath == recording.LastCaptured!.AudioPath,
            "reference selection fills voice name, exact transcript and audio without starting training");
        vm.RecordingText = "改过的文稿";
        Assert(!vm.RecordingReviewed && !vm.SaveRecordingCommand.CanExecute(null), "transcript changes invalidate the previous human review");
        vm.RecordingText = "不允许|破坏训练清单";
        vm.RecordingReviewed = true;
        vm.SaveRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(recording.SaveCalls == 0 && vm.RecordingStatus.Contains("竖线"), "invalid transcript delimiters are rejected before library persistence");
        vm.RecordingText = "你好，这是经过校对的本人录音。";
        vm.RecordingStart = "NaN";
        vm.CalibrateRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(recording.CalibrationCalls == 0 && vm.RecordingQuality == firstQuality && vm.RecordingStatus.Contains("有限数字"),
            "non-finite calibration values are rejected without losing audio or calling the processor");
        vm.RecordingStart = "1";
        vm.RecordingEnd = "5";
        vm.RecordingReviewed = true;
        Assert(vm.HasUnappliedRecordingCalibration && !vm.SaveRecordingCommand.CanExecute(null) && !vm.UseRecordingAsReferenceCommand.CanExecute(null),
            "unapplied calibration edits cannot be mistaken for the saved or reference audio");
        vm.CalibrateRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.RecordingQuality.Contains("4.00") && !vm.HasUnappliedRecordingCalibration && !vm.RecordingReviewed,
            "calibration creates a new four second version requiring fresh listening and transcript review");
        var calibratedQuality = vm.RecordingQuality;
        vm.RecordingGain = "3";
        recording.FailCalibration = true;
        vm.CalibrateRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.RecordingQuality == calibratedQuality && vm.RecordingStatus.Contains("模拟削波"), "failed recalibration preserves the previous successful calibrated version");
        recording.FailCalibration = false;
        vm.RecordingGain = "19";
        var callsBeforeInvalidGain = recording.CalibrationCalls;
        vm.CalibrateRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(recording.CalibrationCalls == callsBeforeInvalidGain && vm.RecordingStatus.Contains("18"), "excessive gain is rejected before audio processing");
        vm.RecordingGain = "0";
        vm.RecordingReviewed = true;
        vm.SaveRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(recording.SaveCalls == 1 && vm.RecordingSamples.Count == 1 && vm.SelectedRecording is not null, "saving registers the checked calibrated audio in the local library");
        vm.RecordingName = "本人中文录音第二条";
        vm.RecordingReviewed = true;
        vm.SaveRecordingCommand.Execute(null);
        await WaitIdle(vm);
        foreach (var sample in vm.RecordingSamples) sample.IsSelected = true;
        Assert(vm.ExportRecordingDatasetCommand.CanExecute(null) && vm.RecordingSelectionSummary.Contains("2"), "training dataset selection supports multiple saved recordings");
        vm.ExportRecordingDatasetCommand.Execute(null);
        await WaitIdle(vm);
        Assert(recording.LastExportIds?.Count == 2 && vm.LocalDatasetPath.EndsWith("recording-stub.list", StringComparison.Ordinal),
            "selected recordings export a local dataset and populate the existing training workflow");
        vm.RestoreOriginalRecordingCommand.Execute(null);
        Assert(vm.RecordingQuality == firstQuality && !vm.RecordingReviewed && !vm.HasUnappliedRecordingCalibration,
            "restoring the original resets trim and gain without overwriting saved library items");

        vm.SelectedRecording = vm.RecordingSamples[0];
        recording.FailImport = true;
        vm.LoadRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.RecordingQuality == firstQuality && vm.RecordingStatus.Contains("模拟素材缺失"), "missing library media does not replace the current editing take");
        recording.FailImport = false;
        vm.LoadRecordingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.RecordingQuality.Contains("4.00") && vm.RecordingText == recording.Samples[0].Text,
            "loading a library sample restores its exact audio metadata and transcript for re-editing");
        vm.RestoreOriginalRecordingCommand.Execute(null);
        Assert(vm.RecordingQuality == firstQuality, "saved calibrated samples retain their actual original audio for later comparison and restoration");
        vm.StartRecordingCommand.Execute(null);
        await vm.CancelAndWaitAsync();
        Assert(!vm.IsBusy && !vm.IsRecording && recording.Cancellations == 3, "application shutdown awaits microphone cancellation and preserves completed samples");

        vm.SelectedTab = 3;
        var window = new MainWindow(vm) { Width = 1440, Height = 1000 };
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var panel = VisualChildren<VoiceRecordingPanel>(window).Single();
        var localVoice = VisualChildren<LocalVoicePanel>(window).Single();
        var scroll = VisualChildren<ScrollViewer>(localVoice).First();
        await ScrollRecordingIntoViewAsync(window, scroll, panel);
        Capture(window, Path.Combine(output, "recording-workflow.png"));
        window.Width = 1120;
        window.Height = 720;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await ScrollRecordingIntoViewAsync(window, scroll, panel);
        Assert(((ComboBox)panel.FindName("RecordingDevicePicker")).ActualWidth >= 120 && ((TextBox)panel.FindName("RecordingTranscript")).ActualWidth >= 250,
            "minimum-size window retains usable microphone and transcript editing widths");
        Capture(window, Path.Combine(output, "recording-workflow-minimum.png"));
        await ScrollRecordingIntoViewAsync(window, scroll, (FrameworkElement)panel.FindName("RecordingTrimStart"), 84);
        Capture(window, Path.Combine(output, "recording-calibration-minimum.png"));
        var library = (Expander)panel.FindName("RecordingLibraryExpander");
        library.IsExpanded = true;
        await ScrollRecordingIntoViewAsync(window, scroll, library);
        Capture(window, Path.Combine(output, "recording-library-minimum.png"));
        window.Close();
    }

    private static async Task ScrollRecordingIntoViewAsync(Window window, ScrollViewer scroll, FrameworkElement target, double topSpace = 12)
    {
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        // Use content coordinates: BringIntoView may be consumed by nested controls and leave the outer page unchanged.
        var content = (FrameworkElement)scroll.Content;
        var top = target.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
        var offset = Math.Clamp(top - topSpace, 0, scroll.ScrollableHeight);
        scroll.ScrollToVerticalOffset(offset);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        var visibleTop = target.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
        Assert(Math.Abs(scroll.VerticalOffset - offset) < 2 && visibleTop >= 0 && visibleTop < scroll.ViewportHeight,
            "recording screenshot target is actually visible inside the scrolled viewport");
    }

    private sealed class RecordingStub(string output) : IVoiceRecordingService
    {
        private TaskCompletionSource<VoiceRecordingTake>? _capture;
        private int _takeSequence;
        private readonly Dictionary<string, double> _audioDurations = new(StringComparer.OrdinalIgnoreCase);
        public int RecordCalls { get; private set; }
        public int Cancellations { get; private set; }
        public int CalibrationCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public bool FailCapture { get; set; }
        public bool FailCalibration { get; set; }
        public bool FailImport { get; set; }
        public VoiceRecordingTake? LastCaptured { get; private set; }
        public List<VoiceRecordingSample> Samples { get; } = [];
        public IReadOnlyList<string>? LastExportIds { get; private set; }

        public Task<IReadOnlyList<VoiceRecordingDevice>> ListDevicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<VoiceRecordingDevice>>([new(0, "UI smoke microphone · no hardware capture")]);
        public async Task<VoiceRecordingTake> RecordAsync(int deviceId, IProgress<VoiceRecordingProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            RecordCalls++;
            if (FailCapture) throw new IOException("模拟设备断开");
            _capture = new(TaskCreationOptions.RunContinuationsAsynchronously);
            progress?.Report(new VoiceRecordingProgress(1, -9, -23, 0.35));
            using var registration = cancellationToken.Register(() => _capture.TrySetCanceled(cancellationToken));
            try { return await _capture.Task; }
            catch (OperationCanceledException) { Cancellations++; throw; }
            finally { _capture = null; }
        }
        public Task StopAsync(CancellationToken ct = default)
        {
            LastCaptured = CreateTake(6);
            _capture?.TrySetResult(LastCaptured);
            return Task.CompletedTask;
        }
        public Task<VoiceRecordingTake> ImportAsync(string audioPath, CancellationToken ct = default)
        {
            if (FailImport) throw new IOException("模拟素材缺失");
            return Task.FromResult(CreateTake(_audioDurations[audioPath]));
        }
        public Task<VoiceRecordingTake> CalibrateAsync(VoiceRecordingTake take, VoiceRecordingCalibration calibration, CancellationToken ct = default)
        {
            CalibrationCalls++;
            if (FailCalibration) throw new IOException("模拟削波，校准被拒绝");
            return Task.FromResult(CreateTake((calibration.EndSeconds ?? take.Quality.DurationSeconds) - calibration.StartSeconds, take.OriginalAudioPath));
        }
        public Task<VoiceRecordingSample> SaveSampleAsync(VoiceRecordingSampleDraft draft, CancellationToken ct = default)
        {
            SaveCalls++;
            var sample = new VoiceRecordingSample { Id = Guid.NewGuid().ToString("N"), Name = draft.Name, Speaker = draft.Speaker, Text = draft.Text, Language = draft.Language, AudioPath = draft.Take.AudioPath, OriginalAudioPath = draft.Take.OriginalAudioPath, Quality = draft.Take.Quality };
            Samples.Add(sample);
            return Task.FromResult(sample);
        }
        public Task<IReadOnlyList<VoiceRecordingSample>> ListSamplesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<VoiceRecordingSample>>(Samples.ToArray());
        public Task<string> ExportDatasetAsync(IReadOnlyList<string> sampleIds, CancellationToken ct = default)
        {
            LastExportIds = sampleIds.ToArray();
            return Task.FromResult(Path.Combine(output, "recording-stub.list"));
        }
        private VoiceRecordingTake CreateTake(double duration, string? originalPath = null)
        {
            var path = Path.Combine(output, $"recording-stub-{++_takeSequence}.wav");
            _audioDurations[path] = duration;
            return new VoiceRecordingTake { Id = Guid.NewGuid().ToString("N"), AudioPath = path, OriginalAudioPath = originalPath ?? path, Quality = new VoiceRecordingQuality { DurationSeconds = duration, SampleRate = 48000, Channels = 1, PeakDbfs = -9, RmsDbfs = -23, SilentFraction = .1, ClippedFraction = 0, CanUseForTraining = true, CanUseAsReference = duration is >= 3 and <= 10 } };
        }
    }
}
