using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

/// <summary>
/// Manages online video URL inspection, video downloading, and standalone browser verification sessions.
/// </summary>
public sealed class OnlineVideoViewModel : ObservableObject
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly ISettingsStore _settingsStore;
    private readonly IMediaService _media;
    private readonly IOnlineVideoService _online;
    private readonly IWebsiteVerificationService _verification;
    private readonly IWebsiteSessionStore _websiteSessions;
    private readonly Action<VideoInfo, MediaSettings, OnlineVideoInfo?> _applyImportedVideo;
    private IWebsiteVerificationSession? _verificationSession;
    private string _verificationUrl = "";
    private string _verificationProxy = "";
    private string _verificationNotice = "遇到登录或人机验证时，可打开网页登录／验证。";
    private readonly HashSet<VideoWebsite> _savedWebsiteSessions = [];
    private string _onlineUrl = "";
    private int _downloadMaxHeight = 1080;
    private OnlineVideoInfo? _onlineInfo;

    public OnlineVideoViewModel(WorkspaceCoordinator workspace, ISettingsStore settingsStore,
        IMediaService media, IOnlineVideoService online, IWebsiteVerificationService verification,
        IWebsiteSessionStore websiteSessions, Action<VideoInfo, MediaSettings, OnlineVideoInfo?> applyImportedVideo)
    {
        _workspace = workspace;
        _settingsStore = settingsStore;
        _media = media;
        _online = online;
        _verification = verification;
        _websiteSessions = websiteSessions;
        _applyImportedVideo = applyImportedVideo;

        _workspace.ClosingVerificationRequested += CloseVerificationCoreAsync;
        _workspace.CancelledWithVerificationCleanup += () =>
        {
            _verificationNotice = "操作已取消；已保存的会话是否可用，请重新读取视频信息确认。";
            NotifyVerification();
        };

        InspectOnlineCommand = new(_ => InspectOnlineAsync(),
            _ => _workspace.CanConfigure && !HasVerificationSession && !string.IsNullOrWhiteSpace(OnlineUrl));
        DownloadOnlineCommand = new(_ => DownloadOnlineAsync(),
            _ => _workspace.CanConfigure && !HasVerificationSession && !string.IsNullOrWhiteSpace(OnlineUrl));
        OpenVerificationCommand = new(_ => OpenVerificationAsync(),
            _ => _workspace.CanConfigure && !HasVerificationSession && VideoWebsites.FromUrl(OnlineUrl.Trim()) is not null);
        UseVerificationCommand = new(_ => UseVerificationAsync(),
            _ => _workspace.CanConfigure && VerificationMatchesSource);
        CloseVerificationCommand = new(_ => _workspace.RunAsync("关闭验证窗口", async _ =>
        {
            await CloseVerificationCoreAsync();
            _verificationNotice = "验证窗口已关闭。已保存的会话仍可用于该站下载。";
            NotifyVerification();
            _workspace.Status = _verificationNotice;
        }), _ => !_workspace.IsBusy && HasVerificationSession);
        ClearWebsiteSessionCommand = new(_ => ClearWebsiteSessionAsync(),
            _ => !_workspace.IsBusy && !HasVerificationSession && VideoWebsites.FromUrl(OnlineUrl.Trim()) is not null);
    }

    public string OnlineUrl
    {
        get => _onlineUrl;
        set
        {
            if (_workspace.HasProject || !Set(ref _onlineUrl, value)) return;
            _onlineInfo = null;
            OnPropertyChanged(nameof(OnlineSummary));
            _verificationNotice = "遇到登录或人机验证时，可打开网页登录／验证。";
            NotifyVerification();
            _workspace.RaiseStateChanged();
        }
    }

    public string OnlineProxy
    {
        get => _workspace.Settings.OnlineVideo.Proxy;
        set
        {
            if (_workspace.Settings.OnlineVideo.Proxy == value) return;
            _workspace.Settings.OnlineVideo.Proxy = value;
            OnPropertyChanged();
            NotifyVerification();
        }
    }

    public bool HasVerificationSession => _verificationSession is not null;

    public bool VerificationMatchesSource => _verificationSession is not null &&
        OnlineUrl.Trim() == _verificationUrl && _workspace.Settings.OnlineVideo.Proxy.Trim() == _verificationProxy &&
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
            var manual = string.IsNullOrWhiteSpace(_workspace.Settings.OnlineVideo.CookiesFilePath)
                ? "" : " 当前已选择手动 Cookie 文件，读取和下载优先使用该文件；使用新验证会话会清除该路径设置。";
            return saved + _verificationNotice + manual;
        }
    }

    public int DownloadMaxHeight
    {
        get => _downloadMaxHeight;
        set { if (!_workspace.HasProject) Set(ref _downloadMaxHeight, value); }
    }

    public string OnlineSummary => _workspace.HasProject ? "当前任务使用已保存的视频素材。下载清晰度仅在下载时生效；更换素材请先点击「重新设置」。" :
        _onlineInfo is null ? "粘贴 B 站、YouTube 等网站的单个视频链接，可先查看信息，也可直接下载导入。" :
        $"{_onlineInfo.Title}  ·  {_onlineInfo.Platform}  ·  {(_onlineInfo.DurationSeconds > 0 ? TimeSpan.FromSeconds(_onlineInfo.DurationSeconds).ToString(@"hh\:mm\:ss") : "时长未知")}";

    public IReadOnlyList<KeyValuePair<int, string>> DownloadQualities { get; } =
        [new(720, "最高 720p"), new(1080, "最高 1080p"), new(2160, "最高 4K"), new(0, "最佳可用画质")];

    public AsyncCommand InspectOnlineCommand { get; }
    public AsyncCommand DownloadOnlineCommand { get; }
    public AsyncCommand OpenVerificationCommand { get; }
    public AsyncCommand UseVerificationCommand { get; }
    public AsyncCommand CloseVerificationCommand { get; }
    public AsyncCommand ClearWebsiteSessionCommand { get; }

    private Task InspectOnlineAsync() => _workspace.RunAsync("读取在线视频信息", async ct => { await InspectOnlineCoreAsync(ct); });

    private async Task<bool> InspectOnlineCoreAsync(CancellationToken ct)
    {
        var request = new OnlineVideoRequest { Url = OnlineUrl.Trim(), MaxHeight = DownloadMaxHeight };
        var info = await _online.InspectAsync(request, WorkspaceCoordinator.Clone(_workspace.Settings.OnlineVideo), ct);
        ct.ThrowIfCancellationRequested();
        if (request.Url != OnlineUrl.Trim()) { _workspace.Status = "视频链接已变更，请重新读取信息。"; return false; }
        _onlineInfo = info;
        OnPropertyChanged(nameof(OnlineSummary));
        _workspace.Status = "视频信息已加载，可下载并导入。实际画质取决于网站可提供的格式。";
        return true;
    }

    private Task OpenVerificationAsync() => _workspace.RunAsync("打开网页登录／验证", async ct =>
    {
        var url = OnlineUrl.Trim();
        var proxy = _workspace.Settings.OnlineVideo.Proxy.Trim();
        if (VideoWebsites.FromUrl(url) is null) throw new InvalidOperationException("网页登录／验证支持 YouTube 和 B 站的 HTTPS 视频链接。");
        var session = await _verification.OpenAsync(url, proxy, ct);
        if (ct.IsCancellationRequested || url != OnlineUrl.Trim() || proxy != _workspace.Settings.OnlineVideo.Proxy.Trim())
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
        _workspace.Status = VerificationSummary;
    });

    private Task UseVerificationAsync() => _workspace.RunAsync("保存网站会话并重试", async ct =>
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
        _workspace.Settings.OnlineVideo.CookiesFilePath = "";
        _workspace.RaiseStateChanged();
        NotifyVerification();
        try
        {
            await _settingsStore.SaveAsync(_workspace.Settings, ct);
            if (url != OnlineUrl.Trim() || proxy != _workspace.Settings.OnlineVideo.Proxy.Trim())
                throw new InvalidOperationException("链接或代理已变更。会话已保存，请检查当前链接和代理后重新读取信息。");
            _verificationNotice = await InspectOnlineCoreAsync(ct)
                ? "已成功读取视频信息，可以下载并导入。网站之后仍可能要求重新验证。"
                : "会话已保存，但视频链接已变更，请重新读取当前视频信息。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _verificationNotice = "会话已保存，但本次重试未完成：" + _workspace.SafeMessage(ex);
            throw;
        }
        finally { NotifyVerification(); }
    });

    private Task ClearWebsiteSessionAsync() => _workspace.RunAsync("清除该站会话", async ct =>
    {
        var website = VideoWebsites.FromUrl(OnlineUrl.Trim()) ?? throw new InvalidOperationException("请先输入 YouTube 或 B 站链接。");
        await _websiteSessions.DeleteAsync(website, ct);
        _savedWebsiteSessions.Remove(website);
        _verificationNotice = "已清除该站保存的验证会话。";
        NotifyVerification();
        _workspace.Status = VerificationSummary;
    });

    public async Task CloseVerificationCoreAsync()
    {
        if (_verificationSession is not { } session) return;
        await session.DisposeAsync();
        _verificationSession = null;
        _verificationUrl = _verificationProxy = "";
        NotifyVerification();
    }

    public void SetOnlineSource(string url)
    {
        _onlineUrl = url;
        _onlineInfo = null;
        _verificationNotice = "遇到登录或人机验证时，可打开网页登录／验证。";
        OnPropertyChanged(nameof(OnlineUrl));
        OnPropertyChanged(nameof(OnlineSummary));
        NotifyVerification();
    }

    public void NotifyVerification()
    {
        OnPropertyChanged(nameof(HasVerificationSession));
        OnPropertyChanged(nameof(VerificationSummary));
        OnPropertyChanged(nameof(OnlineProxy));
        OnPropertyChanged(nameof(OnlineSummary));
        RefreshCommands();
        _workspace.RaiseStateChanged();
    }

    private Task DownloadOnlineAsync() => _workspace.RunAsync("下载在线视频", async ct =>
    {
        var request = new OnlineVideoRequest { Url = OnlineUrl.Trim(), MaxHeight = DownloadMaxHeight };
        var settings = WorkspaceCoordinator.Clone(_workspace.Settings.OnlineVideo);
        var media = WorkspaceCoordinator.Clone(_workspace.Settings.Media);
        await _media.CheckAvailableAsync(media, ct);
        var download = await _online.DownloadAsync(request, settings, media, _workspace.CreateProgress(), ct);
        var video = await _media.ProbeAsync(download.FilePath, media, ct);
        ct.ThrowIfCancellationRequested();
        if (request.Url != OnlineUrl.Trim()) { _workspace.Status = "视频已下载，但链接已变更。文件保存在：" + download.FilePath; return; }
        _applyImportedVideo(video, media, download.Video);
        _onlineInfo = download.Video;
        OnPropertyChanged(nameof(OnlineSummary));
        _workspace.Status = "在线视频已下载并导入。选择字幕／配音语言，然后生成字幕与配音稿。";
    });

    public void RefreshCommands()
    {
        foreach (var command in new[] { InspectOnlineCommand, DownloadOnlineCommand, OpenVerificationCommand, UseVerificationCommand, CloseVerificationCommand, ClearWebsiteSessionCommand })
            command?.Refresh();
    }
}
