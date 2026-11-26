using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Application.Abstractions;

public interface ILocalVoiceTrainingWebUiService : IDisposable
{
    LocalVoiceTrainingWebUiResult GetStatus();
    Task<LocalVoiceTrainingWebUiResult> StartAsync(LocalVoiceSettings settings, CancellationToken cancellationToken = default);
    Task<LocalVoiceTrainingWebUiResult> StopAsync(CancellationToken cancellationToken = default);
}
