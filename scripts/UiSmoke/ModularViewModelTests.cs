using LinguaForge_X.Domain.Models;
using LinguaForge_X.Presentation.Wpf.ViewModels;

internal static partial class Program
{
    private static async Task VerifyModularViewModelArchitectureAsync(string output)
    {
        var stub = new StubServices(output) { WithTranslation = true };
        var local = new StubLocalVoice();
        var recording = new RecordingStub(output);
        using var training = new StubTrainingWebUi();
        var vm = new MainViewModel(stub, stub, stub, stub, stub, stub, stub, stub, stub, local, recording, training);
        await vm.LoadAsync();

        // Case 1: Sub-ViewModels are instantiated as independent modular components and share WorkspaceCoordinator.
        var components = new object[]
        {
            vm.Workspace, vm.ConversionWorkspace, vm.OnlineVideo,
            vm.ServiceSettings, vm.LocalVoice, vm.Recording, vm.TrainingWebUi
        };
        Assert(components.Length == 7 && components.Distinct().Count() == 7,
            "[TD-01 Case 1] MainViewModel composes 6 domain sub-ViewModels and 1 WorkspaceCoordinator");
        Assert(ReferenceEquals(vm.Settings, vm.Workspace.Settings) && ReferenceEquals(vm.Options, vm.Workspace.Options),
            "[TD-01 Case 1b] Shell facade and WorkspaceCoordinator share identical Settings and Options state");

        // Case 2: PropertyChanged events from sub-ViewModels propagate accurately through MainViewModel for XAML bindings.
        var changedProps = new HashSet<string>(StringComparer.Ordinal);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) changedProps.Add(e.PropertyName); };

        vm.OnlineVideo.OnlineUrl = "https://www.youtube.com/watch?v=td01-modular";
        Assert(changedProps.Contains(nameof(MainViewModel.OnlineUrl)) && vm.OnlineUrl == "https://www.youtube.com/watch?v=td01-modular",
            "[TD-01 Case 2a] OnlineVideoViewModel property mutations propagate through MainViewModel");

        vm.Recording.RecordingName = "模块化录音测试";
        Assert(changedProps.Contains(nameof(MainViewModel.RecordingName)) && vm.RecordingName == "模块化录音测试",
            "[TD-01 Case 2b] VoiceRecordingViewModel property mutations propagate through MainViewModel");

        vm.LocalVoice.LocalDatasetPath = "C:\\voice\\td01.list";
        Assert(changedProps.Contains(nameof(MainViewModel.LocalDatasetPath)) && vm.LocalDatasetPath == "C:\\voice\\td01.list",
            "[TD-01 Case 2c] LocalVoiceViewModel property mutations propagate through MainViewModel");

        // Case 3: Cross-module interaction between VoiceRecordingViewModel and LocalVoiceViewModel.
        vm.Recording.StartRecordingCommand.Execute(null);
        vm.Recording.StopRecordingCommand.Execute(null);
        await WaitIdle(vm);
        vm.Recording.RecordingName = "跨模块参考音色";
        vm.Recording.RecordingSpeaker = "本人";
        vm.Recording.RecordingText = "这是跨子模块填充的参考录音文稿。";
        vm.Recording.RecordingReviewed = true;
        vm.Recording.UseRecordingAsReferenceCommand.Execute(null);
        Assert(vm.LocalVoice.LocalVoiceDraft.Name == "跨模块参考音色"
            && vm.LocalVoice.LocalVoiceDraft.ReferenceText == "这是跨子模块填充的参考录音文稿。"
            && vm.LocalVoiceStatus.Contains("添加本人音色"),
            "[TD-01 Case 3] VoiceRecordingViewModel.UseRecordingAsReferenceCommand populates LocalVoiceViewModel.LocalVoiceDraft");

        // Case 4: Cross-module mutual exclusion via WorkspaceCoordinator when TrainingWebUiViewModel is running.
        vm.SelectedHistory = stub.Completed;
        vm.ConversionWorkspace.RestoreCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.ConversionWorkspace.ExportCommand.CanExecute(null) && vm.LocalVoice.StartLocalVoiceCommand.CanExecute(null),
            "[TD-01 Case 4a] Export and LocalVoice start commands are enabled before TrainingWebUi starts");

        vm.TrainingWebUi.StartTrainingWebUiCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.TrainingWebUi.TrainingWebUiRunning
            && !vm.ConversionWorkspace.ExportCommand.CanExecute(null)
            && !vm.LocalVoice.StartLocalVoiceCommand.CanExecute(null)
            && !vm.LocalVoice.TrainLocalVoiceCommand.CanExecute(null)
            && !vm.TrainingWebUi.CanEditLocalVoiceEnvironment,
            "[TD-01 Case 4b] TrainingWebUiViewModel running state coordinates mutual exclusion across ConversionWorkspace and LocalVoice");

        vm.TrainingWebUi.StopTrainingWebUiCommand.Execute(null);
        await WaitIdle(vm);
        Assert(!vm.TrainingWebUi.TrainingWebUiRunning
            && vm.ConversionWorkspace.ExportCommand.CanExecute(null)
            && vm.LocalVoice.StartLocalVoiceCommand.CanExecute(null),
            "[TD-01 Case 4c] Stopping TrainingWebUiViewModel re-enables ConversionWorkspace and LocalVoice commands");

        // Case 5: Secret redaction in WorkspaceCoordinator.SafeMessage across all sub-ViewModels.
        vm.Settings.Transcription.ApiKey = "secret-stt-key-999";
        vm.Settings.OnlineVideo.Proxy = "http://user:pass@127.0.0.1:7890";
        var redacted = vm.Workspace.SafeMessage(new InvalidOperationException("Failed with secret-stt-key-999 via http://user:pass@127.0.0.1:7890"));
        Assert(!redacted.Contains("secret-stt-key-999", StringComparison.Ordinal)
            && !redacted.Contains("user:pass", StringComparison.Ordinal)
            && redacted.Contains("[密钥已隐藏]", StringComparison.Ordinal)
            && redacted.Contains("[代理凭据已隐藏]", StringComparison.Ordinal),
            "[TD-01 Case 5] WorkspaceCoordinator redacts API keys and proxy credentials across all child ViewModels");
    }
}
