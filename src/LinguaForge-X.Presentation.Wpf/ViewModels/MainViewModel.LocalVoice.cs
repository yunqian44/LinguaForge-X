using System.Collections.ObjectModel;
using System.IO;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using Microsoft.Win32;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

public sealed partial class MainViewModel
{
    private ILocalVoiceService? _localVoice;
    private LocalVoiceProfile? _selectedLocalVoice;
    private string _localVoiceStatus = "选择已安装的 GPT-SoVITS 目录，启动本地服务后检查连接。";
    private LocalVoiceDraft _localVoiceDraft = new();
    private string _localDatasetPath = "";
    private int _localGptEpochs = 5;
    private int _localSoVitsEpochs = 8;
    private bool _isLocalVoiceTraining;

    public LocalVoiceSettings LocalVoiceSettings => Settings.Synthesis.LocalVoice;
    public LocalVoiceDraft LocalVoiceDraft { get => _localVoiceDraft; private set => Set(ref _localVoiceDraft, value); }
    public ObservableCollection<LocalVoiceProfile> LocalVoices { get; } = [];
    public LocalVoiceProfile? SelectedLocalVoice
    {
        get => _selectedLocalVoice;
        set { Set(ref _selectedLocalVoice, value); RefreshLocalVoiceCommands(); }
    }
    public string LocalVoiceStatus { get => _localVoiceStatus; private set => Set(ref _localVoiceStatus, value); }
    public string LocalDatasetPath { get => _localDatasetPath; set => Set(ref _localDatasetPath, value); }
    public int LocalGptEpochs { get => _localGptEpochs; set => Set(ref _localGptEpochs, value); }
    public int LocalSoVitsEpochs { get => _localSoVitsEpochs; set => Set(ref _localSoVitsEpochs, value); }
    public bool IsLocalVoiceTraining { get => _isLocalVoiceTraining; private set { Set(ref _isLocalVoiceTraining, value); RefreshLocalVoiceCommands(); } }
    public IReadOnlyList<KeyValuePair<string, string>> LocalVoiceDevices { get; } =
        [new("auto", "自动选择"), new("cpu", "CPU"), new("cuda", "NVIDIA CUDA")];
    public IReadOnlyList<KeyValuePair<string, string>> LocalReferenceLanguages { get; } =
        [new("zh-CN", "中文（普通话）"), new("en-US", "英语"), new("ja-JP", "日语"), new("ko-KR", "韩语"), new("yue-CN", "粤语")];

    public AsyncCommand CheckLocalVoiceCommand { get; private set; } = null!;
    public AsyncCommand StartLocalVoiceCommand { get; private set; } = null!;
    public AsyncCommand StopLocalVoiceCommand { get; private set; } = null!;
    public AsyncCommand SaveLocalVoiceSettingsCommand { get; private set; } = null!;
    public AsyncCommand SaveLocalVoiceCommand { get; private set; } = null!;
    public AsyncCommand RefreshLocalVoicesCommand { get; private set; } = null!;
    public AsyncCommand TrainLocalVoiceCommand { get; private set; } = null!;
    public AsyncCommand UseLocalVoiceCommand { get; private set; } = null!;
    public AsyncCommand PreviewLocalVoiceCommand { get; private set; } = null!;
    public RelayCommand BrowseLocalVoiceCommand { get; private set; } = null!;
    public RelayCommand NewLocalVoiceCommand { get; private set; } = null!;
    public RelayCommand CancelLocalVoiceTrainingCommand { get; private set; } = null!;
    public RelayCommand OpenLocalVoiceGuideCommand { get; private set; } = null!;

    private void InitializeLocalVoice(ILocalVoiceService? service)
    {
        _localVoice = service;
        CheckLocalVoiceCommand = new(_ => RunLocalVoiceAsync("检查本地服务连接", async ct =>
            LocalVoiceStatus = DescribeLocalResult(await RequiredLocalVoice().CheckAsync(Clone(LocalVoiceSettings), ct))), _ => !IsBusy && _localVoice is not null);
        StartLocalVoiceCommand = new(_ => RunLocalVoiceAsync("启动本地配音服务", async ct =>
            LocalVoiceStatus = DescribeLocalResult(await RequiredLocalVoice().StartAsync(Clone(LocalVoiceSettings), ct))), _ => !IsBusy && _localVoice is not null);
        StopLocalVoiceCommand = new(_ => RunLocalVoiceAsync("停止本地配音服务", async ct =>
            LocalVoiceStatus = DescribeLocalResult(await RequiredLocalVoice().StopAsync(ct))), _ => !IsBusy && _localVoice is not null);
        SaveLocalVoiceSettingsCommand = new(_ => RunLocalVoiceAsync("保存本地配音设置", async ct =>
        {
            await _settingsStore.SaveAsync(Settings, ct);
            LocalVoiceStatus = "本地环境配置已保存。选择音色后点击「用于视频配音」，即可切换到本地配音。";
        }), _ => !IsBusy);
        RefreshLocalVoicesCommand = new(_ => RunLocalVoiceAsync("加载我的声音", async ct =>
        {
            await RefreshLocalVoicesCoreAsync(ct);
            LocalVoiceStatus = $"已加载 {LocalVoices.Count} 个本地音色。";
        }), _ => !IsBusy && _localVoice is not null);
        SaveLocalVoiceCommand = new(_ => RunLocalVoiceAsync("保存本人音色", async ct =>
        {
            var profile = await RequiredLocalVoice().SaveAsync(Clone(LocalVoiceDraft), ct);
            await RefreshLocalVoicesCoreAsync(ct);
            SelectedLocalVoice = LocalVoices.FirstOrDefault(x => x.Id == profile.Id);
            LocalVoiceStatus = profile.IsTrained
                ? "已导入模型并保存音色。选择该音色后可试听并用于视频配音。"
                : "参考录音音色已保存，可直接试听。此步骤没有训练模型；需要微调时请准备下方训练清单。";
        }), _ => !IsBusy && _localVoice is not null);
        TrainLocalVoiceCommand = new(_ => RunLocalVoiceAsync("训练本人音色", async ct =>
        {
            if (string.IsNullOrWhiteSpace(LocalDatasetPath)) throw new InvalidOperationException("请先选择已校对的训练 .list 文件。");
            if (LocalGptEpochs is < 1 or > 100 || LocalSoVitsEpochs is < 1 or > 100)
                throw new InvalidOperationException("训练轮数需为 1–100 的整数。");
            IsLocalVoiceTraining = true;
            try
            {
                LocalVoiceStatus = "正在本机准备数据并训练；可在底部查看阶段与进度。";
                var request = new LocalVoiceTrainingRequest { Voice = Clone(LocalVoiceDraft), DatasetListPath = LocalDatasetPath.Trim(), GptEpochs = LocalGptEpochs, SoVitsEpochs = LocalSoVitsEpochs };
                var profile = await RequiredLocalVoice().TrainAsync(request, Clone(LocalVoiceSettings), CreateProgress(), ct);
                await RefreshLocalVoicesCoreAsync(ct);
                SelectedLocalVoice = LocalVoices.FirstOrDefault(x => x.Id == profile.Id);
                LocalVoiceStatus = "训练完成，模型音色已保存到本机。建议先试听，再用于视频配音。";
            }
            finally { IsLocalVoiceTraining = false; }
        }), _ => !IsBusy && _localVoice is not null);
        UseLocalVoiceCommand = new(_ => RunLocalVoiceAsync("选择本地配音音色", async ct =>
        {
            var profile = SelectedLocalVoice ?? throw new InvalidOperationException("请先选择一个已保存的音色。");
            if (HasProject && !LocalVoiceLanguages.Codes.Contains(Options.SpeechLanguage, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前任务的配音语言不受本地服务支持，请先在视频转换页重新设置配音语言。");
            Settings.Synthesis.Provider = ServiceProviderKind.LocalGptSoVits;
            await _settingsStore.SaveAsync(Settings, ct);
            Settings = Clone(Settings);
            OnPropertyChanged(nameof(LocalVoiceSettings));
            Options.VoiceId = profile.Id;
            if (!HasProject) Options.SpeechLanguage = "zh-CN";
            ApplyLocalVoiceChoices();
            OnPropertyChanged(nameof(Settings));
            NotifySelections();
            SynthesisStatus = "已选择本地 GPT-SoVITS；请启动服务并试听。";
            LocalVoiceStatus = $"已保存配音服务选择：本地 GPT-SoVITS · {profile.Name}。视频转换将使用这个音色。";
        }), _ => !IsBusy && SelectedLocalVoice is not null && _localVoice is not null);
        PreviewLocalVoiceCommand = new(_ => RunLocalVoiceAsync("试听本人音色", async ct =>
        {
            var profile = SelectedLocalVoice ?? throw new InvalidOperationException("请先选择一个已保存的音色。");
            var settings = Clone(Settings.Synthesis);
            settings.Provider = ServiceProviderKind.LocalGptSoVits;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinguaForge-X", "preview");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
            await _synthesis.SynthesizeAsync("你好，这是我的中文配音试听。欢迎使用 LinguaForge。", "zh-CN", profile.Id, path, settings, ct);
            VoicePreviewReady?.Invoke(path);
            LocalVoiceStatus = "中文试听已生成并开始播放。试听不会更改当前视频所选音色。";
        }), _ => !IsBusy && SelectedLocalVoice is not null && _localVoice is not null);
        BrowseLocalVoiceCommand = new(p => ExecuteUiAction(() => BrowseLocalVoice(p?.ToString() ?? "")), _ => !IsBusy);
        NewLocalVoiceCommand = new(_ => { LocalVoiceDraft = new(); LocalDatasetPath = ""; }, _ => !IsBusy);
        CancelLocalVoiceTrainingCommand = new(_ => { LocalVoiceStatus = "正在停止训练并等待本地进程退出…"; _cancellation?.Cancel(); }, _ => IsLocalVoiceTraining);
        OpenLocalVoiceGuideCommand = new(_ => ExecuteUiAction(() =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "tools", "gpt-sovits", "README.md");
            if (!File.Exists(path)) throw new FileNotFoundException("安装说明未包含在当前运行目录，请使用完整发布包。", path);
            OpenExternal(path);
        }));
    }

    private ILocalVoiceService RequiredLocalVoice() => _localVoice ?? throw new InvalidOperationException("当前程序没有加载本地配音服务，请使用完整版本。");
    private static string DescribeLocalResult(ConnectionTestResult result) => (result.Success ? "通过：" : "需要处理：") + result.Message;

    private Task RunLocalVoiceAsync(string stage, Func<CancellationToken, Task> action) => RunAsync(stage, async ct =>
    {
        try { await action(ct); Status = LocalVoiceStatus; }
        catch (OperationCanceledException) { LocalVoiceStatus = "操作已取消。只有完整完成并保存的音色才会出现在列表中。如已停止配音服务，重试前请重新启动。"; throw; }
        catch (Exception ex) { LocalVoiceStatus = "需要处理：" + SafeMessage(ex); throw; }
    });

    private async Task RefreshLocalVoicesCoreAsync(CancellationToken ct)
    {
        if (_localVoice is null) return;
        var selectedId = SelectedLocalVoice?.Id;
        ReplaceItems(LocalVoices, await _localVoice.ListAsync(ct));
        SelectedLocalVoice = LocalVoices.FirstOrDefault(x => x.Id == selectedId) ?? LocalVoices.FirstOrDefault();
        ApplyLocalVoiceChoices();
    }

    private void ApplyLocalVoiceChoices()
    {
        if (Settings.Synthesis.Provider != ServiceProviderKind.LocalGptSoVits) return;
        _allVoices = LocalVoices.Select(x => new VoiceOption(x.Id, x.Name + " · " + x.Description, x.Language, LocalVoiceLanguages.Codes)).ToList();
        RebuildTargetLanguages();
    }

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

    private void RefreshLocalVoiceCommands()
    {
        foreach (var command in new[] { CheckLocalVoiceCommand, StartLocalVoiceCommand, StopLocalVoiceCommand, SaveLocalVoiceSettingsCommand, SaveLocalVoiceCommand, RefreshLocalVoicesCommand, TrainLocalVoiceCommand, UseLocalVoiceCommand, PreviewLocalVoiceCommand }) command?.Refresh();
        foreach (var command in new[] { BrowseLocalVoiceCommand, NewLocalVoiceCommand, CancelLocalVoiceTrainingCommand }) command?.Refresh();
    }
}
