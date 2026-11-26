using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Presentation.Wpf.Controls;
using LinguaForge_X.Presentation.Wpf.ViewModels;

internal static partial class Program
{
    private static async Task VerifyTrainingWebUiWorkflowAsync(string output)
    {
        var services = new StubServices(output);
        var voice = new StubLocalVoice();
        using var training = new StubTrainingWebUi();
        var vm = new MainViewModel(services, services, services, services, services, services, services, services, services,
            voice, trainingWebUi: training);
        await vm.LoadAsync();
        var opened = 0;
        var closed = 0;
        vm.TrainingWebUiRequested += _ => opened++;
        vm.TrainingWebUiStopped += () => closed++;
        Assert(training.Starts == 0 && !vm.TrainingWebUiRunning, "training workbench never starts during application load");
        vm.SelectedHistory = services.Completed;
        vm.RestoreCommand.Execute(null);
        await WaitIdle(vm);
        vm.StartTrainingWebUiCommand.Execute(null);
        await WaitIdle(vm);
        Assert(training.Starts == 1 && opened == 1 && voice.StopCalls == 1 && vm.TrainingWebUiRunning,
            "dedicated start stops owned inference then opens the embedded ready workbench");
        Assert(!vm.StartTrainingWebUiCommand.CanExecute(null) && !vm.StartLocalVoiceCommand.CanExecute(null)
            && !vm.TrainLocalVoiceCommand.CanExecute(null) && !vm.ExportCommand.CanExecute(null)
            && !vm.CanEditLocalVoiceEnvironment, "running workbench prevents conflicting inference, training, export and environment changes");
        vm.OpenTrainingWebUiCommand.Execute(null);
        Assert(opened == 2 && training.Starts == 1, "reopening the workbench does not launch a duplicate process");
        vm.StopTrainingWebUiCommand.Execute(null);
        await WaitIdle(vm);
        Assert(training.Stops == 1 && closed == 1 && !vm.TrainingWebUiRunning && vm.StartLocalVoiceCommand.CanExecute(null)
            && vm.ExportCommand.CanExecute(null), "explicit stop closes the embedded workbench and restores normal processing");

        training.FailStart = true;
        vm.StartTrainingWebUiCommand.Execute(null);
        await WaitIdle(vm);
        Assert(!vm.TrainingWebUiRunning && opened == 2 && vm.TrainingWebUiStatus.Contains("测试启动失败"),
            "failed workbench startup cannot open a stale URL or claim readiness");
        training.FailStart = false;
        training.StartResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.StartTrainingWebUiCommand.Execute(null);
        Assert(vm.IsBusy && !vm.TrainLocalVoiceCommand.CanExecute(null), "workbench startup blocks conflicting actions and is cancellable");
        vm.Cancel();
        await WaitIdle(vm);
        Assert(!vm.TrainingWebUiRunning && opened == 2 && vm.TrainingWebUiStatus.Contains("取消"),
            "cancelled startup cannot expose an embedded workbench");
        training.StartResponse = null;
        vm.StartTrainingWebUiCommand.Execute(null);
        await WaitIdle(vm);
        training.Current = new(false, false, null, "测试进程退出");
        vm.RefreshTrainingWebUiStatus();
        Assert(!vm.TrainingWebUiRunning && closed == 2 && vm.TrainingWebUiStatus.Contains("退出"),
            "unexpected process exit clears the browser URL and restores controls");
        vm.StartTrainingWebUiCommand.Execute(null);
        await WaitIdle(vm);
        await vm.CancelAndWaitAsync();
        Assert(!vm.TrainingWebUiRunning && training.Stops == 2, "application shutdown stops its training workbench");

        foreach (var uri in new[] { "http://127.0.0.1:9874/", "http://localhost:9871/" })
            Assert(TrainingWebUiWindow.IsAllowedNavigation(uri), "embedded navigation accepts explicit local workbench addresses");
        foreach (var uri in new[] { "https://example.com/", "http://127.0.0.1:8000/", "http://user:password@127.0.0.1:9874/",
                     "file:///C:/settings.json", "http://127.0.0.1.example.com:9874/", "javascript:alert(1)" })
            Assert(!TrainingWebUiWindow.IsAllowedNavigation(uri), "embedded navigation rejects remote, file, credential and unexpected service addresses");
    }

    private sealed class StubTrainingWebUi : ILocalVoiceTrainingWebUiService
    {
        public LocalVoiceTrainingWebUiResult Current { get; set; } = new(true, false, null, "尚未启动");
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool FailStart { get; set; }
        public TaskCompletionSource<LocalVoiceTrainingWebUiResult>? StartResponse { get; set; }
        public LocalVoiceTrainingWebUiResult GetStatus() => Current;
        public async Task<LocalVoiceTrainingWebUiResult> StartAsync(LocalVoiceSettings settings, CancellationToken cancellationToken = default)
        {
            Starts++;
            if (StartResponse is not null) return Current = await StartResponse.Task.WaitAsync(cancellationToken);
            return Current = FailStart ? new(false, false, null, "测试启动失败")
                : new(true, true, "http://127.0.0.1:9874/", "模拟训练界面就绪");
        }
        public Task<LocalVoiceTrainingWebUiResult> StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            Current = new(true, false, null, "模拟训练界面已停止");
            return Task.FromResult(Current);
        }
        public void Dispose() { }
    }
}
