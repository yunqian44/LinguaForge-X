using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Application.Abstractions;

public interface ILocalVoiceService
{
    Task<IReadOnlyList<LocalVoiceProfile>> ListAsync(CancellationToken cancellationToken = default);
    Task<LocalVoiceProfile> SaveAsync(LocalVoiceDraft voice, CancellationToken cancellationToken = default);
    Task<LocalVoiceProfile> TrainAsync(LocalVoiceTrainingRequest request, LocalVoiceSettings settings,
        IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> CheckAsync(LocalVoiceSettings settings, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> StartAsync(LocalVoiceSettings settings, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> StopAsync(CancellationToken cancellationToken = default);
}
