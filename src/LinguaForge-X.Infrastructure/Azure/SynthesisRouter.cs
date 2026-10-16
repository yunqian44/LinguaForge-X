using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.LocalVoice;

namespace LinguaForge_X.Infrastructure.Azure;

/// <summary>Routes local speech independently of saved Azure credentials and capabilities.</summary>
public sealed class SynthesisRouter(AzureAiService azure, LocalVoiceService local) : ISynthesisService, IServiceCatalog
{
    public Task<string?> GetCacheIdentityAsync(string voiceId, ServiceSettings settings, CancellationToken cancellationToken)
        => settings.Provider == ServiceProviderKind.LocalGptSoVits
            ? local.GetCacheIdentityAsync(voiceId, settings, cancellationToken)
            : Task.FromResult<string?>(null);

    public Task SynthesizeAsync(string text, string language, string voiceId, string outputPath,
        ServiceSettings settings, CancellationToken cancellationToken) => settings.Provider switch
        {
            ServiceProviderKind.LocalGptSoVits => local.SynthesizeAsync(text, language, voiceId, outputPath, settings, cancellationToken),
            ServiceProviderKind.AzureSpeech or ServiceProviderKind.AzureOpenAI => azure.SynthesizeAsync(text, language, voiceId, outputPath, settings, cancellationToken),
            _ => throw new InvalidOperationException("不支持所选配音服务。")
        };

    public async Task<ServiceCapabilities> GetCapabilitiesAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var capabilities = await azure.GetCapabilitiesAsync(settings, cancellationToken).ConfigureAwait(false);
        if (settings.Synthesis.Provider != ServiceProviderKind.LocalGptSoVits) return capabilities;
        var profiles = await local.ListAsync(cancellationToken).ConfigureAwait(false);
        return capabilities with
        {
            Voices = profiles.Select(p => new VoiceOption(p.Id, p.Name + "（" + p.Description + "）", p.Language, LocalVoiceLanguages.Codes)).ToArray(),
            Description = capabilities.Description + (profiles.Count == 0
                ? " 本地 GPT-SoVITS：请先在“我的声音”保存参考录音音色或训练模型。"
                : $" 已加载 {profiles.Count} 个本地音色；请启动 GPT-SoVITS 并试听，确认模型和录音可用。")
        };
    }

    public Task<ConnectionTestResult> TestConnectionAsync(ServiceSettings settings, ServiceKind kind, CancellationToken cancellationToken)
    {
        if (settings.Provider != ServiceProviderKind.LocalGptSoVits)
            return azure.TestConnectionAsync(settings, kind, cancellationToken);
        return kind == ServiceKind.Synthesis
            ? local.CheckAsync(settings.LocalVoice, cancellationToken)
            : Task.FromResult(new ConnectionTestResult(false, "本地 GPT-SoVITS 仅用于声音训练和配音，请为视频转写、文本翻译选择对应服务。"));
    }
}
