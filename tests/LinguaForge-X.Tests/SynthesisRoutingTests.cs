using System.Net;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Azure;
using LinguaForge_X.Infrastructure.LocalVoice;
using LinguaForge_X.Infrastructure.Persistence;
using Xunit;

namespace LinguaForge_X.Tests;

public sealed class SynthesisRoutingTests
{
    [Fact]
    public async Task LocalCatalogDoesNotUseRetainedAzureCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "lf-routing-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new RejectNetworkHandler());
        using var local = new LocalVoiceService(root);
        var router = new SynthesisRouter(new AzureAiService(http), local);
        var settings = new AppSettings();
        settings.Synthesis.Provider = ServiceProviderKind.LocalGptSoVits;
        settings.Synthesis.ApiKey = "retained-azure-key";
        settings.Synthesis.Endpoint = "https://example.cognitiveservices.azure.com";
        settings.Translation.ApiKey = "retained-text-key";
        settings.Translation.Endpoint = "https://example.openai.azure.com";
        try
        {
            var result = await router.GetCapabilitiesAsync(settings, default);
            Assert.Empty(result.Voices);
            Assert.Contains(result.TranslationLanguages, l => l.Code == "zh-CN");
            Assert.Contains("本地 GPT-SoVITS", result.Description);
            Assert.Equal("retained-azure-key", settings.Synthesis.ApiKey);
            Assert.Equal("https://example.cognitiveservices.azure.com", settings.Synthesis.Endpoint);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(ServiceKind.Translation)]
    [InlineData(ServiceKind.Transcription)]
    public async Task LocalProviderRejectsUnsupportedOperationsWithoutCloudCalls(ServiceKind kind)
    {
        using var http = new HttpClient(new RejectNetworkHandler());
        using var local = new LocalVoiceService(Path.Combine(Path.GetTempPath(), "lf-unused-" + Guid.NewGuid().ToString("N")));
        var router = new SynthesisRouter(new AzureAiService(http), local);
        var result = await router.TestConnectionAsync(new() { Provider = ServiceProviderKind.LocalGptSoVits }, kind, default);
        Assert.False(result.Success);
        Assert.Contains("仅用于", result.Message);
    }

    [Fact]
    public async Task SavedLocalConfigurationRetainsEncryptedAzureSettings()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "lf-local-settings-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings();
        settings.Synthesis.Provider = ServiceProviderKind.LocalGptSoVits;
        settings.Synthesis.ApiKey = "azure-secret-kept-for-switching";
        settings.Synthesis.Endpoint = "https://existing.cognitiveservices.azure.com";
        settings.Synthesis.LocalVoice = new()
        {
            RootDirectory = "C:\\声音模型\\GPT-SoVITS", PythonPath = "C:\\声音模型\\python.exe",
            ApiEndpoint = "http://127.0.0.1:9881", Device = "cpu"
        };
        try
        {
            var store = new LocalSettingsStore(root);
            await store.SaveAsync(settings);
            Assert.DoesNotContain(settings.Synthesis.ApiKey, await File.ReadAllTextAsync(Path.Combine(root, "settings.json")));
            var loaded = await store.LoadAsync();
            Assert.Equal(ServiceProviderKind.LocalGptSoVits, loaded.Synthesis.Provider);
            Assert.Equal(settings.Synthesis.ApiKey, loaded.Synthesis.ApiKey);
            Assert.Equal(settings.Synthesis.Endpoint, loaded.Synthesis.Endpoint);
            Assert.Equal(settings.Synthesis.LocalVoice.RootDirectory, loaded.Synthesis.LocalVoice.RootDirectory);
            Assert.Equal(settings.Synthesis.LocalVoice.PythonPath, loaded.Synthesis.LocalVoice.PythonPath);
            Assert.Equal("cpu", loaded.Synthesis.LocalVoice.Device);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("This operation must not contact Azure: " + request.RequestUri);
    }
}
