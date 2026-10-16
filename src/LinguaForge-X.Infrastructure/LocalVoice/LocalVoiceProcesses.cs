using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LinguaForge_X.Domain.Models;
using LinguaForge_X.Infrastructure.Persistence;

namespace LinguaForge_X.Infrastructure.LocalVoice;

public sealed partial class LocalVoiceService
{
    private readonly ConcurrentQueue<string> _serverLog = new();
    private Task[] _serverReaders = [];

    public async Task<ConnectionTestResult> StartAsync(LocalVoiceSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var endpoint = LocalVoiceFiles.Endpoint(settings.ApiEndpoint);
        await ApiGate.WaitAsync(cancellationToken);
        try
        {
            if (_ownedProcess is { HasExited: false })
            {
                if (_ownedEndpoint != endpoint.AbsoluteUri)
                    return new(false, "本应用已经启动了另一个本地配音服务。请先停止服务，再更换地址。");
                return await CheckAsync(settings, cancellationToken);
            }
            var existing = await CheckAsync(settings, cancellationToken);
            if (existing.Success)
                return new(true, "已连接正在运行的本地 api_v2 服务。该进程由外部启动，本应用不会停止它；请将此接口专用于当前应用。");
            var root = LocalVoiceFiles.RuntimeRoot(settings);
            var weights = LocalVoiceFiles.BaseWeights(root);
            var bert = Path.Combine(root, "GPT_SoVITS", "pretrained_models", "chinese-roberta-wwm-ext-large");
            var hubert = Path.Combine(root, "GPT_SoVITS", "pretrained_models", "chinese-hubert-base");
            if (!Directory.Exists(bert) || !Directory.Exists(hubert))
                throw new InvalidOperationException("运行目录缺少中文 RoBERTa 或 HuBERT 基础模型，请先完成 GPT-SoVITS v2 安装。");
            var device = settings.Device;
            if (device == "auto")
            {
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    var probe = await RunProcessAsync(PythonStart(settings, root,
                        ["-c", "import torch; print('LFX_DEVICE=' + ('cuda' if torch.cuda.is_available() else 'cpu'))"]), null, probeTimeout.Token);
                    if (probe.ExitCode != 0) throw new InvalidOperationException("Python 无法加载 PyTorch，请选择已安装 GPT-SoVITS 依赖的 Python 解释器。\n" + probe.Error);
                    device = probe.Output.Contains("LFX_DEVICE=cuda", StringComparison.Ordinal) ? "cuda" : "cpu";
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new InvalidOperationException("检查本地 PyTorch 环境超时，请确认 Python 解释器可以正常运行。"); }
            }
            var configDirectory = Path.Combine(_profilesDirectory, "runtime");
            Directory.CreateDirectory(configDirectory);
            var config = Path.Combine(configDirectory, "infer-" + Guid.NewGuid().ToString("N") + ".json");
            // JSON is a YAML subset, allowing safe Unicode/path encoding without modifying the runtime's configuration.
            await AtomicJson.WriteAsync(config, new
            {
                custom = new
                {
                    device, is_half = device == "cuda", version = "v2",
                    t2s_weights_path = weights.Gpt, vits_weights_path = weights.SoVits,
                    bert_base_path = bert, cnhuhbert_base_path = hubert
                }
            }, cancellationToken);
            _ownedProcess?.Dispose();
            _ownedProcess = new Process { StartInfo = PythonStart(settings, root,
                ["-u", Path.Combine(root, "api_v2.py"), "-a", endpoint.Host.Trim('[', ']'), "-p", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c", config]) };
            _serverLog.Clear();
            try { StartProcess(_ownedProcess); }
            catch
            {
                _ownedProcess.Dispose();
                _ownedProcess = null;
                _ownedEndpoint = null;
                throw;
            }
            _ownedProcess.StandardInput.Close();
            _ownedEndpoint = endpoint.AbsoluteUri;
            LoadedModels.Remove(endpoint.AbsoluteUri);
            _serverReaders = [DrainServerLogAsync(_ownedProcess.StandardOutput), DrainServerLogAsync(_ownedProcess.StandardError)];
            try
            {
                // Model load can take time; cancellation terminates only the child created above.
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromMinutes(5))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_ownedProcess.HasExited)
                    {
                        await Task.WhenAll(_serverReaders);
                        return new(false, "GPT-SoVITS 启动失败，请检查 Python 依赖、基础模型和设备设置。\n" + string.Join('\n', _serverLog.TakeLast(8)));
                    }
                    var check = await CheckAsync(settings, cancellationToken);
                    if (check.Success)
                    {
                        if (_ownedProcess.HasExited) return new(false, "本应用启动的 GPT-SoVITS 已退出，请检查端口占用和运行环境。");
                        return new(true, $"本地 GPT-SoVITS 已启动（{device.ToUpperInvariant()}）。请保存本人音色并生成试听。");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                Kill(_ownedProcess);
                return new(false, "本地模型加载超过 5 分钟，已停止本次启动。请检查模型文件、Python 环境和内存后重试。");
            }
            catch { Kill(_ownedProcess); LoadedModels.Remove(endpoint.AbsoluteUri); throw; }
        }
        finally { ApiGate.Release(); }
    }

    public async Task<ConnectionTestResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await ApiGate.WaitAsync(cancellationToken);
        try
        {
            if (_ownedProcess is null || _ownedProcess.HasExited)
                return new(true, "没有由本应用启动的运行中服务；外部启动的 GPT-SoVITS 请在原窗口停止。");
            Kill(_ownedProcess);
            await _ownedProcess.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(_serverReaders);
            if (_ownedEndpoint is not null) LoadedModels.Remove(_ownedEndpoint);
            _ownedProcess.Dispose();
            _ownedProcess = null;
            _ownedEndpoint = null;
            return new(true, "本应用启动的 GPT-SoVITS 已停止，本地音色和训练模型已保留。");
        }
        finally { ApiGate.Release(); }
    }

    public async Task<LocalVoiceProfile> TrainAsync(LocalVoiceTrainingRequest request, LocalVoiceSettings settings,
        IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        LocalVoiceFiles.ValidateDraft(request.Voice);
        var root = LocalVoiceFiles.RuntimeRoot(settings);
        var dataset = LocalVoiceFiles.ExistingFile(request.DatasetListPath, "训练数据清单", ".list");
        if (request.GptEpochs is < 1 or > 100 || request.SoVitsEpochs is < 1 or > 100)
            throw new InvalidOperationException("GPT 和 SoVITS 训练轮数需要在 1～100 之间。");
        var script = Path.Combine(AppContext.BaseDirectory, "tools", "gpt-sovits", "train_voice.py");
        LocalVoiceFiles.ExistingFile(script, "随应用分发的 train_voice.py 训练脚本");
        await _trainingGate.WaitAsync(cancellationToken);
        try
        {
            await ApiGate.WaitAsync(cancellationToken);
            try
            {
                // An idle server still occupies GPU memory; release only our own process before training.
                if (_ownedProcess is { HasExited: false })
                {
                    Kill(_ownedProcess);
                    await _ownedProcess.WaitForExitAsync(cancellationToken);
                    await Task.WhenAll(_serverReaders);
                    if (_ownedEndpoint is not null) LoadedModels.Remove(_ownedEndpoint);
                    progress?.Report(new("本地音色训练", 0, "已停止本应用的配音服务以释放内存，训练完成后请重新启动服务。"));
                }
                var job = Path.Combine(_profilesDirectory, "training-jobs", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(job);
                var manifest = Path.Combine(job, "manifest.json");
                await AtomicJson.WriteAsync(manifest, new
                {
                    schema_version = 1, runtime_root = root, dataset_list = dataset, output_directory = job,
                    voice_name = request.Voice.Name.Trim(), gpt_epochs = request.GptEpochs,
                    sovits_epochs = request.SoVitsEpochs, device = settings.Device, batch_size = 2
                }, cancellationToken);
                progress?.Report(new("本地音色训练", 0, "正在检查训练数据与 GPT-SoVITS 环境；录音和模型均保存在本机。"));
                var result = await RunProcessAsync(PythonStart(settings, root, ["-u", script, "--manifest", manifest]), line =>
                {
                    if (!line.StartsWith("LFX_PROGRESS ", StringComparison.Ordinal)) return;
                    try
                    {
                        using var json = JsonDocument.Parse(line[13..]);
                        var value = json.RootElement;
                        var stage = value.TryGetProperty("stage", out var stageItem) ? stageItem.GetString() : "本地音色训练";
                        var message = value.TryGetProperty("message", out var messageItem) ? messageItem.GetString() : "正在训练";
                        var percent = value.TryGetProperty("progress", out var percentItem) && percentItem.TryGetDouble(out var p) && double.IsFinite(p) ? Math.Clamp(p, 0, 99) : 0;
                        progress?.Report(new(stage ?? "本地音色训练", percent, message ?? "正在训练"));
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
                }, cancellationToken);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"本地训练失败，未登记音色。请检查环境、已校对的数据清单和训练日志：{job}\n{result.Error}");
                var resultPath = Path.Combine(job, "result.json");
                if (!File.Exists(resultPath)) throw new InvalidOperationException("训练没有产生结果清单，未登记音色。请检查训练日志：" + job);
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, cancellationToken));
                var body = document.RootElement;
                if (!body.TryGetProperty("status", out var status) || status.GetString() != "completed"
                    || !body.TryGetProperty("gpt_weights_path", out var gpt) || !body.TryGetProperty("sovits_weights_path", out var sovits))
                    throw new InvalidOperationException("训练结果不完整，未登记音色。请检查训练日志：" + job);
                var gptPath = LocalVoiceFiles.ExistingFile(gpt.GetString() ?? "", "训练后的 GPT 模型", ".ckpt");
                var sovitsPath = LocalVoiceFiles.ExistingFile(sovits.GetString() ?? "", "训练后的 SoVITS 模型", ".pth");
                if (!IsChildPath(gptPath, job) || !IsChildPath(sovitsPath, job))
                    throw new InvalidOperationException("训练结果没有位于本次训练目录中，未登记可能属于其他任务的模型。");
                var profile = await SaveAsync(new LocalVoiceDraft
                {
                    Name = request.Voice.Name, ReferenceAudioPath = request.Voice.ReferenceAudioPath,
                    ReferenceText = request.Voice.ReferenceText, Language = request.Voice.Language,
                    GptWeightsPath = gptPath, SoVitsWeightsPath = sovitsPath
                }, cancellationToken);
                progress?.Report(new("本地音色训练", 100, "GPT 和 SoVITS 训练结果已保存为本地音色。请启动配音服务后试听。"));
                return profile;
            }
            finally { ApiGate.Release(); }
        }
        finally { _trainingGate.Release(); }
    }

    private static bool IsChildPath(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static ProcessStartInfo PythonStart(LocalVoiceSettings settings, string root, IReadOnlyList<string> arguments)
    {
        var python = settings.PythonPath.Trim();
        if (!Path.IsPathRooted(python) && (python.Contains(Path.DirectorySeparatorChar) || python.Contains(Path.AltDirectorySeparatorChar)))
            python = Path.GetFullPath(Path.Combine(root, python));
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["PYTHONNOUSERSITE"] = "1";
        start.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, root, Path.Combine(root, "GPT_SoVITS"),
            Path.Combine(root, "GPT_SoVITS", "BigVGAN"), Path.Combine(root, "tools"));
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["GRADIO_ANALYTICS_ENABLED"] = "False";
        start.Environment["HF_HOME"] = Path.Combine(root, ".linguaforge", "cache", "huggingface");
        start.Environment["MODELSCOPE_CACHE"] = Path.Combine(root, ".linguaforge", "cache", "modelscope");
        start.Environment["NLTK_DATA"] = Path.Combine(root, "runtime", "nltk_data");
        start.Environment["version"] = "v2";
        start.Environment["is_half"] = "False";
        foreach (var name in new[] { "OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMBA_NUM_THREADS" })
            start.Environment[name] = "2";
        start.Environment["PATH"] = string.Join(Path.PathSeparator, root, Path.Combine(root, "ffmpeg", "bin"),
            Path.Combine(root, "runtime", "bin"), start.Environment["PATH"] ?? "");
        return start;
    }

    private async Task DrainServerLogAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                _serverLog.Enqueue(line.Length > 2000 ? line[..2000] : line);
                while (_serverLog.Count > 40) _serverLog.TryDequeue(out _);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(ProcessStartInfo start,
        Action<string>? onLine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        StartProcess(process);
        process.StandardInput.Close();
        using var registration = cancellationToken.UnsafeRegister(_ => Kill(process), null);
        var output = new StringBuilder();
        var error = new StringBuilder();
        var readers = new[] { DrainAsync(process.StandardOutput, output), DrainAsync(process.StandardError, error) };
        try
        {
            await Task.WhenAll(readers.Append(process.WaitForExitAsync(cancellationToken)));
            cancellationToken.ThrowIfCancellationRequested();
            return (process.ExitCode, output.ToString(), error.ToString());
        }
        catch
        {
            Kill(process);
            try { await Task.WhenAll(readers.Append(process.WaitForExitAsync(CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* Preserve the original cancellation or read error. */ }
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        async Task DrainAsync(StreamReader reader, StringBuilder destination)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length > 32768) line = line[..32768];
                onLine?.Invoke(line);
                destination.AppendLine(line);
                if (destination.Length > 16384) destination.Remove(0, destination.Length - 16384);
            }
        }
    }

    private static void StartProcess(Process process)
    {
        try { if (!process.Start()) throw new InvalidOperationException("无法启动本地 GPT-SoVITS 进程。"); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        { throw new InvalidOperationException("无法启动 GPT-SoVITS，请检查运行目录和 Python 路径；需要使用安装了 PyTorch 等依赖的解释器。", ex); }
    }

    private static void Kill(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }
}
