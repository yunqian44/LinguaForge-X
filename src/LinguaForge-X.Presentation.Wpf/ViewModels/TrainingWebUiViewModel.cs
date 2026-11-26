using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

/// <summary>
/// Manages the embedded local GPT-SoVITS training WebUI service lifecycle and status.
/// </summary>
public sealed class TrainingWebUiViewModel : ObservableObject
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly ILocalVoiceTrainingWebUiService? _trainingWebUi;
    private readonly ILocalVoiceService? _localVoice;
    private readonly Func<bool> _isLocalVoiceTraining;
    private bool _trainingWebUiRunning;
    private string? _trainingWebUiUrl;
    private string _trainingWebUiStatus = "内嵌官方 GPT-SoVITS 工作台，只在本机运行。可录制并保存素材后再启动。";

    public TrainingWebUiViewModel(WorkspaceCoordinator workspace, ILocalVoiceTrainingWebUiService? trainingWebUi,
        ILocalVoiceService? localVoice, Func<bool> isLocalVoiceTraining)
    {
        _workspace = workspace;
        _trainingWebUi = trainingWebUi;
        _localVoice = localVoice;
        _isLocalVoiceTraining = isLocalVoiceTraining;

        StartTrainingWebUiCommand = new(_ => _workspace.RunAsync("启动 GPT-SoVITS 训练工作台", async ct =>
        {
            try
            {
                TrainingWebUiStatus = "正在校验环境并启动本机训练服务，首次启动可能需要一些时间…";
                if (_localVoice is not null)
                {
                    var stop = await _localVoice.StopAsync(ct);
                    if (!stop.Success) throw new InvalidOperationException(stop.Message);
                }
                var result = await RequiredTrainingWebUi().StartAsync(WorkspaceCoordinator.Clone(_workspace.LocalVoiceSettings), ct);
                ApplyTrainingWebUiStatus(result);
                if (!result.Success || !result.IsRunning || result.Url is null)
                    throw new InvalidOperationException(result.Message);
                _workspace.Status = TrainingWebUiStatus;
                OpenTrainingWorkbench();
            }
            catch (OperationCanceledException)
            {
                ApplyTrainingWebUiStatus(RequiredTrainingWebUi().GetStatus());
                TrainingWebUiStatus = "训练服务启动已取消。可检查配置后重新启动。";
                throw;
            }
            catch (Exception error)
            {
                if (_trainingWebUi is not null) ApplyTrainingWebUiStatus(_trainingWebUi.GetStatus());
                TrainingWebUiStatus = "需要处理：" + _workspace.SafeMessage(error);
                throw;
            }
        }), _ => _workspace.IsIdle && !_isLocalVoiceTraining() && !TrainingWebUiRunning && _trainingWebUi is not null);

        StopTrainingWebUiCommand = new(_ => _workspace.RunAsync("停止 GPT-SoVITS 训练工作台", async ct =>
        {
            var result = await RequiredTrainingWebUi().StopAsync(ct);
            ApplyTrainingWebUiStatus(result);
            _workspace.Status = TrainingWebUiStatus;
            if (!result.Success) throw new InvalidOperationException(result.Message);
        }), _ => _workspace.IsIdle && TrainingWebUiRunning);

        OpenTrainingWebUiCommand = new(_ => _workspace.ExecuteUiAction(OpenTrainingWorkbench),
            _ => _workspace.IsIdle && TrainingWebUiRunning && _trainingWebUiUrl is not null);
    }

    public bool TrainingWebUiRunning => _trainingWebUiRunning;
    public bool CanEditLocalVoiceEnvironment => _workspace.IsIdle && !TrainingWebUiRunning;
    public string TrainingWebUiStatus { get => _trainingWebUiStatus; private set => Set(ref _trainingWebUiStatus, value); }

    public event Action<Uri>? TrainingWebUiRequested;
    public event Action? TrainingWebUiStopped;

    public AsyncCommand StartTrainingWebUiCommand { get; }
    public AsyncCommand StopTrainingWebUiCommand { get; }
    public RelayCommand OpenTrainingWebUiCommand { get; }

    private ILocalVoiceTrainingWebUiService RequiredTrainingWebUi() => _trainingWebUi
        ?? throw new InvalidOperationException("当前版本未加载训练工作台服务，请使用完整发布包。");

    private void OpenTrainingWorkbench()
    {
        ApplyTrainingWebUiStatus(RequiredTrainingWebUi().GetStatus());
        if (!TrainingWebUiRunning || _trainingWebUiUrl is null)
            throw new InvalidOperationException("训练服务尚未就绪，请先启动服务。");
        TrainingWebUiRequested?.Invoke(new Uri(_trainingWebUiUrl));
    }

    public void RefreshStatus()
    {
        if (_trainingWebUi is null || _workspace.IsBusy || !TrainingWebUiRunning) return;
        var result = _trainingWebUi.GetStatus();
        if (result.IsRunning != TrainingWebUiRunning || result.Url != _trainingWebUiUrl)
            ApplyTrainingWebUiStatus(result);
    }

    private void ApplyTrainingWebUiStatus(LocalVoiceTrainingWebUiResult result)
    {
        var wasRunning = _trainingWebUiRunning;
        _trainingWebUiRunning = result.IsRunning;
        _trainingWebUiUrl = result.Url;
        TrainingWebUiStatus = result.Message;
        OnPropertyChanged(nameof(TrainingWebUiRunning));
        OnPropertyChanged(nameof(CanEditLocalVoiceEnvironment));
        _workspace.RaiseStateChanged();
        if (wasRunning && !result.IsRunning) TrainingWebUiStopped?.Invoke();
    }

    public void RefreshCommands()
    {
        StartTrainingWebUiCommand.Refresh();
        StopTrainingWebUiCommand.Refresh();
        OpenTrainingWebUiCommand.Refresh();
        OnPropertyChanged(nameof(CanEditLocalVoiceEnvironment));
    }

    public async Task ShutdownAsync()
    {
        if (_trainingWebUi is null) return;
        var result = await _trainingWebUi.StopAsync();
        ApplyTrainingWebUiStatus(result);
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }
}
