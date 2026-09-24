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
    private readonly SemaphoreSlim _demoMutex=new(1);
    private readonly Dictionary<InputControl,HallSample> _depth=[];
    private readonly Dictionary<string,DateTime> _demoAttempts=[];
    private StrafeAnalyzer? _analyzer;
    private MovementSimulator? _sim;
    private Timer? _timer,_saveTimer,_demoTimer;
    private bool _accepting,_disposing;
    private int _saving,_ticking;
    private long _lastGsiUs;
    private readonly string _token;
    public RuntimeService()
    {
        var tokenPath=Path.Combine(_store.RootDirectory,"gsi-token.txt");
        _token=File.Exists(tokenPath)?File.ReadAllText(tokenPath).Trim():Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        if(_token.Length<32||!_token.All(Uri.IsHexDigit))_token=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        File.WriteAllText(tokenPath,_token);
        _hall=new(_store.RootDirectory);_gsi=new(_token);_demo=new(_steam);
        _raw.InputReceived+=(_,e)=>OnInput(e);
        _raw.MouseMoved+=(_,_)=>{lock(_gate){if(_accepting)_sim?.MarkUncertain(TimeUtil.NowMicroseconds(),750_000);}};
        WireHall(_hall);
        _gsi.SnapshotReceived+=(_,s)=>OnGsi(s);
        _gsi.StatusChanged+=(_,s)=>{lock(_gate)Metrics.GsiStatus=s;};
    }
    private void WireHall(Ace68HallSource hall) { hall.SampleReceived+=(_,s)=>{lock(_gate){_depth[s.Control]=s;if(_accepting && ActiveSession!=null){ActiveSession.HallSamples.Add(s);ActiveSession.InputSource="ACE68 Hall + Raw Input";}}}; }
    public async Task SetHallEnabledAsync(bool enabled)
    {
        await _hall.DisposeAsync();
        if(enabled&&!_disposing){_hall=new(_store.RootDirectory);WireHall(_hall);_hall.Start();}
        lock(_gate)_depth.Clear();
    }
    public SessionDocument? ActiveSession {get;private set;}
    public SessionDocument? LastCompletedSession {get;private set;}
    public LiveMetrics Metrics {get;}=new();
    public Ace68HallProbeResult? ProbeResult {get;private set;}
    public bool IsRecording {get{lock(_gate)return ActiveSession!=null;}}
    public bool AutoRecord {get;set;}=true;
    public bool DiagnosticMode {get;set;}
    public int GsiPort=>_gsi.Port;
    public string DataDirectory=>_store.RootDirectory;
    public string HallStatus=>_hall.Status;
    public long HallReportCount=>_hall.ReportCount;
    public event EventHandler<string>? Notification;
    public async Task InitializeAsync(CancellationToken ct=default)
    {
        await _gsi.StartAsync(3000,ct);
        ProbeResult=await new Ace68HallProbe().ProbeAsync(ct);
        ct.ThrowIfCancellationRequested();if(_disposing)return;
        _raw.Start();
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1")
        {
            _hall.Start();try{InstallGsiConfig();}catch(Exception ex){Notification?.Invoke(this,ex.Message);}
        }
        _timer=new(_=>Tick(),null,0,20);
        _saveTimer=new(_=>QueueSave(),null,5000,5000);
        _demoTimer=new(_=>RetryDemos(),null,15000,30000);
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
            LastCompletedSession=session;_analyzer?.Reset();_sim?.Reset();Metrics.Status="已保存";
        }
        await Task.Run(()=>_store.Save(session));
        if(analyze&&!_disposing&&!session.DiagnosticMode)_=AnalyzeDemoAsync(session,null);
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
                {finish=ActiveSession;finish.EndedAtUtc=DateTime.UtcNow;finish.Revision++;ActiveSession=null;LastCompletedSession=finish;ResetOnPause();}
                else{ActiveSession.Map??=s.Map;ActiveSession.PlayerSteamId??=s.ProviderSteamId;ActiveSession.GsiEvents.Add(new(){Snapshot=s});}
            }
            if(ActiveSession==null&&AutoRecord&&!DiagnosticMode&&s.Map!=null&&s.MapPhase is "live" or "warmup"&&IsSelfPlaying(s,s.ReceivedAtUs))StartRecording();
        }
        if(finish!=null){var saved=finish;_=Task.Run(()=>{_store.Save(saved);_=AnalyzeDemoAsync(saved,null);});}
    }
    private void Tick()
    {
        if(Interlocked.Exchange(ref _ticking,1)!=0)return;
        try
        {
            var now=TimeUtil.NowMicroseconds();var foreground=ForegroundGame.IsCs2();
            lock(_gate)
            {
                Metrics.InputSource=_hall.Verified?"Hall + Raw Input":"Raw Input";
                Metrics.DeviceStatus=_hall.Status;
                if(now-_lastGsiUs>3_000_000)Metrics.GsiStatus="等待 GSI / 数据过期";
                if(ActiveSession==null||_sim==null||_analyzer==null)return;
                if(!ActiveSession.DiagnosticMode && _lastGsiUs>0 && now-_lastGsiUs>30_000_000)
                { _=StopRecordingAsync();return; }
                bool allowed=ActiveSession.DiagnosticMode || (foreground&&IsSelfPlaying(_gsi.Latest,now));
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
        _=Task.Run(()=>{try{SessionDocument? copy;lock(_gate){copy=ActiveSession==null?null:SessionCopy.Take(ActiveSession);}if(copy!=null)_store.Save(copy);}
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
                var confident=shots.Where(t=>t.ModelConfidence>=0.75).ToArray();
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
    public SessionDocument? LoadSession(string id)=>_store.Load(id);
    public string ExportCsv(string id)=>_store.ExportCsv(id);
    private void RetryDemos()
    {
        if(_disposing)return;
        foreach(var summary in _store.LoadRecentSummaries(5))
        {
            var s=_store.Load(summary.SessionId);if(s==null||s.EndedAtUtc==null||s.DiagnosticMode||s.DemoAlignment?.IsReliable==true)continue;
            lock(_gate){if(_demoAttempts.TryGetValue(s.SessionId,out var last)&&DateTime.UtcNow-last<TimeSpan.FromMinutes(2))continue;_demoAttempts[s.SessionId]=DateTime.UtcNow;}
            _=AnalyzeDemoAsync(s,null);break;
        }
    }
    public async Task AnalyzeDemoAsync(SessionDocument session,string? manualPath)
    {
        if(session.DiagnosticMode){lock(_gate)Metrics.DemoStatus="诊断会话不参与 Demo 校准";return;}
        if(!await _demoMutex.WaitAsync(0))return;
        try
        {
            var paths=manualPath==null ? (await Task.Run(()=>_demo.FindCandidates(session,5))).Select(c=>c.FilePath) : [manualPath];
            bool found=false;
            foreach(var path in paths)
            {
                _stop.Token.ThrowIfCancellationRequested();found=true;
                lock(_gate)Metrics.DemoStatus=$"解析 {Path.GetFileName(path)}";
                var parsed=await _demo.ParseAsync(path,session.PlayerSteamId,_stop.Token);
                var alignment=DemoService.Align(session,parsed);
                if(!alignment.IsReliable){lock(_gate)Metrics.DemoStatus=parsed.Error??alignment.Explanation;continue;}
                session.DemoPath=path;session.DemoAlignment=alignment;session.Calibration=DemoService.Calibrate(session,parsed,alignment);
                session.Revision++;_store.Save(session);
                if(session.Calibration.Applied)File.WriteAllText(Path.Combine(_store.RootDirectory,"movement-model.json"),JsonSerializer.Serialize(session.MovementModel));
                lock(_gate){Metrics.DemoStatus=$"同步 {alignment.InlierCount} 锚点 · {session.Calibration.Explanation}";if(LastCompletedSession?.SessionId==session.SessionId)LastCompletedSession=session;}
                return;
            }
            if(!found)lock(_gate)Metrics.DemoStatus="未找到对应 Demo · 稍后自动重试，也可手动选择";
        }
        catch(OperationCanceledException){}
        catch(Exception ex){lock(_gate)Metrics.DemoStatus=$"Demo 分析失败：{ex.Message}";}
        finally{_demoMutex.Release();}
    }
    private MovementModelParameters LoadModel()
    {
        try{var p=JsonSerializer.Deserialize<MovementModelParameters>(File.ReadAllText(Path.Combine(_store.RootDirectory,"movement-model.json")));
            if(p!=null&&p.Accelerate is >=3 and <=8&&p.Friction is >=3 and <=8&&p.MaxGroundSpeed is >=150 and <=320)return p;}catch{}
        return new();
    }
    public async ValueTask DisposeAsync()
    {
        _disposing=true;_stop.Cancel();_timer?.Dispose();_saveTimer?.Dispose();_demoTimer?.Dispose();
        await StopRecordingAsync(false);_raw.Dispose();await _hall.DisposeAsync();await _gsi.DisposeAsync();
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
