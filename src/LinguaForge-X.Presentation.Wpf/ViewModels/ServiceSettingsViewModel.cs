using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using Microsoft.Win32;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

/// <summary>
/// Manages service configuration persistence, connection tests, local tool path selection,
/// and service capability catalog discovery.
/// </summary>
public sealed class ServiceSettingsViewModel : ObservableObject
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly ISettingsStore _settingsStore;
    private readonly IServiceCatalog _catalog;
    private readonly IMediaService _media;
    private readonly IOnlineVideoService _online;
    private readonly Action<ServiceCapabilities> _applyCapabilities;
    private readonly Action _notifyVerification;
    private string _capabilityDescription = "语言目录尚未加载。目录支持不代表当前服务凭据已验证。";
    private string _transcriptionStatus = "未验证";
    private string _translationStatus = "未验证";
    private string _synthesisStatus = "未验证";

    public ServiceSettingsViewModel(WorkspaceCoordinator workspace, ISettingsStore settingsStore,
        IServiceCatalog catalog, IMediaService media, IOnlineVideoService online,
        Action<ServiceCapabilities> applyCapabilities, Action notifyVerification)
    {
        _workspace = workspace;
        _settingsStore = settingsStore;
        _catalog = catalog;
        _media = media;
        _online = online;
        _applyCapabilities = applyCapabilities;
        _notifyVerification = notifyVerification;

        SaveSettingsCommand = new(_ => SaveSettingsAsync(), _ => !_workspace.IsBusy);
        RefreshCapabilitiesCommand = new(_ => _workspace.RunAsync("加载能力目录", RefreshCapabilitiesCoreAsync), _ => !_workspace.IsBusy);
        TestServiceCommand = new(TestServiceAsync, _ => !_workspace.IsBusy);
        CheckMediaCommand = new(_ => _workspace.RunAsync("检查本地工具", async ct =>
        {
            await _media.CheckAvailableAsync(_workspace.Settings.Media, ct);
            _workspace.Status = "FFmpeg 与 FFprobe 可用。";
        }), _ => !_workspace.IsBusy);
        CheckOnlineToolsCommand = new(_ => _workspace.RunAsync("检查下载工具", async ct =>
        {
            _workspace.Status = await _online.CheckAvailableAsync(WorkspaceCoordinator.Clone(_workspace.Settings.OnlineVideo), ct);
        }), _ => !_workspace.IsBusy);
        BrowseToolCommand = new(p => _workspace.ExecuteUiAction(() => BrowseTool(p)), _ => !_workspace.IsBusy);
        BrowseDirectoryCommand = new(_ => _workspace.ExecuteUiAction(BrowseOutputDirectory), _ => !_workspace.IsBusy);
        BrowseDownloadDirectoryCommand = new(_ => _workspace.ExecuteUiAction(BrowseDownloadDirectory), _ => !_workspace.IsBusy);
        BrowseCookiesCommand = new(_ => _workspace.ExecuteUiAction(BrowseCookies), _ => !_workspace.IsBusy);
        ClearCookiesCommand = new(_ =>
        {
            _workspace.Settings.OnlineVideo.CookiesFilePath = "";
            _workspace.RaiseStateChanged();
            _notifyVerification();
        }, _ => !_workspace.IsBusy);
    }

    public string CapabilityDescription { get => _capabilityDescription; set => Set(ref _capabilityDescription, value); }
    public string TranscriptionStatus { get => _transcriptionStatus; set => Set(ref _transcriptionStatus, value); }
    public string TranslationStatus { get => _translationStatus; set => Set(ref _translationStatus, value); }
    public string SynthesisStatus { get => _synthesisStatus; set => Set(ref _synthesisStatus, value); }

    public AsyncCommand SaveSettingsCommand { get; }
    public AsyncCommand RefreshCapabilitiesCommand { get; }
    public AsyncCommand TestServiceCommand { get; }
    public AsyncCommand CheckMediaCommand { get; }
    public AsyncCommand CheckOnlineToolsCommand { get; }
    public RelayCommand BrowseToolCommand { get; }
    public RelayCommand BrowseDirectoryCommand { get; }
    public RelayCommand BrowseDownloadDirectoryCommand { get; }
    public RelayCommand BrowseCookiesCommand { get; }
    public RelayCommand ClearCookiesCommand { get; }

    private Task SaveSettingsAsync() => _workspace.RunAsync("保存服务设置", async ct =>
    {
        await _settingsStore.SaveAsync(_workspace.Settings, ct);
        TranscriptionStatus = TranslationStatus = SynthesisStatus = "设置已保存，连接未验证";
        _workspace.Status = "设置已保存到程序目录的 settings.json；密钥与代理凭据由 Windows 当前用户保护，Cookie 文件仅保存路径。";
        try { await RefreshCapabilitiesCoreAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { CapabilityDescription = "设置已保存，但能力目录刷新失败：" + _workspace.SafeMessage(ex); }
    });

    private Task TestServiceAsync(object? parameter) => _workspace.RunAsync("测试服务连接", async ct =>
    {
        if (!Enum.TryParse<ServiceKind>(parameter?.ToString(), out var kind)) return;
        var service = kind switch
        {
            ServiceKind.Transcription => _workspace.Settings.Transcription,
            ServiceKind.Translation => _workspace.Settings.Translation,
            _ => _workspace.Settings.Synthesis
        };
        var result = await _catalog.TestConnectionAsync(WorkspaceCoordinator.Clone(service), kind, ct);
        var message = $"上次测试（{DateTime.Now:HH:mm}）：" + (result.Success ? "通过。" : "失败。") + result.Message;
        switch (kind)
        {
            case ServiceKind.Transcription: TranscriptionStatus = message; break;
            case ServiceKind.Translation: TranslationStatus = message; break;
            case ServiceKind.Synthesis: SynthesisStatus = message; break;
        }
        _workspace.Status = message;
    });

    public async Task RefreshCapabilitiesCoreAsync(CancellationToken ct)
    {
        var capabilities = await _catalog.GetCapabilitiesAsync(WorkspaceCoordinator.Clone(_workspace.Settings), ct);
        _applyCapabilities(capabilities);
        CapabilityDescription = capabilities.Description + " 语言与音色目录不等同于连接验证；更改配置后请重新测试。";
    }

    private void BrowseTool(object? parameter)
    {
        var name = parameter?.ToString() ?? "ffmpeg";
        var dialog = new OpenFileDialog { Title = "选择 " + (name == "downloader" ? "yt-dlp" : name) + ".exe", Filter = "可执行文件|*.exe|所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        if (name == "ffprobe") _workspace.Settings.Media.FfprobePath = dialog.FileName;
        else if (name == "downloader") _workspace.Settings.OnlineVideo.DownloaderPath = dialog.FileName;
        else if (name == "deno") _workspace.Settings.OnlineVideo.JavaScriptRuntimePath = dialog.FileName;
        else _workspace.Settings.Media.FfmpegPath = dialog.FileName;
        _workspace.RaiseStateChanged();
    }

    private void BrowseOutputDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "默认输出文件夹" };
        if (dialog.ShowDialog() == true)
        {
            _workspace.Settings.Media.OutputDirectory = dialog.FolderName;
            _workspace.RaiseStateChanged();
        }
    }

    private void BrowseDownloadDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "在线视频素材保存文件夹" };
        if (dialog.ShowDialog() == true)
        {
            _workspace.Settings.OnlineVideo.DownloadDirectory = dialog.FolderName;
            _workspace.RaiseStateChanged();
        }
    }

    private void BrowseCookies()
    {
        var dialog = new OpenFileDialog { Title = "选择 Netscape 格式的 Cookie 文件", Filter = "Cookie 文件|*.txt;*.cookies|所有文件|*.*" };
        if (dialog.ShowDialog() == true)
        {
            _workspace.Settings.OnlineVideo.CookiesFilePath = dialog.FileName;
            _workspace.RaiseStateChanged();
            _notifyVerification();
        }
    }

    public void RefreshCommands()
    {
        foreach (var command in new[] { SaveSettingsCommand, RefreshCapabilitiesCommand, TestServiceCommand, CheckMediaCommand, CheckOnlineToolsCommand })
            command?.Refresh();
        foreach (var command in new[] { BrowseToolCommand, BrowseDirectoryCommand, BrowseDownloadDirectoryCommand, BrowseCookiesCommand, ClearCookiesCommand })
            command?.Refresh();
    }
}
