using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Application.Abstractions;

public interface IWebsiteSessionStore
{
    Task SaveAsync(WebsiteSession session, CancellationToken cancellationToken);
    Task<WebsiteSession?> LoadAsync(VideoWebsite website, CancellationToken cancellationToken);
    Task DeleteAsync(VideoWebsite website, CancellationToken cancellationToken);
}

public interface IWebsiteVerificationService
{
    Task<IWebsiteVerificationSession> OpenAsync(string url, string proxy, CancellationToken cancellationToken);
}

public interface IWebsiteVerificationSession : IAsyncDisposable
{
    VideoWebsite Website { get; }
    string BrowserName { get; }
    Task<WebsiteSession> CaptureAsync(CancellationToken cancellationToken);
}
