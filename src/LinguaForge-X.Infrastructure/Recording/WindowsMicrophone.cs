using System.Diagnostics;
using System.Runtime.InteropServices;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.Recording;

/// <summary>Uses event notification instead of managed native callbacks; all native headers live on one capture worker.</summary>
internal static class WindowsMicrophone
{
    private const int SampleRate = 44100;
    private const uint HeaderDone = 1;

    internal static IReadOnlyList<VoiceRecordingDevice> Devices()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var count = waveInGetNumDevs();
        var devices = new List<VoiceRecordingDevice>();
        for (uint i = 0; i < count; i++)
        {
            if (waveInGetDevCapsW((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<WaveInCaps>()) == 0)
                devices.Add(new((int)i, caps.Name));
        }
        return devices;
    }

    internal static RecordingAudio Capture(int deviceId, ManualResetEvent stop, IProgress<VoiceRecordingProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("麦克风录音仅支持 Windows，请导入本地 WAV 录音。");
        if (!Devices().Any(device => device.Id == deviceId))
            throw new InvalidOperationException("未找到所选麦克风，请连接麦克风并刷新设备列表；远程桌面需启用音频录制重定向。");
        using var ready = new AutoResetEvent(false);
        var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSecond = SampleRate, AverageBytesPerSecond = SampleRate * 2, BlockAlign = 2, BitsPerSample = 16 };
        Check(waveInOpen(out var handle, (uint)deviceId, ref format, ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x00050000), "打开麦克风失败，请检查 Windows 麦克风隐私权限以及设备是否被其他应用独占");
        var buffers = new List<NativeBuffer>();
        using var pcm = new MemoryStream(SampleRate * 2 * RecordingAudio.MaximumSeconds);
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var buffer = new NativeBuffer(SampleRate / 10 * 2);
                buffers.Add(buffer);
                Check(waveInPrepareHeader(handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()), "准备麦克风缓冲区失败");
                buffer.Prepared = true;
                Check(waveInAddBuffer(handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()), "排队麦克风缓冲区失败");
            }
            Check(waveInStart(handle), "启动麦克风失败");
            var watch = Stopwatch.StartNew();
            var lastAudioTime = TimeSpan.Zero;
            var maximumBytes = SampleRate * 2 * RecordingAudio.MaximumSeconds;
            var waits = new WaitHandle[] { ready, stop, token.WaitHandle };
            var nextBuffer = 0;
            while (pcm.Length < maximumBytes && !stop.WaitOne(0) && !token.IsCancellationRequested)
            {
                WaitHandle.WaitAny(waits, 250);
                if (watch.Elapsed.TotalSeconds > RecordingAudio.MaximumSeconds + 2) break;
                for (var i = 0; i < buffers.Count; i++)
                {
                    var buffer = buffers[nextBuffer];
                    var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
                    if ((header.Flags & HeaderDone) == 0) break;
                    if (header.BytesRecorded > 0) lastAudioTime = watch.Elapsed;
                    Consume(buffer.Header, header, pcm, maximumBytes, progress);
                    nextBuffer = (nextBuffer + 1) % buffers.Count;
                    if (pcm.Length >= maximumBytes || stop.WaitOne(0) || token.IsCancellationRequested) break;
                    Check(waveInAddBuffer(handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()), "录音设备已断开或录音中断");
                }
                if (watch.Elapsed - lastAudioTime > TimeSpan.FromSeconds(3) && !stop.WaitOne(0) && !token.IsCancellationRequested)
                    throw new InvalidOperationException("麦克风连续 3 秒未返回音频，可能已断开连接；请检查设备后重新录制。");
            }
            Check(waveInReset(handle), "停止录音失败");
            // Reset returns outstanding partial buffers. Already consumed headers are marked below.
            for (var i = 0; i < buffers.Count; i++)
            {
                var buffer = buffers[(nextBuffer + i) % buffers.Count];
                var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
                if ((header.Flags & HeaderDone) != 0) Consume(buffer.Header, header, pcm, maximumBytes, progress);
            }
            token.ThrowIfCancellationRequested();
            if (pcm.Length == 0) throw new InvalidOperationException("麦克风未返回任何音频，请检查设备连接和 Windows 麦克风权限。");
            var bytes = pcm.ToArray();
            var samples = new float[bytes.Length / 2];
            for (var i = 0; i < samples.Length; i++) samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2)) / 32768f;
            return new(SampleRate, samples);
        }
        finally
        {
            waveInReset(handle);
            foreach (var buffer in buffers)
            {
                var released = !buffer.Prepared;
                for (var attempt = 0; !released && attempt < 3; attempt++)
                {
                    released = waveInUnprepareHeader(handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()) == 0;
                    if (!released) { waveInReset(handle); Thread.Sleep(20); }
                }
                // Never free a buffer that a faulty driver still owns. Reset normally releases all headers synchronously.
                if (released) buffer.Dispose();
            }
            Check(waveInClose(handle), "麦克风未能完全释放，请关闭程序并检查音频设备");
        }
    }

    private static void Consume(IntPtr pointer, WaveHeader header, MemoryStream pcm, int maximumBytes, IProgress<VoiceRecordingProgress>? progress)
    {
        if (header.BytesRecorded > header.BufferLength)
            throw new InvalidOperationException("录音设备返回了无效音频缓冲区，请重新连接麦克风。");
        var size = Math.Min((int)header.BytesRecorded, maximumBytes - (int)pcm.Length) & ~1;
        if (size <= 0) return;
        var bytes = new byte[size];
        Marshal.Copy(header.Data, bytes, 0, size);
        pcm.Write(bytes);
        Marshal.WriteInt32(pointer, IntPtr.Size + sizeof(uint), 0);
        double peak = 0, square = 0;
        for (var i = 0; i < size; i += 2)
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i, 2)) / 32768d;
            peak = Math.Max(peak, Math.Abs(value)); square += value * value;
        }
        progress?.Report(new(pcm.Length / (double)(SampleRate * 2), RecordingAudio.Db(peak), RecordingAudio.Db(Math.Sqrt(square / (size / 2))), peak));
    }

    private static void Check(uint result, string message)
    {
        if (result != 0) throw new InvalidOperationException($"{message}（Windows 音频错误 {result}）。");
    }

    private sealed class NativeBuffer : IDisposable
    {
        internal IntPtr Header { get; }
        private readonly IntPtr _data;
        internal bool Prepared { get; set; }
        internal NativeBuffer(int size)
        {
            _data = Marshal.AllocHGlobal(size);
            Header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.StructureToPtr(new WaveHeader { Data = _data, BufferLength = (uint)size }, Header, false);
        }
        public void Dispose() { Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(_data); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps
    {
        public ushort Manufacturer, Product;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats;
        public ushort Channels, Reserved;
    }
    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveInGetDevCapsW(UIntPtr device, out WaveInCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
}
