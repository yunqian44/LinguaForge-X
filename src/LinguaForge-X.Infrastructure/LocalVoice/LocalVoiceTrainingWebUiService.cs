using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.LocalVoice;

/// <summary>Owns only the training WebUI process launched by this application.</summary>
public sealed class LocalVoiceTrainingWebUiService : ILocalVoiceTrainingWebUiService
{
    public const int Port = 9874;
    public const string Address = "http://127.0.0.1:9874/";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _stateLock = new();
    private readonly ConcurrentQueue<string> _tail = new();
    private readonly string _scriptsDirectory;
    private readonly string _logsDirectory;
    private readonly TimeSpan _startupTimeout;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(2) };
    private Process? _process;
    private OwnedProcessJob? _job;
    private Task[] _readers = [];
    private StreamWriter? _log;
    private string? _identity;
    private string? _logPath;
    private bool _ready;
    private bool _disposed;
    private bool _statusSuccess = true;
    private string _message = "训练界面尚未启动。";

    public LocalVoiceTrainingWebUiService(string? scriptsDirectory = null, string? logsDirectory = null,
        TimeSpan? startupTimeout = null)
    {
        _scriptsDirectory = scriptsDirectory ?? Path.Combine(AppContext.BaseDirectory, "tools", "gpt-sovits");
        _logsDirectory = logsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinguaForge-X", "local-voices", "training-webui-logs");
        _startupTimeout = startupTimeout ?? TimeSpan.FromMinutes(3);
        if (_startupTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(startupTimeout));
    }

    public LocalVoiceTrainingWebUiResult GetStatus()
    {
        lock (_stateLock)
        {
            if (IsAlive(_process))
                return new(true, true, _ready ? Address : null, _message, _logPath);
            if (_process is not null && _ready)
            {
                _ready = false;
                _statusSuccess = false;
                _message = "训练界面已退出，请查看日志并重新启动。\n" + string.Join('\n', _tail.TakeLast(6));
                _job?.Dispose();
                _job = null;
            }
            return new(_statusSuccess, false, null, _message, _logPath);
        }
    }

    public async Task<LocalVoiceTrainingWebUiResult> StartAsync(LocalVoiceSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        // Capture mutable UI settings before awaiting, preventing configuration changes mid-start.
        var root = LocalVoiceFiles.RuntimeRoot(settings);
        LocalVoiceFiles.ExistingFile(Path.Combine(root, "webui.py"), "GPT-SoVITS 官方 webui.py");
        LocalVoiceFiles.BaseWeights(root);
        var python = ResolvePython(settings.PythonPath, root);
        var device = settings.Device;
        var identity = string.Join('|', root.ToUpperInvariant(), python.ToUpperInvariant(), device);
        var script = LocalVoiceFiles.ExistingFile(Path.Combine(_scriptsDirectory, "start_training_webui.py"), "随应用分发的训练界面启动脚本");
        LocalVoiceFiles.ExistingFile(Path.Combine(_scriptsDirectory, "training_webui_bootstrap", "sitecustomize.py"), "训练界面本地访问保护脚本");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = lifetime.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_stateLock)
            {
                if (IsAlive(_process))
                    return _identity == identity ? GetStatus() : new(false, true, _ready ? Address : null,
                        "训练界面正在使用另一组运行目录、Python 或设备配置。请先停止服务，再更换配置。", _logPath);
            }
            await ReleaseProcessAsync().ConfigureAwait(false);
            try { EnsurePortAvailable(); }
            catch (SocketException)
            {
                return Failure($"训练界面端口 {Port} 已被其他程序占用。本应用不会连接或终止该进程，请先关闭原服务。");
            }
            Directory.CreateDirectory(_logsDirectory);
            _logPath = Path.Combine(_logsDirectory, $"training-webui-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            _log = new StreamWriter(new FileStream(_logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            _tail.Clear();
            var start = CreateStartInfo(python, root, device, script);
            var process = new Process { StartInfo = start };
            lock (_stateLock)
            {
                _process = process;
                _identity = identity;
                _ready = false;
                _statusSuccess = true;
                _message = "正在启动本地训练界面并检查依赖，请稍候。";
            }
            try
            {
                if (!process.Start()) throw new InvalidOperationException("无法启动训练界面的 Python 进程。");
                _job = OwnedProcessJob.Attach(process);
                process.StandardInput.Close();
                _readers = [ReadLogAsync(process.StandardOutput), ReadLogAsync(process.StandardError)];
                var elapsed = Stopwatch.StartNew();
                while (elapsed.Elapsed < _startupTimeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        await Task.WhenAll(_readers).ConfigureAwait(false);
                        var message = "训练界面启动失败；请检查 Python、Gradio 和 v2 模型。\n" + string.Join('\n', _tail.TakeLast(12));
                        await ReleaseProcessAsync().ConfigureAwait(false);
                        return Failure(message);
                    }
                    if (await IsReadyAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (process.HasExited) continue;
                        lock (_stateLock)
                        {
                            _ready = true;
                            _message = "GPT-SoVITS 训练界面已启动，仅本机可访问。模型版本默认 v2，录音和训练结果保存在本机。";
                        }
                        return GetStatus();
                    }
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                await ReleaseProcessAsync().ConfigureAwait(false);
                return Failure("训练界面启动超时，已停止本次启动的进程。请检查依赖、端口和日志后重试。");
            }
            catch (OperationCanceledException)
            {
                await ReleaseProcessAsync().ConfigureAwait(false);
                Failure("已取消训练界面的启动，并停止本次创建的进程。");
                throw;
            }
            catch (Exception error)
            {
                await ReleaseProcessAsync().ConfigureAwait(false);
                return Failure("训练界面无法启动：" + error.Message);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<LocalVoiceTrainingWebUiResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReleaseProcessAsync().ConfigureAwait(false);
            lock (_stateLock)
            {
                _message = "本应用启动的训练界面及其子任务已停止，已保存的录音和模型保留。";
                _statusSuccess = true;
            }
            return GetStatus();
        }
        finally { _gate.Release(); }
    }

    private LocalVoiceTrainingWebUiResult Failure(string message)
    {
        lock (_stateLock) { _message = message; _statusSuccess = false; return new(false, false, null, message, _logPath); }
    }

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await _http.GetAsync(Address + "config", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 2_000_000) return false;
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[16_384];
            int count;
            while ((count = await source.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > 2_000_000) return false;
                buffer.Write(bytes, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.TryGetProperty("title", out var title) && title.GetString() == "GPT-SoVITS WebUI"
                && document.RootElement.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException) { return false; }
    }

    private async Task ReadLogAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 8000) line = line[..8000];
                _tail.Enqueue(line);
                while (_tail.Count > 40) _tail.TryDequeue(out _);
                lock (_stateLock) _log?.WriteLine(line);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private async Task ReleaseProcessAsync()
    {
        Process? process;
        lock (_stateLock)
        {
            process = _process;
            _job?.Dispose();
            _job = null;
            TryKill(process);
        }
        if (process is not null)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException) { }
            try { await Task.WhenAll(_readers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        lock (_stateLock)
        {
            process?.Dispose();
            _process = null; _identity = null; _ready = false; _readers = [];
            _log?.Dispose(); _log = null;
        }
    }

    private static void EnsurePortAvailable()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        listener.Bind(new IPEndPoint(IPAddress.Loopback, Port));
    }

    private static string ResolvePython(string configured, string root)
    {
        var path = configured.Trim();
        if (!Path.IsPathRooted(path) && (path.Contains('/') || path.Contains('\\'))) path = Path.GetFullPath(Path.Combine(root, path));
        if (Path.IsPathRooted(path)) return LocalVoiceFiles.ExistingFile(path, "Python 解释器");
        if (path.Any(char.IsWhiteSpace) || path.Contains('"') || path.Contains('\''))
            throw new InvalidOperationException("Python 路径只能填写解释器文件，不能包含命令参数。");
        return path;
    }

    private static ProcessStartInfo CreateStartInfo(string python, string root, string device, string script)
    {
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-u", "-s", script, "--runtime-root", root, "--device", device, "--port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(x => x.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["PYTHONNOUSERSITE"] = "1";
        start.Environment["GRADIO_ANALYTICS_ENABLED"] = "False";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["CUDA_VISIBLE_DEVICES"] = device == "cpu" ? "" : "0";
        foreach (var name in new[] { "OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMBA_NUM_THREADS" }) start.Environment[name] = "2";
        return start;
    }

    private static bool IsAlive(Process? process)
    {
        try { return process is { HasExited: false }; }
        catch (InvalidOperationException) { return false; }
    }

    private static void TryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseProcessAsync().GetAwaiter().GetResult();
            _http.Dispose();
        }
        finally { _gate.Release(); }
        GC.SuppressFinalize(this);
    }
}
