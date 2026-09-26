using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StrafeLab.Platform;

namespace StrafeLab.Core;

/// <summary>One cancellable worker: settling downloads, persistent queue, hash deduplication,
/// identity/map matching and small report files. Never runs on the raw input/UI thread.</summary>
public sealed class DemoMonitor : IAsyncDisposable
{
    private DemoLibrary _library;
    private readonly string[] _defaultRoots;
    private FileStream? _workerLease;
    private bool _ownershipAttempted;
    public bool IsReadOnly => _ownershipAttempted && _workerLease==null;
    private readonly SessionStore _sessions;
    private readonly DemoService _parser;
    private readonly CancellationTokenSource _stop=new();
    private readonly SemaphoreSlim _wake=new(0,1);
    private readonly SemaphoreSlim _scan=new(1,1);
    private readonly ConcurrentQueue<Action> _commands=new();
    private readonly Dictionary<string,(string Stamp,SessionIndex? Session)> _sessionCache=[];
    private sealed record SessionIndex(string SessionId,string? Map,string? PlayerSteamId,string Stamp);
    private Task? _worker;
    private readonly Func<bool> _defer;
    private string _snapshot="{}";
    public string Status {get;private set;}="等待后台扫描";
    public event EventHandler? Changed;
    public DemoMonitor(SessionStore store,IEnumerable<string> roots,DemoService? parser=null,Func<bool>? defer=null)
    {
        _defer=defer??GamePresence.IsRunning;
        _sessions=store;_defaultRoots=roots.ToArray();_library=new(store.RootDirectory,_defaultRoots);_parser=parser??new(new());
        _snapshot=JsonSerializer.Serialize(_library.State,DemoLibrary.Json);
    }
    public DemoLibraryState Snapshot()
    {
        if(IsReadOnly)try{_snapshot=File.ReadAllText(Path.Combine(_library.Root,"demo-library.json"));}catch{}
        return JsonSerializer.Deserialize<DemoLibraryState>(_snapshot,DemoLibrary.Json)!;
    }
    public IReadOnlyList<MatchReport> Reports()=>_library.ReadReports();
    public void Start()=>_worker??=Task.Run(RunAsync);
    public void Wake(){try{_wake.Release();}catch(SemaphoreFullException){} }
    private void Command(Action action){_commands.Enqueue(action);Wake();}
    public void AddRoot(string path)=>Command(()=>
    {
        var full=Path.GetFullPath(path);
        if(!_library.State.Roots.Contains(full,StringComparer.OrdinalIgnoreCase))_library.State.Roots.Add(full);
    });
    public void RemoveRoot(string path)=>Command(()=>_library.State.Roots.RemoveAll(p=>p.Equals(path,StringComparison.OrdinalIgnoreCase)));
    public void Import(string path)=>Command(()=>
    {
        var full=Path.GetFullPath(path);
        if(!DemoLibrary.Supported(full))return;
        if(!_library.State.ManualFiles.Contains(full,StringComparer.OrdinalIgnoreCase))_library.State.ManualFiles.Add(full);
        var job=_library.State.Jobs.FirstOrDefault(j=>j.Path.Equals(full,StringComparison.OrdinalIgnoreCase));
        if(job!=null){job.SessionStamp="";job.NextRetryUtc=default;job.Attempts=0;job.State="待分析";job.AttemptedSessions.Clear();}
    });
    public void SetEnabled(bool enabled)=>Command(()=>_library.State.Enabled=enabled);
    public void Retry()=>Command(()=>{foreach(var job in _library.State.Jobs.Where(j=>j.State is "失败" or "未匹配"))
        {job.SessionStamp="";job.NextRetryUtc=default;job.Attempts=0;job.State="待分析";job.AttemptedSessions.Clear();}});
    private void Publish()
    {
        _library.Save();_snapshot=JsonSerializer.Serialize(_library.State,DemoLibrary.Json);Changed?.Invoke(this,EventArgs.Empty);
    }
    private async Task RunAsync()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                try{await ScanOnceAsync(_stop.Token);}
                catch(OperationCanceledException) when(_stop.IsCancellationRequested){break;}
                catch(Exception ex){Status="后台扫描失败："+ex.Message;Changed?.Invoke(this,EventArgs.Empty);}
                await _wake.WaitAsync(TimeSpan.FromSeconds(20),_stop.Token);
            }
        }
        catch(OperationCanceledException){}
    }
    public async Task ScanOnceAsync(CancellationToken cancellationToken=default)
    {
        await _scan.WaitAsync(cancellationToken);
        using var playingCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watcher=new Timer(_=>{if(_defer())try{playingCancellation.Cancel();}catch(ObjectDisposedException){}},null,1000,1000);
        var originalCancellation=cancellationToken;
        cancellationToken=playingCancellation.Token;
        try
        {
            if(_workerLease==null)
            {
                _ownershipAttempted=true;
                try {_workerLease=new FileStream(Path.Combine(_library.Root,"demo-worker.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
                catch(IOException)
                {Status="后台任务由另一个 StrafeLab 窗口处理；本页仍可查看报告，请在该窗口管理目录";Changed?.Invoke(this,EventArgs.Empty);return;}
                // Another window may have updated the queue while this instance was read-only.
                _library=new DemoLibrary(_sessions.RootDirectory,_defaultRoots);_sessionCache.Clear();
            }
            while(_commands.TryDequeue(out var command))command();
            var state=_library.State;
            if(!state.Enabled){Status="后台分析已暂停";Publish();return;}
            if(_defer()){Status="CS2 运行中 · Demo 解析等待游戏退出";Publish();return;}
            var sessions=new List<SessionIndex>();var stamps=new List<string>();
            var reports=_library.ReadReports().ToDictionary(r=>r.SessionId);
            foreach(var path in Directory.EnumerateFiles(_sessions.SessionsDirectory,"*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info=new FileInfo(path);var id=Path.GetFileNameWithoutExtension(path);var stamp=$"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
                if(!_sessionCache.TryGetValue(id,out var cached)||cached.Stamp!=stamp)
                {
                    var loaded=_sessions.Load(id);
                    // In-progress and diagnostic sessions are never paired or included in the report cohort.
                    if(loaded?.EndedAtUtc==null||loaded.DiagnosticMode)loaded=null;
                    cached=(stamp,loaded==null?null:new SessionIndex(id,loaded.Map,loaded.PlayerSteamId,stamp));_sessionCache[id]=cached;
                    if(loaded!=null&&(!reports.TryGetValue(id,out var old)||old.Alignment?.IsReliable!=true))
                        _library.SaveReport(MatchAnalysis.Build(loaded));
                }
                if(cached.Session!=null&&cached.Session.PlayerSteamId!=null){sessions.Add(cached.Session);stamps.Add(id+stamp);}
            }
            string sessionStamp=DemoLibrary.Stamp(string.Join("|",stamps.Order())+MatchReport.CurrentVersion);
            var now=DateTime.UtcNow;var ready=new List<DemoJob>();
            var paths=DemoLibrary.Enumerate(state.Roots).Concat(state.ManualFiles).Distinct(StringComparer.OrdinalIgnoreCase);
            var jobs=state.Jobs.ToDictionary(j=>j.Path,StringComparer.OrdinalIgnoreCase);
            foreach(var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info=new FileInfo(path);if(!info.Exists)continue;
                    if(!jobs.TryGetValue(path,out var job)){job=new(){Path=path,StableSinceUtc=now};state.Jobs.Add(job);jobs[path]=job;}
                    if(!DemoLibrary.Observe(job,info,now,TimeSpan.FromSeconds(10)))continue;
                    if(job.State=="等待下载完成")job.State="待分析";
                    if(job.State=="已忽略")continue;
                    if(job.SessionStamp!=sessionStamp&&job.NextRetryUtc<=now&&sessions.Count>0)ready.Add(job);
                }
                catch(IOException){}catch(UnauthorizedAccessException){}
            }
            Publish();
            var next=ready.OrderByDescending(j=>state.ManualFiles.Contains(j.Path,StringComparer.OrdinalIgnoreCase)).ThenByDescending(j=>j.WrittenUtc).FirstOrDefault();
            if(next==null)
            {
                var inaccessible=state.Roots.Count(r=>!Directory.Exists(r));
                Status=$"监控 {state.Roots.Count} 个目录 · {state.Jobs.Count} 个候选 · 等待新 Demo"+(inaccessible>0?$" · {inaccessible} 个目录不可用":"");
                Publish();return;
            }
            bool hadMatch=next.State=="已完成";
            next.State="分析中";next.Attempts++;Status="后台解析："+next.Name;Publish();
            try
            {
                // Deny simultaneous writers while hashing, decompressing and parsing; never touch an incomplete download.
                using var lease=new FileStream(next.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
                if(lease.Length!=next.Length||File.GetLastWriteTimeUtc(next.Path)!=next.WrittenUtc)throw new IOException("下载文件仍在变化");
                var materialized=await _library.MaterializeAsync(next.Path,cancellationToken);
                bool matched=hadMatch;var notes=new List<string>();if(hadMatch)notes.Add("保留既有配对结果");next.ContentHashes.Clear();
                foreach(var path in materialized)
                {
                    if(!DemoHeaderScanner.Read(path).IsSource2){notes.Add("非 Source 2 Demo");continue;}
                    await using var file=File.OpenRead(path);
                    string hash=Convert.ToHexString(await SHA256.HashDataAsync(file,cancellationToken)).ToLowerInvariant();
                    if(next.ContentHashes.Contains(hash)){notes.Add("压缩包内存在重复 Demo，已跳过");continue;}
                    next.ContentHashes.Add(hash);
                    matched|=reports.Values.Any(r=>r.DemoHash==hash&&r.Alignment?.IsReliable==true);
                    var duplicate=state.Jobs.FirstOrDefault(j=>j!=next&&j.ContentHashes.Contains(hash)&&j.SessionStamp==sessionStamp);
                    if(duplicate!=null){matched|=duplicate.State=="已完成";notes.Add("内容重复，复用既有结果");continue;}
                    var map=DemoHeaderScanner.Read(path).MapHint;
                    foreach(var group in sessions.Where(s=>(map==null||s.Map==map)&&
                        next.AttemptedSessions.GetValueOrDefault(hash+":"+s.SessionId)!=s.Stamp).GroupBy(s=>s.PlayerSteamId!))
                    {
                        var anchorPath=Path.Combine(_library.CachePath,hash+"-"+DemoLibrary.Stamp(group.Key)+"-v"+MatchReport.CurrentVersion+".anchors.json");
                        DemoParseResult? parsed=null;bool hasFrames=false;
                        if(File.Exists(anchorPath))try{parsed=JsonSerializer.Deserialize<DemoParseResult>(File.ReadAllText(anchorPath),DemoLibrary.Json);}catch{}
                        if(parsed==null)
                        {
                            parsed=await _parser.ParseAsync(path,group.Key,cancellationToken);hasFrames=true;
                            if(parsed.Error==null)DemoLibrary.Atomic(anchorPath,new DemoParseResult{IsSource2=parsed.IsSource2,MapHint=parsed.MapHint,
                                TickRate=parsed.TickRate,Parser=parsed.Parser,Observations=parsed.Observations.Where(o=>o.Kind!="player_sample").ToList()});
                        }
                        if(parsed.Error!=null)throw new IOException(parsed.Error);
                        foreach(var entry in group)
                        {
                            var session=_sessions.Load(entry.SessionId);if(session?.EndedAtUtc==null)continue;
                            cancellationToken.ThrowIfCancellationRequested();
                            if(reports.TryGetValue(session.SessionId,out var existing)&&existing.Alignment?.IsReliable==true&&existing.DemoHash!=hash)
                                continue; // A different demo must not overwrite an already identity-bound match.
                            var alignment=DemoService.Align(session,parsed);
                            if(!alignment.IsReliable){next.AttemptedSessions[hash+":"+session.SessionId]=entry.Stamp;notes.Add($"{session.StartedAtUtc.ToLocalTime():MM-dd HH:mm}：{alignment.Explanation}");continue;}
                            if(!hasFrames)
                            {
                                parsed=await _parser.ParseAsync(path,group.Key,cancellationToken);hasFrames=true;
                                if(parsed.Error!=null)throw new IOException(parsed.Error);
                            }
                            var report=MatchAnalysis.Build(session,parsed,alignment);report.DemoPath=next.Path;report.DemoHash=hash;
                            // Position differences are interval velocities, not instantaneous state for physical parameter fitting.
                            report.Calibration=new(){Explanation="Demo 区间速度用于复盘；尚不用于瞬时移动模型校准，保留原参数"};
                            _library.SaveReport(report);reports[session.SessionId]=report;matched=true;next.AttemptedSessions[hash+":"+session.SessionId]=entry.Stamp;
                            notes.Add($"{report.LocalTime} {report.Map}：匹配 {report.Matched}/{report.Count} 次动作，残差 {alignment.ResidualRmsMs:F1} ms");
                        }
                    }
                }
                next.State=materialized.Count==0?"已忽略":matched?"已完成":"未匹配";
                next.Detail=materialized.Count==0?"ZIP 中没有 .dem 文件":string.Join("；",notes.Distinct().Take(8));
                if(next.Detail.Length==0)next.Detail="没有地图/身份/时间锚点相符的已结束记录；新记录出现后自动重试";
                next.SessionStamp=sessionStamp;Status=next.State+" · "+next.Name;
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){next.State="待分析";next.SessionStamp="";throw;}
            catch(Exception ex)
            {
                next.State="失败";next.Detail=ex.Message.Length>800?ex.Message[..800]:ex.Message;
                next.NextRetryUtc=DateTime.UtcNow.AddMinutes(Math.Min(60,Math.Pow(2,Math.Min(next.Attempts,6))));
                Status="Demo 将稍后重试："+next.Name;
            }
            finally {Publish();}
            if(ready.Count>1)Wake();
        }
        catch(OperationCanceledException) when(!originalCancellation.IsCancellationRequested)
        {Status="CS2 运行中 · 已暂停解析，退出游戏后继续";Publish();}
        finally {_scan.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();if(_worker!=null)await _worker;
        await _scan.WaitAsync();
        try{if(_workerLease!=null){while(_commands.TryDequeue(out var command))command();Publish();_workerLease.Dispose();_workerLease=null;}}
        finally{_scan.Release();}
        _stop.Dispose();_wake.Dispose();_scan.Dispose();
    }
}
