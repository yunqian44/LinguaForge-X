using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Application.Abstractions;

public interface IOnlineVideoService
{
    Task<string> CheckAvailableAsync(OnlineVideoSettings settings, CancellationToken cancellationToken);
    Task<OnlineVideoInfo> InspectAsync(OnlineVideoRequest request, OnlineVideoSettings settings,
        CancellationToken cancellationToken);
    Task<OnlineVideoDownload> DownloadAsync(OnlineVideoRequest request, OnlineVideoSettings settings,
        MediaSettings mediaSettings, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken);
}
