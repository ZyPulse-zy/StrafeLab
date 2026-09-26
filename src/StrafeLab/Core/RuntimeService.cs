using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StrafeLab.Core.Gsi;
using StrafeLab.Input;
using StrafeLab.Platform;
namespace StrafeLab.Core;

public sealed class RuntimeService : IAsyncDisposable
{
    private readonly object _gate=new();
    private readonly SessionStore _store=new();
    private readonly SteamLocator _steam=new();
    private readonly RawInputSource _raw=new();
    private Ace68HallSource _hall;
    private readonly GsiServer _gsi;
    private readonly DemoService _demo;
    private readonly CancellationTokenSource _stop=new();
    public DemoMonitor DemoMonitor { get; }
    private readonly Dictionary<InputControl,HallSample> _depth=[];
    private StrafeAnalyzer? _analyzer;
    private MovementSimulator? _sim;
    private Timer? _timer,_saveTimer;
    private bool _accepting,_disposing;
    private int _saving,_ticking;
    private Task _saveTask=Task.CompletedTask;
    private readonly List<Task> _finalSaves=[];
    private long _lastGsiUs;
    private readonly string _token;
    private readonly bool _captureOnly;
    private bool _hallEnabled;
    public RuntimeService(bool captureOnly=false,bool hallEnabled=true)
    {
        _captureOnly=captureOnly;_hallEnabled=hallEnabled;
        var tokenPath=Path.Combine(_store.RootDirectory,"gsi-token.txt");
        _token=File.Exists(tokenPath)?File.ReadAllText(tokenPath).Trim():Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        if(_token.Length<32||!_token.All(Uri.IsHexDigit))_token=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        if(!File.Exists(tokenPath)||File.ReadAllText(tokenPath).Trim()!=_token)File.WriteAllText(tokenPath,_token);
        _hall=new(_store.RootDirectory);_gsi=new(_token);_demo=new(_steam);
        DemoMonitor=new(_store,_steam.FindDemoRoots(),_demo);
        DemoMonitor.Changed+=(_,_)=>{lock(_gate)Metrics.DemoStatus=DemoMonitor.Status;};
        _raw.InputReceived+=(_,e)=>OnInput(e);
        _raw.MouseMoved+=(_,_)=>{lock(_gate){if(_accepting)_sim?.MarkUncertain(TimeUtil.NowMicroseconds(),750_000);}};
        WireHall(_hall);
        _gsi.SnapshotReceived+=(_,s)=>OnGsi(s);
        _gsi.StatusChanged+=(_,s)=>{lock(_gate)Metrics.GsiStatus=s;};
    }
    private void WireHall(Ace68HallSource hall) { hall.SampleReceived+=(_,s)=>{lock(_gate){_depth[s.Control]=s;if(_accepting && ActiveSession!=null){ActiveSession.HallSamples.Add(s);ActiveSession.InputSource="ACE68 Hall + Raw Input";}}}; }
    public async Task SetHallEnabledAsync(bool enabled)
    {
        _hallEnabled=enabled;
        await _hall.DisposeAsync();
        if(enabled&&!_disposing){_hall=new(_store.RootDirectory);WireHall(_hall);_hall.Start();}
        lock(_gate)_depth.Clear();
    }
    public SessionDocument? ActiveSession {get;private set;}
    public SessionDocument? LastCompletedSession {get;private set;}
    public LiveMetrics Metrics {get;}=new();
    public Ace68HallProbeResult? ProbeResult {get;private set;}
    public bool IsRecording {get{lock(_gate)return ActiveSession!=null;}}
    public int InputCount {get{lock(_gate)return ActiveSession?.InputEvents.Count??0;}}
    public bool AutoRecord {get;set;}=true;
    public bool DiagnosticMode {get;set;}
    public int GsiPort=>_gsi.Port;
    public string DataDirectory=>_store.RootDirectory;
    public string HallStatus=>_hallEnabled?_hall.Status:"Hall 已关闭 · 使用 Raw Input";
    public long HallReportCount=>_hall.ReportCount;
    public event EventHandler<string>? Notification;
    public async Task InitializeAsync(CancellationToken ct=default)
    {
        await _gsi.StartAsync(3000,ct);
        if(_hallEnabled)ProbeResult=await new Ace68HallProbe().ProbeAsync(ct);
        ct.ThrowIfCancellationRequested();if(_disposing)return;
        _raw.Start();
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1")
        {
            if(_hallEnabled)_hall.Start();try{InstallGsiConfig();}catch(Exception ex){Notification?.Invoke(this,ex.Message);}
        }
        _timer=new(_=>Tick(),null,0,20);
        _saveTimer=new(_=>QueueSave(),null,5000,5000);
        if(!_captureOnly&&Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1")DemoMonitor.Start();
        Metrics.Status="就绪 · GSI 进入比赛后自动记录";
    }
    public IReadOnlyList<string> InstallGsiConfig()=>new GsiConfigInstaller(_steam).Install(_gsi.Port,_token);
    public void InstallGsiConfigAt(string cfgDirectory)
    {
        var path=Path.Combine(cfgDirectory,GsiConfigInstaller.FileName);
        if(File.Exists(path))File.Copy(path,path+".bak",true);
        File.WriteAllText(path,new GsiConfigInstaller(_steam).BuildConfig(_gsi.Port,_token));
    }
    public void StartRecording()
    {
        lock(_gate)
        {
            if(ActiveSession!=null||_disposing)return;
            ActiveSession=new() {InputSource="Raw Input + ACE68 Hall (when verified)",DeviceName=ProbeResult?.Device?.ProductName,
                DeviceVendorId=ProbeResult?.Device?.VendorId,DeviceProductId=ProbeResult?.Device?.ProductId,
                DiagnosticMode=DiagnosticMode,Map=_gsi.Latest?.Map,PlayerSteamId=_gsi.Latest?.ProviderSteamId,
                MovementModel=LoadModel()};
            _analyzer=new(ActiveSession.Transitions);_sim=new(ActiveSession.MovementModel);
            _sim.MarkUncertain(TimeUtil.NowMicroseconds());
            Metrics.SpeedHistory.Clear();_accepting=false;
            Metrics.Status=DiagnosticMode?"桌面诊断记录中（不用于校准）":"记录中 · 等待 CS2 前台";
            if(_gsi.Latest!=null)ActiveSession.GsiEvents.Add(new(){Snapshot=_gsi.Latest});
        }
    }
    public async Task StopRecordingAsync(bool analyze=true)
    {
        SessionDocument? session;
        lock(_gate)
        {
            session=ActiveSession;if(session==null)return;
            session.EndedAtUtc=DateTime.UtcNow;session.Revision++;ActiveSession=null;_accepting=false;
            LastCompletedSession=_captureOnly?null:session;_analyzer?.Reset();_analyzer=null;_sim?.Reset();_sim=null;Metrics.Status="已保存";
        }
        Task save;
        lock(_gate)
        {
            _finalSaves.RemoveAll(t=>t.IsCompletedSuccessfully);
            save=Task.Run(()=>_store.Save(session));_finalSaves.Add(save);
        }
        await save;
        if(analyze&&!_disposing&&!session.DiagnosticMode)DemoMonitor.Wake();
    }
    private static bool IsSelfPlaying(GsiSnapshot? s,long now) => s!=null && now-s.ReceivedAtUs<2_000_000 &&
        s.ProviderSteamId!=null && s.ProviderSteamId==s.PlayerSteamId && s.PlayerActivity=="playing" && s.IsAlive==true;
    private void OnInput(InputEvent e)
    {
        lock(_gate)
        {
            if(ActiveSession==null||_analyzer==null||_sim==null)return;
            var allowed=ActiveSession.DiagnosticMode || (ForegroundGame.IsCs2() && IsSelfPlaying(_gsi.Latest,e.TimestampUs));
            if(!allowed){ResetOnPause();return;}
            _accepting=true;
            if((e.Action==InputAction.Down)==_analyzer.IsHeld(e.Control))return;
            if(e.Confidence<.75)_sim.MarkUncertain(e.TimestampUs);
            _sim.AdvanceTo(e.TimestampUs,_analyzer.Snapshot,_gsi.Latest);
            var update=_analyzer.Process(e,_gsi.Latest,_sim.State);
            var physics=_sim.AdvanceTo(e.TimestampUs,update.Snapshot,_gsi.Latest);
            ActiveSession.InputEvents.Add(e);ActiveSession.PhysicsSamples.Add(physics);
            if(update.Shot!=null)ActiveSession.Shots.Add(update.Shot);
        }
    }
    private void ResetOnPause()
    {
        if(!_accepting)return;
        _accepting=false;_analyzer?.Reset();_sim?.Reset();_sim?.MarkUncertain(TimeUtil.NowMicroseconds());
    }
    private void OnGsi(GsiSnapshot s)
    {
        SessionDocument? finish=null;
        lock(_gate)
        {
            _lastGsiUs=s.ReceivedAtUs;
            Metrics.CurrentMap=s.Map??"—";Metrics.CurrentWeapon=s.WeaponName??"—";Metrics.CurrentRound=s.Round?.ToString()??"—";
            Metrics.GsiStatus="已连接";
            if(ActiveSession!=null)
            {
                if(!ActiveSession.DiagnosticMode && ((s.Map!=null&&ActiveSession.Map!=null&&s.Map!=ActiveSession.Map)||s.MapPhase=="gameover"))
                {finish=ActiveSession;finish.EndedAtUtc=DateTime.UtcNow;finish.Revision++;ActiveSession=null;LastCompletedSession=_captureOnly?null:finish;ResetOnPause();_analyzer=null;_sim=null;}
                else{ActiveSession.Map??=s.Map;ActiveSession.PlayerSteamId??=s.ProviderSteamId;ActiveSession.GsiEvents.Add(new(){Snapshot=s});}
            }
            if(ActiveSession==null&&AutoRecord&&!DiagnosticMode&&s.Map!=null&&s.MapPhase is "live" or "warmup"&&IsSelfPlaying(s,s.ReceivedAtUs))StartRecording();
        }
        if(finish!=null)
        {
            var saved=finish;
            lock(_gate)
            {
                _finalSaves.RemoveAll(t=>t.IsCompletedSuccessfully);
                _finalSaves.Add(Task.Run(()=>{_store.Save(saved);if(!_captureOnly)_=AnalyzeDemoAsync(saved,null);}));
            }
        }
    }
    private void Tick()
    {
        if(Interlocked.Exchange(ref _ticking,1)!=0)return;
        try
        {
            var now=TimeUtil.NowMicroseconds();
            lock(_gate)
            {
                Metrics.InputSource=_hall.Verified?"Hall + Raw Input":"Raw Input";
                Metrics.DeviceStatus=_hall.Status;
                if(now-_lastGsiUs>3_000_000)Metrics.GsiStatus="等待 GSI / 数据过期";
                if(ActiveSession==null||_sim==null||_analyzer==null)return;
                if(!ActiveSession.DiagnosticMode && _lastGsiUs>0 && now-_lastGsiUs>30_000_000)
                { _=StopRecordingAsync();return; }
                bool allowed=ActiveSession.DiagnosticMode || (ForegroundGame.IsCs2()&&IsSelfPlaying(_gsi.Latest,now));
                if(!allowed){ResetOnPause();Metrics.Status="记录已暂停 · 需要 CS2 前台且正在操控自己";return;}
                _accepting=true;Metrics.Status=ActiveSession.DiagnosticMode?"桌面诊断中":"记录中";
                var sample=_sim.AdvanceForTimer(now,_gsi.Latest);ActiveSession.PhysicsSamples.Add(sample);
                Metrics.CurrentSpeed=sample.Speed;Metrics.PhysicsSampleCount=ActiveSession.PhysicsSamples.Count;
                Metrics.SpeedHistory.Add(sample.Speed);if(Metrics.SpeedHistory.Count>200)Metrics.SpeedHistory.RemoveAt(0);
            }
        }
        catch(Exception ex){lock(_gate)Metrics.Status=ex.Message;}
        finally{Volatile.Write(ref _ticking,0);}
    }
    private void QueueSave()
    {
        if(Interlocked.Exchange(ref _saving,1)!=0)return;
        _saveTask=Task.Run(()=>{try{SessionDocument? copy;lock(_gate){copy=ActiveSession==null?null:SessionCopy.Take(ActiveSession);}if(copy!=null)_store.Save(copy);}
            catch(Exception ex){lock(_gate)Metrics.Status=$"保存失败：{ex.Message}";}finally{Volatile.Write(ref _saving,0);}});
    }
    public RuntimeView GetView()
    {
        lock(_gate)
        {
            var s=ActiveSession??LastCompletedSession;
            if(s!=null)
            {
                var ts=s.Transitions.Where(t=>t.Confidence>=0.75).ToArray();var shots=s.Shots.Where(t=>t.Confidence>=0.75).ToArray();
                Metrics.TransitionCount=ts.Length;Metrics.ShotCount=shots.Length;
                Metrics.AverageGapMs=ts.Length==0?0:ts.Average(x=>x.GapMs);
                var complete=ts.Where(t=>t.OverlapUs!=null).ToArray();Metrics.AverageOverlapMs=complete.Length==0?0:complete.Average(x=>x.OverlapMs);
                var matched=shots.Where(t=>t.DeltaFromTransitionUs!=null).ToArray();Metrics.AverageShotDeltaMs=matched.Length==0?0:matched.Average(t=>t.DeltaFromTransitionUs!.Value/1000d);
                var confident=shots.Where(t=>t.ModelConfidence>=0.75&&CounterStrafeCohort.IsRifle(t.WeaponName)&&
                    t.RelatedTransitionUs.HasValue&&t.DeltaFromTransitionUs is >=0 and <=250_000).ToArray();
                Metrics.ModelShotCount=confident.Length;
                Metrics.AverageSpeedAtShot=confident.Length==0?0:confident.Average(t=>t.EstimatedSpeed??0);
                Metrics.FireWindowRate=confident.Length==0?0:confident.Count(t=>t.IsWithinFireWindow)/(double)confident.Length;
                Metrics.ConfidenceRate=s.Transitions.Count==0?0:ts.Length/(double)s.Transitions.Count;
            }
            // JSON clones only tiny live metrics; never serialize a match on the input thread.
            var m=JsonSerializer.Deserialize<LiveMetrics>(JsonSerializer.Serialize(Metrics))!;
            m.SpeedHistory.AddRange(Metrics.SpeedHistory);
            var rows=s?.Transitions.AsEnumerable().Reverse().Take(12).Select(t=>$"{t.From} → {t.To}   gap {t.GapMs:F1} ms   overlap {(t.OverlapUs.HasValue?t.OverlapMs.ToString("F1"):"…")} ms   hold {(t.ReverseHoldUs/1000d)?.ToString("F1")??"…"} ms   click {(t.ShotDeltaUs/1000d)?.ToString("F1")??"—"} ms").ToArray()??[];
            return new(m,rows,_depth.ToDictionary(x=>x.Key,x=>x.Value));
        }
    }
    public IReadOnlyList<SessionSummary> GetRecentSummaries(int limit=100)=>_store.LoadRecentSummaries(limit);
    public Task SaveShutdownCheckpointAsync()
    {
        SessionDocument? copy;
        lock(_gate){copy=ActiveSession==null?null:SessionCopy.Take(ActiveSession);if(copy!=null)copy.EndedAtUtc=DateTime.UtcNow;}
        return copy==null?Task.CompletedTask:Task.Run(()=>_store.Save(copy));
    }
    public SessionDocument? LoadSession(string id)=>_store.Load(id);
    public string ExportCsv(string id)=>_store.ExportCsv(id);
    public Task AnalyzeDemoAsync(SessionDocument session,string? manualPath)
    {
        if(manualPath!=null)DemoMonitor.Import(manualPath);else DemoMonitor.Wake();
        return Task.CompletedTask;
    }
    private MovementModelParameters LoadModel()
    {
        try{var p=JsonSerializer.Deserialize<MovementModelParameters>(File.ReadAllText(Path.Combine(_store.RootDirectory,"movement-model.json")));
            if(p!=null&&p.Accelerate is >=3 and <=8&&p.Friction is >=3 and <=8&&p.MaxGroundSpeed is >=150 and <=320)return p;}catch{}
        return new();
    }
    public async ValueTask DisposeAsync()
    {
        _disposing=true;_stop.Cancel();
        if(_timer!=null)await _timer.DisposeAsync();if(_saveTimer!=null)await _saveTimer.DisposeAsync();
        try
        {
            await _gsi.DisposeAsync();
            await StopRecordingAsync(false);await _saveTask;
            Task[] pending;lock(_gate)pending=_finalSaves.ToArray();await Task.WhenAll(pending);
        }
        finally{_raw.Dispose();await _hall.DisposeAsync();await DemoMonitor.DisposeAsync();_stop.Dispose();}
    }
}
public sealed record RuntimeView(LiveMetrics Metrics,string[] Rows,Dictionary<InputControl,HallSample> Hall);
public static class SessionCopy
{
    public static SessionDocument Take(SessionDocument s) => new() {SessionId=s.SessionId,Revision=++s.Revision,StartedAtUtc=s.StartedAtUtc,SessionStartUs=s.SessionStartUs,
        EndedAtUtc=s.EndedAtUtc,InputSource=s.InputSource,DeviceName=s.DeviceName,DeviceVendorId=s.DeviceVendorId,DeviceProductId=s.DeviceProductId,
        Map=s.Map,PlayerSteamId=s.PlayerSteamId,DiagnosticMode=s.DiagnosticMode,MovementModel=s.MovementModel,
        InputEvents=[..s.InputEvents],PhysicsSamples=[..s.PhysicsSamples],Shots=[..s.Shots],GsiEvents=[..s.GsiEvents],HallSamples=[..s.HallSamples],
        Transitions=s.Transitions.Select(t=>t with{}).ToList(),DemoPath=s.DemoPath,DemoAlignment=s.DemoAlignment,Calibration=s.Calibration};
}
