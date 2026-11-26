using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using Microsoft.Win32;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

/// <summary>
/// Manages local microphone recording, WAV import, audio trim/gain calibration, quality checking,
/// and training dataset export.
/// </summary>
public sealed class VoiceRecordingViewModel : ObservableObject
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly LocalVoiceViewModel _localVoice;
    private readonly IVoiceRecordingService? _recording;
    private VoiceRecordingDevice? _recordingDevice;
    private VoiceRecordingTake? _recordingTake;
    private VoiceRecordingTake? _recordingOriginal;
    private RecordingLibraryItem? _selectedRecording;
    private bool _isRecording;
    private bool _recordingStopping;
    private bool _recordingPlaying;
    private bool _recordingReviewed;
    private bool _recordingNormalize;
    private double _recordingLevel;
    private string _recordingMeter = "尚未开始录音";
    private string _recordingStatus = "录音只在点击「开始录音」后进行。录音、校准和训练素材均保存在本机。";
    private string _recordingName = "";
    private string _recordingSpeaker = "本人";
    private string _recordingText = "";
    private string _recordingLanguage = "zh-CN";
    private string _recordingStart = "0";
    private string _recordingEnd = "";
    private string _recordingGain = "0";
    private (string Start, string End, string Gain, bool Normalize) _recordingAppliedCalibration;

    public VoiceRecordingViewModel(WorkspaceCoordinator workspace, LocalVoiceViewModel localVoice,
        IVoiceRecordingService? recording)
    {
        _workspace = workspace;
        _localVoice = localVoice;
        _recording = recording;

        RefreshRecordingsCommand = new(_ => RunRecordingAsync("刷新麦克风与录音库", RefreshRecordingLibraryCoreAsync),
            _ => CanEditRecording);

        StartRecordingCommand = new(_ => RunRecordingAsync("录制本人声音", async ct =>
        {
            var device = RecordingDevice ?? throw new InvalidOperationException("请先选择可用的麦克风；可点击「刷新设备与录音库」。");
            StopRecordingPreview();
            RecordingCaptureStarting?.Invoke();
            IsRecording = true;
            _recordingStopping = false;
            RecordingLevel = 0;
            RecordingMeter = "正在打开麦克风…";
            RecordingStatus = "正在录音，请按文稿自然朗读。点击「停止并检查」结束；最长 45 秒自动结束。";
            var operation = _workspace.CurrentCancellation;
            var progress = new Progress<VoiceRecordingProgress>(p =>
            {
                if (!IsRecording || operation != _workspace.CurrentCancellation) return;
                RecordingLevel = Math.Clamp(p.Level * 100, 0, 100);
                RecordingMeter = $"{p.DurationSeconds:F1} / {p.MaximumSeconds} 秒 · 峰值 {FormatDb(p.PeakDbfs)} dBFS · 平均 {FormatDb(p.RmsDbfs)} dBFS";
                _workspace.Progress = Math.Clamp(p.DurationSeconds / p.MaximumSeconds * 100, 0, 100);
            });
            try
            {
                var take = await RequiredRecording().RecordAsync(device.Id, progress, ct);
                SetRecordingTake(take, true);
                if (string.IsNullOrWhiteSpace(RecordingName)) RecordingName = "本人录音 " + DateTime.Now.ToString("MMdd HHmmss");
                RecordingStatus = "录音已完成。请先试听，再裁剪、校准并校对文稿；重新录音失败或取消会保留本次录音。";
            }
            finally
            {
                IsRecording = false;
                _recordingStopping = false;
                RecordingLevel = 0;
                RefreshCommands();
            }
        }), _ => CanEditRecording && RecordingDevice is not null);

        StopRecordingCommand = new(_ => StopRecordingCaptureAsync(), _ => IsRecording && !_recordingStopping);

        CancelRecordingCommand = new(_ =>
        {
            RecordingStatus = "正在取消本次录音并释放麦克风…";
            _workspace.Cancel();
        }, _ => IsRecording);

        ImportRecordingCommand = new(_ => ImportRecordingAsync(), _ => CanEditRecording);

        CalibrateRecordingCommand = new(_ => RunRecordingAsync("校准录音", async ct =>
        {
            var original = _recordingOriginal ?? throw new InvalidOperationException("请先录制或导入音频。");
            var calibration = ParseRecordingCalibration(original.Quality.DurationSeconds);
            var take = await RequiredRecording().CalibrateAsync(original, calibration, ct);
            SetRecordingTake(take, false);
            RecordingStatus = "校准版本已生成。原始录音已保留，请分别试听比较，并按保留的内容重新校对文稿。";
        }), _ => CanEditRecording && HasRecording);

        RestoreOriginalRecordingCommand = new(_ =>
        {
            if (_recordingOriginal is not { } original) return;
            StopRecordingPreview();
            SetRecordingTake(original, true);
            RecordingStatus = "已恢复原始录音。请重新试听并确认文稿。";
        }, _ => CanEditRecording && HasRecording);

        PlayOriginalRecordingCommand = new(_ => PlayRecordingAudio(RecordingOriginalPath),
            _ => CanEditRecording && _recordingOriginal is not null);
        PlayRecordingCommand = new(_ => PlayRecordingAudio(_recordingTake?.AudioPath),
            _ => CanEditRecording && HasRecording);
        StopRecordingPlaybackCommand = new(_ => StopRecordingPreview(), _ => _recordingPlaying);

        SaveRecordingCommand = new(_ => RunRecordingAsync("保存录音素材", async ct =>
        {
            var draft = CreateRecordingDraft();
            var sample = await RequiredRecording().SaveSampleAsync(draft, ct);
            await RefreshRecordingSamplesCoreAsync(ct);
            SelectedRecording = RecordingSamples.FirstOrDefault(x => x.Sample.Id == sample.Id);
            RecordingStatus = "已保存到本机录音库。可勾选多条素材生成训练清单，或把 3–10 秒录音用作参考音色。";
        }), _ => CanEditRecording && !HasUnappliedRecordingCalibration && RecordingReviewed && _recordingTake?.Quality.CanUseForTraining == true && RecordingMetadataComplete);

        UseRecordingAsReferenceCommand = new(_ => _workspace.ExecuteUiAction(() =>
        {
            var draft = CreateRecordingDraft();
            if (!draft.Take.Quality.CanUseAsReference) throw new InvalidOperationException("参考音色需要 3–10 秒且音量检查通过的录音，请先裁剪或重新录制。");
            _localVoice.LocalVoiceDraft = new LocalVoiceDraft { Name = draft.Name, Language = draft.Language, ReferenceAudioPath = draft.Take.AudioPath, ReferenceText = draft.Text };
            RecordingStatus = "已填入下方「添加本人音色」。点击「保存为新音色」，再中文试听；这一步不会自动训练模型。";
            _localVoice.LocalVoiceStatus = RecordingStatus;
            _workspace.Status = RecordingStatus;
        }), _ => CanEditRecording && !HasUnappliedRecordingCalibration && RecordingReviewed && _recordingTake?.Quality.CanUseAsReference == true && RecordingMetadataComplete);

        LoadRecordingCommand = new(_ => RunRecordingAsync("载入已保存录音", async ct =>
        {
            var sample = SelectedRecording?.Sample ?? throw new InvalidOperationException("请先选择一条录音素材。");
            var take = await RequiredRecording().ImportAsync(sample.AudioPath, ct);
            var original = string.IsNullOrWhiteSpace(sample.OriginalAudioPath) || string.Equals(sample.AudioPath, sample.OriginalAudioPath, StringComparison.OrdinalIgnoreCase)
                ? take : await RequiredRecording().ImportAsync(sample.OriginalAudioPath, ct);
            SetRecordingTake(original, true);
            SetRecordingTake(take, false);
            RecordingName = sample.Name;
            RecordingSpeaker = sample.Speaker;
            RecordingText = sample.Text;
            RecordingLanguage = sample.Language;
            RecordingStatus = "已载入所选素材及其原始录音，可以分别试听、再次校准。保存会新增一条素材，保留原记录。";
        }), _ => CanEditRecording && SelectedRecording is not null);

        ExportRecordingDatasetCommand = new(_ => RunRecordingAsync("生成本地训练清单", async ct =>
        {
            var selected = RecordingSamples.Where(x => x.IsSelected).Select(x => x.Sample).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("请先勾选训练素材。");
            if (selected.Select(x => x.Speaker).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                throw new InvalidOperationException("一次训练应只包含同一说话人的录音，请检查所选素材的说话人名称。");
            _localVoice.LocalDatasetPath = await RequiredRecording().ExportDatasetAsync(selected.Select(x => x.Id).ToArray(), ct);
            RecordingStatus = $"已将 {selected.Length} 条本机素材生成 .list，并填入下方训练清单。请检查参考音色和轮数后，点击「开始本地训练」。";
            _localVoice.LocalVoiceStatus = RecordingStatus;
        }), _ => CanEditRecording && RecordingSamples.Any(x => x.IsSelected));
    }

    public ObservableCollection<VoiceRecordingDevice> RecordingDevices { get; } = [];
    public ObservableCollection<RecordingLibraryItem> RecordingSamples { get; } = [];
    public VoiceRecordingDevice? RecordingDevice { get => _recordingDevice; set { Set(ref _recordingDevice, value); RefreshCommands(); } }
    public RecordingLibraryItem? SelectedRecording { get => _selectedRecording; set { Set(ref _selectedRecording, value); RefreshCommands(); } }
    public bool IsRecording { get => _isRecording; private set { Set(ref _isRecording, value); RefreshCommands(); } }
    public bool HasRecording => _recordingTake is not null;
    public bool CanEditRecording => !_workspace.IsBusy && _recording is not null;
    public double RecordingLevel { get => _recordingLevel; private set => Set(ref _recordingLevel, value); }
    public string RecordingMeter { get => _recordingMeter; private set => Set(ref _recordingMeter, value); }
    public string RecordingStatus { get => _recordingStatus; private set => Set(ref _recordingStatus, value); }
    public string RecordingName { get => _recordingName; set { if (Set(ref _recordingName, value)) InvalidateRecordingReview(); } }
    public string RecordingSpeaker { get => _recordingSpeaker; set { if (Set(ref _recordingSpeaker, value)) InvalidateRecordingReview(); } }
    public string RecordingText { get => _recordingText; set { if (Set(ref _recordingText, value)) InvalidateRecordingReview(); } }
    public string RecordingLanguage { get => _recordingLanguage; set { if (Set(ref _recordingLanguage, value)) InvalidateRecordingReview(); } }
    public string RecordingStart { get => _recordingStart; set { if (Set(ref _recordingStart, value)) NotifyRecordingCalibrationChanged(); } }
    public string RecordingEnd { get => _recordingEnd; set { if (Set(ref _recordingEnd, value)) NotifyRecordingCalibrationChanged(); } }
    public string RecordingGain { get => _recordingGain; set { if (Set(ref _recordingGain, value)) NotifyRecordingCalibrationChanged(); } }
    public bool RecordingNormalize { get => _recordingNormalize; set { if (Set(ref _recordingNormalize, value)) NotifyRecordingCalibrationChanged(); } }
    public bool RecordingReviewed { get => _recordingReviewed; set { Set(ref _recordingReviewed, value); RefreshCommands(); } }

    public string RecordingQuality => _recordingTake is not { } take ? "暂无录音。建议安静环境、麦克风距嘴 15–20 厘米，保持自然音量。" :
        $"{take.Quality.DurationSeconds:F2} 秒 · {take.Quality.SampleRate:N0} Hz · {take.Quality.Channels} 声道\n" +
        $"峰值 {FormatDb(take.Quality.PeakDbfs)} dBFS · 平均 {FormatDb(take.Quality.RmsDbfs)} dBFS · 静音 {take.Quality.SilentFraction:P0} · 削波 {take.Quality.ClippedFraction:P2}\n" +
        take.Quality.Summary + $"\n参考音色：{(take.Quality.CanUseAsReference ? "可用" : "需处理（时长应为 3–10 秒）")}；训练素材：{(take.Quality.CanUseForTraining ? "可用" : "需处理")}";

    public string RecordingOriginalSummary => _recordingOriginal is null ? "" : $"裁剪时间以原始录音为基准（{_recordingOriginal.Quality.DurationSeconds:F2} 秒）。每次校准都从原始录音生成新版本，不累积增益。";
    public string RecordingSelectionSummary => $"录音库 {RecordingSamples.Count} 条 · 已勾选 {RecordingSamples.Count(x => x.IsSelected)} 条训练素材";
    public bool HasUnappliedRecordingCalibration => HasRecording && _recordingAppliedCalibration != (RecordingStart, RecordingEnd, RecordingGain, RecordingNormalize);
    public string RecordingCalibrationHint => !HasRecording ? "录音完成或导入后，可按原始时间裁剪并校准音量。" : HasUnappliedRecordingCalibration ? "校准参数已修改，尚未应用。请先点击「应用校准并检查」，或恢复原始版本后再保存。" : "当前版本已完成检查，试听的是最近一次成功生成的版本。";

    public event Action? RecordingCaptureStarting;
    public event Action<string>? RecordingPreviewRequested;
    public event Action? RecordingPreviewStopRequested;

    public AsyncCommand RefreshRecordingsCommand { get; }
    public AsyncCommand StartRecordingCommand { get; }
    public AsyncCommand StopRecordingCommand { get; }
    public RelayCommand CancelRecordingCommand { get; }
    public AsyncCommand ImportRecordingCommand { get; }
    public AsyncCommand CalibrateRecordingCommand { get; }
    public RelayCommand RestoreOriginalRecordingCommand { get; }
    public RelayCommand PlayOriginalRecordingCommand { get; }
    public RelayCommand PlayRecordingCommand { get; }
    public RelayCommand StopRecordingPlaybackCommand { get; }
    public AsyncCommand SaveRecordingCommand { get; }
    public RelayCommand UseRecordingAsReferenceCommand { get; }
    public AsyncCommand LoadRecordingCommand { get; }
    public AsyncCommand ExportRecordingDatasetCommand { get; }

    private bool RecordingMetadataComplete => !string.IsNullOrWhiteSpace(RecordingName) && !string.IsNullOrWhiteSpace(RecordingSpeaker) && !string.IsNullOrWhiteSpace(RecordingText) && !string.IsNullOrWhiteSpace(RecordingLanguage);
    private string? RecordingOriginalPath => !string.IsNullOrWhiteSpace(_recordingOriginal?.OriginalAudioPath) ? _recordingOriginal.OriginalAudioPath : _recordingOriginal?.AudioPath;
    private IVoiceRecordingService RequiredRecording() => _recording ?? throw new InvalidOperationException("当前程序没有加载录音服务，请使用完整版本。");
    private static string FormatDb(double value) => double.IsFinite(value) ? value.ToString("F1", CultureInfo.CurrentCulture) : "−∞";
    private void InvalidateRecordingReview() { RecordingReviewed = false; RefreshCommands(); }

    private void NotifyRecordingCalibrationChanged()
    {
        InvalidateRecordingReview();
        OnPropertyChanged(nameof(HasUnappliedRecordingCalibration));
        OnPropertyChanged(nameof(RecordingCalibrationHint));
    }

    private void SetRecordingTake(VoiceRecordingTake take, bool replaceOriginal)
    {
        _recordingTake = take;
        if (replaceOriginal)
        {
            _recordingOriginal = take;
            RecordingStart = "0";
            RecordingEnd = take.Quality.DurationSeconds.ToString("0.###", CultureInfo.CurrentCulture);
            RecordingGain = "0";
            RecordingNormalize = false;
        }
        _recordingAppliedCalibration = (RecordingStart, RecordingEnd, RecordingGain, RecordingNormalize);
        OnPropertyChanged(nameof(HasUnappliedRecordingCalibration));
        OnPropertyChanged(nameof(RecordingCalibrationHint));
        RecordingReviewed = false;
        RecordingMeter = $"已完成 · {take.Quality.DurationSeconds:F2} 秒";
        OnPropertyChanged(nameof(HasRecording));
        OnPropertyChanged(nameof(RecordingQuality));
        OnPropertyChanged(nameof(RecordingOriginalSummary));
        RefreshCommands();
    }

    private Task RunRecordingAsync(string stage, Func<CancellationToken, Task> action) => _workspace.RunAsync(stage, async ct =>
    {
        StopRecordingPreview();
        try { await action(ct); _workspace.Status = RecordingStatus; }
        catch (OperationCanceledException) { RecordingStatus = "操作已取消，上一次完成的录音仍保留，可继续试听或校准。"; throw; }
        catch (Exception ex) { RecordingStatus = "需要处理：" + _workspace.SafeMessage(ex) + " 上一次完成的录音已保留。"; throw; }
    });

    private async Task StopRecordingCaptureAsync()
    {
        if (!IsRecording || _recordingStopping) return;
        _recordingStopping = true;
        RefreshCommands();
        RecordingStatus = "正在结束录音、释放麦克风并检查音频…";
        try { await RequiredRecording().StopAsync(); }
        catch (Exception ex) { _recordingStopping = false; RecordingStatus = "停止录音失败：" + _workspace.SafeMessage(ex) + "，可点击「取消本次」。"; }
        finally { RefreshCommands(); }
    }

    private async Task ImportRecordingAsync()
    {
        var dialog = new OpenFileDialog { Title = "导入本人录音（仅保存本机，不上传云端）", Filter = "WAV 录音|*.wav", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        await RunRecordingAsync("导入并检查录音", async ct =>
        {
            var take = await RequiredRecording().ImportAsync(dialog.FileName, ct);
            SetRecordingTake(take, true);
            RecordingName = Path.GetFileNameWithoutExtension(dialog.FileName);
            RecordingText = "";
            RecordingStatus = "已导入本机录音。请填写准确文稿并试听；导入内容未发送到云端。";
        });
    }

    private VoiceRecordingCalibration ParseRecordingCalibration(double duration)
    {
        static double Parse(string text, string label)
        {
            if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) || !double.IsFinite(value))
                throw new InvalidOperationException(label + "必须是有限数字。");
            return value;
        }
        var start = Parse(RecordingStart, "开始时间");
        var end = string.IsNullOrWhiteSpace(RecordingEnd) ? duration : Parse(RecordingEnd, "结束时间");
        var gain = Parse(RecordingGain, "增益");
        if (start < 0 || start >= end || end > duration + 0.001) throw new InvalidOperationException("裁剪范围无效：开始时间须不小于 0，结束时间应大于开始时间，且不超过原始录音时长。");
        if (end - start < 1) throw new InvalidOperationException("裁剪后的录音至少需要 1 秒；用作参考音色时需要 3–10 秒。");
        if (gain is < -24 or > 18) throw new InvalidOperationException("增益需在 −24 至 +18 dB 之间；过小或削波的录音应重新录制。");
        return new VoiceRecordingCalibration { StartSeconds = start, EndSeconds = Math.Min(end, duration), GainDb = gain, Normalize = RecordingNormalize };
    }

    private VoiceRecordingSampleDraft CreateRecordingDraft()
    {
        if (_recordingTake is null) throw new InvalidOperationException("请先录制或导入音频。");
        if (!RecordingReviewed) throw new InvalidOperationException("请先试听并逐字校对文稿，再勾选确认。");
        if (HasUnappliedRecordingCalibration) throw new InvalidOperationException("校准参数尚未应用，请先应用并试听，或恢复原始版本。");
        if (!RecordingMetadataComplete) throw new InvalidOperationException("请填写录音名称、说话人、语言和准确文稿。");
        foreach (var (value, label, maximum) in new[] { (RecordingName, "录音名称", 80), (RecordingSpeaker, "说话人名称", 80), (RecordingText, "录音文稿", 1000) })
            if (value.Length > maximum || value.Contains('|') || value.Any(char.IsControl))
                throw new InvalidOperationException($"{label}最多 {maximum} 字，且不能包含竖线、换行、制表符或其他控制字符。");
        if (!LocalVoiceLanguages.Codes.Contains(RecordingLanguage, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择支持的录音语言。");
        if (!_recordingTake.Quality.CanUseForTraining) throw new InvalidOperationException("当前录音的音量或时长检查未通过，请处理提示后重试。");
        var take = new VoiceRecordingTake
        {
            Id = _recordingTake.Id,
            AudioPath = _recordingTake.AudioPath,
            OriginalAudioPath = RecordingOriginalPath ?? _recordingTake.OriginalAudioPath,
            CreatedAt = _recordingTake.CreatedAt,
            Quality = _recordingTake.Quality
        };
        return new VoiceRecordingSampleDraft { Take = take, Name = RecordingName.Trim(), Speaker = RecordingSpeaker.Trim(), Text = RecordingText.Trim(), Language = RecordingLanguage };
    }

    private void PlayRecordingAudio(string? path)
    {
        _workspace.ExecuteUiAction(() =>
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("录音文件不存在，请重新录制或导入。");
            StopRecordingPreview();
            RecordingCaptureStarting?.Invoke();
            _recordingPlaying = true;
            RecordingPreviewRequested?.Invoke(path);
            RecordingStatus = "正在试听。请留意断字、噪声、爆音与文稿是否一致；数值检查不能代替人工试听。";
            RefreshCommands();
        });
    }

    private void StopRecordingPreview()
    {
        RecordingPreviewStopRequested?.Invoke();
        _recordingPlaying = false;
        RefreshCommands();
    }

    public void ReportRecordingPlaybackEnded() { _recordingPlaying = false; RefreshCommands(); }
    public void ReportRecordingPlaybackError(string message)
    {
        _recordingPlaying = false;
        RecordingStatus = "试听失败：" + message;
        RefreshCommands();
    }

    public async Task RefreshRecordingLibraryCoreAsync(CancellationToken ct)
    {
        if (_recording is null) return;
        try
        {
            var devices = (await _recording.ListDevicesAsync(ct)).ToList();
            var selectedId = RecordingDevice?.Id;
            RecordingDevices.Clear();
            foreach (var d in devices) RecordingDevices.Add(d);
            RecordingDevice = RecordingDevices.FirstOrDefault(x => x.Id == selectedId) ?? RecordingDevices.FirstOrDefault();
            await RefreshRecordingSamplesCoreAsync(ct);
            RecordingStatus = RecordingDevices.Count == 0
                ? "未检测到录音设备。请连接麦克风并在 Windows 隐私设置中允许桌面应用访问麦克风后刷新；也可导入已有 WAV。"
                : $"发现 {RecordingDevices.Count} 个录音设备，已加载 {RecordingSamples.Count} 条本机素材。请选择麦克风后开始录音。";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { RecordingStatus = "刷新录音设备或素材失败：" + _workspace.SafeMessage(ex); }
    }

    private async Task RefreshRecordingSamplesCoreAsync(CancellationToken ct)
    {
        var samples = await RequiredRecording().ListSamplesAsync(ct);
        var selectedId = SelectedRecording?.Sample.Id;
        var checkedIds = RecordingSamples.Where(x => x.IsSelected).Select(x => x.Sample.Id).ToHashSet(StringComparer.Ordinal);
        var items = samples.Select(x => new RecordingLibraryItem(x, () =>
        {
            OnPropertyChanged(nameof(RecordingSelectionSummary));
            RefreshCommands();
        }) { IsSelected = checkedIds.Contains(x.Id) }).ToList();
        RecordingSamples.Clear();
        foreach (var item in items) RecordingSamples.Add(item);
        SelectedRecording = RecordingSamples.FirstOrDefault(x => x.Sample.Id == selectedId);
        OnPropertyChanged(nameof(RecordingSelectionSummary));
        RefreshCommands();
    }

    public Task ShutdownAsync()
    {
        StopRecordingPreview();
        return Task.CompletedTask;
    }

    public void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEditRecording));
        foreach (var command in new[] { RefreshRecordingsCommand, StartRecordingCommand, StopRecordingCommand, ImportRecordingCommand, CalibrateRecordingCommand, SaveRecordingCommand, LoadRecordingCommand, ExportRecordingDatasetCommand })
            command?.Refresh();
        foreach (var command in new[] { CancelRecordingCommand, RestoreOriginalRecordingCommand, PlayOriginalRecordingCommand, PlayRecordingCommand, StopRecordingPlaybackCommand, UseRecordingAsReferenceCommand })
            command?.Refresh();
    }
}

public sealed class RecordingLibraryItem(VoiceRecordingSample sample, Action selectionChanged) : ObservableObject
{
    private bool _isSelected;
    public VoiceRecordingSample Sample { get; } = sample;
    public bool IsSelected { get => _isSelected; set { if (Set(ref _isSelected, value)) selectionChanged(); } }
    public string Summary => $"{Sample.Quality.DurationSeconds:F2} 秒 · {Sample.Language} · {Sample.Speaker}";
}
