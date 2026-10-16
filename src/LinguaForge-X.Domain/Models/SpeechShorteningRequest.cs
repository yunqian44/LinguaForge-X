namespace LinguaForge_X.Domain.Models;

/// <summary>A single attempt to fit one dubbing line while retaining the original wording as the meaning reference.</summary>
public sealed record SpeechShorteningRequest(int SegmentId, string SourceText, string OriginalText,
    string CurrentText, string TargetLanguage, int MaxCharacters, double CurrentDurationSeconds,
    double TargetDurationSeconds, string PreviousText, string NextText);
