using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.LocalVoice;
using Xunit;

namespace LinguaForge_X.Tests;

public sealed class LocalVoiceTests
{
    [Fact]
    public async Task SavedProfileOwnsItsRecordingAndModelsAfterOriginalsAreRemoved()
    {
        using var files = new Files();
        using var service = files.Service();
        var draft = files.Draft();
        var profile = await service.SaveAsync(draft);
        File.Delete(draft.ReferenceAudioPath);
        File.Delete(draft.GptWeightsPath);
        File.Delete(draft.SoVitsWeightsPath);
        var restored = Assert.Single(await service.ListAsync());
        Assert.Equal(profile.Id, restored.Id);
        Assert.True(restored.IsTrained);
        Assert.NotEmpty(restored.Revision);
        Assert.Equal(Wave(5), await File.ReadAllBytesAsync(restored.ReferenceAudioPath));
        Assert.Equal("gpt weights", await File.ReadAllTextAsync(restored.GptWeightsPath));
        Assert.Equal("sovits weights", await File.ReadAllTextAsync(restored.SoVitsWeightsPath));
        Assert.Empty(Directory.GetDirectories(files.Profiles, ".pending-*"));
    }

    [Fact]
    public async Task SavingAnotherRevisionProducesANewIdentityAndPreservesOldProfile()
    {
        using var files = new Files();
        using var service = files.Service();
        var draft = files.Draft();
        var first = await service.SaveAsync(draft);
        draft.ReferenceText = "这是新的参考录音文字。";
        var second = await service.SaveAsync(draft);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.Equal(2, (await service.ListAsync()).Count);
        Assert.NotEqual(await service.GetCacheIdentityAsync(first.Id, Settings(), default),
            await service.GetCacheIdentityAsync(second.Id, Settings(), default));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(11)]
    public async Task ReferenceMustBeThreeToTenSeconds(int seconds)
    {
        using var files = new Files();
        using var service = files.Service();
        var draft = files.Draft();
        await File.WriteAllBytesAsync(draft.ReferenceAudioPath, Wave(seconds));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(draft));
        Assert.Contains("3～10", error.Message);
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task HalfAModelPairAndCompressedOrTruncatedWaveAreRejected()
    {
        using var files = new Files();
        using var service = files.Service();
        var draft = files.Draft();
        draft.SoVitsWeightsPath = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(draft));
        draft.GptWeightsPath = "";
        await File.WriteAllBytesAsync(draft.ReferenceAudioPath, Wave(5)[..^2]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(draft));
        var compressed = Wave(5);
        BinaryPrimitives.WriteUInt16LittleEndian(compressed.AsSpan(20), 6);
        await File.WriteAllBytesAsync(draft.ReferenceAudioPath, compressed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(draft));
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task CancelledSaveDoesNotCreateAProfile()
    {
        using var files = new Files();
        using var service = files.Service();
        var draft = files.Draft();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(draft, cancelled.Token));
        Assert.False(Directory.Exists(files.Profiles));
    }

    [Theory]
    [InlineData("https://127.0.0.1:9880")]
    [InlineData("http://192.168.0.5:9880")]
    [InlineData("http://example.com:9880")]
    [InlineData("http://localhost:9880/tts")]
    [InlineData("http://user:secret@localhost:9880")]
    [InlineData("http://127.0.0.1:9880/?key=secret")]
    [InlineData("http://127.0.0.1:9880/#fragment")]
    public async Task RemoteOrCredentialBearingEndpointsAreRejectedBeforeAnyRequest(string endpoint)
    {
        using var files = new Files();
        using var service = files.Service((_, _) => throw new Xunit.Sdk.XunitException("No request was expected."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(new() { ApiEndpoint = endpoint }));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9880", "127.0.0.1")]
    [InlineData("http://localhost:9880", "127.0.0.1")]
    [InlineData("http://[::1]:9880", "[::1]")]
    public async Task HealthCheckRequiresTheRealApiContract(string endpoint, string host)
    {
        using var files = new Files();
        using var service = files.Service((request, _) =>
        {
            Assert.Equal(host, request.RequestUri!.Host);
            Assert.Equal("/openapi.json", request.RequestUri.AbsolutePath);
            return Task.FromResult(Json("""{"paths":{"/tts":{"post":{}},"/set_gpt_weights":{"get":{}},"/set_sovits_weights":{"get":{}}}}"""));
        });
        var result = await service.CheckAsync(new() { ApiEndpoint = endpoint });
        Assert.True(result.Success);
        Assert.Contains("试听", result.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"paths\":{\"/tts\":{\"get\":{}}}}")]
    [InlineData("<html>unrelated web server</html>")]
    public async Task AnUnrelatedHttpServiceIsNotReportedAsReady(string body)
    {
        using var files = new Files();
        using var service = files.Service((_, _) => Task.FromResult(Json(body)));
        Assert.False((await service.CheckAsync(new())).Success);
    }

    [Fact]
    public async Task SynthesisUsesSavedFilesLanguageAndNeverAzureCredentials()
    {
        using var files = new Files();
        var routes = new List<string>();
        LocalVoiceProfile? profile = null;
        using var service = files.Service(async (request, token) =>
        {
            Assert.Empty(request.Headers);
            Assert.DoesNotContain("azure-secret", request.RequestUri!.AbsoluteUri);
            var route = request.RequestUri.AbsolutePath;
            routes.Add(route);
            if (route != "/tts")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                var expected = route == "/set_gpt_weights" ? profile!.GptWeightsPath : profile!.SoVitsWeightsPath;
                Assert.Equal("?weights_path=" + Uri.EscapeDataString(expected), request.RequestUri.Query);
                return Json("{\"message\":\"success\"}");
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.DoesNotContain("azure-secret", body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("你好，世界。", json.RootElement.GetProperty("text").GetString());
            Assert.Equal("zh", json.RootElement.GetProperty("text_lang").GetString());
            Assert.Equal("en", json.RootElement.GetProperty("prompt_lang").GetString());
            Assert.Equal(profile!.ReferenceAudioPath, json.RootElement.GetProperty("ref_audio_path").GetString());
            Assert.False(json.RootElement.GetProperty("streaming_mode").GetBoolean());
            Assert.Equal("wav", json.RootElement.GetProperty("media_type").GetString());
            return Audio(Wave(1));
        });
        var draft = files.Draft();
        draft.Language = "en-US";
        profile = await service.SaveAsync(draft);
        var output = Path.Combine(files.Root, "output.wav");
        await service.SynthesizeAsync("你好，世界。", "zh-CN", profile.Id, output, Settings(), default);
        Assert.Equal(new[] { "/set_gpt_weights", "/set_sovits_weights", "/tts" }, routes);
        Assert.Equal(Wave(1), await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task ReferenceOnlyVoiceExplicitlyRestoresBaseModelsAfterTrainedVoice()
    {
        using var files = new Files();
        var weights = new List<string>();
        using var service = files.Service((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/tts") return Task.FromResult(Audio(Wave(1)));
            weights.Add(Uri.UnescapeDataString(request.RequestUri.Query[14..]));
            return Task.FromResult(Json("{\"message\":\"success\"}"));
        });
        var draft = files.Draft();
        var trained = await service.SaveAsync(draft);
        draft.GptWeightsPath = draft.SoVitsWeightsPath = "";
        var reference = await service.SaveAsync(draft);
        var settings = Settings();
        settings.LocalVoice.RootDirectory = files.Runtime();
        await service.SynthesizeAsync("测试", "zh-CN", trained.Id, Path.Combine(files.Root, "a.wav"), settings, default);
        await service.SynthesizeAsync("测试", "zh-CN", reference.Id, Path.Combine(files.Root, "b.wav"), settings, default);
        Assert.Equal(trained.GptWeightsPath, weights[0]);
        Assert.Equal(trained.SoVitsWeightsPath, weights[1]);
        Assert.Equal(Path.GetFullPath(Path.Combine(settings.LocalVoice.RootDirectory, LocalVoiceFiles.DefaultGpt)), weights[2]);
        Assert.Equal(Path.GetFullPath(Path.Combine(settings.LocalVoice.RootDirectory, LocalVoiceFiles.DefaultSoVits)), weights[3]);
    }

    [Fact]
    public async Task TwoParallelVoicesCannotInterleaveModelSwitchesAndSynthesis()
    {
        using var files = new Files();
        var events = new List<string>();
        using var service = files.Service(async (request, token) =>
        {
            events.Add(request.RequestUri!.AbsolutePath);
            await Task.Delay(15, token);
            return request.RequestUri.AbsolutePath == "/tts" ? Audio(Wave(1)) : Json("{\"message\":\"success\"}");
        });
        var a = await service.SaveAsync(files.Draft());
        var b = await service.SaveAsync(files.Draft());
        await Task.WhenAll(service.SynthesizeAsync("甲", "zh-CN", a.Id, Path.Combine(files.Root, "a.wav"), Settings(), default),
            service.SynthesizeAsync("乙", "zh-CN", b.Id, Path.Combine(files.Root, "b.wav"), Settings(), default));
        Assert.Equal(new[] { "/set_gpt_weights", "/set_sovits_weights", "/tts", "/set_gpt_weights", "/set_sovits_weights", "/tts" }, events);
    }

    [Theory]
    [InlineData("invalid-audio")]
    [InlineData("truncated-wave")]
    [InlineData("redirect")]
    [InlineData("model-failure")]
    public async Task FailurePreservesExistingOutputAndRemovesPartialFile(string mode)
    {
        using var files = new Files();
        using var service = files.Service((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/tts")
                return Task.FromResult(Json(mode == "model-failure" ? "{\"message\":\"error\"}" : "{\"message\":\"success\"}"));
            if (mode == "redirect")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/should-not-follow") } });
            return Task.FromResult(Audio(mode == "truncated-wave" ? Wave(1)[..^1] : Encoding.UTF8.GetBytes("not audio")));
        });
        var profile = await service.SaveAsync(files.Draft());
        var output = Path.Combine(files.Root, "output.wav");
        await File.WriteAllTextAsync(output, "existing result");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynthesizeAsync("测试", "zh-CN", profile.Id, output, Settings(), default));
        Assert.Equal("existing result", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(files.Root, "*.partial"));
    }

    [Fact]
    public async Task CancellationRemovesPartialOutputAndReleasesTheModelGate()
    {
        using var files = new Files();
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        using var service = files.Service((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/tts") return Task.FromResult(Json("{\"message\":\"success\"}"));
            if (++attempts > 1) return Task.FromResult(Audio(Wave(1)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelStream(started)) });
        });
        var profile = await service.SaveAsync(files.Draft());
        var output = Path.Combine(files.Root, "output.wav");
        var pending = service.SynthesizeAsync("测试", "zh-CN", profile.Id, output, Settings(), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(files.Root, "*.partial"));
        await service.SynthesizeAsync("重试", "zh-CN", profile.Id, output, Settings(), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Wave(1), await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task LocalServerErrorIncludesBoundedDiagnosticWithoutWritingAudio()
    {
        using var files = new Files();
        using var service = files.Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"message\":\"change gpt weight failed\",\"Exception\":\"missing local model\"}")
        }));
        var profile = await service.SaveAsync(files.Draft());
        var output = Path.Combine(files.Root, "failed.wav");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynthesizeAsync("测试", "zh-CN", profile.Id, output, Settings(), default));
        Assert.Contains("HTTP 400", error.Message);
        Assert.Contains("missing local model", error.Message);
        Assert.DoesNotContain("azure-secret", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CacheIdentityDetectsChangedModelsAndMissingProfile()
    {
        using var files = new Files();
        using var service = files.Service();
        var profile = await service.SaveAsync(files.Draft());
        var first = await service.GetCacheIdentityAsync(profile.Id, Settings(), default);
        await File.AppendAllTextAsync(profile.GptWeightsPath, "modified");
        var second = await service.GetCacheIdentityAsync(profile.Id, Settings(), default);
        Assert.NotEqual(first, second);
        File.Delete(profile.ReferenceAudioPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCacheIdentityAsync(profile.Id, Settings(), default));
    }

    [Fact]
    public async Task StopNeverSendsExitToAnExternalServer()
    {
        using var files = new Files();
        using var service = files.Service((_, _) => throw new Xunit.Sdk.XunitException("An external server must not be controlled."));
        Assert.True((await service.StopAsync()).Success);
    }

    private static ServiceSettings Settings() => new()
    {
        Provider = ServiceProviderKind.LocalGptSoVits, ApiKey = "azure-secret", Endpoint = "https://some.azure.com", Region = "eastus"
    };

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Audio(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };

    private static byte[] Wave(int seconds)
    {
        var bytes = new byte[44 + seconds * 16000 * 2];
        "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 16000); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)bytes.Length - 44);
        return bytes;
    }

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LinguaForge-LocalVoiceTests-" + Guid.NewGuid().ToString("N"));
        public string Profiles => Path.Combine(Root, "profiles");
        public Files() => Directory.CreateDirectory(Root);
        public LocalVoiceService Service(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null) =>
            new(Profiles, new Handler(send ?? ((_, _) => throw new Xunit.Sdk.XunitException("No HTTP expected."))));
        public LocalVoiceDraft Draft()
        {
            var reference = Path.Combine(Root, "本人参考.wav");
            var gpt = Path.Combine(Root, "本人 GPT.ckpt");
            var sovits = Path.Combine(Root, "本人 SoVITS.pth");
            File.WriteAllBytes(reference, Wave(5)); File.WriteAllText(gpt, "gpt weights"); File.WriteAllText(sovits, "sovits weights");
            return new() { Name = "我的声音", ReferenceAudioPath = reference, ReferenceText = "这是本人的参考录音。", GptWeightsPath = gpt, SoVitsWeightsPath = sovits };
        }
        public string Runtime()
        {
            var root = Path.Combine(Root, "runtime");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "api_v2.py"), "# fixture");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, LocalVoiceFiles.DefaultGpt))!);
            File.WriteAllText(Path.Combine(root, LocalVoiceFiles.DefaultGpt), "base gpt");
            File.WriteAllText(Path.Combine(root, LocalVoiceFiles.DefaultSoVits), "base sovits");
            return root;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }

    private sealed class CancelStream(TaskCompletionSource started) : Stream
    {
        private bool _first = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first) { _first = false; "RIFF"u8.CopyTo(buffer.Span); return 4; }
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
    }
}
