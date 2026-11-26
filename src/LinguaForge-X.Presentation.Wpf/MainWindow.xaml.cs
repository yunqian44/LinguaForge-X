using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LinguaForge_X.Presentation.Wpf.Controls;
using LinguaForge_X.Presentation.Wpf.ViewModels;

namespace LinguaForge_X.Presentation.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closingAfterCancellation;
    private bool _waitingToClose;
    private TrainingWebUiWindow? _trainingWindow;
    private readonly DispatcherTimer _trainingStatusTimer;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.VoicePreviewReady += path => { VoicePlayer.Source = new Uri(path); VoicePlayer.Play(); };
        viewModel.RecordingCaptureStarting += () => { VideoPlayer.Pause(); VoicePlayer.Close(); };
        viewModel.TrainingWebUiRequested += OpenTrainingWindow;
        viewModel.TrainingWebUiStopped += CloseTrainingWindow;
        _trainingStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _trainingStatusTimer.Tick += (_, _) => _viewModel.RefreshTrainingWebUiStatus();
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _trainingStatusTimer.Start();
        await _viewModel.LoadAsync();
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_waitingToClose) { e.Cancel = true; return; }
        if (!_closingAfterCancellation && (_viewModel.IsBusy || _viewModel.HasVerificationSession || _viewModel.TrainingWebUiRunning))
        {
            e.Cancel = true;
            if (_viewModel.IsBusy || _viewModel.TrainingWebUiRunning)
            {
                var result = MessageBox.Show("当前任务或本地训练服务正在运行。关闭将取消处理并停止本项目的训练服务，是否退出？", "退出 LinguaForge-X", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
            }
            _waitingToClose = true;
            try { await _viewModel.CancelAndWaitAsync(); }
            catch (Exception) { _waitingToClose = false; _viewModel.ReportShutdownError(); return; }
            _waitingToClose = false;
            _closingAfterCancellation = true;
            // Disposal may finish synchronously; let the current Closing event unwind first.
            _ = Dispatcher.BeginInvoke(new Action(Close));
            return;
        }
        VideoPlayer.Close();
        VoicePlayer.Close();
        _trainingStatusTimer.Stop();
        CloseTrainingWindow();
        _viewModel.TrainingWebUiRequested -= OpenTrainingWindow;
        _viewModel.TrainingWebUiStopped -= CloseTrainingWindow;
    }
    private void OpenTrainingWindow(Uri address)
    {
        if (_trainingWindow is not null) { _trainingWindow.Activate(); return; }
        var window = new TrainingWebUiWindow(address) { Owner = this };
        _trainingWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_trainingWindow, window)) _trainingWindow = null; };
        window.Show();
    }
    private void CloseTrainingWindow()
    {
        _trainingWindow?.Close();
        _trainingWindow = null;
    }
    private void Play_Click(object sender, RoutedEventArgs e) => VideoPlayer.Play();
    private void Pause_Click(object sender, RoutedEventArgs e) => VideoPlayer.Pause();
    private void Stop_Click(object sender, RoutedEventArgs e) => VideoPlayer.Stop();
    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e) => VideoPlayer.Pause();
    private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e) => _viewModel.ReportPreviewError(e.ErrorException.Message);
    private void SegmentsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e) { if (_viewModel.IsBusy) e.Cancel = true; }
    private void CommitEdits(object sender, MouseButtonEventArgs e)
    {
        if (!SegmentsGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !SegmentsGrid.CommitEdit(DataGridEditingUnit.Row, true)) e.Handled = true;
    }
}
