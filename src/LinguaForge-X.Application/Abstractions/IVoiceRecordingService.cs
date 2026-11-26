using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Application.Abstractions;

public interface IVoiceRecordingService
{
    Task<IReadOnlyList<VoiceRecordingDevice>> ListDevicesAsync(CancellationToken cancellationToken = default);
    Task<VoiceRecordingTake> RecordAsync(int deviceId, IProgress<VoiceRecordingProgress>? progress = null, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<VoiceRecordingTake> ImportAsync(string audioPath, CancellationToken cancellationToken = default);
    Task<VoiceRecordingTake> CalibrateAsync(VoiceRecordingTake take, VoiceRecordingCalibration calibration, CancellationToken cancellationToken = default);
    Task<VoiceRecordingSample> SaveSampleAsync(VoiceRecordingSampleDraft draft, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoiceRecordingSample>> ListSamplesAsync(CancellationToken cancellationToken = default);
    Task<string> ExportDatasetAsync(IReadOnlyList<string> sampleIds, CancellationToken cancellationToken = default);
}
