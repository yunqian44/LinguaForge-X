using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using Microsoft.Win32;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly IServiceCatalog _catalog;
    private readonly IMediaService _media;
    private readonly IConversionService _conversion;
    private readonly IProjectStore _projects;
    private readonly ISynthesisService _synthesis;
    private readonly IOnlineVideoService _online;
    private readonly IWebsiteVerificationService _verification;
    private readonly IWebsiteSessionStore _websiteSessions;
    private IWebsiteVerificationSession? _verificationSession;
    private string _verificationUrl = "";
    private string _verificationProxy = "";
    private string _verificationNotice = "遇到登录或人机验证时，可打开网页登录／验证。";
    private readonly HashSet<VideoWebsite> _savedWebsiteSessions = [];
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _operationCompletion;
    private bool _changingChoices;
    private IReadOnlyList<LanguageOption> _translationLanguages = [];
    private AppSettings _settings = new();
    private ConversionOptions _options = new();
    private ConversionProject? _project;
    private VideoInfo? _video;
    private bool _isBusy;
    private bool _loaded;
    private string _status = "请先在「服务设置」配置 Azure 服务和 FFmpeg，再导入视频。";
    private string _stage = "就绪";
    private double _progress;
    private string _capabilityDescription = "语言目录尚未加载。目录支持不代表当前服务凭据已验证。";
    private int _selectedTab;
    private ConversionProject? _selectedHistory;
    private string? _previewPath;
    private string _transcriptionStatus = "未验证";
    private string _translationStatus = "未验证";
    private string _synthesisStatus = "未验证";
    private IReadOnlyList<VoiceOption> _allVoices = [];
    private string _onlineUrl = "";
    private int _downloadMaxHeight = 1080;
    private OnlineVideoInfo? _onlineInfo;

    public MainViewModel(ISettingsStore settingsStore, IServiceCatalog catalog, IMediaService media,
        IConversionService conversion, IProjectStore projects, ISynthesisService synthesis, IOnlineVideoService online,
        IWebsiteVerificationService verification, IWebsiteSessionStore websiteSessions, ILocalVoiceService? localVoice = null)
    {
        _settingsStore = settingsStore;
        _catalog = catalog;
        _media = media;
        _conversion = conversion;
        _projects = projects;
        _synthesis = synthesis;
        _online = online;
        _verification = verification;
        _websiteSessions = websiteSessions;
        InitializeLocalVoice(localVoice);
        ImportCommand = new(_ => ImportAsync(), _ => CanConfigure);
        InspectOnlineCommand = new(_ => InspectOnlineAsync(), _ => CanConfigure && !HasVerificationSession && !string.IsNullOrWhiteSpace(OnlineUrl));
        DownloadOnlineCommand = new(_ => DownloadOnlineAsync(), _ => CanConfigure && !HasVerificationSession && !string.IsNullOrWhiteSpace(OnlineUrl));
        OpenVerificationCommand = new(_ => OpenVerificationAsync(), _ => CanConfigure && !HasVerificationSession && VideoWebsites.FromUrl(OnlineUrl.Trim()) is not null);
        UseVerificationCommand = new(_ => UseVerificationAsync(), _ => CanConfigure && VerificationMatchesSource);
        CloseVerificationCommand = new(_ => RunAsync("关闭验证窗口", async _ =>
        {
            await CloseVerificationCoreAsync();
            _verificationNotice = "验证窗口已关闭。已保存的会话仍可用于该站下载。";
            NotifyVerification();
            Status = _verificationNotice;
        }), _ => !IsBusy && HasVerificationSession);
        ClearWebsiteSessionCommand = new(_ => ClearWebsiteSessionAsync(), _ => !IsBusy && !HasVerificationSession && VideoWebsites.FromUrl(OnlineUrl.Trim()) is not null);
        CheckOnlineToolsCommand = new(_ => RunAsync("检查下载工具", async ct =>
        {
            Status = await _online.CheckAvailableAsync(Clone(Settings.OnlineVideo), ct);
        }), _ => !IsBusy);
        SaveSettingsCommand = new(_ => SaveSettingsAsync(), _ => !IsBusy);
        RefreshCapabilitiesCommand = new(_ => RunAsync("加载能力目录", RefreshCapabilitiesCoreAsync), _ => !IsBusy);
        TestServiceCommand = new(TestServiceAsync, _ => !IsBusy);
        CheckMediaCommand = new(_ => RunAsync("检查本地工具", async ct =>
        {
            await _media.CheckAvailableAsync(Settings.Media, ct);
            Status = "FFmpeg 与 FFprobe 可用。";
        }), _ => !IsBusy);
        PrepareCommand = new(_ => PrepareAsync(), _ => CanConfigure && _video is not null);
        ExportCommand = new(_ => ExportAsync(), _ => !IsBusy && Project?.PreparationComplete == true && Segments.Count > 0);
        PreviewVoiceCommand = new(_ => PreviewVoiceAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(Options.VoiceId));
        SaveEditsCommand = new(_ => RunAsync("保存校对结果", async ct =>
        {
            if (Project is null) return;
            Project.Segments = Segments.ToList();
            await _projects.SaveAsync(Project, ct);
            Status = "校对结果已保存，可在任务历史中继续。";
            await RefreshHistoryCoreAsync(ct);
        }), _ => !IsBusy && HasProject);
        RefreshHistoryCommand = new(_ => RunAsync("加载任务历史", RefreshHistoryCoreAsync), _ => !IsBusy);
        RestoreCommand = new(_ => RestoreAsync(), _ => !IsBusy && SelectedHistory is not null);
        CancelCommand = new(_ => { Status = "正在取消，请等待当前步骤结束…"; _cancellation?.Cancel(); }, _ => IsBusy);
        ResetCommand = new(_ => ResetProject(), _ => !IsBusy && HasProject);
        ChooseOutputCommand = new(_ => ExecuteUiAction(ChooseOutput), _ => !IsBusy);
        BrowseToolCommand = new(p => ExecuteUiAction(() => BrowseTool(p)), _ => !IsBusy);
        BrowseDirectoryCommand = new(_ => ExecuteUiAction(BrowseOutputDirectory), _ => !IsBusy);
        BrowseDownloadDirectoryCommand = new(_ => ExecuteUiAction(BrowseDownloadDirectory), _ => !IsBusy);
        BrowseCookiesCommand = new(_ => ExecuteUiAction(BrowseCookies), _ => !IsBusy);
        ClearCookiesCommand = new(_ => { Settings.OnlineVideo.CookiesFilePath = ""; OnPropertyChanged(nameof(Settings)); NotifyVerification(); }, _ => !IsBusy);
        PlayResultCommand = new(_ => OpenResult(), _ => !IsBusy && Project?.ResultPath is not null);
        OpenOutputFolderCommand = new(_ => OpenOutputFolder(), _ => !IsBusy && !string.IsNullOrWhiteSpace(Options.OutputPath));
    }

    public AppSettings Settings { get => _settings; private set { Set(ref _settings, value); OnPropertyChanged(nameof(OnlineProxy)); NotifyVerification(); } }
    public ConversionOptions Options { get => _options; private set { Set(ref _options, value); NotifySelections(); OnPropertyChanged(nameof(SourceSummary)); } }
    public ConversionProject? Project
    {
        get => _project;
        private set { Set(ref _project, value); OnPropertyChanged(nameof(HasProject)); OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(ProjectSummary)); OnPropertyChanged(nameof(OnlineSummary)); RefreshCommands(); }
    }
    public bool HasProject => Project is not null;
    public bool CanConfigure => !IsBusy && !HasProject;
    public bool IsBusy
    {
        get => _isBusy;
        private set { Set(ref _isBusy, value); OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(CanConfigure)); RefreshCommands(); }
    }
    public bool IsIdle => !IsBusy;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Stage { get => _stage; private set => Set(ref _stage, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string CapabilityDescription { get => _capabilityDescription; private set => Set(ref _capabilityDescription, value); }
    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }
    public ConversionProject? SelectedHistory { get => _selectedHistory; set { Set(ref _selectedHistory, value); RefreshCommands(); } }
    public string? PreviewPath { get => _previewPath; private set => Set(ref _previewPath, value); }
    public string TranscriptionStatus { get => _transcriptionStatus; private set => Set(ref _transcriptionStatus, value); }
    public string TranslationStatus { get => _translationStatus; private set => Set(ref _translationStatus, value); }
    public string SynthesisStatus { get => _synthesisStatus; private set => Set(ref _synthesisStatus, value); }
    public string OnlineUrl
    {
        get => _onlineUrl;
        set
        {
            if (HasProject || !Set(ref _onlineUrl, value)) return;
            _onlineInfo = null;
            OnPropertyChanged(nameof(OnlineSummary));
            _verificationNotice = "遇到登录或人机验证时，可打开网页登录／验证。";
            NotifyVerification();
            RefreshCommands();
        }
    }
    public string OnlineProxy
    {
        get => Settings.OnlineVideo.Proxy;
        set
        {
            if (Settings.OnlineVideo.Proxy == value) return;
            Settings.OnlineVideo.Proxy = value;
            OnPropertyChanged();
            NotifyVerification();
        }
    }
    public bool HasVerificationSession => _verificationSession is not null;
    private bool VerificationMatchesSource => _verificationSession is not null &&
        OnlineUrl.Trim() == _verificationUrl && Settings.OnlineVideo.Proxy.Trim() == _verificationProxy &&
        VideoWebsites.FromUrl(OnlineUrl.Trim()) == _verificationSession.Website;
    public string VerificationSummary
    {
        get
        {
            if (HasVerificationSession)
                return VerificationMatchesSource
                    ? $"{_verificationSession!.BrowserName} 验证窗口已打开：请亲自在网站完成登录／验证并确认视频可播放，然后点击「使用会话并重试」。"
                    : "链接或代理已变更。当前验证会话不能使用，请关闭验证窗口后重新打开。";
            var saved = VideoWebsites.FromUrl(OnlineUrl.Trim()) is { } website && _savedWebsiteSessions.Contains(website)
                ? "该站会话已加密保存在本机；是否可用以读取信息或下载结果为准。 " : "";
            var manual = string.IsNullOrWhiteSpace(Settings.OnlineVideo.CookiesFilePath)
                ? "" : " 当前已选择手动 Cookie 文件，读取和下载优先使用该文件；使用新验证会话会清除该路径设置。";
            return saved + _verificationNotice + manual;
        }
    }
    public int DownloadMaxHeight { get => _downloadMaxHeight; set { if (!HasProject) Set(ref _downloadMaxHeight, value); } }
    public string OnlineSummary => HasProject ? "当前任务使用已保存的视频素材。下载清晰度仅在下载时生效；更换素材请先点击「重新设置」。" :
        _onlineInfo is null ? "粘贴 B 站、YouTube 等网站的单个视频链接，可先查看信息，也可直接下载导入。" :
        $"{_onlineInfo.Title}  ·  {_onlineInfo.Platform}  ·  {(_onlineInfo.DurationSeconds > 0 ? TimeSpan.FromSeconds(_onlineInfo.DurationSeconds).ToString(@"hh\:mm\:ss") : "时长未知")}";
    public string SourceSummary => string.IsNullOrWhiteSpace(Options.SourceUrl) ? "" : $"在线来源：{Options.SourceTitle}\n{Options.SourceUrl}";
    public string VideoSummary => _video is null ? "尚未导入视频" : $"{Path.GetFileName(_video.FilePath)}  ·  {TimeSpan.FromSeconds(_video.DurationSeconds):hh\\:mm\\:ss}  ·  {_video.Width} × {_video.Height}  ·  {_video.AudioTracks.Count} 条音轨";
    public string ProjectSummary => Project is null ? "先选择输出选项，再生成可校对的字幕与配音稿。" :
        $"当前任务：{Project.Options.SourceLanguage} → 字幕 {Project.Options.SubtitleLanguage} / 配音 {Project.Options.SpeechLanguage}。音色、输出画质与导出编码可直接修改；更改语言或生成模式，请先点击「重新设置」。";
    public string SpeechLanguage
    {
        get => Options.SpeechLanguage;
        set { if (_changingChoices || HasProject || Options.SpeechLanguage == value || string.IsNullOrWhiteSpace(value)) return; Options.SpeechLanguage = value; OnPropertyChanged(); FilterVoices(); }
    }
    public string SourceLanguage
    {
        get => Options.SourceLanguage;
        set { if (_changingChoices || HasProject || Options.SourceLanguage == value || string.IsNullOrWhiteSpace(value)) return; Options.SourceLanguage = value; OnPropertyChanged(); RebuildTargetLanguages(); }
    }
    public string SubtitleLanguage
    {
        get => Options.SubtitleLanguage;
        set { if (_changingChoices || HasProject || string.IsNullOrWhiteSpace(value)) return; Options.SubtitleLanguage = value; OnPropertyChanged(); }
    }
    public string VoiceId
    {
        get => Options.VoiceId;
        set { if (_changingChoices || IsBusy || string.IsNullOrWhiteSpace(value)) return; Options.VoiceId = value; OnPropertyChanged(); RefreshCommands(); }
    }
    public int AudioStreamIndex
    {
        get => Options.AudioStreamIndex;
        set { if (_changingChoices || HasProject) return; Options.AudioStreamIndex = value; OnPropertyChanged(); }
    }
    public event Action<string>? VoicePreviewReady;

    public ObservableCollection<AudioTrack> AudioTracks { get; } = [];
    public ObservableCollection<LanguageOption> SourceLanguages { get; } = [];
    public ObservableCollection<LanguageOption> SubtitleLanguages { get; } = [];
    public ObservableCollection<LanguageOption> SpeechLanguages { get; } = [];
    public ObservableCollection<VoiceOption> Voices { get; } = [];
    public ObservableCollection<TranscriptSegment> Segments { get; } = [];
    public ObservableCollection<ConversionProject> History { get; } = [];
    public IReadOnlyList<KeyValuePair<OriginalAudioMode, string>> AudioModes { get; } =
    [new(OriginalAudioMode.Replace, "完全替换原音轨（原音乐也会移除）"), new(OriginalAudioMode.QuietMix, "降低原音后混合（仍会有英文人声）")];
    public IReadOnlyList<KeyValuePair<VideoEncodingMode, string>> VideoEncodingModes { get; } =
    [new(VideoEncodingMode.Auto, "自动加速（推荐）"), new(VideoEncodingMode.CpuFast, "CPU 快速"), new(VideoEncodingMode.CpuQuality, "CPU 画质与体积优先")];
    public IReadOnlyList<KeyValuePair<OutputVideoQuality, string>> OutputQualities { get; } =
    [new(OutputVideoQuality.Original, "保持源分辨率（推荐）"), new(OutputVideoQuality.UltraHd2160, "4K（最高2160p）"),
     new(OutputVideoQuality.FullHd1080, "全高清（最高1080p）"), new(OutputVideoQuality.Hd720, "高清（最高720p）"), new(OutputVideoQuality.Compact480, "标清（最高480p）")];
    public IReadOnlyList<KeyValuePair<int, string>> DownloadQualities { get; } =
    [new(720, "最高 720p"), new(1080, "最高 1080p"), new(2160, "最高 4K"), new(0, "最佳可用画质")];

    public AsyncCommand ImportCommand { get; }
    public AsyncCommand InspectOnlineCommand { get; }
    public AsyncCommand DownloadOnlineCommand { get; }
    public AsyncCommand OpenVerificationCommand { get; }
    public AsyncCommand UseVerificationCommand { get; }
    public AsyncCommand CloseVerificationCommand { get; }
    public AsyncCommand ClearWebsiteSessionCommand { get; }
    public AsyncCommand CheckOnlineToolsCommand { get; }
    public AsyncCommand SaveSettingsCommand { get; }
    public AsyncCommand RefreshCapabilitiesCommand { get; }
    public AsyncCommand TestServiceCommand { get; }
    public AsyncCommand CheckMediaCommand { get; }
    public AsyncCommand PrepareCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand PreviewVoiceCommand { get; }
    public AsyncCommand SaveEditsCommand { get; }
    public AsyncCommand RefreshHistoryCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ChooseOutputCommand { get; }
    public RelayCommand BrowseToolCommand { get; }
    public RelayCommand BrowseDirectoryCommand { get; }
    public RelayCommand BrowseDownloadDirectoryCommand { get; }
    public RelayCommand BrowseCookiesCommand { get; }
    public RelayCommand ClearCookiesCommand { get; }
    public RelayCommand PlayResultCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RunAsync("加载本地设置", async ct =>
        {
            Settings = await _settingsStore.LoadAsync(ct);
            OnPropertyChanged(nameof(LocalVoiceSettings));
            try { await RefreshLocalVoicesCoreAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { LocalVoiceStatus = "音色列表未加载：" + SafeMessage(ex); }
            await RefreshHistoryCoreAsync(ct);
            try { await RefreshCapabilitiesCoreAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { CapabilityDescription = "能力目录未更新：" + SafeMessage(ex); }
            Status = "已加载本地设置。可验证服务连接，或在「我的声音」配置本地配音，再导入视频。";
        });
    }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog { Title = "选择本地视频", Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.ts|所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        await RunAsync("读取视频", async ct =>
        {
            await CloseVerificationCoreAsync();
            var media = Clone(Settings.Media);
            await _media.CheckAvailableAsync(media, ct);
            var video = await _media.ProbeAsync(dialog.FileName, media, ct);
            ct.ThrowIfCancellationRequested();
            ApplyImportedVideo(video, media, null);
            Status = "视频已读取。选择语言和输出模式，然后生成字幕与配音稿。";
        });
    }

    private Task InspectOnlineAsync() => RunAsync("读取在线视频信息", async ct => { await InspectOnlineCoreAsync(ct); });

    private async Task<bool> InspectOnlineCoreAsync(CancellationToken ct)
    {
        var request = new OnlineVideoRequest { Url = OnlineUrl.Trim(), MaxHeight = DownloadMaxHeight };
        var info = await _online.InspectAsync(request, Clone(Settings.OnlineVideo), ct);
        ct.ThrowIfCancellationRequested();
        if (request.Url != OnlineUrl.Trim()) { Status = "视频链接已变更，请重新读取信息。"; return false; }
        _onlineInfo = info;
        OnPropertyChanged(nameof(OnlineSummary));
        Status = "视频信息已加载，可下载并导入。实际画质取决于网站可提供的格式。";
        return true;
    }

    private Task OpenVerificationAsync() => RunAsync("打开网页登录／验证", async ct =>
    {
        var url = OnlineUrl.Trim();
        var proxy = Settings.OnlineVideo.Proxy.Trim();
        if (VideoWebsites.FromUrl(url) is null) throw new InvalidOperationException("网页登录／验证支持 YouTube 和 B 站的 HTTPS 视频链接。");
        var session = await _verification.OpenAsync(url, proxy, ct);
        if (ct.IsCancellationRequested || url != OnlineUrl.Trim() || proxy != Settings.OnlineVideo.Proxy.Trim())
        {
            await session.DisposeAsync();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("链接或代理已变更，刚打开的验证窗口已关闭，请重新打开。");
        }
        _verificationSession = session;
        _onlineInfo = null;
        OnPropertyChanged(nameof(OnlineSummary));
        _verificationUrl = url;
        _verificationProxy = proxy;
        NotifyVerification();
        Status = VerificationSummary;
    });

    private Task UseVerificationAsync() => RunAsync("保存网站会话并重试", async ct =>
    {
        if (!VerificationMatchesSource) throw new InvalidOperationException("链接或代理已变更，请关闭验证窗口后重新打开。");
        var session = _verificationSession!;
        var url = _verificationUrl;
        var proxy = _verificationProxy;
        var captured = await session.CaptureAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (!VerificationMatchesSource || captured.Website != session.Website || captured.Proxy.Trim() != _verificationProxy)
            throw new InvalidOperationException("链接或代理已变更，本次会话未保存，请关闭验证窗口后重新打开。");
        await _websiteSessions.SaveAsync(captured, ct);
        _savedWebsiteSessions.Add(captured.Website);
        _verificationNotice = "会话已保存，正在读取视频信息确认是否可用。";
        await CloseVerificationCoreAsync();
        Settings.OnlineVideo.CookiesFilePath = "";
        OnPropertyChanged(nameof(Settings));
        NotifyVerification();
        try
        {
            // Persist the explicit preference without refreshing or calling Azure services.
            await _settingsStore.SaveAsync(Settings, ct);
            if (url != OnlineUrl.Trim() || proxy != Settings.OnlineVideo.Proxy.Trim())
                throw new InvalidOperationException("链接或代理已变更。会话已保存，请检查当前链接和代理后重新读取信息。");
            _verificationNotice = await InspectOnlineCoreAsync(ct)
                ? "已成功读取视频信息，可以下载并导入。网站之后仍可能要求重新验证。"
                : "会话已保存，但视频链接已变更，请重新读取当前视频信息。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _verificationNotice = "会话已保存，但本次重试未完成：" + SafeMessage(ex);
            throw;
        }
        finally { NotifyVerification(); }
    });

    private Task ClearWebsiteSessionAsync() => RunAsync("清除该站会话", async ct =>
    {
        var website = VideoWebsites.FromUrl(OnlineUrl.Trim()) ?? throw new InvalidOperationException("请先输入 YouTube 或 B 站链接。");
        await _websiteSessions.DeleteAsync(website, ct);
        _savedWebsiteSessions.Remove(website);
        _verificationNotice = "已清除该站保存的验证会话。";
        NotifyVerification();
        Status = VerificationSummary;
    });

    private async Task CloseVerificationCoreAsync()
    {
        if (_verificationSession is not { } session) return;
        await session.DisposeAsync();
        _verificationSession = null;
        _verificationUrl = _verificationProxy = "";
        NotifyVerification();
    }

    private void NotifyVerification()
    {
        OnPropertyChanged(nameof(HasVerificationSession));
        OnPropertyChanged(nameof(VerificationSummary));
        RefreshCommands();
    }

    private Task DownloadOnlineAsync() => RunAsync("下载在线视频", async ct =>
    {
        var request = new OnlineVideoRequest { Url = OnlineUrl.Trim(), MaxHeight = DownloadMaxHeight };
        var settings = Clone(Settings.OnlineVideo);
        var media = Clone(Settings.Media);
        await _media.CheckAvailableAsync(media, ct);
        var download = await _online.DownloadAsync(request, settings, media, CreateProgress(), ct);
        var video = await _media.ProbeAsync(download.FilePath, media, ct);
        ct.ThrowIfCancellationRequested();
        if (request.Url != OnlineUrl.Trim()) { Status = "视频已下载，但链接已变更。文件保存在：" + download.FilePath; return; }
        ApplyImportedVideo(video, media, download.Video);
        _onlineInfo = download.Video;
        OnPropertyChanged(nameof(OnlineSummary));
        Status = "在线视频已下载并导入。选择字幕／配音语言，然后生成字幕与配音稿。";
    });

    private void ApplyImportedVideo(VideoInfo video, MediaSettings media, OnlineVideoInfo? source)
    {
        if (video.AudioTracks.Count == 0) throw new InvalidOperationException("这个视频没有音轨，无法转写或配音。");
        var directory = media.OutputDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            var videosDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            directory = source is not null && !string.IsNullOrWhiteSpace(videosDirectory)
                ? Path.Combine(videosDirectory, "LinguaForge-X") : Path.GetDirectoryName(video.FilePath)!;
        }
        var title = source?.Title ?? Path.GetFileNameWithoutExtension(video.FilePath);
        var filename = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(filename)) filename = "在线视频";
        if (filename.Length > 100) filename = filename[..100];
        var outputPath = Path.Combine(directory, filename + "_中文.mp4");
        _video = video;
        Options.InputPath = video.FilePath;
        Options.AudioStreamIndex = video.AudioTracks[0].StreamIndex;
        Options.OutputPath = outputPath;
        Options.SourceUrl = source?.WebpageUrl ?? "";
        Options.SourceTitle = source?.Title ?? "";
        if (source is null) SetOnlineSource("");
        ReplaceItems(AudioTracks, video.AudioTracks);
        PreviewPath = video.FilePath;
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(SourceSummary));
        NotifySelections();
        OnPropertyChanged(nameof(VideoSummary));
        RefreshCommands();
    }

    private Task SaveSettingsAsync() => RunAsync("保存服务设置", async ct =>
    {
        await _settingsStore.SaveAsync(Settings, ct);
        TranscriptionStatus = TranslationStatus = SynthesisStatus = "设置已保存，连接未验证";
        Status = "设置已保存到程序目录的 settings.json；密钥与代理凭据由 Windows 当前用户保护，Cookie 文件仅保存路径。";
        try { await RefreshCapabilitiesCoreAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { CapabilityDescription = "设置已保存，但能力目录刷新失败：" + SafeMessage(ex); }
    });

    private Task TestServiceAsync(object? parameter) => RunAsync("测试服务连接", async ct =>
    {
        if (!Enum.TryParse<ServiceKind>(parameter?.ToString(), out var kind)) return;
        var service = kind switch { ServiceKind.Transcription => Settings.Transcription, ServiceKind.Translation => Settings.Translation, _ => Settings.Synthesis };
        var result = await _catalog.TestConnectionAsync(Clone(service), kind, ct);
        var message = $"上次测试（{DateTime.Now:HH:mm}）：" + (result.Success ? "通过。" : "失败。") + result.Message;
        switch (kind)
        {
            case ServiceKind.Transcription: TranscriptionStatus = message; break;
            case ServiceKind.Translation: TranslationStatus = message; break;
            case ServiceKind.Synthesis: SynthesisStatus = message; break;
        }
        Status = message;
    });

    private async Task RefreshCapabilitiesCoreAsync(CancellationToken ct)
    {
        var capabilities = await _catalog.GetCapabilitiesAsync(Clone(Settings), ct);
        _allVoices = capabilities.Voices;
        _translationLanguages = capabilities.TranslationLanguages;
        _changingChoices = true;
        var sources = capabilities.SourceLanguages.ToList();
        if (HasProject && !sources.Any(x => x.Code == Options.SourceLanguage)) sources.Add(new(Options.SourceLanguage, Options.SourceLanguage + "（任务原选项）"));
        ReplaceItems(SourceLanguages, sources);
        if (!HasProject) EnsureSelection(SourceLanguages, Options.SourceLanguage, x => Options.SourceLanguage = x);
        _changingChoices = false;
        RebuildTargetLanguages();
        NotifySelections();
        CapabilityDescription = capabilities.Description + " 语言与音色目录不等同于连接验证；更改配置后请重新测试。";
    }

    private void RebuildTargetLanguages()
    {
        _changingChoices = true;
        var subtitles = new[] { new LanguageOption("source", "保持原语言") }.Concat(_translationLanguages).DistinctBy(x => x.Code).ToList();
        if (HasProject && !subtitles.Any(x => x.Code == Options.SubtitleLanguage)) subtitles.Add(new(Options.SubtitleLanguage, Options.SubtitleLanguage + "（任务原选项）"));
        ReplaceItems(SubtitleLanguages, subtitles);
        var locales = _allVoices.SelectMany(x => x.SupportedLocales.Append(x.Locale)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var speechLanguages = _translationLanguages.Concat(SourceLanguages.Where(x => x.Code == Options.SourceLanguage))
            .Where(x => x.Code != "auto" && locales.Any(l => LocaleMatches(x.Code, l))).DistinctBy(x => x.Code).ToList();
        if (HasProject && !speechLanguages.Any(x => x.Code == Options.SpeechLanguage)) speechLanguages.Add(new(Options.SpeechLanguage, Options.SpeechLanguage + "（任务原选项）"));
        ReplaceItems(SpeechLanguages, speechLanguages);
        if (!HasProject)
        {
            EnsureSelection(SubtitleLanguages, Options.SubtitleLanguage, x => Options.SubtitleLanguage = x);
            EnsureSelection(SpeechLanguages, Options.SpeechLanguage, x => Options.SpeechLanguage = x);
        }
        _changingChoices = false;
        FilterVoices();
        NotifySelections();
    }

    private void FilterVoices()
    {
        var voiceId = Options.VoiceId;
        _changingChoices = true;
        var voices = _allVoices.Where(v => v.SupportedLocales.Append(v.Locale).Any(l => LocaleMatches(l, Options.SpeechLanguage))).ToList();
        if (HasProject && !string.IsNullOrWhiteSpace(voiceId) && !voices.Any(v => v.Id == voiceId)) voices.Add(new(voiceId, voiceId + "（任务原音色，当前目录未验证）", Options.SpeechLanguage, [Options.SpeechLanguage]));
        ReplaceItems(Voices, voices);
        if (!HasProject) Options.VoiceId = Voices.FirstOrDefault(v => v.Id == voiceId)?.Id ?? Voices.FirstOrDefault()?.Id ?? "";
        _changingChoices = false;
        OnPropertyChanged(nameof(VoiceId));
        RefreshCommands();
    }

    private Task PrepareAsync() => RunAsync("生成字幕与配音稿", async ct =>
    {
        await CloseVerificationCoreAsync();
        if (!Options.GenerateSubtitles && !Options.GenerateSpeech) throw new InvalidOperationException("请至少选择添加字幕或生成配音。");
        if (Options.GenerateSpeech && string.IsNullOrWhiteSpace(Options.VoiceId)) throw new InvalidOperationException("当前语言没有可用音色。请配置配音服务并刷新能力目录。");
        var project = await _conversion.PrepareAsync(Clone(Options), Clone(Settings), CreateProgress(), ct);
        SetProject(project);
        await RefreshHistoryCoreAsync(ct);
        Status = $"已生成 {Segments.Count} 段。请校对时间与文本，再导出视频。";
    });

    private Task ExportAsync() => RunAsync("合成并导出视频", async ct =>
    {
        if (Project is null) return;
        Project.Segments = Segments.ToList();
        if (Project.Segments.Any(s => !double.IsFinite(s.StartSeconds) || !double.IsFinite(s.EndSeconds) || s.StartSeconds < 0 || s.EndSeconds <= s.StartSeconds || s.EndSeconds > Project.Video.DurationSeconds + 0.5))
            throw new InvalidOperationException("请检查字幕时间：开始时间须 ≥ 0，结束时间须晚于开始时间且不能超出视频时长。");
        if (string.IsNullOrWhiteSpace(Project.Options.OutputPath)) throw new InvalidOperationException("请选择导出视频的保存位置。");
        if (File.Exists(Project.Options.OutputPath))
        {
            throw new InvalidOperationException("目标文件已存在，请选择新的输出文件名，以保留现有视频。");
        }
        var exporting = Project;
        // The pipeline works on a copy. Preserve the user's latest edits before it can save revisions.
        await _projects.SaveAsync(exporting, ct);
        ConversionResult result;
        try { result = await _conversion.ExportAsync(exporting, Clone(Settings), CreateProgress(), ct); }
        finally
        {
            // Duration fitting may have saved revised text before an export failure or cancellation.
            try
            {
                await RefreshHistoryCoreAsync(CancellationToken.None);
                if (History.FirstOrDefault(item => item.Id == exporting.Id) is { } persisted) SetProject(persisted);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { /* A history read failure must not replace the original export result or failure. */ }
        }
        Project.ResultPath = result.VideoPath;
        Project.Status = "已完成";
        PreviewPath = result.VideoPath;
        Status = "导出完成：" + result.VideoPath + (result.SubtitlePath is null ? "" : "；字幕：" + result.SubtitlePath);
    });

    private Task RestoreAsync() => RunAsync("恢复任务", async ct =>
    {
        if (SelectedHistory is null) return;
        await CloseVerificationCoreAsync();
        var project = Clone(SelectedHistory);
        if (!File.Exists(project.Options.InputPath)) throw new FileNotFoundException("原视频已移动或删除，无法恢复：" + project.Options.InputPath);
        if (project.PreparationComplete) SetProject(project);
        else
        {
            Project = null;
            _video = project.Video;
            Options = Clone(project.Options);
            SetOnlineSource(project.Options.SourceUrl);
            ReplaceItems(AudioTracks, project.Video.AudioTracks);
            Segments.Clear();
            PreviewPath = project.Options.InputPath;
            RebuildTargetLanguages();
            OnPropertyChanged(nameof(VideoSummary));
            NotifySelections();
        }
        SelectedTab = 0;
        Status = project.PreparationComplete ? "已恢复任务和生成时的输出选项。可继续校对并重新导出。" : "此任务尚未完成转写与翻译。请点击「生成字幕与配音稿」继续，已完成的片段将尝试复用缓存。";
        await Task.CompletedTask;
    });

    private void SetProject(ConversionProject project)
    {
        _video = project.Video;
        Project = project;
        Options = project.Options;
        SetOnlineSource(project.Options.SourceUrl);
        if (!SourceLanguages.Any(x => x.Code == project.Options.SourceLanguage))
            SourceLanguages.Add(new(project.Options.SourceLanguage, project.Options.SourceLanguage + "（任务原选项）"));
        ReplaceItems(AudioTracks, project.Video.AudioTracks);
        RebuildTargetLanguages();
        NotifySelections();
        ReplaceItems(Segments, project.Segments);
        PreviewPath = File.Exists(project.ResultPath) ? project.ResultPath : project.Options.InputPath;
        OnPropertyChanged(nameof(VideoSummary));
        RefreshCommands();
    }

    private void ResetProject()
    {
        if (Project is not null) Options = Clone(Project.Options);
        Project = null;
        Segments.Clear();
        RebuildTargetLanguages();
        Status = "已回到设置步骤。现有任务仍保留在历史中；新的生成将创建新任务。";
    }

    private void SetOnlineSource(string url)
    {
        _onlineUrl = url;
        _onlineInfo = null;
        _verificationNotice = "遇到登录或人机验证时，可打开网页登录／验证。";
        OnPropertyChanged(nameof(OnlineUrl));
        OnPropertyChanged(nameof(OnlineSummary));
        NotifyVerification();
    }

    private void ChooseOutput()
    {
        var dialog = new SaveFileDialog { Title = "导出视频到本地", Filter = "MP4 视频|*.mp4", DefaultExt = ".mp4", AddExtension = true, FileName = string.IsNullOrWhiteSpace(Options.OutputPath) ? "转换后视频.mp4" : Path.GetFileName(Options.OutputPath), OverwritePrompt = false };
        var directory = Path.GetDirectoryName(Options.OutputPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        if (dialog.ShowDialog() == true) { Options.OutputPath = dialog.FileName; OnPropertyChanged(nameof(Options)); RefreshCommands(); }
    }

    private void BrowseTool(object? parameter)
    {
        var name = parameter?.ToString() ?? "ffmpeg";
        var dialog = new OpenFileDialog { Title = "选择 " + (name == "downloader" ? "yt-dlp" : name) + ".exe", Filter = "可执行文件|*.exe|所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        if (name == "ffprobe") Settings.Media.FfprobePath = dialog.FileName;
        else if (name == "downloader") Settings.OnlineVideo.DownloaderPath = dialog.FileName;
        else if (name == "deno") Settings.OnlineVideo.JavaScriptRuntimePath = dialog.FileName;
        else Settings.Media.FfmpegPath = dialog.FileName;
        OnPropertyChanged(nameof(Settings));
    }

    private void BrowseOutputDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "默认输出文件夹" };
        if (dialog.ShowDialog() == true) { Settings.Media.OutputDirectory = dialog.FolderName; OnPropertyChanged(nameof(Settings)); }
    }

    private void BrowseDownloadDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "在线视频素材保存文件夹" };
        if (dialog.ShowDialog() == true) { Settings.OnlineVideo.DownloadDirectory = dialog.FolderName; OnPropertyChanged(nameof(Settings)); }
    }

    private void BrowseCookies()
    {
        var dialog = new OpenFileDialog { Title = "选择 Netscape 格式的 Cookie 文件", Filter = "Cookie 文件|*.txt;*.cookies|所有文件|*.*" };
        if (dialog.ShowDialog() == true) { Settings.OnlineVideo.CookiesFilePath = dialog.FileName; OnPropertyChanged(nameof(Settings)); NotifyVerification(); }
    }

    private void OpenResult() => OpenExternal(Project?.ResultPath);
    private void OpenOutputFolder() => ExecuteUiAction(() => OpenExternal(Path.GetDirectoryName(Options.OutputPath)));
    private void ExecuteUiAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { Stage = "需要处理"; Status = SafeMessage(ex); }
    }
    private void OpenExternal(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path))) throw new FileNotFoundException("文件或文件夹尚不存在，请先完成导出。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Status = SafeMessage(ex); }
    }

    private async Task RefreshHistoryCoreAsync(CancellationToken ct) => ReplaceItems(History, (await _projects.ListAsync(ct)).OrderByDescending(x => x.CreatedAt));
    private IProgress<ProcessingProgress> CreateProgress()
    {
        var operation = _cancellation;
        return new Progress<ProcessingProgress>(p =>
        {
            if (operation != _cancellation || !IsBusy) return;
            Stage = p.Stage; Progress = Math.Clamp(p.Percent, 0, 100); Status = p.Message;
        });
    }
    private async Task RunAsync(string stage, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource();
        _operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellation = cancellation;
        IsBusy = true;
        Stage = stage;
        Progress = 0;
        try { await action(cancellation.Token); Stage = "就绪"; }
        catch (OperationCanceledException)
        {
            try { await CloseVerificationCoreAsync(); }
            catch (Exception) { /* App closing or the explicit close command can retry disposal. */ }
            _verificationNotice = "操作已取消；已保存的会话是否可用，请重新读取视频信息确认。";
            NotifyVerification();
            Stage = "已取消"; Status = "任务已取消。已保存的任务可从历史恢复。";
        }
        catch (Exception ex) { Stage = "需要处理"; Status = SafeMessage(ex); }
        finally { _cancellation = null; IsBusy = false; _operationCompletion.TrySetResult(); }
    }

    public void Cancel() => _cancellation?.Cancel();
    public async Task CancelAndWaitAsync()
    {
        _cancellation?.Cancel();
        if (_operationCompletion is { } completion) await completion.Task;
        await CloseVerificationCoreAsync();
    }
    public void ReportPreviewError(string message) => Status = "内置播放器无法播放此媒体，可使用「外部播放结果」：" + message;
    public void ReportVerificationCloseError()
    {
        Stage = "需要处理";
        Status = "验证窗口尚未完成关闭，请使用「关闭验证窗口」后再退出。";
    }
    private string SafeMessage(Exception ex)
    {
        var message = ex.Message;
        foreach (var key in new[] { Settings.Transcription.ApiKey, Settings.Translation.ApiKey, Settings.Synthesis.ApiKey })
            if (!string.IsNullOrEmpty(key)) message = message.Replace(key, "[密钥已隐藏]", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(Settings.OnlineVideo.Proxy)) message = message.Replace(Settings.OnlineVideo.Proxy, "[代理凭据已隐藏]", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(Settings.OnlineVideo.CookiesFilePath)) message = message.Replace(Settings.OnlineVideo.CookiesFilePath, "[Cookie 文件]", StringComparison.Ordinal);
        return message;
    }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static bool LocaleMatches(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
        first is "*" || second is "*" ||
        (!first.Contains('-') || !second.Contains('-')) && string.Equals(first.Split('-')[0], second.Split('-')[0], StringComparison.OrdinalIgnoreCase);
    private static void EnsureSelection(IEnumerable<LanguageOption> options, string selected, Action<string> assign)
    {
        if (!options.Any(x => x.Code == selected) && options.FirstOrDefault() is { } first) assign(first.Code);
    }
    private static void ReplaceItems<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        var snapshot = items.ToList();
        collection.Clear();
        foreach (var item in snapshot) collection.Add(item);
    }
    private void RefreshCommands()
    {
        foreach (var command in new[] { ImportCommand, InspectOnlineCommand, DownloadOnlineCommand, OpenVerificationCommand, UseVerificationCommand, CloseVerificationCommand, ClearWebsiteSessionCommand, CheckOnlineToolsCommand, SaveSettingsCommand, RefreshCapabilitiesCommand, TestServiceCommand, CheckMediaCommand, PrepareCommand, ExportCommand, PreviewVoiceCommand, SaveEditsCommand, RefreshHistoryCommand, RestoreCommand }) command?.Refresh();
        foreach (var command in new[] { CancelCommand, ResetCommand, ChooseOutputCommand, BrowseToolCommand, BrowseDirectoryCommand, BrowseDownloadDirectoryCommand, BrowseCookiesCommand, ClearCookiesCommand, PlayResultCommand, OpenOutputFolderCommand }) command?.Refresh();
        RefreshLocalVoiceCommands();
    }
    private void NotifySelections()
    {
        OnPropertyChanged(nameof(SourceLanguage));
        OnPropertyChanged(nameof(SubtitleLanguage));
        OnPropertyChanged(nameof(SpeechLanguage));
        OnPropertyChanged(nameof(VoiceId));
        OnPropertyChanged(nameof(AudioStreamIndex));
    }
    private Task PreviewVoiceAsync() => RunAsync("生成音色试听", async ct =>
    {
        var language = Options.SpeechLanguage;
        var sample = language.Split('-')[0].ToLowerInvariant() switch
        {
            "zh" => "你好，这是视频配音试听。欢迎使用 LinguaForge。",
            "ja" => "こんにちは。これは動画の音声サンプルです。",
            "ko" => "안녕하세요. 동영상 음성 미리 듣기입니다.",
            "fr" => "Bonjour, voici un aperçu de la voix de votre vidéo.",
            "de" => "Hallo, dies ist eine Vorschau der Videostimme.",
            "es" => "Hola, esta es una muestra de la voz del vídeo.",
            _ => "Hello. This is a preview of your video narration."
        };
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinguaForge-X", "preview");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
        await _synthesis.SynthesizeAsync(sample, language, Options.VoiceId, path, Clone(Settings.Synthesis), ct);
        VoicePreviewReady?.Invoke(path);
        Status = "音色试听已生成并开始播放。部分语言使用英语示例，实际配音以目标语言文本为准。";
    });
}
