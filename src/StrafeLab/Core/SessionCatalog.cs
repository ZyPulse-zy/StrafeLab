using System.IO;
using System.Text.Json;

namespace StrafeLab.Core;

public sealed class SessionCatalogEntry
{
    public string SessionId {get;set;}="";
    public string Stamp {get;set;}="";
    public string? Map {get;set;}
    public string? PlayerSteamId {get;set;}
    public bool Finished {get;set;}
    public long Revision {get;set;}
}
public sealed class SessionCatalogState
{
    public int Version {get;set;}
    public int ReportVersion {get;set;}
    public Dictionary<string,SessionCatalogEntry> Entries {get;set;}=[];
}
/// <summary>Disposable metadata, never a replacement for a recording. Protected by the Demo worker lease.</summary>
public sealed class SessionCatalog
{
    public const int CurrentVersion=1;
    private readonly string _path;
    private SessionCatalogState _state;
    private string _saved="";
    public int LastLoadedCount {get;private set;}
    public SessionCatalog(string root)
    {
        _path=Path.Combine(root,"session-index.json");_state=new(){Version=CurrentVersion,ReportVersion=MatchReport.CurrentVersion};
        try
        {
            if(File.Exists(_path))
            {
                var state=JsonSerializer.Deserialize<SessionCatalogState>(File.ReadAllText(_path),DemoLibrary.Json);
                if(state?.Version==CurrentVersion&&state.ReportVersion==MatchReport.CurrentVersion&&state.Entries!=null&&
                    state.Entries.All(p=>Guid.TryParseExact(p.Key,"N",out _)&&p.Value!=null&&p.Value.SessionId==p.Key))
                {_state=state;_saved=JsonSerializer.Serialize(_state,DemoLibrary.Json);}
            }
        }
        catch(IOException){}catch(JsonException){}catch(UnauthorizedAccessException){}
    }
    public IReadOnlyList<SessionCatalogEntry> Refresh(SessionStore store,DemoLibrary library,
        IDictionary<string,MatchReport> reports,CancellationToken token,Func<string,SessionDocument?>? load=null)
    {
        LastLoadedCount=0;load??=store.Load;var seen=new HashSet<string>();var result=new List<SessionCatalogEntry>();
        foreach(var path in Directory.EnumerateFiles(store.SessionsDirectory,"*.json"))
        {
            token.ThrowIfCancellationRequested();var id=Path.GetFileNameWithoutExtension(path);
            if(!Guid.TryParseExact(id,"N",out _))continue;
            var info=new FileInfo(path);if((info.Attributes&FileAttributes.ReparsePoint)!=0)continue;
            string stamp=$"{info.Length}:{info.LastWriteTimeUtc.Ticks}";seen.Add(id);
            _state.Entries.TryGetValue(id,out var entry);reports.TryGetValue(id,out var report);
            bool sourceChanged=entry!=null&&entry.Stamp!=stamp;
            bool cached=entry!=null&&entry.SessionId==id&&entry.Stamp==stamp&&
                (!entry.Finished||report?.Version==MatchReport.CurrentVersion);
            if(!cached)
            {
                LastLoadedCount++;var document=load(id);
                if(document==null||document.SessionId!=id){_state.Entries.Remove(id);continue;}
                info.Refresh();if(!info.Exists||stamp!=$"{info.Length}:{info.LastWriteTimeUtc.Ticks}")continue;
                bool finished=document.EndedAtUtc.HasValue&&!document.DiagnosticMode;
                entry=new(){SessionId=id,Stamp=stamp,Map=document.Map,PlayerSteamId=document.PlayerSteamId,Finished=finished,Revision=document.Revision};
                if(finished&&(report==null||report.Version!=MatchReport.CurrentVersion||sourceChanged||report.UpdatedAtUtc<info.LastWriteTimeUtc))
                {
                    var updated=MatchAnalysis.Build(document);
                    library.SaveReportIfChanged(updated);reports[id]=updated;
                }
                _state.Entries[id]=entry;
            }
            if(entry!.Finished&&entry.PlayerSteamId!=null)result.Add(entry);
        }
        foreach(var id in _state.Entries.Keys.Where(k=>!seen.Contains(k)).ToArray())_state.Entries.Remove(id);
        var serialized=JsonSerializer.Serialize(_state,DemoLibrary.Json);
        if(serialized!=_saved||!File.Exists(_path)){DemoLibrary.Atomic(_path,_state);_saved=serialized;}
        return result;
    }
}
