using System.Net;
using System.Net.Sockets;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.LocalVoice;
using Xunit;

namespace LinguaForge_X.Tests;

[Collection("Training WebUI port")]
public sealed class LocalVoiceTrainingWebUiTests
{
    [Fact]
    public async Task ConstructionAndStopNeverConnectToAnExternalService()
    {
        using var service = new LocalVoiceTrainingWebUiService();
        Assert.False(service.GetStatus().IsRunning);
        Assert.Null(service.GetStatus().Url);
        Assert.True((await service.StopAsync()).Success);
    }

    [Fact]
    public async Task OccupiedPortIsRejectedWithoutConnectingOrKillingItsOwner()
    {
        using var files = new Files();
        using var listener = new TcpListener(IPAddress.Loopback, LocalVoiceTrainingWebUiService.Port);
        listener.Start();
        using var service = files.Service();
        var result = await service.StartAsync(files.Settings);
        Assert.False(result.Success);
        Assert.False(result.IsRunning);
        Assert.Null(result.Url);
        Assert.Contains("占用", result.Message);
        Assert.False(service.GetStatus().Success);
        Assert.Equal(result.Message, service.GetStatus().Message);
        Assert.False(listener.Pending());
        await service.StopAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, LocalVoiceTrainingWebUiService.Port);
        Assert.True(listener.Pending());
    }

    [Theory]
    [InlineData("webui.py")]
    [InlineData("api_v2.py")]
    [InlineData("GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s2G2333k.pth")]
    public async Task IncompleteRuntimeIsRejectedBeforeStarting(string missing)
    {
        using var files = new Files();
        File.Delete(Path.Combine(files.Root, missing));
        using var service = files.Service();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(files.Settings));
        Assert.False(service.GetStatus().IsRunning);
        Assert.False(Directory.Exists(files.Logs));
    }

    [Fact]
    public async Task MissingLoopbackGuardIsRejectedBeforeStarting()
    {
        using var files = new Files();
        File.Delete(Path.Combine(files.Scripts, "training_webui_bootstrap", "sitecustomize.py"));
        using var service = files.Service();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(files.Settings));
        Assert.False(Directory.Exists(files.Logs));
    }

    [Theory]
    [InlineData("python --other-command")]
    [InlineData("python\"bad")]
    [InlineData("python'bad")]
    public async Task PythonCommandArgumentsAreNotAccepted(string python)
    {
        using var files = new Files();
        files.Settings.PythonPath = python;
        using var service = files.Service();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(files.Settings));
        Assert.False(Directory.Exists(files.Logs));
    }

    [Fact]
    public async Task InvalidDeviceAndCancelledStartupCreateNoProcessOrLogs()
    {
        using var files = new Files();
        using var service = files.Service();
        files.Settings.Device = "remote";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(files.Settings));
        files.Settings.Device = "cpu";
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(files.Settings, cancelled.Token));
        Assert.False(Directory.Exists(files.Logs));
    }

    [Fact]
    public async Task DisposedServiceCannotStartAgain()
    {
        using var files = new Files();
        var service = files.Service();
        service.Dispose();
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartAsync(files.Settings));
    }

    [Fact]
    public void TimeoutMustBePositive() => Assert.Throws<ArgumentOutOfRangeException>(
        () => new LocalVoiceTrainingWebUiService(startupTimeout: TimeSpan.Zero));

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lfx-webui-tests-" + Guid.NewGuid().ToString("N"));
        public string Scripts => Path.Combine(Root, "scripts");
        public string Logs => Path.Combine(Root, "logs");
        public LocalVoiceSettings Settings { get; }
        public Files()
        {
            foreach (var file in new[] { "api_v2.py", "webui.py", "python.exe",
                         "GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s2G2333k.pth",
                         "GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt",
                         "scripts/start_training_webui.py", "scripts/training_webui_bootstrap/sitecustomize.py" })
            {
                var path = Path.Combine(Root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "fixture");
            }
            Settings = new() { RootDirectory = Root, PythonPath = Path.Combine(Root, "python.exe"), Device = "cpu" };
        }
        public LocalVoiceTrainingWebUiService Service() => new(Scripts, Logs);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}

[CollectionDefinition("Training WebUI port", DisableParallelization = true)]
public sealed class TrainingWebUiPortCollection { }
