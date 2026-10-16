namespace LinguaForge_X.Domain.Models;

public sealed class SpeechDurationExceededException(int clipIndex, double durationSeconds,
    double windowSeconds, double maxSpeechSpeed) : InvalidOperationException(
        $"第 {clipIndex + 1} 段配音为 {durationSeconds:F2} 秒，时间窗口仅 {windowSeconds:F2} 秒，超过 {maxSpeechSpeed:F2} 倍语速限制。请缩短该段配音文字、延长结束时间，或提高最大语速后重试；不会截断配音。")
{
    public int ClipIndex { get; } = clipIndex;
    public double DurationSeconds { get; } = durationSeconds;
    public double WindowSeconds { get; } = windowSeconds;
    public double MaxSpeechSpeed { get; } = maxSpeechSpeed;
}
