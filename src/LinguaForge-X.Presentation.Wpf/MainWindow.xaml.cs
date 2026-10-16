using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinguaForge_X.Presentation.Wpf.ViewModels;

namespace LinguaForge_X.Presentation.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closingAfterCancellation;
    private bool _waitingToClose;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.VoicePreviewReady += path => { VoicePlayer.Source = new Uri(path); VoicePlayer.Play(); };
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await _viewModel.LoadAsync();
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_waitingToClose) { e.Cancel = true; return; }
        if (!_closingAfterCancellation && (_viewModel.IsBusy || _viewModel.HasVerificationSession))
        {
            e.Cancel = true;
            if (_viewModel.IsBusy)
            {
                var result = MessageBox.Show("当前任务正在运行。关闭将取消处理，是否退出？", "退出 LinguaForge-X", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
            }
            _waitingToClose = true;
            try { await _viewModel.CancelAndWaitAsync(); }
            catch (Exception) { _waitingToClose = false; _viewModel.ReportVerificationCloseError(); return; }
            _waitingToClose = false;
            _closingAfterCancellation = true;
            // Disposal may finish synchronously; let the current Closing event unwind first.
            _ = Dispatcher.BeginInvoke(new Action(Close));
            return;
        }
        VideoPlayer.Close();
        VoicePlayer.Close();
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
