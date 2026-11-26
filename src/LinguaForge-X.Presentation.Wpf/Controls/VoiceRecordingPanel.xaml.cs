using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinguaForge_X.Presentation.Wpf.ViewModels;

namespace LinguaForge_X.Presentation.Wpf.Controls;

public partial class VoiceRecordingPanel : UserControl
{
    private readonly MediaPlayer _player = new();
    private MainViewModel? _model;

    public VoiceRecordingPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachModel();
        Loaded += (_, _) => AttachModel();
        Unloaded += (_, _) => DetachModel();
        _player.MediaEnded += (_, _) => { _player.Close(); _model?.ReportRecordingPlaybackEnded(); };
        _player.MediaFailed += (_, e) => { _player.Close(); _model?.ReportRecordingPlaybackError(e.ErrorException.Message); };
    }

    private void AttachModel()
    {
        if (ReferenceEquals(_model, DataContext)) return;
        DetachModel();
        _model = DataContext as MainViewModel;
        if (_model is null) return;
        _model.RecordingPreviewRequested += Play;
        _model.RecordingPreviewStopRequested += Stop;
    }

    private void DetachModel()
    {
        _player.Close();
        if (_model is not null)
        {
            _model.RecordingPreviewRequested -= Play;
            _model.RecordingPreviewStopRequested -= Stop;
            _model.ReportRecordingPlaybackEnded();
        }
        _model = null;
    }

    private void Play(string path)
    {
        try { _player.Close(); _player.Open(new Uri(path, UriKind.Absolute)); _player.Play(); }
        catch (Exception ex) { _model?.ReportRecordingPlaybackError(ex.Message); }
    }

    private void Stop() => _player.Close();
}
