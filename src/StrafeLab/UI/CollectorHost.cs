using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using StrafeLab.Core;
using StrafeLab.Core.Gsi;
using StrafeLab.Platform;
using Forms = System.Windows.Forms;

namespace StrafeLab.UI;

public sealed class CollectorHost : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly string _root = new SessionStore().RootDirectory;
    private readonly CaptureLifecycle _capture;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _pause;
    private readonly LocalControlServer _server;
    private volatile bool _paused, _closing;
    private volatile bool _hallEnabled=true;
    private bool _appliedHall=true;
    private Process? _analysis;
    private DateTime _nextAnalysis = DateTime.UtcNow.AddSeconds(15);
    private string? _scanStamp;
    private string? _error;
    public CollectorHost()
    {
        try
        {
            var path=Path.Combine(_root,"collector-settings.json");
            if(File.Exists(path))_hallEnabled=JsonSerializer.Deserialize<CollectorSettings>(File.ReadAllText(path),DemoLibrary.Json)?.HallEnabled??true;
        }
        catch(Exception ex){_error="采集设置读取失败："+ex.Message;}
        _appliedHall=_hallEnabled;
        _capture=new CaptureLifecycle(()=>StartCaptureAsync(_hallEnabled));
        _tray = new Forms.NotifyIcon { Text = "StrafeLab · 等待 CS2", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开分析与统计", null, (_, _) => AppLauncher.Start("--analysis-only")?.Dispose());
        _pause = new Forms.ToolStripMenuItem("暂停采集", null, async (_, _) => { _paused = !_paused; await StepAsync(); });
        menu.Items.Add(_pause);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出后台采集", null, (_, _) => _dispatcher.BeginInvoke(new Action(async () => await ((App)Application.Current).ExitCollectorAsync())));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => AppLauncher.Start("--analysis-only")?.Dispose();
        _server = new LocalControlServer("collector", command =>
        {
            switch (command)
            {
                case "ping": return Task.FromResult("ready");
                case "pause": _paused = true; break;
                case "resume": _paused = false; break;
                case "hall-on": _hallEnabled=true; break;
                case "hall-off": _hallEnabled=false; break;
                case "exit": _dispatcher.BeginInvoke(new Action(async () => await ((App)Application.Current).ExitCollectorAsync())); break;
                default: return Task.FromResult("invalid");
            }
            _dispatcher.BeginInvoke(new Action(async () => await StepAsync()));
            return Task.FromResult("ok");
        });
        _timer.Tick += async (_, _) => await StepAsync();
    }
    public async Task StartAsync()
    {
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")=="1")
            throw new InvalidOperationException("隔离测试不可启动真实托盘采集，请使用 --analysis-only 或 --analysis-worker");
        // Install before CS2 starts. Do not create input hooks, HID handles or a web server while idle.
        try
        {
            var tokenPath = Path.Combine(_root, "gsi-token.txt");
            var token = File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : "";
            if (token.Length < 32 || !token.All(Uri.IsHexDigit))
            { token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); File.WriteAllText(tokenPath, token); }
            new GsiConfigInstaller(new SteamLocator()).Install(3000, token);
        }
        catch (Exception ex) { _error = "GSI 配置：" + ex.Message; }
        _timer.Start(); await StepAsync();
    }
    private static async Task<ICaptureSession> StartCaptureAsync(bool hallEnabled)
    {
        var runtime = new RuntimeService(captureOnly: true,hallEnabled:hallEnabled);
        try { await runtime.InitializeAsync(); return new RuntimeCapture(runtime); }
        catch { await runtime.DisposeAsync(); throw; }
    }
    private async Task StepAsync()
    {
        if (_closing || !await _gate.WaitAsync(0)) return;
        try
        {
            var playing = GamePresence.IsRunning();
            await _capture.StepAsync(playing, _paused);
            if(_hallEnabled!=_appliedHall)
            {
                await _capture.SetHallEnabledAsync(_hallEnabled);
                DemoLibrary.Atomic(Path.Combine(_root,"collector-settings.json"),new CollectorSettings(_hallEnabled));
                _appliedHall=_hallEnabled;
            }
            _pause.Text = _paused ? "恢复采集" : "暂停采集";
            string state = _error ?? _capture.State;
            _tray.Text = ("StrafeLab · " + state)[..Math.Min(63, ("StrafeLab · " + state).Length)];
            using var process = Process.GetCurrentProcess();
            var status = new CollectorStatus(process.Id, process.StartTime.ToUniversalTime(), DateTime.UtcNow, _paused,
                state, _capture.Gsi, _hallEnabled?_capture.Hall:"Hall 已关闭 · 使用 Raw Input", _capture.InputCount, process.WorkingSet64, process.PrivateMemorySize64,_hallEnabled);
            DemoLibrary.Atomic(Path.Combine(_root, "collector-status.json"), status);
            if (_analysis != null && _analysis.HasExited) { _analysis.Dispose(); _analysis = null; }
            if (!playing && !_paused && _analysis == null && DateTime.UtcNow >= _nextAnalysis)
            {
                var candidate=await Task.Run(()=>BackgroundDemoWorker.Fingerprint(_root));
                if(candidate.HasValue&&(candidate.Value.Stamp!=_scanStamp||candidate.Value.Pending))
                {_scanStamp=candidate.Value.Stamp;_analysis = AppLauncher.Start("--analysis-worker");}
                _nextAnalysis = DateTime.UtcNow.AddSeconds(60);
            }
            _error = null;
        }
        catch (Exception ex) { _error = "等待重试：" + ex.Message; Log(ex); }
        finally { _gate.Release(); }
    }
    private void Log(Exception ex)
    { try { File.AppendAllText(Path.Combine(_root, "errors.log"), DateTime.UtcNow + " collector " + ex + Environment.NewLine); } catch { } }
    public void CheckpointForSessionEnd()
    {try{_capture.CheckpointAsync().Wait(TimeSpan.FromSeconds(5));}catch(Exception ex){Log(ex);}}
    public async ValueTask DisposeAsync()
    {
        _closing = true; _timer.Stop();
        await _server.DisposeAsync();
        await _gate.WaitAsync();
        try
        {
            await _capture.DisposeAsync();
            // Workers are bounded batches and may finish an already queued report after the collector exits.
            _analysis?.Dispose(); _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Icon?.Dispose(); _tray.Dispose();
            File.Delete(Path.Combine(_root, "collector-status.json"));
        }
        finally { _gate.Release(); }
    }
    private sealed class RuntimeCapture(RuntimeService runtime) : ICaptureSession
    {
        public string State => runtime.IsRecording ? runtime.Metrics.Status : "CS2 已启动 · 等待进入对局";
        public string Gsi => runtime.Metrics.GsiStatus;
        public string Hall => runtime.HallStatus;
        public int InputCount => runtime.InputCount;
        public Task SetHallEnabledAsync(bool enabled)=>runtime.SetHallEnabledAsync(enabled);
        public Task CheckpointAsync()=>runtime.SaveShutdownCheckpointAsync();
        public ValueTask DisposeAsync() => runtime.DisposeAsync();
    }
    private sealed record CollectorSettings(bool HallEnabled);
}
