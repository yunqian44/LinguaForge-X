using System.Collections.ObjectModel;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

public sealed partial class MainViewModel
{
    public ObservableCollection<VoiceRecordingDevice> RecordingDevices => Recording.RecordingDevices;
    public ObservableCollection<RecordingLibraryItem> RecordingSamples => Recording.RecordingSamples;

    public VoiceRecordingDevice? RecordingDevice
    {
        get => Recording.RecordingDevice;
        set => Recording.RecordingDevice = value;
    }

    public RecordingLibraryItem? SelectedRecording
    {
        get => Recording.SelectedRecording;
        set => Recording.SelectedRecording = value;
    }

    public bool IsRecording => Recording.IsRecording;
    public bool HasRecording => Recording.HasRecording;
    public bool CanEditRecording => Recording.CanEditRecording;
    public double RecordingLevel => Recording.RecordingLevel;
    public string RecordingMeter => Recording.RecordingMeter;
    public string RecordingStatus => Recording.RecordingStatus;

    public string RecordingName
    {
        get => Recording.RecordingName;
        set => Recording.RecordingName = value;
    }

    public string RecordingSpeaker
    {
        get => Recording.RecordingSpeaker;
        set => Recording.RecordingSpeaker = value;
    }

    public string RecordingText
    {
        get => Recording.RecordingText;
        set => Recording.RecordingText = value;
    }

    public string RecordingLanguage
    {
        get => Recording.RecordingLanguage;
        set => Recording.RecordingLanguage = value;
    }

    public string RecordingStart
    {
        get => Recording.RecordingStart;
        set => Recording.RecordingStart = value;
    }

    public string RecordingEnd
    {
        get => Recording.RecordingEnd;
        set => Recording.RecordingEnd = value;
    }

    public string RecordingGain
    {
        get => Recording.RecordingGain;
        set => Recording.RecordingGain = value;
    }

    public bool RecordingNormalize
    {
        get => Recording.RecordingNormalize;
        set => Recording.RecordingNormalize = value;
    }

    public bool RecordingReviewed
    {
        get => Recording.RecordingReviewed;
        set => Recording.RecordingReviewed = value;
    }

    public string RecordingQuality => Recording.RecordingQuality;
    public string RecordingOriginalSummary => Recording.RecordingOriginalSummary;
    public string RecordingSelectionSummary => Recording.RecordingSelectionSummary;
    public bool HasUnappliedRecordingCalibration => Recording.HasUnappliedRecordingCalibration;
    public string RecordingCalibrationHint => Recording.RecordingCalibrationHint;

    public event Action? RecordingCaptureStarting;
    public event Action<string>? RecordingPreviewRequested;
    public event Action? RecordingPreviewStopRequested;

    public AsyncCommand RefreshRecordingsCommand => Recording.RefreshRecordingsCommand;
    public AsyncCommand StartRecordingCommand => Recording.StartRecordingCommand;
    public AsyncCommand StopRecordingCommand => Recording.StopRecordingCommand;
    public RelayCommand CancelRecordingCommand => Recording.CancelRecordingCommand;
    public AsyncCommand ImportRecordingCommand => Recording.ImportRecordingCommand;
    public AsyncCommand CalibrateRecordingCommand => Recording.CalibrateRecordingCommand;
    public RelayCommand RestoreOriginalRecordingCommand => Recording.RestoreOriginalRecordingCommand;
    public RelayCommand PlayOriginalRecordingCommand => Recording.PlayOriginalRecordingCommand;
    public RelayCommand PlayRecordingCommand => Recording.PlayRecordingCommand;
    public RelayCommand StopRecordingPlaybackCommand => Recording.StopRecordingPlaybackCommand;
    public AsyncCommand SaveRecordingCommand => Recording.SaveRecordingCommand;
    public RelayCommand UseRecordingAsReferenceCommand => Recording.UseRecordingAsReferenceCommand;
    public AsyncCommand LoadRecordingCommand => Recording.LoadRecordingCommand;
    public AsyncCommand ExportRecordingDatasetCommand => Recording.ExportRecordingDatasetCommand;

    public void ReportRecordingPlaybackEnded() => Recording.ReportRecordingPlaybackEnded();
    public void ReportRecordingPlaybackError(string message) => Recording.ReportRecordingPlaybackError(message);
}
