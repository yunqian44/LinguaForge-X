using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Infrastructure.Azure;
using LinguaForge_X.Infrastructure.Media;
using LinguaForge_X.Infrastructure.LocalVoice;
using LinguaForge_X.Infrastructure.OnlineVideo;
using LinguaForge_X.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaForge_X.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsStore>(_ => new LocalSettingsStore());
        services.AddSingleton<IProjectStore>(_ => new LocalProjectStore());
        services.AddSingleton<IMediaService, FfmpegMediaService>();
        services.AddSingleton<IWebsiteSessionStore>(_ => new ProtectedWebsiteSessionStore());
        services.AddSingleton<IWebsiteVerificationService, BrowserVerificationService>();
        services.AddSingleton<IOnlineVideoService>(provider => new YtDlpOnlineVideoService(
            provider.GetRequiredService<IMediaService>(), new OnlineVideoProcessRunner(),
            provider.GetRequiredService<IWebsiteSessionStore>()));
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
            MaxResponseContentBufferSize = 64 * 1024 * 1024
        });
        services.AddSingleton<AzureAiService>();
        services.AddSingleton(_ => new LocalVoiceService());
        services.AddSingleton<ILocalVoiceService>(provider => provider.GetRequiredService<LocalVoiceService>());
        services.AddSingleton<SynthesisRouter>();
        services.AddSingleton<ITranscriptionService>(provider => provider.GetRequiredService<AzureAiService>());
        services.AddSingleton<ITranslationService>(provider => provider.GetRequiredService<AzureAiService>());
        services.AddSingleton<ISynthesisService>(provider => provider.GetRequiredService<SynthesisRouter>());
        services.AddSingleton<IServiceCatalog>(provider => provider.GetRequiredService<SynthesisRouter>());
        return services;
    }
}
