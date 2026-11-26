using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

/// <summary>
/// Coordinates shared workspace state, task operation lifecycle, progress reporting,
/// and cross-module mutual exclusion across modular view models.
/// </summary>
public sealed class WorkspaceCoordinator : ObservableObject
{
    private AppSettings _settings = new();
    private ConversionOptions _options = new();
    private ConversionProject? _project;
    private VideoInfo? _video;
    private bool _isBusy;
    private string _status = "请先在「服务设置」配置 Azure 服务和 FFmpeg，再导入视频。";
    private string _stage = "就绪";
    private double _progress;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _operationCompletion;

    public event Action? StateChanged;
    public event Action<string>? VoicePreviewReady;
    public event Func<Task>? ClosingVerificationRequested;
    public event Action? CancelledWithVerificationCleanup;

    public AppSettings Settings
    {
        get => _settings;
        set
        {
            if (Set(ref _settings, value))
            {
                OnPropertyChanged(nameof(LocalVoiceSettings));
                RaiseStateChanged();
            }
        }
    }

    public LocalVoiceSettings LocalVoiceSettings => Settings.Synthesis.LocalVoice;

    public ConversionOptions Options
    {
        get => _options;
        set
        {
            if (Set(ref _options, value))
            {
                RaiseStateChanged();
            }
        }
    }

    public ConversionProject? Project
    {
        get => _project;
        set
        {
            if (Set(ref _project, value))
            {
                OnPropertyChanged(nameof(HasProject));
                OnPropertyChanged(nameof(CanConfigure));
                RaiseStateChanged();
            }
        }
    }

    public VideoInfo? Video
    {
        get => _video;
        set
        {
            if (Set(ref _video, value))
            {
                RaiseStateChanged();
            }
        }
    }

    public bool HasProject => Project is not null;
    public bool CanConfigure => !IsBusy && !HasProject;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(CanConfigure));
                RaiseStateChanged();
            }
        }
    }

    public bool IsIdle => !IsBusy;
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Stage { get => _stage; set => Set(ref _stage, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public CancellationTokenSource? CurrentCancellation => _cancellation;

    public void RaiseStateChanged() => StateChanged?.Invoke();
    public void RaiseVoicePreviewReady(string path) => VoicePreviewReady?.Invoke(path);

    public Task CloseVerificationIfOpenAsync() =>
        ClosingVerificationRequested is { } handler ? handler.Invoke() : Task.CompletedTask;

    public IProgress<ProcessingProgress> CreateProgress()
    {
        var operation = _cancellation;
        return new Progress<ProcessingProgress>(p =>
        {
            if (operation != _cancellation || !IsBusy) return;
            Stage = p.Stage;
            Progress = Math.Clamp(p.Percent, 0, 100);
            Status = p.Message;
        });
    }

    public async Task RunAsync(string stage, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource();
        _operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellation = cancellation;
        IsBusy = true;
        Stage = stage;
        Progress = 0;
        try
        {
            await action(cancellation.Token);
            Stage = "就绪";
        }
        catch (OperationCanceledException)
        {
            try { await CloseVerificationIfOpenAsync(); }
            catch (Exception) { /* App closing or explicit close command can retry disposal. */ }
            CancelledWithVerificationCleanup?.Invoke();
            Stage = "已取消";
            Status = "任务已取消。已保存的任务可从历史恢复。";
        }
        catch (Exception ex)
        {
            Stage = "需要处理";
            Status = SafeMessage(ex);
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
            _operationCompletion.TrySetResult();
        }
    }

    public void Cancel() => _cancellation?.Cancel();

    public async Task CancelAndWaitCoreAsync()
    {
        _cancellation?.Cancel();
        if (_operationCompletion is { } completion) await completion.Task;
    }

    public void ExecuteUiAction(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Stage = "需要处理";
            Status = SafeMessage(ex);
        }
    }

    public void OpenExternal(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
                throw new FileNotFoundException("文件或文件夹尚不存在，请先完成导出。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Status = SafeMessage(ex); }
    }

    public string SafeMessage(Exception ex)
    {
        var message = ex.Message;
        foreach (var key in new[] { Settings.Transcription.ApiKey, Settings.Translation.ApiKey, Settings.Synthesis.ApiKey })
            if (!string.IsNullOrEmpty(key)) message = message.Replace(key, "[密钥已隐藏]", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(Settings.OnlineVideo.Proxy))
            message = message.Replace(Settings.OnlineVideo.Proxy, "[代理凭据已隐藏]", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(Settings.OnlineVideo.CookiesFilePath))
            message = message.Replace(Settings.OnlineVideo.CookiesFilePath, "[Cookie 文件]", StringComparison.Ordinal);
        return message;
    }

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
