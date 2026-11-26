using System.Collections.ObjectModel;
using System.ComponentModel;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

/// <summary>
/// Shell facade ViewModel that composes domain-specific child ViewModels
/// (<see cref="WorkspaceCoordinator"/>, <see cref="ConversionWorkspaceViewModel"/>,
/// <see cref="OnlineVideoViewModel"/>, <see cref="ServiceSettingsViewModel"/>,
/// <see cref="LocalVoiceViewModel"/>, <see cref="VoiceRecordingViewModel"/>,
/// and <see cref="TrainingWebUiViewModel"/>) while preserving flat XAML and test bindings.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private bool _loaded;
    private int _selectedTab;

    public WorkspaceCoordinator Workspace { get; }
    public ConversionWorkspaceViewModel ConversionWorkspace { get; }
    public OnlineVideoViewModel OnlineVideo { get; }
    public ServiceSettingsViewModel ServiceSettings { get; }
    public LocalVoiceViewModel LocalVoice { get; }
    public VoiceRecordingViewModel Recording { get; }
    public TrainingWebUiViewModel TrainingWebUi { get; }

    public MainViewModel(ISettingsStore settingsStore, IServiceCatalog catalog, IMediaService media,
        IConversionService conversion, IProjectStore projects, ISynthesisService synthesis, IOnlineVideoService online,
        IWebsiteVerificationService verification, IWebsiteSessionStore websiteSessions, ILocalVoiceService? localVoice = null,
        IVoiceRecordingService? recording = null, ILocalVoiceTrainingWebUiService? trainingWebUi = null)
    {
        _settingsStore = settingsStore;
        Workspace = new WorkspaceCoordinator();

        ConversionWorkspace = new ConversionWorkspaceViewModel(
            Workspace, media, conversion, projects, synthesis,
            () => TrainingWebUi?.TrainingWebUiRunning == true,
            url => OnlineVideo?.SetOnlineSource(url),
            tab => SelectedTab = tab);

        OnlineVideo = new OnlineVideoViewModel(
            Workspace, settingsStore, media, online, verification, websiteSessions,
            ConversionWorkspace.ApplyImportedVideo);

        ServiceSettings = new ServiceSettingsViewModel(
            Workspace, settingsStore, catalog, media, online,
            ConversionWorkspace.ApplyCapabilities,
            OnlineVideo.NotifyVerification);

        LocalVoice = new LocalVoiceViewModel(
            Workspace, settingsStore, synthesis, localVoice,
            () => TrainingWebUi?.TrainingWebUiRunning == true,
            ConversionWorkspace.SetVoicesCatalog);

        Recording = new VoiceRecordingViewModel(
            Workspace, LocalVoice, recording);

        TrainingWebUi = new TrainingWebUiViewModel(
            Workspace, trainingWebUi, localVoice,
            () => LocalVoice.IsLocalVoiceTraining);

        CancelCommand = new(_ =>
        {
            Workspace.Status = "正在取消，请等待当前步骤结束…";
            Workspace.Cancel();
        }, _ => Workspace.IsBusy);

        LocalVoice.SynthesisSelected += () =>
        {
            ServiceSettings.SynthesisStatus = "已选择本地 GPT-SoVITS；请启动服务并试听。";
            ConversionWorkspace.NotifySelections();
        };

        ForwardChildNotifications();
        Workspace.StateChanged += RefreshAllState;
    }

    private void ForwardChildNotifications()
    {
        Workspace.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        Workspace.VoicePreviewReady += path => VoicePreviewReady?.Invoke(path);
        ConversionWorkspace.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        OnlineVideo.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        ServiceSettings.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        LocalVoice.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        Recording.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        Recording.RecordingCaptureStarting += () => RecordingCaptureStarting?.Invoke();
        Recording.RecordingPreviewRequested += path => RecordingPreviewRequested?.Invoke(path);
        Recording.RecordingPreviewStopRequested += () => RecordingPreviewStopRequested?.Invoke();
        TrainingWebUi.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        TrainingWebUi.TrainingWebUiRequested += uri => TrainingWebUiRequested?.Invoke(uri);
        TrainingWebUi.TrainingWebUiStopped += () => TrainingWebUiStopped?.Invoke();
    }

    private void RefreshAllState()
    {
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(CanConfigure));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(ProjectSummary));
        OnPropertyChanged(nameof(OnlineSummary));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(VideoSummary));
        OnPropertyChanged(nameof(VerificationSummary));
        OnPropertyChanged(nameof(HasVerificationSession));
        OnPropertyChanged(nameof(OnlineProxy));
        OnPropertyChanged(nameof(LocalVoiceSettings));
        OnPropertyChanged(nameof(CanEditLocalVoiceEnvironment));
        ConversionWorkspace.NotifySelections();
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        CancelCommand.Refresh();
        ConversionWorkspace.RefreshCommands();
        OnlineVideo.RefreshCommands();
        ServiceSettings.RefreshCommands();
        LocalVoice.RefreshCommands();
        Recording.RefreshCommands();
        TrainingWebUi.RefreshCommands();
    }

    public AppSettings Settings => Workspace.Settings;
    public ConversionOptions Options => Workspace.Options;
    public ConversionProject? Project => Workspace.Project;
    public bool HasProject => Workspace.HasProject;
    public bool CanConfigure => Workspace.CanConfigure;
    public bool IsBusy => Workspace.IsBusy;
    public bool IsIdle => Workspace.IsIdle;
    public string Status => Workspace.Status;
    public string Stage => Workspace.Stage;
    public double Progress => Workspace.Progress;
    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }

    public string CapabilityDescription => ServiceSettings.CapabilityDescription;
    public string TranscriptionStatus => ServiceSettings.TranscriptionStatus;
    public string TranslationStatus => ServiceSettings.TranslationStatus;
    public string SynthesisStatus => ServiceSettings.SynthesisStatus;

    public ConversionProject? SelectedHistory
    {
        get => ConversionWorkspace.SelectedHistory;
        set => ConversionWorkspace.SelectedHistory = value;
    }

    public string? PreviewPath => ConversionWorkspace.PreviewPath;
    public string SourceSummary => ConversionWorkspace.SourceSummary;
    public string VideoSummary => ConversionWorkspace.VideoSummary;
    public string ProjectSummary => ConversionWorkspace.ProjectSummary;

    public string SpeechLanguage
    {
        get => ConversionWorkspace.SpeechLanguage;
        set => ConversionWorkspace.SpeechLanguage = value;
    }

    public string SourceLanguage
    {
        get => ConversionWorkspace.SourceLanguage;
        set => ConversionWorkspace.SourceLanguage = value;
    }

    public string SubtitleLanguage
    {
        get => ConversionWorkspace.SubtitleLanguage;
        set => ConversionWorkspace.SubtitleLanguage = value;
    }

    public string VoiceId
    {
        get => ConversionWorkspace.VoiceId;
        set => ConversionWorkspace.VoiceId = value;
    }

    public int AudioStreamIndex
    {
        get => ConversionWorkspace.AudioStreamIndex;
        set => ConversionWorkspace.AudioStreamIndex = value;
    }

    public string OnlineUrl
    {
        get => OnlineVideo.OnlineUrl;
        set => OnlineVideo.OnlineUrl = value;
    }

    public string OnlineProxy
    {
        get => OnlineVideo.OnlineProxy;
        set => OnlineVideo.OnlineProxy = value;
    }

    public bool HasVerificationSession => OnlineVideo.HasVerificationSession;
    public string VerificationSummary => OnlineVideo.VerificationSummary;

    public int DownloadMaxHeight
    {
        get => OnlineVideo.DownloadMaxHeight;
        set => OnlineVideo.DownloadMaxHeight = value;
    }

    public string OnlineSummary => OnlineVideo.OnlineSummary;

    public event Action<string>? VoicePreviewReady;

    public ObservableCollection<AudioTrack> AudioTracks => ConversionWorkspace.AudioTracks;
    public ObservableCollection<LanguageOption> SourceLanguages => ConversionWorkspace.SourceLanguages;
    public ObservableCollection<LanguageOption> SubtitleLanguages => ConversionWorkspace.SubtitleLanguages;
    public ObservableCollection<LanguageOption> SpeechLanguages => ConversionWorkspace.SpeechLanguages;
    public ObservableCollection<VoiceOption> Voices => ConversionWorkspace.Voices;
    public ObservableCollection<TranscriptSegment> Segments => ConversionWorkspace.Segments;
    public ObservableCollection<ConversionProject> History => ConversionWorkspace.History;
    public IReadOnlyList<KeyValuePair<OriginalAudioMode, string>> AudioModes => ConversionWorkspace.AudioModes;
    public IReadOnlyList<KeyValuePair<VideoEncodingMode, string>> VideoEncodingModes => ConversionWorkspace.VideoEncodingModes;
    public IReadOnlyList<KeyValuePair<OutputVideoQuality, string>> OutputQualities => ConversionWorkspace.OutputQualities;
    public IReadOnlyList<KeyValuePair<int, string>> DownloadQualities => OnlineVideo.DownloadQualities;

    public AsyncCommand ImportCommand => ConversionWorkspace.ImportCommand;
    public AsyncCommand InspectOnlineCommand => OnlineVideo.InspectOnlineCommand;
    public AsyncCommand DownloadOnlineCommand => OnlineVideo.DownloadOnlineCommand;
    public AsyncCommand OpenVerificationCommand => OnlineVideo.OpenVerificationCommand;
    public AsyncCommand UseVerificationCommand => OnlineVideo.UseVerificationCommand;
    public AsyncCommand CloseVerificationCommand => OnlineVideo.CloseVerificationCommand;
    public AsyncCommand ClearWebsiteSessionCommand => OnlineVideo.ClearWebsiteSessionCommand;
    public AsyncCommand CheckOnlineToolsCommand => ServiceSettings.CheckOnlineToolsCommand;
    public AsyncCommand SaveSettingsCommand => ServiceSettings.SaveSettingsCommand;
    public AsyncCommand RefreshCapabilitiesCommand => ServiceSettings.RefreshCapabilitiesCommand;
    public AsyncCommand TestServiceCommand => ServiceSettings.TestServiceCommand;
    public AsyncCommand CheckMediaCommand => ServiceSettings.CheckMediaCommand;
    public AsyncCommand PrepareCommand => ConversionWorkspace.PrepareCommand;
    public AsyncCommand ExportCommand => ConversionWorkspace.ExportCommand;
    public AsyncCommand PreviewVoiceCommand => ConversionWorkspace.PreviewVoiceCommand;
    public AsyncCommand SaveEditsCommand => ConversionWorkspace.SaveEditsCommand;
    public AsyncCommand RefreshHistoryCommand => ConversionWorkspace.RefreshHistoryCommand;
    public AsyncCommand RestoreCommand => ConversionWorkspace.RestoreCommand;
    public RelayCommand CancelCommand { get; }
    public RelayCommand ResetCommand => ConversionWorkspace.ResetCommand;
    public RelayCommand ChooseOutputCommand => ConversionWorkspace.ChooseOutputCommand;
    public RelayCommand BrowseToolCommand => ServiceSettings.BrowseToolCommand;
    public RelayCommand BrowseDirectoryCommand => ServiceSettings.BrowseDirectoryCommand;
    public RelayCommand BrowseDownloadDirectoryCommand => ServiceSettings.BrowseDownloadDirectoryCommand;
    public RelayCommand BrowseCookiesCommand => ServiceSettings.BrowseCookiesCommand;
    public RelayCommand ClearCookiesCommand => ServiceSettings.ClearCookiesCommand;
    public RelayCommand PlayResultCommand => ConversionWorkspace.PlayResultCommand;
    public RelayCommand OpenOutputFolderCommand => ConversionWorkspace.OpenOutputFolderCommand;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await Workspace.RunAsync("加载本地设置", async ct =>
        {
            Workspace.Settings = await _settingsStore.LoadAsync(ct);
            LocalVoice.NotifySettingsChanged();
            try { await LocalVoice.RefreshLocalVoicesCoreAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { LocalVoice.LocalVoiceStatus = "音色列表未加载：" + Workspace.SafeMessage(ex); }
            await Recording.RefreshRecordingLibraryCoreAsync(ct);
            await ConversionWorkspace.RefreshHistoryCoreAsync(ct);
            try { await ServiceSettings.RefreshCapabilitiesCoreAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { ServiceSettings.CapabilityDescription = "能力目录未更新：" + Workspace.SafeMessage(ex); }
            Workspace.Status = "已加载本地设置。可验证服务连接，或在「我的声音」配置本地配音，再导入视频。";
        });
    }

    public void Cancel() => Workspace.Cancel();

    public async Task CancelAndWaitAsync()
    {
        await Workspace.CancelAndWaitCoreAsync();
        await Recording.ShutdownAsync();
        await TrainingWebUi.ShutdownAsync();
        await OnlineVideo.CloseVerificationCoreAsync();
    }

    public void ReportPreviewError(string message) =>
        Workspace.Status = "内置播放器无法播放此媒体，可使用「外部播放结果」：" + message;

    public void ReportShutdownError()
    {
        Workspace.Stage = "需要处理";
        Workspace.Status = "任务或服务尚未完成关闭，请检查录音、训练服务和验证窗口的状态，停止后再退出。";
    }
}
