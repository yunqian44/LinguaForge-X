using System.IO;
using System.Text.Json;
using System.Windows;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.LocalVoice;
using LinguaForge_X.Presentation.Wpf.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static partial class Program
{
    // Opt-in integration check: no recording, model training, or real user settings are used.
    private static async Task VerifyLiveTrainingBrowserAsync(string output, string root, string python)
    {
        using var service = new LocalVoiceTrainingWebUiService(logsDirectory: Path.Combine(output, "logs"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        TrainingWebUiWindow? window = null;
        try
        {
            var start = await service.StartAsync(new LocalVoiceSettings
            {
                RootDirectory = Path.GetFullPath(root), PythonPath = Path.GetFullPath(python), Device = "cpu"
            }, deadline.Token);
            Assert(start.Success && start.IsRunning && start.Url is not null, "real owned training service is ready");
            window = new TrainingWebUiWindow(new Uri(start.Url!));
            window.Show();
            var browser = (WebView2)window.FindName("Browser");
            var loaded = false;
            for (var attempt = 0; attempt < 180 && !loaded; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (browser.CoreWebView2 is { } core)
                {
                    var json = await core.ExecuteScriptAsync("JSON.stringify({buttons:document.querySelectorAll('button').length,text:document.body.innerText})");
                    var payload = JsonSerializer.Deserialize<string>(json);
                    if (payload is not null)
                    {
                        using var data = JsonDocument.Parse(payload);
                        loaded = data.RootElement.GetProperty("buttons").GetInt32() > 10
                                 && data.RootElement.GetProperty("text").GetString()!.Contains("GPT-SoVITS", StringComparison.Ordinal);
                    }
                }
                if (!loaded) await Task.Delay(500, deadline.Token);
            }
            Assert(loaded, "embedded WebView2 renders the live Gradio workbench controls");
            await using (var screenshot = File.Create(Path.Combine(output, "live-training-workbench.png")))
                await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
            var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (args.Uri == "https://example.com/") blocked.TrySetResult(args.Cancel);
            };
            browser.CoreWebView2.Navigate("https://example.com/");
            Assert(await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)), "embedded browser cancels external navigation before loading it");
            window.Close();
            window = null;
            Assert(service.GetStatus().IsRunning, "closing only the workbench window keeps the service running");
        }
        finally
        {
            window?.Close();
            var stopped = await service.StopAsync();
            Assert(stopped.Success && !stopped.IsRunning, "live browser smoke releases its owned training service");
        }
        Console.WriteLine($"Live training browser smoke passed: {_assertions} assertions.");
    }
}
