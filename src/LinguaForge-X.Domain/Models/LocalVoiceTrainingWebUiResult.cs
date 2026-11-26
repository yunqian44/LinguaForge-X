namespace LinguaForge_X.Domain.Models;

public sealed record LocalVoiceTrainingWebUiResult(
    bool Success, bool IsRunning, string? Url, string Message, string? LogPath = null);
