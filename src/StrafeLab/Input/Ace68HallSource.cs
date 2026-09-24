using System.IO;
using System.Text.Json;
using HidSharp;
using StrafeLab.Core;

namespace StrafeLab.Input;

/// <summary>
/// Opens only the verified MI_01 collection. Uses the official driver's function-config
/// debug bit, preserving every other bit. No key mapping, RT, calibration, RGB or firmware writes.
/// Raw Input remains authoritative for digital edges: Hall depth must never emulate RT.
/// </summary>
public sealed class Ace68HallSource : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly string _journalPath;
    private Task? _worker;
    private long _count;
    private int _disposed;
    private readonly object _lifetime=new();
    public string Status { get; private set; } = "Hall 尚未连接";
    public bool Enabled { get; private set; }
    public bool Verified => Interlocked.Read(ref _count) > 0 && Enabled;
    public long ReportCount => Interlocked.Read(ref _count);
    public event EventHandler<HallSample>? SampleReceived;
    public event EventHandler<string>? StatusChanged;
    public Ace68HallSource(string dataDirectory) => _journalPath = Path.Combine(dataDirectory, "hall-debug-recovery.json");
    public void Start(){lock(_lifetime){if(_disposed==0)_worker??=Task.Run(Run);}}
    private void SetStatus(string text) { Status = text; StatusChanged?.Invoke(this, text); }

    private void Run()
    {
        using var mutex=new Mutex(false,"Local\\StrafeLab-Ace68-41E4-2120");
        bool owned=false;
        try
        {
            try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
            if(!owned){SetStatus("Hall 已由另一个 StrafeLab 实例使用 · Raw Input 继续");return;}
            RunLease();
        }
        finally{if(owned)mutex.ReleaseMutex();}
    }
    private void RunLease()
    {
        try
        {
            var device = DeviceList.Local.GetHidDevices(0x41E4, 0x2120).FirstOrDefault(d =>
                d.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase) &&
                d.GetMaxInputReportLength() == 65 && d.GetMaxOutputReportLength() == 65 && d.GetMaxFeatureReportLength() == 0);
            if (device == null) { SetStatus("未发现受支持的 Hall 通道 · 使用 Raw Input"); return; }
            using var stream = device.Open(); stream.ReadTimeout = 100; stream.WriteTimeout = 1500;
            var info = Exchange(stream, 3, 0, 56);
            if (info.Length < 2 || (info[0] | (info[1] << 8)) != Ace68Protocol.VerifiedFirmware)
            { SetStatus("Hall 固件版本未验证 · 使用 Raw Input"); return; }
            Recover(stream, device.DevicePath);
            var baseInfo = Exchange(stream, 4, 0, 56);
            if (baseInfo.Length < 1 || baseInfo[0] > 2) throw new IOException("无法确认当前板载配置");
            int profile = baseInfo[0];
            var original = ReadConfig(stream, profile);
            var journal = new DebugJournal(device.DevicePath, profile, original);
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            File.WriteAllText(_journalPath, JsonSerializer.Serialize(journal));
            try
            {
                var target = (byte[])original.Clone(); target[7] |= 8;
                WriteConfig(stream, profile, target);
                if (!ReadConfig(stream, profile).SequenceEqual(target)) throw new IOException("Hall 监测开关校验失败");
                Enabled = true; SetStatus("Hall 通道已开启 · 等待手动按压 WASD 验证");
                var buffer = new byte[65];
                long lastProfileCheck = Environment.TickCount64;
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        int count = stream.Read(buffer, 0, buffer.Length);
                        Accept(buffer.AsSpan(0, count));
                    }
                    catch (TimeoutException) { }
                    // A hardware profile switch or another driver can disable debug. Do not
                    // keep rewriting a user's new settings; stop and fall back instead.
                    if (Environment.TickCount64 - lastProfileCheck > 10000)
                    {
                        var active = Exchange(stream, 4, 0, 56);
                        if (active.Length == 0 || active[0] != profile || (ReadConfig(stream, profile)[7] & 8) == 0)
                            throw new IOException("板载配置/驱动状态已改变");
                        lastProfileCheck = Environment.TickCount64;
                    }
                }
            }
            finally
            {
                Enabled = false;
                Restore(stream, journal);
                File.Delete(_journalPath);
            }
            SetStatus("Hall 监测已关闭，原监测位已恢复");
        }
        catch (Exception ex)
        {
            Enabled = false;
            SetStatus($"Hall 读取停止 · Raw Input 继续：{ex.Message}" +
                (File.Exists(_journalPath) ? "；保留恢复记录，下次连接重试恢复" : ""));
        }
    }

    private void Accept(ReadOnlySpan<byte> bytes)
    {
        if (!Ace68Protocol.TryDecode(bytes, TimeUtil.NowMicroseconds(), out var sample)) return;
        if (Interlocked.Increment(ref _count) == 1) SetStatus("Hall 连续键程已验证 · Raw Input 记录触发边沿");
        SampleReceived?.Invoke(this, sample!);
    }

    private byte[] Exchange(HidStream stream, byte command, int offset, int size, byte[]? data = null)
    {
        var packet = Ace68Protocol.Command(command, offset, size, data);
        stream.Write(packet);
        long until = Environment.TickCount64 + 1800;
        var buffer = new byte[65];
        while (Environment.TickCount64 < until)
        {
            int count;
            try { count = stream.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { continue; }
            if (count != 65 || buffer[0] != 0) continue;
            if (buffer[1] == 0xA0) { Accept(buffer); continue; }
            if (buffer[1] != 0xAA || buffer[2] != command || buffer[6] != packet[6] || buffer[7] != packet[7]) continue;
            int length = buffer[5];
            if (length > size || length > 56) throw new IOException("HID 响应长度无效");
            if (unchecked((byte)buffer.Skip(5).Sum(b => b)) != buffer[4]) throw new IOException("HID 响应校验失败");
            return buffer.AsSpan(9, length).ToArray();
        }
        throw new TimeoutException($"HID 命令 0x{command:X2} 超时");
    }

    private byte[] ReadConfig(HidStream stream, int profile)
    {
        var config = Exchange(stream, 5, profile * 64, 56).Concat(Exchange(stream, 5, profile * 64 + 56, 8)).ToArray();
        if (config.Length != 64) throw new IOException("功能配置不完整");
        return config;
    }
    private void WriteConfig(HidStream stream, int profile, byte[] config)
    {
        Exchange(stream, 6, profile * 64, 56, config[..56]);
        Exchange(stream, 6, profile * 64 + 56, 8, config[56..]);
    }
    private void Restore(HidStream stream, DebugJournal journal)
    {
        var current = ReadConfig(stream, journal.Profile);
        current[7] = (byte)((current[7] & ~8) | (journal.Original[7] & 8));
        WriteConfig(stream, journal.Profile, current);
        if (!ReadConfig(stream, journal.Profile).SequenceEqual(current)) throw new IOException("监测位恢复未通过校验");
    }
    private void Recover(HidStream stream, string path)
    {
        if (!File.Exists(_journalPath)) return;
        var journal = JsonSerializer.Deserialize<DebugJournal>(File.ReadAllText(_journalPath));
        if (journal is null || journal.Path != path || journal.Profile is < 0 or > 2 || journal.Original.Length != 64)
            throw new IOException("存在不匹配的 Hall 恢复记录，请先查看诊断");
        Restore(stream, journal); File.Delete(_journalPath);
    }
    public async ValueTask DisposeAsync()
    {
        Task? worker;bool first;
        lock(_lifetime){first=_disposed==0;_disposed=1;worker=_worker;if(first)_stop.Cancel();}
        if(worker!=null)await worker.ConfigureAwait(false);if(first)_stop.Dispose();
    }
    public sealed record DebugJournal(string Path, int Profile, byte[] Original);
}
