using System.Collections.ObjectModel;
using System.IO;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using Microsoft.Win32;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

/// <summary>
/// Manages local GPT-SoVITS voice profiles, environment settings, direct model fine-tuning, and voice previews.
/// </summary>
public sealed class LocalVoiceViewModel : ObservableObject
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly ISettingsStore _settingsStore;
    private readonly ISynthesisService _synthesis;
    private readonly ILocalVoiceService? _localVoice;
    private readonly Func<bool> _isTrainingWebUiRunning;
    private readonly Action<IReadOnlyList<VoiceOption>> _applyVoicesCallback;
    private LocalVoiceProfile? _selectedLocalVoice;
    private string _localVoiceStatus = "选择已安装的 GPT-SoVITS 目录，启动本地服务后检查连接。";
    private LocalVoiceDraft _localVoiceDraft = new();
    private string _localDatasetPath = "";
    private int _localGptEpochs = 5;
    private int _localSoVitsEpochs = 8;
    private bool _isLocalVoiceTraining;

    public LocalVoiceViewModel(WorkspaceCoordinator workspace, ISettingsStore settingsStore,
        ISynthesisService synthesis, ILocalVoiceService? localVoice, Func<bool> isTrainingWebUiRunning,
        Action<IReadOnlyList<VoiceOption>> applyVoicesCallback)
    {
        _workspace = workspace;
        _settingsStore = settingsStore;
        _synthesis = synthesis;
        _localVoice = localVoice;
        _isTrainingWebUiRunning = isTrainingWebUiRunning;
        _applyVoicesCallback = applyVoicesCallback;

        CheckLocalVoiceCommand = new(_ => RunLocalVoiceAsync("检查本地服务连接", async ct =>
            LocalVoiceStatus = DescribeLocalResult(await RequiredLocalVoice().CheckAsync(WorkspaceCoordinator.Clone(LocalVoiceSettings), ct))),
            _ => !_workspace.IsBusy && _localVoice is not null);

        StartLocalVoiceCommand = new(_ => RunLocalVoiceAsync("启动本地配音服务", async ct =>
            LocalVoiceStatus = DescribeLocalResult(await RequiredLocalVoice().StartAsync(WorkspaceCoordinator.Clone(LocalVoiceSettings), ct))),
            _ => !_workspace.IsBusy && !_isTrainingWebUiRunning() && _localVoice is not null);

        StopLocalVoiceCommand = new(_ => RunLocalVoiceAsync("停止本地配音服务", async ct =>
            LocalVoiceStatus = DescribeLocalResult(await RequiredLocalVoice().StopAsync(ct))),
            _ => !_workspace.IsBusy && _localVoice is not null);

        SaveLocalVoiceSettingsCommand = new(_ => RunLocalVoiceAsync("保存本地配音设置", async ct =>
        {
            await _settingsStore.SaveAsync(_workspace.Settings, ct);
            LocalVoiceStatus = "本地环境配置已保存。选择音色后点击「用于视频配音」，即可切换到本地配音。";
        }), _ => !_workspace.IsBusy);

        RefreshLocalVoicesCommand = new(_ => RunLocalVoiceAsync("加载我的声音", async ct =>
        {
            await RefreshLocalVoicesCoreAsync(ct);
            LocalVoiceStatus = $"已加载 {LocalVoices.Count} 个本地音色。";
        }), _ => !_workspace.IsBusy && _localVoice is not null);

        SaveLocalVoiceCommand = new(_ => RunLocalVoiceAsync("保存本人音色", async ct =>
        {
            var profile = await RequiredLocalVoice().SaveAsync(WorkspaceCoordinator.Clone(LocalVoiceDraft), ct);
            await RefreshLocalVoicesCoreAsync(ct);
            SelectedLocalVoice = LocalVoices.FirstOrDefault(x => x.Id == profile.Id);
            LocalVoiceStatus = profile.IsTrained
                ? "已导入模型并保存音色。选择该音色后可试听并用于视频配音。"
                : "参考录音音色已保存，可直接试听。此步骤没有训练模型；需要微调时请准备下方训练清单。";
        }), _ => !_workspace.IsBusy && _localVoice is not null);

        TrainLocalVoiceCommand = new(_ => RunLocalVoiceAsync("训练本人音色", async ct =>
        {
            if (string.IsNullOrWhiteSpace(LocalDatasetPath)) throw new InvalidOperationException("请先选择已校对的训练 .list 文件。");
            if (LocalGptEpochs is < 1 or > 100 || LocalSoVitsEpochs is < 1 or > 100)
                throw new InvalidOperationException("训练轮数需为 1–100 的整数。");
            IsLocalVoiceTraining = true;
            try
            {
                LocalVoiceStatus = "正在本机准备数据并训练；可在底部查看阶段与进度。";
                var request = new LocalVoiceTrainingRequest
                {
                    Voice = WorkspaceCoordinator.Clone(LocalVoiceDraft),
                    DatasetListPath = LocalDatasetPath.Trim(),
                    GptEpochs = LocalGptEpochs,
                    SoVitsEpochs = LocalSoVitsEpochs
                };
                var profile = await RequiredLocalVoice().TrainAsync(request, WorkspaceCoordinator.Clone(LocalVoiceSettings), _workspace.CreateProgress(), ct);
                await RefreshLocalVoicesCoreAsync(ct);
                SelectedLocalVoice = LocalVoices.FirstOrDefault(x => x.Id == profile.Id);
                LocalVoiceStatus = "训练完成，模型音色已保存到本机。建议先试听，再用于视频配音。";
            }
            finally { IsLocalVoiceTraining = false; }
        }), _ => !_workspace.IsBusy && !_isTrainingWebUiRunning() && _localVoice is not null);

        UseLocalVoiceCommand = new(_ => RunLocalVoiceAsync("选择本地配音音色", async ct =>
        {
            var profile = SelectedLocalVoice ?? throw new InvalidOperationException("请先选择一个已保存的音色。");
            if (_workspace.HasProject && !LocalVoiceLanguages.Codes.Contains(_workspace.Options.SpeechLanguage, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前任务的配音语言不受本地服务支持，请先在视频转换页重新设置配音语言。");
            _workspace.Settings.Synthesis.Provider = ServiceProviderKind.LocalGptSoVits;
            await _settingsStore.SaveAsync(_workspace.Settings, ct);
            _workspace.Settings = WorkspaceCoordinator.Clone(_workspace.Settings);
            OnPropertyChanged(nameof(LocalVoiceSettings));
            _workspace.Options.VoiceId = profile.Id;
            if (!_workspace.HasProject) _workspace.Options.SpeechLanguage = "zh-CN";
            ApplyLocalVoiceChoices();
            SynthesisSelected?.Invoke();
            LocalVoiceStatus = $"已保存配音服务选择：本地 GPT-SoVITS · {profile.Name}。视频转换将使用这个音色。";
        }), _ => !_workspace.IsBusy && SelectedLocalVoice is not null && _localVoice is not null);

        PreviewLocalVoiceCommand = new(_ => RunLocalVoiceAsync("试听本人音色", async ct =>
        {
            var profile = SelectedLocalVoice ?? throw new InvalidOperationException("请先选择一个已保存的音色。");
            var settings = WorkspaceCoordinator.Clone(_workspace.Settings.Synthesis);
            settings.Provider = ServiceProviderKind.LocalGptSoVits;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinguaForge-X", "preview");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
            await _synthesis.SynthesizeAsync("你好，这是我的中文配音试听。欢迎使用 LinguaForge。", "zh-CN", profile.Id, path, settings, ct);
            _workspace.RaiseVoicePreviewReady(path);
            LocalVoiceStatus = "中文试听已生成并开始播放。试听不会更改当前视频所选音色。";
        }), _ => !_workspace.IsBusy && !_isTrainingWebUiRunning() && SelectedLocalVoice is not null && _localVoice is not null);

        BrowseLocalVoiceCommand = new(p => _workspace.ExecuteUiAction(() => BrowseLocalVoice(p?.ToString() ?? "")),
            _ => !_workspace.IsBusy && !_isTrainingWebUiRunning());
        NewLocalVoiceCommand = new(_ => { LocalVoiceDraft = new(); LocalDatasetPath = ""; }, _ => !_workspace.IsBusy);
        CancelLocalVoiceTrainingCommand = new(_ =>
        {
            LocalVoiceStatus = "正在停止训练并等待本地进程退出…";
            _workspace.Cancel();
        }, _ => IsLocalVoiceTraining);
        OpenLocalVoiceGuideCommand = new(_ => _workspace.ExecuteUiAction(() =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "tools", "gpt-sovits", "README.md");
            if (!File.Exists(path)) throw new FileNotFoundException("安装说明未包含在当前运行目录，请使用完整发布包。", path);
            _workspace.OpenExternal(path);
        }));
    }

    public event Action? SynthesisSelected;

    public LocalVoiceSettings LocalVoiceSettings => _workspace.LocalVoiceSettings;
    public LocalVoiceDraft LocalVoiceDraft { get => _localVoiceDraft; set => Set(ref _localVoiceDraft, value); }
    public ObservableCollection<LocalVoiceProfile> LocalVoices { get; } = [];

    public LocalVoiceProfile? SelectedLocalVoice
    {
        get => _selectedLocalVoice;
        set { Set(ref _selectedLocalVoice, value); RefreshCommands(); }
    }

    public string LocalVoiceStatus { get => _localVoiceStatus; set => Set(ref _localVoiceStatus, value); }
    public string LocalDatasetPath { get => _localDatasetPath; set => Set(ref _localDatasetPath, value); }
    public int LocalGptEpochs { get => _localGptEpochs; set => Set(ref _localGptEpochs, value); }
    public int LocalSoVitsEpochs { get => _localSoVitsEpochs; set => Set(ref _localSoVitsEpochs, value); }

    public bool IsLocalVoiceTraining
    {
        get => _isLocalVoiceTraining;
        private set
        {
            if (Set(ref _isLocalVoiceTraining, value))
            {
                _workspace.RaiseStateChanged();
            }
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> LocalVoiceDevices { get; } =
        [new("auto", "自动选择"), new("cpu", "CPU"), new("cuda", "NVIDIA CUDA")];
    public IReadOnlyList<KeyValuePair<string, string>> LocalReferenceLanguages { get; } =
        [new("zh-CN", "中文（普通话）"), new("en-US", "英语"), new("ja-JP", "日语"), new("ko-KR", "韩语"), new("yue-CN", "粤语")];

    public AsyncCommand CheckLocalVoiceCommand { get; }
    public AsyncCommand StartLocalVoiceCommand { get; }
    public AsyncCommand StopLocalVoiceCommand { get; }
    public AsyncCommand SaveLocalVoiceSettingsCommand { get; }
    public AsyncCommand SaveLocalVoiceCommand { get; }
    public AsyncCommand RefreshLocalVoicesCommand { get; }
    public AsyncCommand TrainLocalVoiceCommand { get; }
    public AsyncCommand UseLocalVoiceCommand { get; }
    public AsyncCommand PreviewLocalVoiceCommand { get; }
    public RelayCommand BrowseLocalVoiceCommand { get; }
    public RelayCommand NewLocalVoiceCommand { get; }
    public RelayCommand CancelLocalVoiceTrainingCommand { get; }
    public RelayCommand OpenLocalVoiceGuideCommand { get; }

    private ILocalVoiceService RequiredLocalVoice() => _localVoice ?? throw new InvalidOperationException("当前程序没有加载本地配音服务，请使用完整版本。");
    private static string DescribeLocalResult(ConnectionTestResult result) => (result.Success ? "通过：" : "需要处理：") + result.Message;

    private Task RunLocalVoiceAsync(string stage, Func<CancellationToken, Task> action) => _workspace.RunAsync(stage, async ct =>
    {
        try { await action(ct); _workspace.Status = LocalVoiceStatus; }
        catch (OperationCanceledException)
        {
            LocalVoiceStatus = "操作已取消。只有完整完成并保存的音色才会出现在列表中。如已停止配音服务，重试前请重新启动。";
            throw;
        }
        catch (Exception ex)
        {
            LocalVoiceStatus = "需要处理：" + _workspace.SafeMessage(ex);
            throw;
        }
    });

    public async Task RefreshLocalVoicesCoreAsync(CancellationToken ct)
    {
        if (_localVoice is null) return;
        var selectedId = SelectedLocalVoice?.Id;
        var items = (await _localVoice.ListAsync(ct)).ToList();
        LocalVoices.Clear();
        foreach (var item in items) LocalVoices.Add(item);
        SelectedLocalVoice = LocalVoices.FirstOrDefault(x => x.Id == selectedId) ?? LocalVoices.FirstOrDefault();
        ApplyLocalVoiceChoices();
    }

    public void ApplyLocalVoiceChoices()
    {
        if (_workspace.Settings.Synthesis.Provider != ServiceProviderKind.LocalGptSoVits) return;
        var voices = LocalVoices.Select(x => new VoiceOption(x.Id, x.Name + " · " + x.Description, x.Language, LocalVoiceLanguages.Codes)).ToList();
        _applyVoicesCallback(voices);
    }

    public void NotifySettingsChanged() => OnPropertyChanged(nameof(LocalVoiceSettings));

    private void BrowseLocalVoice(string field)
    {
        if (field == "root")
        {
            var folder = new OpenFolderDialog { Title = "选择包含 api_v2.py 的 GPT-SoVITS 安装目录" };
            if (folder.ShowDialog() == true)
            {
                LocalVoiceSettings.RootDirectory = folder.FolderName;
                var bundledPython = Path.Combine(folder.FolderName, "runtime", "python.exe");
                if (File.Exists(bundledPython)) LocalVoiceSettings.PythonPath = bundledPython;
                OnPropertyChanged(nameof(LocalVoiceSettings));
            }
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = field switch { "python" => "选择 Python 解释器", "reference" => "选择 3–10 秒本人录音（WAV）", "gpt" => "选择 GPT 模型", "sovits" => "选择 SoVITS 模型", _ => "选择已校对的训练清单" },
            Filter = field switch { "python" => "Python 解释器|python.exe;python3.exe|所有文件|*.*", "reference" => "WAV 音频|*.wav", "gpt" => "GPT 模型|*.ckpt", "sovits" => "SoVITS 模型|*.pth", _ => "训练清单|*.list" }
        };
        if (dialog.ShowDialog() != true) return;
        switch (field)
        {
            case "python": LocalVoiceSettings.PythonPath = dialog.FileName; OnPropertyChanged(nameof(LocalVoiceSettings)); break;
            case "reference": LocalVoiceDraft.ReferenceAudioPath = dialog.FileName; OnPropertyChanged(nameof(LocalVoiceDraft)); break;
            case "gpt": LocalVoiceDraft.GptWeightsPath = dialog.FileName; OnPropertyChanged(nameof(LocalVoiceDraft)); break;
            case "sovits": LocalVoiceDraft.SoVitsWeightsPath = dialog.FileName; OnPropertyChanged(nameof(LocalVoiceDraft)); break;
            default: LocalDatasetPath = dialog.FileName; break;
        }
    }

    public void RefreshCommands()
    {
        foreach (var command in new[] { CheckLocalVoiceCommand, StartLocalVoiceCommand, StopLocalVoiceCommand, SaveLocalVoiceSettingsCommand, SaveLocalVoiceCommand, RefreshLocalVoicesCommand, TrainLocalVoiceCommand, UseLocalVoiceCommand, PreviewLocalVoiceCommand })
            command?.Refresh();
        foreach (var command in new[] { BrowseLocalVoiceCommand, NewLocalVoiceCommand, CancelLocalVoiceTrainingCommand })
            command?.Refresh();
    }
}
