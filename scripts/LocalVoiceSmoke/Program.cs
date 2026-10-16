using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.LocalVoice;

if (args.Length is not (2 or 4))
{
    Console.Error.WriteLine("Usage: LocalVoiceSmoke <GPT-SoVITS runtime directory> <python.exe> [synthetic-reference.wav accurate-English-reference-text]");
    return 2;
}
var settings = new LocalVoiceSettings
{
    RootDirectory = Path.GetFullPath(args[0]), PythonPath = Path.GetFullPath(args[1]),
    ApiEndpoint = "http://127.0.0.1:19880", Device = "cpu"
};
using var service = new LocalVoiceService(Path.Combine(Path.GetTempPath(), "linguaforge-local-runtime-smoke"));
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(6));
try
{
    var start = await service.StartAsync(settings, cancellation.Token);
    Console.WriteLine(start.Message);
    if (!start.Success) return 1;
    var check = await service.CheckAsync(settings, cancellation.Token);
    Console.WriteLine(check.Message);
    if (check.Success && args.Length == 4)
    {
        // This diagnostic profile is kept outside the user's app voice library.
        var profile = await service.SaveAsync(new LocalVoiceDraft
        {
            Name = "Synthetic diagnostic fixture", ReferenceAudioPath = Path.GetFullPath(args[2]),
            ReferenceText = args[3], Language = "en-US"
        }, cancellation.Token);
        var output = Path.Combine(Path.GetTempPath(), "linguaforge-local-runtime-smoke", "chinese-diagnostic.wav");
        await service.SynthesizeAsync("你好，这是本地中文语音合成测试。", "zh-CN", profile.Id, output,
            new ServiceSettings { Provider = ServiceProviderKind.LocalGptSoVits, LocalVoice = settings }, cancellation.Token);
        Console.WriteLine("真实中文合成已返回有效 WAV：" + output);
    }
    return check.Success ? 0 : 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
finally
{
    var stop = await service.StopAsync();
    Console.WriteLine(stop.Message);
}
