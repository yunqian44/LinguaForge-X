using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Application;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure;
using LinguaForge_X.Presentation.Wpf;
using LinguaForge_X.Presentation.Wpf.Controls;
using LinguaForge_X.Presentation.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;

internal static class Program
{
    private static int _assertions;
    private static int _screenshots;
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-smoke");
        Directory.CreateDirectory(output);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var errors = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var result = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await RunAsync(output, errors); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        });
        app.Run();
        return result;
    }

    private static async Task RunAsync(string output, StringWriter errors)
    {
        using (var services = new ServiceCollection().AddApplication().AddInfrastructure()
            .AddSingleton<MainViewModel>().AddSingleton<MainWindow>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
        {
            var compositionWindow = services.GetRequiredService<MainWindow>();
            Assert(compositionWindow.DataContext is MainViewModel, "production DI resolves MainWindow with all conversion services");
            compositionWindow.Close();
        }
        var stub = new StubServices(output);
        var vm = new MainViewModel(stub, stub, stub, stub, stub, stub, stub, stub, stub);
        await vm.LoadAsync();
        Assert(vm.SubtitleLanguages.Count == 1 && vm.SubtitleLanguages[0].Code == "source", "without translator, only original subtitle language is available");
        Assert(vm.SpeechLanguages.Count == 1 && vm.SpeechLanguages[0].Code == "en-US", "without translator, dubbing is restricted to known source language");
        vm.SourceLanguage = "zh-CN";
        Assert(vm.SpeechLanguages.Count == 1 && vm.SpeechLanguages[0].Code == "zh-CN", "source change updates dubbing language choices");
        Assert(vm.VoiceId == "chinese-voice", "source change updates selected voice");
        vm.SelectedHistory = stub.Completed;
        vm.RestoreCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Project?.PreparationComplete == true && vm.ExportCommand.CanExecute(null), "completed task can be exported");
        vm.RefreshCapabilitiesCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Options.SourceLanguage == "fr-FR" && vm.Options.SubtitleLanguage == "de-DE" && vm.Options.SpeechLanguage == "ja-JP" && vm.Options.VoiceId == "saved-voice", "catalog refresh preserves prepared task options");
        vm.SelectedHistory = stub.Incomplete;
        vm.RestoreCommand.Execute(null);
        await WaitIdle(vm);
        Assert(!vm.HasProject && !vm.ExportCommand.CanExecute(null) && vm.PrepareCommand.CanExecute(null), "incomplete history restores generation draft, not exportable project");

        vm.OnlineUrl = "https://www.youtube.com/watch?v=smoke-video";
        vm.DownloadMaxHeight = 720;
        vm.Settings.OnlineVideo.DownloadDirectory = output;
        vm.Settings.Media.OutputDirectory = output;
        vm.InspectOnlineCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.OnlineSummary.Contains("在线视频测试"), "online inspection displays title and platform");
        vm.OnlineUrl = "https://www.bilibili.com/video/BV-smoke";
        Assert(!vm.OnlineSummary.Contains("在线视频测试"), "editing URL invalidates previously inspected metadata");
        vm.DownloadOnlineCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Options.SourceUrl == vm.OnlineUrl && vm.Options.SourceTitle == "在线视频测试" && vm.Options.InputPath == stub.OnlinePath, "direct download imports media and records source without mandatory inspection");
        Assert(stub.LastDownloadRequest?.MaxHeight == 720 && Path.GetFileName(vm.Options.OutputPath) == "在线视频测试_中文.mp4", "quality selection and readable export filename are applied");
        var previousInput = vm.Options.InputPath;
        var previousUrl = vm.Options.SourceUrl;
        stub.FailDownload = true;
        vm.OnlineUrl = "https://www.youtube.com/watch?v=unavailable";
        vm.DownloadOnlineCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Stage == "需要处理" && vm.Options.InputPath == previousInput && vm.Options.SourceUrl == previousUrl, "failed download preserves prior imported media and metadata");
        stub.FailDownload = false;
        stub.DownloadResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.DownloadOnlineCommand.Execute(null);
        Assert(vm.IsBusy && !vm.PrepareCommand.CanExecute(null), "download keeps conversion controls disabled while active");
        vm.CancelCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Stage == "已取消" && vm.Options.InputPath == previousInput && vm.Options.SourceUrl == previousUrl, "cancelled download preserves prior imported media");
        stub.DownloadResponse = null;
        stub.InspectionResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.InspectOnlineCommand.Execute(null);
        vm.OnlineUrl = "https://www.youtube.com/watch?v=newer";
        stub.InspectionResponse.SetResult(new("old", "过期标题", "YouTube", "https://www.youtube.com/watch?v=old", 60));
        await WaitIdle(vm);
        Assert(!vm.OnlineSummary.Contains("过期标题"), "inspection completing after URL changes cannot show stale metadata");
        stub.InspectionResponse = null;
        stub.DownloadResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.DownloadOnlineCommand.Execute(null);
        vm.Settings.OnlineVideo.DownloadDirectory = "edited-after-start";
        vm.DownloadMaxHeight = 2160;
        vm.OnlineUrl = "https://www.youtube.com/watch?v=latest";
        stub.DownloadResponse.SetResult(new(new("old", "过期视频", "YouTube", "https://www.youtube.com/watch?v=old", 60), stub.OnlinePath));
        await WaitIdle(vm);
        Assert(vm.Options.SourceUrl == previousUrl && vm.Options.InputPath == previousInput, "download completing after URL changes cannot replace prior import");
        Assert(stub.LastDownloadRequest?.MaxHeight == 720 && stub.LastOnlineSettings?.DownloadDirectory == output, "online requests snapshot quality and settings before asynchronous work");
        stub.DownloadResponse = null;
        vm.Settings.OnlineVideo.DownloadDirectory = output;
        vm.PrepareCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Project?.Options.SourceUrl == previousUrl && vm.Project.Options.SourceTitle == "在线视频测试", "downloaded media enters conversion with source metadata");
        Assert(!vm.DownloadOnlineCommand.CanExecute(null) && !vm.InspectOnlineCommand.CanExecute(null), "prepared project prevents replacing its online source");
        vm.ExportCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Project?.Status == "已完成" && stub.LastExportSourceUrl == previousUrl, "online imported conversion exports through the existing pipeline");
        vm.Settings.OnlineVideo.CookiesFilePath = "selected-cookies.txt";
        vm.ClearCookiesCommand.Execute(null);
        Assert(vm.Settings.OnlineVideo.CookiesFilePath == "", "explicit cookie clear removes configured path");

        var settings = new ServiceSettings();
        var password = new PasswordBox { DataContext = settings };
        PasswordBinding.SetEnabled(password, true);
        BindingOperations.SetBinding(password, PasswordBinding.PasswordProperty, new Binding("ApiKey") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        password.Password = "smoke-only-not-a-real-key";
        Assert(settings.ApiKey == "smoke-only-not-a-real-key", "typing first key updates empty source binding");

        await VerifyWebsiteWorkflowAsync(output);
        await VerifyAutoShortenWorkflowAsync(output);
        await VerifyVideoEncodingWorkflowAsync(output);
        await VerifyLocalVoiceWorkflowAsync(output);

        stub.WithTranslation = true;
        vm.RefreshCapabilitiesCommand.Execute(null);
        await WaitIdle(vm);
        vm.SelectedHistory = stub.DisplayProject;
        vm.RestoreCommand.Execute(null);
        await WaitIdle(vm);
        var window = new MainWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        var shortenToggle = (CheckBox)window.FindName("AutoShortenSpeechToggle");
        Assert(shortenToggle.IsEnabled && shortenToggle.IsChecked == true, "auto-shortening can be changed on an idle prepared task");
        shortenToggle.IsChecked = false;
        Assert(!vm.Options.AutoShortenSpeech, "auto-shortening checkbox writes its option through WPF binding");
        shortenToggle.IsChecked = true;
        var encodingSelector = (ComboBox)window.FindName("VideoEncodingSelector");
        Assert(encodingSelector.IsEnabled && encodingSelector.Items.Count == 3 &&
            Equals(encodingSelector.SelectedValue, VideoEncodingMode.Auto), "encoding selector defaults to automatic and stays editable on a prepared task");
        encodingSelector.SelectedValue = VideoEncodingMode.CpuQuality;
        Assert(vm.Settings.Media.VideoEncoding == VideoEncodingMode.CpuQuality, "encoding selector writes global media settings through WPF binding");
        var outputQualitySelector = (ComboBox)window.FindName("OutputQualitySelector");
        Assert(outputQualitySelector.IsEnabled && outputQualitySelector.Items.Count == 5 &&
            Equals(outputQualitySelector.SelectedValue, OutputVideoQuality.Original), "output quality defaults to source resolution and stays editable on a prepared task");
        outputQualitySelector.SelectedValue = OutputVideoQuality.Hd720;
        Assert(vm.Settings.Media.OutputQuality == OutputVideoQuality.Hd720, "output quality selector writes global media settings through WPF binding");
        stub.ExportResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ExportCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Assert(vm.IsBusy && !encodingSelector.IsEnabled, "encoding selection is disabled while an export is running");
        Assert(!outputQualitySelector.IsEnabled, "output quality selection is disabled while an export is running");
        stub.ExportResponse.SetResult(new ConversionResult(vm.Options.OutputPath, null));
        await WaitIdle(vm);
        stub.ExportResponse = null;
        Assert(encodingSelector.IsEnabled && stub.LastExportEncoding == VideoEncodingMode.CpuQuality, "export uses the selected encoding without regenerating the prepared project");
        Assert(outputQualitySelector.IsEnabled && stub.LastExportOutputQuality == OutputVideoQuality.Hd720, "export uses the selected output quality without regenerating the prepared project");
        encodingSelector.SelectedValue = VideoEncodingMode.Auto;
        outputQualitySelector.SelectedValue = OutputVideoQuality.Original;
        foreach (var (width, height, size) in new[] { (1360d, 900d, "default"), (1120d, 720d, "minimum") })
        {
            window.Width = width;
            window.Height = height;
            foreach (var (index, name) in new[] { (0, "conversion"), (1, "settings"), (2, "history"), (3, "my-voice") })
            {
                vm.SelectedTab = index;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                var scroll = VisualChildren<ScrollViewer>(window).FirstOrDefault(v => v.ScrollableHeight > 0);
                scroll?.ScrollToTop();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Capture(window, Path.Combine(output, $"{name}-{size}.png"));
                if (index == 1)
                {
                    ((FrameworkElement)window.FindName("OnlineSettingsCard")).BringIntoView();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    Capture(window, Path.Combine(output, $"settings-{size}-online.png"));
                }
                if (index == 0)
                {
                    encodingSelector.BringIntoView();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    scroll?.ScrollToVerticalOffset(scroll.VerticalOffset + 120);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    Capture(window, Path.Combine(output, $"conversion-{size}-encoding.png"));
                    shortenToggle.BringIntoView();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    scroll?.ScrollToVerticalOffset(scroll.VerticalOffset + 120);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    Capture(window, Path.Combine(output, $"conversion-{size}-speech-fit.png"));
                }
                if (index is 0 or 1 or 3 && scroll is not null)
                {
                    scroll.ScrollToBottom();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    Capture(window, Path.Combine(output, $"{name}-{size}-bottom.png"));
                }
            }
        }
        window.Close();
        var bindingErrors = errors.ToString();
        await File.WriteAllTextAsync(Path.Combine(output, "binding-errors.log"), bindingErrors);
        Assert(string.IsNullOrWhiteSpace(bindingErrors), "no WPF binding errors: " + bindingErrors);
        Console.WriteLine($"UI smoke: {_assertions} assertions passed; {_screenshots} screenshots; zero binding warnings/errors.");
    }

    private static async Task VerifyLocalVoiceWorkflowAsync(string output)
    {
        var services = new StubServices(output) { WithTranslation = true };
        var local = new StubLocalVoice();
        var vm = new MainViewModel(services, services, services, services, services, services, services, services, services, local);
        await vm.LoadAsync();
        vm.Settings.Synthesis.ApiKey = "preserved-azure-placeholder";
        vm.LocalVoiceSettings.RootDirectory = "C:\\GPT-SoVITS";
        vm.LocalVoiceDraft.Name = "本人声音";
        vm.LocalVoiceDraft.ReferenceAudioPath = "C:\\voice\\reference.wav";
        vm.LocalVoiceDraft.ReferenceText = "你好，这是我的声音。";
        var catalogCalls = services.CatalogCalls;
        vm.SaveLocalVoiceCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.LocalVoices.Count == 1 && !vm.LocalVoices[0].IsTrained && vm.LocalVoiceStatus.Contains("没有训练"), "reference voice save is clearly distinguished from model training");
        Assert(services.CatalogCalls == catalogCalls && vm.Settings.Synthesis.Provider == ServiceProviderKind.AzureSpeech, "saving reference audio does not call Azure or silently change synthesis provider");
        vm.SelectedHistory = services.DisplayProject;
        vm.RestoreCommand.Execute(null);
        await WaitIdle(vm);
        var speechText = vm.Segments[0].SpeechText;
        var preparedLanguage = vm.SpeechLanguage;
        vm.UseLocalVoiceCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Settings.Synthesis.Provider == ServiceProviderKind.LocalGptSoVits && vm.VoiceId == local.Profiles[0].Id, "explicit selection switches prepared project to the saved local voice");
        Assert(vm.Settings.Synthesis.ApiKey == "preserved-azure-placeholder" && vm.SpeechLanguage == preparedLanguage && vm.Segments[0].SpeechText == speechText && services.CatalogCalls == catalogCalls, "local selection preserves Azure credentials and prepared text without cloud calls");
        var persisted = await services.LoadAsync();
        Assert(persisted.Synthesis.Provider == ServiceProviderKind.LocalGptSoVits && persisted.Synthesis.LocalVoice.RootDirectory == "C:\\GPT-SoVITS", "local provider and environment choices persist");
        vm.VoiceId = "another-saved-voice";
        Assert(vm.Options.VoiceId == "another-saved-voice", "voice can change on a prepared project without resetting languages");
        vm.PreviewLocalVoiceCommand.Execute(null);
        await WaitIdle(vm);
        Assert(services.LastSynthesisProvider == ServiceProviderKind.LocalGptSoVits && services.LastSynthesisVoiceId == local.Profiles[0].Id && vm.VoiceId == "another-saved-voice", "personal voice preview uses local synthesis without changing current video selection");
        vm.LocalDatasetPath = "C:\\voice\\training.list";
        local.TrainingResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.TrainLocalVoiceCommand.Execute(null);
        Assert(vm.IsLocalVoiceTraining && !vm.UseLocalVoiceCommand.CanExecute(null) && vm.CancelLocalVoiceTrainingCommand.CanExecute(null), "training exposes cancellation and blocks conflicting voice changes");
        vm.CancelLocalVoiceTrainingCommand.Execute(null);
        await WaitIdle(vm);
        Assert(!vm.IsLocalVoiceTraining && vm.LocalVoices.Count == 1 && vm.LocalVoiceStatus.Contains("取消"), "cancelled training does not register a completed voice");
        local.TrainingResponse = null;
        vm.TrainLocalVoiceCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.LocalVoices.Count == 2 && vm.SelectedLocalVoice?.IsTrained == true && vm.LocalVoiceStatus.Contains("训练完成"), "completed training registers the returned model profile");
        Assert(services.CatalogCalls == catalogCalls, "training workflow never calls cloud capability discovery");

        var window = new MainWindow(vm) { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        vm.SelectedTab = 1;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var editors = VisualChildren<ServiceSettingsEditor>(window).ToArray();
        Assert(editors.Length == 3 && editors.Count(x => x.AllowLocalVoice) == 1, "local provider is offered only by the synthesis settings editor");
        var synthesisEditor = (ServiceSettingsEditor)window.FindName("SynthesisSettingsEditor");
        var providerSelector = (ComboBox)synthesisEditor.FindName("ProviderSelector");
        Assert(Equals(providerSelector.SelectedValue, ServiceProviderKind.LocalGptSoVits), "provider control reflects explicit selection from My Voice");
        Assert(!VisualChildren<PasswordBox>(synthesisEditor).Single().IsVisible, "Azure key fields are hidden when local synthesis is selected");
        vm.SelectedTab = 0;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Assert(((ComboBox)window.FindName("VoiceSelector")).IsEnabled, "prepared project keeps voice selector enabled");
        window.Close();
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        _screenshots++;
        Console.WriteLine(path);
    }

    private static async Task VerifyWebsiteWorkflowAsync(string output)
    {
        var stub = new StubServices(output);
        var vm = new MainViewModel(stub, stub, stub, stub, stub, stub, stub, stub, stub);
        await vm.LoadAsync();
        vm.OnlineUrl = "https://www.youtube.com/watch?v=manual-verification";
        vm.Settings.OnlineVideo.CookiesFilePath = "manual-fallback.txt";
        vm.OpenVerificationCommand.Execute(null);
        await WaitIdle(vm);
        var session = stub.LastVerification!;
        Assert(vm.HasVerificationSession && session.Captures == 0 && stub.WebsiteSaveCount == 0 && stub.InspectCount == 0,
            "opening verification never captures cookies or calls downloader automatically");
        Assert(vm.UseVerificationCommand.CanExecute(null) && !vm.DownloadOnlineCommand.CanExecute(null), "active verification offers explicit capture and holds normal download");
        vm.OnlineUrl = "https://www.youtube.com/watch?v=changed";
        Assert(!vm.UseVerificationCommand.CanExecute(null) && vm.VerificationSummary.Contains("已变更"), "changed URL blocks stale verification capture");
        vm.OnlineUrl = "https://www.youtube.com/watch?v=manual-verification";
        vm.OnlineProxy = "http://127.0.0.1:8899";
        Assert(!vm.UseVerificationCommand.CanExecute(null), "changed proxy blocks stale verification capture");
        vm.OnlineProxy = "";
        var catalogCalls = stub.CatalogCalls;
        vm.UseVerificationCommand.Execute(null);
        await WaitIdle(vm);
        Assert(session.Captures == 1 && session.Disposed && !vm.HasVerificationSession && stub.WebsiteSaveCount == 1 && stub.InspectCount == 1,
            "explicit capture saves once, closes own browser, and retries inspection once");
        Assert(vm.Settings.OnlineVideo.CookiesFilePath == "" && stub.SettingsSaveCount == 1 && stub.CatalogCalls == catalogCalls && stub.LastDownloadRequest is null,
            "using captured session persists precedence without cloud refresh or automatic download");
        Assert(vm.OnlineSummary.Contains("在线视频测试") && vm.VerificationSummary.Contains("成功读取"), "successful retry displays real inspected information");

        vm.OpenVerificationCommand.Execute(null);
        await WaitIdle(vm);
        session = stub.LastVerification!;
        session.FailCapture = true;
        vm.UseVerificationCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.HasVerificationSession && !session.Disposed && vm.Stage == "需要处理" && vm.UseVerificationCommand.CanExecute(null),
            "capture failure leaves verification browser available for user retry");
        session.FailCapture = false;
        stub.FailWebsiteSave = true;
        vm.UseVerificationCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.HasVerificationSession && !session.Disposed && stub.WebsiteSaveCount == 1, "save failure keeps browser and does not claim saved session");
        stub.FailWebsiteSave = false;
        stub.FailInspect = true;
        vm.UseVerificationCommand.Execute(null);
        await WaitIdle(vm);
        Assert(session.Disposed && !vm.HasVerificationSession && vm.Stage == "需要处理" && vm.VerificationSummary.Contains("重试未完成") && !vm.OnlineSummary.Contains("在线视频测试"),
            "postcapture inspection failure preserves saved session without false verification success");
        stub.FailInspect = false;

        vm.Settings.OnlineVideo.CookiesFilePath = "keep-this-file.txt";
        vm.ClearWebsiteSessionCommand.Execute(null);
        await WaitIdle(vm);
        Assert(stub.LastDeletedWebsite == VideoWebsite.YouTube && vm.Settings.OnlineVideo.CookiesFilePath == "keep-this-file.txt" && vm.VerificationSummary.Contains("优先"),
            "clear removes only selected site saved session and preserves manual cookie setting");
        vm.OpenVerificationCommand.Execute(null);
        await WaitIdle(vm);
        session = stub.LastVerification!;
        session.CaptureResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.UseVerificationCommand.Execute(null);
        vm.CancelCommand.Execute(null);
        await WaitIdle(vm);
        Assert(session.Disposed && !vm.HasVerificationSession && vm.Stage == "已取消", "cancelling capture closes owned verification browser");

        stub.OpenResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.OpenVerificationCommand.Execute(null);
        vm.OnlineUrl = "https://www.youtube.com/watch?v=changed-while-opening";
        session = new StubVerification(VideoWebsite.YouTube, "");
        stub.OpenResponse.SetResult(session);
        await WaitIdle(vm);
        Assert(session.Disposed && !vm.HasVerificationSession, "opening result after URL change is disposed before becoming active");
        stub.OpenResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.OpenVerificationCommand.Execute(null);
        vm.CancelCommand.Execute(null);
        session = new StubVerification(VideoWebsite.YouTube, "");
        stub.OpenResponse.SetResult(session);
        await WaitIdle(vm);
        Assert(session.Disposed && !vm.HasVerificationSession && vm.Stage == "已取消", "late browser open result after cancellation cannot leak an active session");
        stub.OpenResponse = null;

        vm.OpenVerificationCommand.Execute(null);
        await WaitIdle(vm);
        session = stub.LastVerification!;
        session.CaptureResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousSaveCount = stub.WebsiteSaveCount;
        vm.UseVerificationCommand.Execute(null);
        vm.OnlineProxy = "http://127.0.0.1:8899";
        session.CaptureResponse.SetResult(new(VideoWebsite.YouTube, [], "test-browser", "", DateTimeOffset.UtcNow));
        await WaitIdle(vm);
        Assert(stub.WebsiteSaveCount == previousSaveCount && vm.HasVerificationSession && !vm.UseVerificationCommand.CanExecute(null), "proxy changes during capture cannot save a stale session");
        vm.CloseVerificationCommand.Execute(null);
        await WaitIdle(vm);
        Assert(session.Disposed && !vm.HasVerificationSession, "explicit close disposes mismatched verification session");
        vm.OnlineProxy = "";

        vm.OpenVerificationCommand.Execute(null);
        await WaitIdle(vm);
        session = stub.LastVerification!;
        vm.SelectedHistory = stub.Completed;
        vm.RestoreCommand.Execute(null);
        await WaitIdle(vm);
        Assert(session.Disposed && !vm.HasVerificationSession && !vm.UseVerificationCommand.CanExecute(null), "history restore closes pending verification and clears stale capture state");
        vm.ResetCommand.Execute(null);
        vm.OnlineUrl = "https://www.youtube.com/watch?v=close-test";
        vm.OpenVerificationCommand.Execute(null);
        await WaitIdle(vm);
        session = stub.LastVerification!;
        await vm.CancelAndWaitAsync();
        Assert(session.Disposed && !vm.HasVerificationSession, "shutdown cleanup disposes active verification while no conversion is busy");

        vm.OpenVerificationCommand.Execute(null);
        await WaitIdle(vm);
        session = stub.LastVerification!;
        var window = new MainWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        foreach (var (width, height, size) in new[] { (1360d, 900d, "default"), (1120d, 720d, "minimum") })
        {
            window.Width = width;
            window.Height = height;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Capture(window, Path.Combine(output, $"verification-{size}.png"));
        }
        window.Close();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert(session.Disposed && !vm.HasVerificationSession && !window.IsVisible, "window closing cleans idle verification without a conversion prompt");
    }

    private static async Task VerifyAutoShortenWorkflowAsync(string output)
    {
        var stub = new StubServices(output);
        var vm = new MainViewModel(stub, stub, stub, stub, stub, stub, stub, stub, stub);
        await vm.LoadAsync();
        Assert(vm.Options.AutoShortenSpeech && JsonSerializer.Deserialize<ConversionOptions>("{}")!.AutoShortenSpeech,
            "auto-shortening defaults on for new options and older saved tasks without the property");
        vm.SelectedHistory = stub.DisplayProject;
        vm.RestoreCommand.Execute(null);
        await WaitIdle(vm);
        vm.Options.AutoShortenSpeech = false;
        vm.SaveEditsCommand.Execute(null);
        await WaitIdle(vm);
        vm.SelectedHistory = vm.History.Single(p => p.Id == stub.DisplayProject.Id);
        vm.RestoreCommand.Execute(null);
        await WaitIdle(vm);
        Assert(!vm.Options.AutoShortenSpeech, "an explicitly disabled auto-shortening option survives save and restore");
        vm.Options.AutoShortenSpeech = true;
        vm.Segments[0].SpeechText = vm.Segments[0].SubtitleText = "导出前刚刚校对的完整文本";
        var input = vm.Project;
        stub.ShortenOnExport = true;
        vm.ExportCommand.Execute(null);
        await WaitIdle(vm);
        Assert(stub.SavedBeforeExportMatchesInput, "export persists the user's latest edits before the isolated pipeline starts");
        Assert(!ReferenceEquals(vm.Project, input) && vm.Segments[0].SpeechText == stub.PersistedSpeechText && vm.Segments[0].SubtitleText == stub.PersistedSpeechText,
            "successful export refreshes shortened speech and synchronized subtitles from persisted project copy");
        Assert(vm.Project?.Status == "已完成" && vm.Project.ResultPath == vm.PreviewPath, "successful export still updates result and preview after history refresh");

        stub.FailExportAfterShorten = true;
        stub.PersistedSpeechText = "失败前已保存的精简文本";
        vm.ExportCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Stage == "需要处理" && vm.Segments[0].SpeechText == stub.PersistedSpeechText,
            "failed export refreshes saved shortened text for correction and retry");
        stub.FailExportAfterShorten = false;
        stub.CancelExportAfterShorten = true;
        stub.PersistedSpeechText = "取消前已保存的精简文本";
        vm.ExportCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Stage == "已取消" && vm.Segments[0].SpeechText == stub.PersistedSpeechText,
            "cancelled export refreshes saved shortened text using an uncancelled history read");
        stub.CancelExportAfterShorten = false;
        stub.FailExportBeforeSave = true;
        vm.Segments[0].SpeechText = "前置检查失败也不能丢失的手动编辑";
        vm.ExportCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Stage == "需要处理" && vm.Segments[0].SpeechText == "前置检查失败也不能丢失的手动编辑",
            "pipeline validation failure cannot restore an older draft over unsaved edits");
        stub.FailExportBeforeSave = false;
        stub.FailExportAfterShorten = true;
        stub.FailHistoryAfterExport = true;
        vm.ExportCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Status == "模拟导出失败", "history I/O failure cannot hide the original export error");
        stub.FailExportAfterShorten = false;
        vm.ExportCommand.Execute(null);
        await WaitIdle(vm);
        Assert(vm.Stage == "就绪" && vm.Project?.Status == "已完成", "successful export remains successful when history refresh cannot read disk");
    }

    private static async Task VerifyVideoEncodingWorkflowAsync(string output)
    {
        var stub = new StubServices(output);
        var vm = new MainViewModel(stub, stub, stub, stub, stub, stub, stub, stub, stub);
        await vm.LoadAsync();
        Assert(vm.Settings.Media.VideoEncoding == VideoEncodingMode.Auto &&
            JsonSerializer.Deserialize<AppSettings>("{}")!.Media.VideoEncoding == VideoEncodingMode.Auto &&
            JsonSerializer.Deserialize<AppSettings>("{\"Media\":{\"OutputDirectory\":\"legacy-output\"}}")!.Media.VideoEncoding == VideoEncodingMode.Auto,
            "new settings and older settings JSON without encoding default to automatic acceleration");
        Assert(vm.Settings.Media.OutputQuality == OutputVideoQuality.Original &&
            JsonSerializer.Deserialize<AppSettings>("{}")!.Media.OutputQuality == OutputVideoQuality.Original &&
            JsonSerializer.Deserialize<AppSettings>("{\"Media\":{\"VideoEncoding\":2}}")!.Media.OutputQuality == OutputVideoQuality.Original,
            "new settings and older settings JSON without output quality preserve source resolution by default");
        vm.SelectedHistory = stub.DisplayProject;
        vm.RestoreCommand.Execute(null);
        await WaitIdle(vm);
        vm.Settings.Media.VideoEncoding = VideoEncodingMode.CpuQuality;
        vm.Settings.Media.OutputQuality = OutputVideoQuality.FullHd1080;
        vm.ExportCommand.Execute(null);
        await WaitIdle(vm);
        Assert(stub.LastExportEncoding == VideoEncodingMode.CpuQuality && stub.PrepareCalls == 0 && vm.Project?.PreparationComplete == true,
            "changing global encoding re-exports an existing prepared task without preparing again");
        Assert(stub.LastExportOutputQuality == OutputVideoQuality.FullHd1080 && stub.PrepareCalls == 0,
            "changing global output quality re-exports an existing prepared task without preparing again");
        vm.Settings.Media.VideoEncoding = VideoEncodingMode.CpuFast;
        vm.Settings.Media.OutputQuality = OutputVideoQuality.Compact480;
        vm.SaveSettingsCommand.Execute(null);
        await WaitIdle(vm);
        var reloaded = new MainViewModel(stub, stub, stub, stub, stub, stub, stub, stub, stub);
        await reloaded.LoadAsync();
        Assert(reloaded.Settings.Media.VideoEncoding == VideoEncodingMode.CpuFast && stub.SettingsSaveCount == 1,
            "saved global encoding survives loading settings in a new view model");
        Assert(reloaded.Settings.Media.OutputQuality == OutputVideoQuality.Compact480,
            "saved global output quality survives loading settings in a new view model");
        reloaded.SelectedHistory = stub.DisplayProject;
        reloaded.RestoreCommand.Execute(null);
        await WaitIdle(reloaded);
        Assert(reloaded.Settings.Media.VideoEncoding == VideoEncodingMode.CpuFast,
            "restoring a project does not replace the current global encoding setting");
        Assert(reloaded.Settings.Media.OutputQuality == OutputVideoQuality.Compact480,
            "restoring a project does not replace the current global output quality setting");
        reloaded.ExportCommand.Execute(null);
        await WaitIdle(reloaded);
        Assert(stub.LastExportEncoding == VideoEncodingMode.CpuFast && stub.PrepareCalls == 0,
            "a restored historical project uses the saved global encoding on its next export");
        Assert(stub.LastExportOutputQuality == OutputVideoQuality.Compact480 && stub.PrepareCalls == 0,
            "a restored historical project uses the saved global output quality on its next export");
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }

    private static async Task WaitIdle(MainViewModel vm)
    {
        while (vm.IsBusy) await Task.Delay(10);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    }
    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("UI smoke failed: " + description);
        _assertions++;
        Console.WriteLine("PASS " + description);
    }

    private sealed class StubServices : ISettingsStore, IServiceCatalog, IMediaService, IConversionService, IProjectStore, ISynthesisService, IOnlineVideoService, IWebsiteVerificationService, IWebsiteSessionStore
    {
        public bool WithTranslation { get; set; }
        public ConversionProject Completed { get; }
        public ConversionProject Incomplete { get; }
        public ConversionProject DisplayProject { get; }
        private readonly string _directory;
        public string OnlinePath { get; }
        public bool FailDownload { get; set; }
        public TaskCompletionSource<OnlineVideoInfo>? InspectionResponse { get; set; }
        public TaskCompletionSource<OnlineVideoDownload>? DownloadResponse { get; set; }
        public OnlineVideoRequest? LastDownloadRequest { get; private set; }
        public OnlineVideoSettings? LastOnlineSettings { get; private set; }
        public string? LastExportSourceUrl { get; private set; }
        public VideoEncodingMode? LastExportEncoding { get; private set; }
        public OutputVideoQuality? LastExportOutputQuality { get; private set; }
        public TaskCompletionSource<ConversionResult>? ExportResponse { get; set; }
        public int PrepareCalls { get; private set; }
        private AppSettings _savedSettings = new();
        public int SettingsSaveCount { get; private set; }
        public int CatalogCalls { get; private set; }
        public int InspectCount { get; private set; }
        public int WebsiteSaveCount { get; private set; }
        public bool FailInspect { get; set; }
        public bool FailWebsiteSave { get; set; }
        public VideoWebsite? LastDeletedWebsite { get; private set; }
        public WebsiteSession? SavedWebsiteSession { get; private set; }
        public StubVerification? LastVerification { get; private set; }
        public TaskCompletionSource<IWebsiteVerificationSession>? OpenResponse { get; set; }
        private readonly Dictionary<string, ConversionProject> _savedProjects = [];
        public bool ShortenOnExport { get; set; }
        public bool FailExportAfterShorten { get; set; }
        public bool CancelExportAfterShorten { get; set; }
        public bool FailExportBeforeSave { get; set; }
        public bool FailHistoryAfterExport { get; set; }
        private bool _historyUnavailable;
        public bool SavedBeforeExportMatchesInput { get; private set; }
        public string PersistedSpeechText { get; set; } = "自动精简后的配音稿";
        public StubServices(string directory)
        {
            _directory = directory;
            var input = Path.Combine(directory, "smoke-placeholder.mp4");
            File.WriteAllBytes(input, []);
            OnlinePath = Path.Combine(directory, "smoke-online-placeholder.mp4");
            File.WriteAllBytes(OnlinePath, []);
            Completed = Make(input, "fr-FR", "de-DE", "ja-JP", "saved-voice", true);
            Incomplete = Make(input, "en-US", "zh-CN", "zh-CN", "chinese-voice", false);
            DisplayProject = Make(input, "en-US", "zh-CN", "zh-CN", "chinese-voice", true);
            DisplayProject.Options.SourceTitle = "从英文视频到中文字幕与配音";
            DisplayProject.Options.SourceUrl = "https://www.youtube.com/watch?v=smoke-example";
            _savedProjects[DisplayProject.Id] = Copy(DisplayProject);
            _savedProjects[Incomplete.Id] = Copy(Incomplete);
        }
        private ConversionProject Make(string path, string source, string subtitle, string speech, string voice, bool complete) => new()
        {
            PreparationComplete = complete, Status = complete ? "待校对" : "已取消", WorkDirectory = _directory,
            Options = new() { InputPath = path, OutputPath = Path.Combine(_directory, "示例视频_中文.mp4"), AudioStreamIndex = 1, SourceLanguage = source, SubtitleLanguage = subtitle, SpeechLanguage = speech, VoiceId = voice, GenerateSpeech = true },
            Video = new(path, 64, 1920, 1080, [new(1, "英语立体声 · AAC")]),
            Segments = [new() { Id = 1, StartSeconds = 0, EndSeconds = 4.5, SourceText = "Welcome to our video. Today we will explore a new idea.", SubtitleText = "欢迎观看本期视频。今天我们将探索一个新想法。", SpeechText = "欢迎观看本期视频。今天我们将探索一个新想法。" }, new() { Id = 2, StartSeconds = 5, EndSeconds = 9, SourceText = "Let's begin with a simple example.", SubtitleText = "我们先从一个简单的例子开始。", SpeechText = "我们先从一个简单的例子开始。" }]
        };
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_savedSettings))!);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SettingsSaveCount++;
            _savedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            return Task.CompletedTask;
        }
        public Task<ServiceCapabilities> GetCapabilitiesAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            CatalogCalls++;
            return Task.FromResult(new ServiceCapabilities([new("en-US", "英语（美国）"), new("zh-CN", "中文（简体）")], WithTranslation ? [new("en-US", "英语（美国）"), new("zh-CN", "中文（简体）")] : [], [new("english-voice", "英语示例音色", "en-US", ["en-US"]), new("chinese-voice", "晓晓 · 中文自然音色", "zh-CN", ["zh-CN"])], "此处为 UI 验证模拟目录，没有发送云端请求。"));
        }
        public Task<ConnectionTestResult> TestConnectionAsync(ServiceSettings settings, ServiceKind kind, CancellationToken cancellationToken) => Task.FromResult(new ConnectionTestResult(true, "模拟服务"));
        public string CreateWorkDirectory(string id) => _directory;
        public Task SaveAsync(ConversionProject project, CancellationToken cancellationToken = default)
        { _savedProjects[project.Id] = Copy(project); return Task.CompletedTask; }
        public Task<IReadOnlyList<ConversionProject>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_historyUnavailable) throw new IOException("模拟历史读取失败");
            return Task.FromResult<IReadOnlyList<ConversionProject>>(_savedProjects.Values.Select(Copy).ToArray());
        }
        private static ConversionProject Copy(ConversionProject project) => JsonSerializer.Deserialize<ConversionProject>(JsonSerializer.Serialize(project))!;
        public Task CheckAvailableAsync(MediaSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<VideoInfo> ProbeAsync(string path, MediaSettings settings, CancellationToken cancellationToken) => Task.FromResult(new VideoInfo(path, 64, 1920, 1080, [new(1, "英语立体声 · AAC")]));
        public Task<double> GetAudioDurationAsync(string path, MediaSettings settings, CancellationToken cancellationToken) => Task.FromResult(1d);
        public Task<IReadOnlyList<AudioChunk>> ExtractAudioChunksAsync(VideoInfo video, int audioStreamIndex, string workDirectory, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task BuildDubbedAudioAsync(IReadOnlyList<SpeechClip> clips, double videoDurationSeconds, double maxSpeechSpeed, string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RenderAsync(VideoInfo video, ConversionOptions options, string? subtitlePath, string? dubbedAudioPath, string outputPath, MediaSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConversionProject> PrepareAsync(ConversionOptions options, AppSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            var project = Make(options.InputPath, options.SourceLanguage, options.SubtitleLanguage, options.SpeechLanguage, options.VoiceId, true);
            project.Options = options;
            return Task.FromResult(project);
        }
        public async Task<ConversionResult> ExportAsync(ConversionProject project, AppSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
        {
            LastExportSourceUrl = project.Options.SourceUrl;
            LastExportEncoding = settings.Media.VideoEncoding;
            LastExportOutputQuality = settings.Media.OutputQuality;
            SavedBeforeExportMatchesInput = _savedProjects.TryGetValue(project.Id, out var saved) &&
                JsonSerializer.Serialize(saved.Segments) == JsonSerializer.Serialize(project.Segments);
            if (FailExportBeforeSave) throw new InvalidOperationException("模拟前置校验失败");
            if (ShortenOnExport)
            {
                var copy = Copy(project);
                if (copy.Segments[0].SubtitleText == copy.Segments[0].SpeechText) copy.Segments[0].SubtitleText = PersistedSpeechText;
                copy.Segments[0].SpeechText = PersistedSpeechText;
                await SaveAsync(copy, CancellationToken.None);
            }
            _historyUnavailable = FailHistoryAfterExport;
            if (FailExportAfterShorten) throw new IOException("模拟导出失败");
            if (CancelExportAfterShorten) throw new OperationCanceledException(cancellationToken);
            if (ExportResponse is not null) return await ExportResponse.Task.WaitAsync(cancellationToken);
            return new ConversionResult(project.Options.OutputPath, null);
        }
        public ServiceProviderKind? LastSynthesisProvider { get; private set; }
        public string? LastSynthesisVoiceId { get; private set; }
        public Task SynthesizeAsync(string text, string language, string voiceId, string outputPath, ServiceSettings settings, CancellationToken cancellationToken)
        { LastSynthesisProvider = settings.Provider; LastSynthesisVoiceId = voiceId; return Task.CompletedTask; }
        public Task<string> CheckAvailableAsync(OnlineVideoSettings settings, CancellationToken cancellationToken) => Task.FromResult("下载工具与运行时可用（模拟）。");
        public Task<OnlineVideoInfo> InspectAsync(OnlineVideoRequest request, OnlineVideoSettings settings, CancellationToken cancellationToken)
        {
            InspectCount++;
            if (FailInspect) throw new InvalidOperationException("模拟网站仍要求验证");
            return InspectionResponse?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(new OnlineVideoInfo("smoke", "在线视频测试", "YouTube", request.Url, 64));
        }
        public Task<IWebsiteVerificationSession> OpenAsync(string url, string proxy, CancellationToken cancellationToken)
        {
            LastVerification = new(VideoWebsites.FromUrl(url)!.Value, proxy);
            return OpenResponse?.Task ?? Task.FromResult<IWebsiteVerificationSession>(LastVerification);
        }
        public Task SaveAsync(WebsiteSession session, CancellationToken cancellationToken)
        {
            if (FailWebsiteSave) throw new IOException("模拟保存失败");
            SavedWebsiteSession = session;
            WebsiteSaveCount++;
            return Task.CompletedTask;
        }
        public Task<WebsiteSession?> LoadAsync(VideoWebsite website, CancellationToken cancellationToken) => Task.FromResult(SavedWebsiteSession?.Website == website ? SavedWebsiteSession : null);
        public Task DeleteAsync(VideoWebsite website, CancellationToken cancellationToken) { LastDeletedWebsite = website; SavedWebsiteSession = null; return Task.CompletedTask; }
        public Task<OnlineVideoDownload> DownloadAsync(OnlineVideoRequest request, OnlineVideoSettings settings, MediaSettings mediaSettings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
        {
            LastDownloadRequest = request;
            LastOnlineSettings = settings;
            if (FailDownload) return Task.FromException<OnlineVideoDownload>(new InvalidOperationException("模拟下载失败"));
            progress?.Report(new("下载在线视频", 30, "正在下载模拟视频"));
            return DownloadResponse?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(new OnlineVideoDownload(new("smoke", "在线视频测试", "Bilibili", request.Url, 64), OnlinePath));
        }
    }

    private sealed class StubLocalVoice : ILocalVoiceService
    {
        public List<LocalVoiceProfile> Profiles { get; } = [];
        public TaskCompletionSource<LocalVoiceProfile>? TrainingResponse { get; set; }
        public Task<IReadOnlyList<LocalVoiceProfile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LocalVoiceProfile>>(Profiles.ToArray());
        public Task<LocalVoiceProfile> SaveAsync(LocalVoiceDraft voice, CancellationToken cancellationToken = default)
        {
            var profile = new LocalVoiceProfile { Id = "local-" + Profiles.Count, Name = voice.Name, ReferenceAudioPath = voice.ReferenceAudioPath, ReferenceText = voice.ReferenceText, Language = voice.Language, Revision = "revision" };
            Profiles.Add(profile);
            return Task.FromResult(profile);
        }
        public async Task<LocalVoiceProfile> TrainAsync(LocalVoiceTrainingRequest request, LocalVoiceSettings settings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken = default)
        {
            if (TrainingResponse is not null) return await TrainingResponse.Task.WaitAsync(cancellationToken);
            var profile = new LocalVoiceProfile { Id = "trained-" + Profiles.Count, Name = request.Voice.Name, GptWeightsPath = "gpt.ckpt", SoVitsWeightsPath = "sovits.pth", Revision = "trained-revision" };
            Profiles.Add(profile);
            return profile;
        }
        public Task<ConnectionTestResult> CheckAsync(LocalVoiceSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "模拟检测"));
        public Task<ConnectionTestResult> StartAsync(LocalVoiceSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "模拟启动"));
        public Task<ConnectionTestResult> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "模拟停止"));
    }

    private sealed class StubVerification(VideoWebsite website, string proxy) : IWebsiteVerificationSession
    {
        public VideoWebsite Website => website;
        public string BrowserName => "测试浏览器";
        public int Captures { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailCapture { get; set; }
        public TaskCompletionSource<WebsiteSession>? CaptureResponse { get; set; }
        public Task<WebsiteSession> CaptureAsync(CancellationToken cancellationToken)
        {
            Captures++;
            if (FailCapture) throw new InvalidOperationException("模拟捕获失败，请继续在网页操作");
            return CaptureResponse?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(new WebsiteSession(website, [], "test-browser", proxy, DateTimeOffset.UtcNow));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
