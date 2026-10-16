using LinguaForge_X.Application.UseCases;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Persistence;
using Xunit;

namespace LinguaForge_X.Tests;

public sealed class PersistenceAndSubtitleTests
{
    [Fact]
    public async Task SettingsRoundTripEncryptsAllKeysWithoutMutatingCaller()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "linguaforge-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalSettingsStore(directory);
            var settings = new AppSettings();
            settings.Transcription.ApiKey = "key-one-test";
            settings.Translation.ApiKey = "key-two-test";
            settings.Synthesis.ApiKey = "key-three-test";
            settings.Media.OutputDirectory = "C:\\测试目录";
            settings.OnlineVideo.Proxy = "http://proxy-user:proxy-password@127.0.0.1:7890";
            settings.OnlineVideo.CookiesFilePath = "C:\\用户选择\\cookies.txt";
            await store.SaveAsync(settings);
            var json = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));
            Assert.DoesNotContain("key-one-test", json);
            Assert.DoesNotContain("key-two-test", json);
            Assert.DoesNotContain("key-three-test", json);
            Assert.Contains("dpapi:", json);
            Assert.DoesNotContain("proxy-password", json);
            Assert.Equal("key-one-test", settings.Transcription.ApiKey);
            var loaded = await new LocalSettingsStore(directory).LoadAsync();
            Assert.Equal(settings.Transcription.ApiKey, loaded.Transcription.ApiKey);
            Assert.Equal(settings.Translation.ApiKey, loaded.Translation.ApiKey);
            Assert.Equal(settings.Synthesis.ApiKey, loaded.Synthesis.ApiKey);
            Assert.Equal(settings.Media.OutputDirectory, loaded.Media.OutputDirectory);
            Assert.Equal(settings.OnlineVideo.Proxy, loaded.OnlineVideo.Proxy);
            Assert.Equal(settings.OnlineVideo.CookiesFilePath, loaded.OnlineVideo.CookiesFilePath);
            Assert.Contains("proxy-password", settings.OnlineVideo.Proxy);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExistingSettingsWithoutOnlineVideoSectionStillLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "linguaforge-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                """{"Version":1,"Settings":{"Transcription":{},"Translation":{},"Synthesis":{},"Media":{"OutputDirectory":"legacy-output"}}}""");
            var loaded = await new LocalSettingsStore(directory).LoadAsync();
            Assert.NotNull(loaded.OnlineVideo);
            Assert.Equal("legacy-output", loaded.Media.OutputDirectory);
            Assert.Empty(loaded.OnlineVideo.Proxy);
            Assert.Empty(loaded.OnlineVideo.CookiesFilePath);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SrtUsesInvariantMillisecondsAndBilingualText()
    {
        var project = new ConversionProject
        {
            Video = new("test.mp4", 7200, 640, 360, []), Options = new() { BilingualSubtitles = true },
            Segments = [new() { Id = 1, StartSeconds = 3661.25, EndSeconds = 3663.501, SourceText = "Hello", SubtitleText = "你好" }]
        };
        var text = SubtitleWriter.BuildSrt(project);
        Assert.Contains("01:01:01,250 --> 01:01:03,501", text);
        Assert.Contains("Hello\n你好", text);
    }

    [Fact]
    public void OverlappingEditedCuesAreRejected()
        => Assert.Throws<InvalidOperationException>(() => SubtitleWriter.Validate(
            [new() { Id = 1, StartSeconds = 0, EndSeconds = 3 }, new() { Id = 2, StartSeconds = 2, EndSeconds = 5 }], 10));

    [Fact]
    public void ProjectIdsCannotEscapeJobDirectory()
        => Assert.Throws<ArgumentException>(() => new LocalProjectStore().CreateWorkDirectory("../elsewhere"));
}
