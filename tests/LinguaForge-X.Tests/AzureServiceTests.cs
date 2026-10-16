using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Azure;
using Xunit;

namespace LinguaForge_X.Tests;

public sealed class AzureServiceTests
{
    [Theory]
    [InlineData("https://resource.openai.azure.com", "stt", "2024-10-21", "https://resource.openai.azure.com/openai/deployments/stt/audio/transcriptions?api-version=2024-10-21")]
    [InlineData("https://resource.openai.azure.com/openai/deployments/stt/audio/transcriptions?api-version=2025-04-01-preview", "", "2024-10-21", "https://resource.openai.azure.com/openai/deployments/stt/audio/transcriptions?api-version=2025-04-01-preview")]
    [InlineData("https://resource.openai.azure.com/openai/v1/", "stt", "2024-10-21", "https://resource.openai.azure.com/openai/v1/audio/transcriptions")]
    public void NormalizesSupportedOpenAiEndpoints(string endpoint, string deployment, string version, string expected)
    {
        var settings = OpenAi(); settings.Endpoint = endpoint; settings.Deployment = deployment; settings.ApiVersion = version;
        Assert.Equal(expected, AzureEndpoints.OpenAI(settings, "audio/transcriptions").AbsoluteUri);
    }

    [Theory]
    [InlineData("http://resource.openai.azure.com")]
    [InlineData("https://user:secret@resource.openai.azure.com")]
    [InlineData("https://resource.openai.azure.com?api-key=secret")]
    [InlineData("https://resource.openai.azure.com/openai/deployments/other/audio/transcriptions")]
    [InlineData("https://resource.openai.azure.com/openai/deployments/model/audio/speech")]
    [InlineData("https://resource.openai.azure.com/path-we-cannot-normalize")]
    public void RejectsUnsafeOrAmbiguousOpenAiEndpoints(string endpoint)
    {
        var settings = OpenAi(); settings.Endpoint = endpoint;
        Assert.Throws<InvalidOperationException>(() => AzureEndpoints.OpenAI(settings, "audio/transcriptions"));
    }

    [Fact]
    public void SpeechResourceAndRegionUrlsHaveCorrectPaths()
    {
        var settings = Speech();
        Assert.Equal("https://resource.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15", AzureEndpoints.Speech(settings, false).AbsoluteUri);
        Assert.Equal("https://resource.cognitiveservices.azure.com/tts/cognitiveservices/v1", AzureEndpoints.Speech(settings, true).AbsoluteUri);
        Assert.Equal("https://resource.cognitiveservices.azure.com/tts/cognitiveservices/voices/list", AzureEndpoints.Speech(settings, true, true).AbsoluteUri);
        settings.Endpoint = ""; settings.Region = "eastus";
        Assert.Equal("https://eastus.tts.speech.microsoft.com/cognitiveservices/v1", AzureEndpoints.Speech(settings, true).AbsoluteUri);
    }

    [Fact]
    public async Task WhisperSendsVerboseMultipartAndUsesActualTimes()
    {
        using var file = new TemporaryFile();
        await File.WriteAllBytesAsync(file.Path, Wav());
        using var handler = new FakeHandler(async (request, _) =>
        {
            Assert.Equal("test-secret", request.Headers.GetValues("api-key").Single());
            Assert.False(request.Headers.Contains("Ocp-Apim-Subscription-Key"));
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            Assert.Equal("verbose_json", await Part(form, "response_format").ReadAsStringAsync());
            Assert.Equal("en", await Part(form, "language").ReadAsStringAsync());
            Assert.Equal(Wav(), await Part(form, "file").ReadAsByteArrayAsync());
            return Json("""{"text":"hello","segments":[{"start":1.25,"end":2.5,"text":"hello"}]}""");
        });
        using var client = new HttpClient(handler);
        var segment = Assert.Single(await new AzureAiService(client).TranscribeAsync(file.Path, "en-US", OpenAi(), default));
        Assert.Equal(1.25, segment.StartSeconds); Assert.Equal(2.5, segment.EndSeconds);
    }

    [Theory]
    [InlineData("{\"text\":\"hello\"}")]
    [InlineData("{\"text\":\"hello\",\"segments\":[]}")]
    [InlineData("{\"segments\":[{\"text\":\"hello\",\"start\":2,\"end\":1}]}")]
    [InlineData("{\"segments\":[{\"text\":\"hello\",\"start\":-1,\"end\":1}]}")]
    public void RejectsMissingAndInvalidTranscriptionTimes(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Throws<InvalidOperationException>(() => AzureResponseParser.ParseTranscript(json.RootElement, false));
    }

    [Fact]
    public void SpeechConvertsMillisecondsWithoutInventingTimes()
    {
        using var json = JsonDocument.Parse("""{"phrases":[{"offsetMilliseconds":1500,"durationMilliseconds":750,"text":"hello"}]}""");
        var segment = Assert.Single(AzureResponseParser.ParseTranscript(json.RootElement, true));
        Assert.Equal(1.5, segment.StartSeconds); Assert.Equal(2.25, segment.EndSeconds);
    }

    [Fact]
    public async Task TranscriptionConnectionTestActuallyPostsOneSecondSilentWav()
    {
        using var handler = new FakeHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("transcriptions:transcribe", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-secret", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            Assert.Equal(32044, (await Part(form, "audio").ReadAsByteArrayAsync()).Length);
            using var definition = JsonDocument.Parse(await Part(form, "definition").ReadAsStringAsync());
            Assert.Equal("en-US", definition.RootElement.GetProperty("locales")[0].GetString());
            return Json("""{"phrases":[],"combinedPhrases":[{"text":""}]}""");
        });
        using var client = new HttpClient(handler);
        var result = await new AzureAiService(client).TestConnectionAsync(Speech(), ServiceKind.Transcription, default);
        Assert.True(result.Success); Assert.Contains("真实视频验证", result.Message); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TranslationPreservesInputOrderAndIdsFromJsonMode()
    {
        using var handler = new FakeHandler(async (request, _) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("json_object", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            Assert.Equal("model", json.RootElement.GetProperty("model").GetString());
            using var user = JsonDocument.Parse(json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Assert.Equal("zh-CN", user.RootElement.GetProperty("targetLanguage").GetString());
            return Translation("""{"items":[{"id":20,"text":"世界"},{"id":10,"text":"你好"}]}""");
        });
        using var client = new HttpClient(handler);
        var result = await new AzureAiService(client).TranslateAsync([new(10, "Hello"), new(20, "World")], "en-US", "zh-CN", OpenAi(), default);
        Assert.Equal([10, 20], result.Select(x => x.Id)); Assert.Equal("你好", result[0].Text);
    }

    [Theory]
    [InlineData("{\"items\":[{\"id\":10,\"text\":\"a\"},{\"id\":10,\"text\":\"b\"}]}")]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":[{\"id\":99,\"text\":\"a\"}]}")]
    [InlineData("{\"items\":[{\"id\":10,\"text\":\"\"}]}")]
    [InlineData("{\"items\":[{\"id\":\"10\",\"text\":\"a\"}]}")]
    public void TranslationRejectsIncompleteDuplicateAndUnknownIds(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Throws<InvalidOperationException>(() => AzureResponseParser.ParseTranslation(json.RootElement, [new(10, "Hello")]));
    }

    [Fact]
    public async Task TranslationRejectsTruncatedCompletion()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Translation("""{"items":[{"id":1,"text":"你好"}]}""", "length")));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AzureAiService(client).TranslateAsync([new(1, "Hello")], "en-US", "zh-CN", OpenAi(), default));
    }

    [Fact]
    public async Task SpeechCatalogIncludesMultilingualVoiceLocalesAndDoesNotClaimSynthesisTested()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.EndsWith("/tts/cognitiveservices/voices/list", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""[{"ShortName":"en-US-JennyMultilingualNeural","LocalName":"Jenny","Locale":"en-US","SecondaryLocaleList":["zh-CN","ja-JP","en-US"]}]"""));
        });
        using var client = new HttpClient(handler);
        var result = await new AzureAiService(client).GetCapabilitiesAsync(new AppSettings { Synthesis = Speech() }, default);
        var voice = Assert.Single(result.Voices);
        Assert.Equal(["en-US", "zh-CN", "ja-JP"], voice.SupportedLocales);
        Assert.Contains("仍需试听验证", result.Description);
    }

    [Fact]
    public async Task OpenAiCatalogMakesNoCloudRequestsAndMarksVoicesUnverified()
    {
        using var handler = new FakeHandler((_, _) => throw new Exception("Catalog must not send a paid OpenAI request."));
        using var client = new HttpClient(handler);
        var synthesis = OpenAi(); synthesis.Model = "tts-1";
        var result = await new AzureAiService(client).GetCapabilitiesAsync(new AppSettings { Synthesis = synthesis }, default);
        Assert.NotEmpty(result.Voices); Assert.Contains("未验证当前部署", result.Description); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SpeechSynthesisEscapesSsmlAndWritesRealWav()
    {
        using var output = new TemporaryFile();
        const string input = "你好 <voice name='other'> & 世界";
        using var handler = new FakeHandler(async (request, _) =>
        {
            Assert.Equal("riff-24khz-16bit-mono-pcm", request.Headers.GetValues("X-Microsoft-OutputFormat").Single());
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync());
            XNamespace ns = "http://www.w3.org/2001/10/synthesis";
            Assert.Single(xml.Descendants(ns + "voice"));
            Assert.Equal(input, xml.Descendants(ns + "lang").Single().Value);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wav()) };
        });
        using var client = new HttpClient(handler);
        await new AzureAiService(client).SynthesizeAsync(input, "zh-CN", "zh-CN-XiaoxiaoNeural", output.Path, Speech(), default);
        Assert.Equal(Wav(), await File.ReadAllBytesAsync(output.Path));
    }

    [Fact]
    public async Task RedirectIsRejectedWithoutAnotherCredentialedRequest()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        { Headers = { Location = new Uri("https://unrelated.invalid/collect") } }));
        using var client = new HttpClient(handler);
        var result = await new AzureAiService(client).TestConnectionAsync(OpenAi(), ServiceKind.Translation, default);
        Assert.False(result.Success); Assert.Contains("重定向", result.Message); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Transient429RetriesWithFreshRequestAndRespectsRetryAfter()
    {
        var requests = new List<HttpRequestMessage>();
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.DoesNotContain(request, requests); requests.Add(request);
            if (requests.Count == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero) } });
            return Task.FromResult(Translation("""{"items":[{"id":1,"text":"你好"}]}"""));
        });
        using var client = new HttpClient(handler);
        var result = await new AzureAiService(client).TestConnectionAsync(OpenAi(), ServiceKind.Translation, default);
        Assert.True(result.Success); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AuthFailureDoesNotRetryOrExposeRemoteErrorBody()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("remote echoed test-secret and private transcript") }));
        using var client = new HttpClient(handler);
        var result = await new AzureAiService(client).TestConnectionAsync(OpenAi(), ServiceKind.Translation, default);
        Assert.False(result.Success); Assert.Contains("401", result.Message); Assert.DoesNotContain("test-secret", result.Message); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CanceledTokenPreventsCloudCalls()
    {
        using var handler = new FakeHandler((_, _) => throw new Exception("Must not send."));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AzureAiService(client).TranslateAsync([new(1, "Hello")], "en-US", "zh-CN", OpenAi(), cancellation.Token));
        Assert.Equal(0, handler.Calls);
    }

    private static ServiceSettings OpenAi() => new() { Provider = ServiceProviderKind.AzureOpenAI, Endpoint = "https://resource.openai.azure.com", ApiKey = "test-secret", Deployment = "model", Model = "whisper" };
    private static ServiceSettings Speech() => new() { Provider = ServiceProviderKind.AzureSpeech, Endpoint = "https://resource.cognitiveservices.azure.com", ApiKey = "test-secret" };
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Translation(string content, string reason = "stop") => Json(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = reason, message = new { content } } } }));
    private static HttpContent Part(MultipartFormDataContent form, string name) => form.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == name);
    private static byte[] Wav()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(68); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1);
        writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(32); writer.Write(new byte[32]); return stream.ToArray();
    }
    private sealed class TemporaryFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LinguaForge-X-test-" + Guid.NewGuid().ToString("N") + ".wav");
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return callback(request, cancellationToken); }
    }
}
