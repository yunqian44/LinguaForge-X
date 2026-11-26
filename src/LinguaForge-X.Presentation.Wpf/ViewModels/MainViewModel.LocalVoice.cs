using System.Collections.ObjectModel;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

public sealed partial class MainViewModel
{
    public LocalVoiceSettings LocalVoiceSettings => LocalVoice.LocalVoiceSettings;
    public LocalVoiceDraft LocalVoiceDraft => LocalVoice.LocalVoiceDraft;
    public ObservableCollection<LocalVoiceProfile> LocalVoices => LocalVoice.LocalVoices;

    public LocalVoiceProfile? SelectedLocalVoice
    {
        get => LocalVoice.SelectedLocalVoice;
        set => LocalVoice.SelectedLocalVoice = value;
    }

    public string LocalVoiceStatus => LocalVoice.LocalVoiceStatus;

    public string LocalDatasetPath
    {
        get => LocalVoice.LocalDatasetPath;
        set => LocalVoice.LocalDatasetPath = value;
    }

    public int LocalGptEpochs
    {
        get => LocalVoice.LocalGptEpochs;
        set => LocalVoice.LocalGptEpochs = value;
    }

    public int LocalSoVitsEpochs
    {
        get => LocalVoice.LocalSoVitsEpochs;
        set => LocalVoice.LocalSoVitsEpochs = value;
    }

    public bool IsLocalVoiceTraining => LocalVoice.IsLocalVoiceTraining;
    public IReadOnlyList<KeyValuePair<string, string>> LocalVoiceDevices => LocalVoice.LocalVoiceDevices;
    public IReadOnlyList<KeyValuePair<string, string>> LocalReferenceLanguages => LocalVoice.LocalReferenceLanguages;

    public AsyncCommand CheckLocalVoiceCommand => LocalVoice.CheckLocalVoiceCommand;
    public AsyncCommand StartLocalVoiceCommand => LocalVoice.StartLocalVoiceCommand;
    public AsyncCommand StopLocalVoiceCommand => LocalVoice.StopLocalVoiceCommand;
    public AsyncCommand SaveLocalVoiceSettingsCommand => LocalVoice.SaveLocalVoiceSettingsCommand;
    public AsyncCommand SaveLocalVoiceCommand => LocalVoice.SaveLocalVoiceCommand;
    public AsyncCommand RefreshLocalVoicesCommand => LocalVoice.RefreshLocalVoicesCommand;
    public AsyncCommand TrainLocalVoiceCommand => LocalVoice.TrainLocalVoiceCommand;
    public AsyncCommand UseLocalVoiceCommand => LocalVoice.UseLocalVoiceCommand;
    public AsyncCommand PreviewLocalVoiceCommand => LocalVoice.PreviewLocalVoiceCommand;
    public RelayCommand BrowseLocalVoiceCommand => LocalVoice.BrowseLocalVoiceCommand;
    public RelayCommand NewLocalVoiceCommand => LocalVoice.NewLocalVoiceCommand;
    public RelayCommand CancelLocalVoiceTrainingCommand => LocalVoice.CancelLocalVoiceTrainingCommand;
    public RelayCommand OpenLocalVoiceGuideCommand => LocalVoice.OpenLocalVoiceGuideCommand;
}
