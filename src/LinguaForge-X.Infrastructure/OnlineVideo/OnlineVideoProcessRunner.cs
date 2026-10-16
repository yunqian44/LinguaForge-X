using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace LinguaForge_X.Infrastructure.OnlineVideo;

public sealed record OnlineVideoProcessRequest(string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory, bool CaptureOutput = true, int OutputLimit = 4_194_304);

public sealed record OnlineVideoProcessResult(int ExitCode, string Output, string Error);

/// <summary>Small process boundary that allows deterministic, offline download tests.</summary>
public interface IOnlineVideoProcessRunner
{
    Task<OnlineVideoProcessResult> RunAsync(OnlineVideoProcessRequest request,
        Action<string>? outputLine, CancellationToken cancellationToken);
}

public sealed class OnlineVideoProcessRunner : IOnlineVideoProcessRunner
{
    public async Task<OnlineVideoProcessResult> RunAsync(OnlineVideoProcessRequest request,
        Action<string>? outputLine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        // Embedded Python must not inherit external modules or user startup hooks.
        foreach (var key in start.Environment.Keys.Where(k =>
                     k.StartsWith("YTDLP_", StringComparison.OrdinalIgnoreCase) ||
                     k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["PYTHONNOUSERSITE"] = "1";
        start.Environment["PYTHONUTF8"] = "1";
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动在线视频工具。");
        }
        catch (Exception error) when (error is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException("无法启动在线视频工具，请检查设置中的 yt-dlp、Deno 或 FFmpeg 路径。");
        }
        process.StandardInput.Close();
        using var registration = cancellationToken.UnsafeRegister(_ => Kill(process), null);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputTask = DrainAsync(process.StandardOutput, false);
        var errorTask = DrainAsync(process.StandardError, true);
        try
        {
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch
        {
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { /* Preserve the original cancellation or output-limit exception. */ }
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        async Task DrainAsync(StreamReader reader, bool isError)
        {
            var buffer = new char[4096];
            var line = new StringBuilder();
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    for (var index = 0; index < count; index++)
                    {
                        var character = buffer[index];
                        if (character is '\n' or '\r')
                        {
                            if (line.Length > 0) FlushLine();
                            continue;
                        }
                        if (line.Length >= (isError ? 32_768 : request.OutputLimit))
                            throw new InvalidOperationException("在线视频工具返回的数据过大，已停止处理。请使用具体视频链接或更新工具。");
                        line.Append(character);
                    }
                }
                if (line.Length > 0) FlushLine();
            }
            catch { Kill(process); throw; }

            void FlushLine()
            {
                var value = line.ToString();
                line.Clear();
                if (isError)
                {
                    if (value.StartsWith("LF_PROGRESS:", StringComparison.Ordinal) || value == "LF_POSTPROCESS")
                        outputLine?.Invoke(value);
                    // Only a bounded diagnostic tail is retained, and never shown verbatim in the UI.
                    stderr.AppendLine(value);
                    if (stderr.Length > 32_768) stderr.Remove(0, stderr.Length - 32_768);
                }
                else
                {
                    if (request.CaptureOutput)
                    {
                        if (stdout.Length + value.Length + 1 > request.OutputLimit)
                            throw new InvalidOperationException("在线视频信息过大，请使用具体视频链接或更新工具。");
                        stdout.AppendLine(value);
                    }
                    outputLine?.Invoke(value);
                }
            }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }
}
