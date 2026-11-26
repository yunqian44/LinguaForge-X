namespace LinguaForge_X.Presentation.Wpf.ViewModels;

public sealed partial class MainViewModel
{
    public bool TrainingWebUiRunning => TrainingWebUi.TrainingWebUiRunning;
    public bool CanEditLocalVoiceEnvironment => TrainingWebUi.CanEditLocalVoiceEnvironment;
    public string TrainingWebUiStatus => TrainingWebUi.TrainingWebUiStatus;

    public event Action<Uri>? TrainingWebUiRequested;
    public event Action? TrainingWebUiStopped;

    public AsyncCommand StartTrainingWebUiCommand => TrainingWebUi.StartTrainingWebUiCommand;
    public AsyncCommand StopTrainingWebUiCommand => TrainingWebUi.StopTrainingWebUiCommand;
    public RelayCommand OpenTrainingWebUiCommand => TrainingWebUi.OpenTrainingWebUiCommand;

    public void RefreshTrainingWebUiStatus() => TrainingWebUi.RefreshStatus();
}
