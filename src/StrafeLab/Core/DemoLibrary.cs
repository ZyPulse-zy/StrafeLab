using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StrafeLab.Core;

public sealed class DemoJob
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public DateTime WrittenUtc { get; set; }
    public DateTime StableSinceUtc { get; set; }
    public DateTime NextRetryUtc { get; set; }
    public int Attempts { get; set; }
    public string State { get; set; } = "等待下载完成";
    public string Detail { get; set; } = "";
    public string SessionStamp { get; set; } = "";
    public List<string> ContentHashes { get; set; } = [];
    public Dictionary<string,string> AttemptedSessions { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    [System.Text.Json.Serialization.JsonIgnore] public string UpdatedLocal => WrittenUtc.ToLocalTime().ToString("MM-dd HH:mm");
}
public sealed class DemoLibraryState
{
    public int Version { get; set; } = MatchReport.CurrentVersion;
    public bool Enabled { get; set; } = true;
    public List<string> Roots { get; set; } = [];
    public List<string> ManualFiles { get; set; } = [];
    public List<DemoJob> Jobs { get; set; } = [];
}

public sealed class DemoLibrary
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented=true };
    public string Root { get; }
    public string ReportsPath => System.IO.Path.Combine(Root,"analysis");
    public string CachePath => System.IO.Path.Combine(Root,"demo-cache");
    public DemoLibraryState State { get; }
    public DemoLibrary(string root,IEnumerable<string> defaultRoots)
    {
        Root=root;Directory.CreateDirectory(ReportsPath);Directory.CreateDirectory(CachePath);
        var path=System.IO.Path.Combine(Root,"demo-library.json");
        if(File.Exists(path))
        {
            try {State=JsonSerializer.Deserialize<DemoLibraryState>(File.ReadAllText(path),Json)??new();}
            catch { File.Copy(path,path+".corrupt-"+DateTime.UtcNow.Ticks);State=new(); }
        }
        else State=new(){Roots=defaultRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList()};
        foreach(var job in State.Jobs)
        {
            if(State.Version!=MatchReport.CurrentVersion){job.AttemptedSessions.Clear();job.SessionStamp="";job.State="待分析";}
            if(job.State=="分析中") {job.SessionStamp="";job.State="待分析";}
            // Always observe stability again after restarting, including downloads still in flight.
            job.StableSinceUtc=DateTime.UtcNow;
        }
        State.Version=MatchReport.CurrentVersion;
    }
    public static bool Supported(string path) => path.EndsWith(".dem",StringComparison.OrdinalIgnoreCase)||
        path.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".dem.bz2",StringComparison.OrdinalIgnoreCase);
    public static string Stamp(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public void Save() => Atomic(System.IO.Path.Combine(Root,"demo-library.json"),State);
    public void SaveReport(MatchReport report) => Atomic(System.IO.Path.Combine(ReportsPath,report.SessionId+".json"),report);
    public IReadOnlyList<MatchReport> ReadReports()
    {
        var reports=new List<MatchReport>();
        foreach(var path in Directory.EnumerateFiles(ReportsPath,"*.json"))
            try{var r=JsonSerializer.Deserialize<MatchReport>(File.ReadAllText(path),Json);if(r?.Version==MatchReport.CurrentVersion)reports.Add(r);}catch{}
        return reports.OrderByDescending(r=>r.StartedAtUtc).ToArray();
    }
    public static void Atomic<T>(string path,T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp=path+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(value,Json));File.Move(tmp,path,true);
    }
    public static IEnumerable<string> Enumerate(IEnumerable<string> roots)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var root in roots)
        {
            string[] files;
            try { files=Directory.EnumerateFiles(root,"*",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,
                AttributesToSkip=FileAttributes.ReparsePoint,MaxRecursionDepth=10}).Where(Supported).ToArray(); }
            catch { continue; }
            foreach(var file in files) if(seen.Add(System.IO.Path.GetFullPath(file)))yield return file;
        }
    }
    public static bool Observe(DemoJob job,FileInfo info,DateTime now,TimeSpan settle)
    {
        if(job.Length!=info.Length||job.WrittenUtc!=info.LastWriteTimeUtc)
        {
            job.Length=info.Length;job.WrittenUtc=info.LastWriteTimeUtc;job.StableSinceUtc=now;
            job.State="等待下载完成";job.SessionStamp="";job.Attempts=0;job.NextRetryUtc=default;job.ContentHashes.Clear();job.AttemptedSessions.Clear();return false;
        }
        return info.Length>=8&&now-job.StableSinceUtc>=settle&&now-info.LastWriteTimeUtc>=settle;
    }
    public async Task<IReadOnlyList<string>> MaterializeAsync(string path,CancellationToken token)
    {
        if(path.EndsWith(".dem",StringComparison.OrdinalIgnoreCase))return [path];
        if(path.EndsWith(".bz2",StringComparison.OrdinalIgnoreCase))
        {
            var dest=System.IO.Path.Combine(CachePath,Stamp(path+new FileInfo(path).Length+File.GetLastWriteTimeUtc(path).Ticks)+".dem");
            if(!File.Exists(dest))await DemoService.UnpackBzipAsync(path,dest,token);
            return [dest];
        }
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        using var zip=new ZipArchive(file,ZipArchiveMode.Read);
        var entries=zip.Entries.Where(e=>e.Name.EndsWith(".dem",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(entries.Length>16||entries.Sum(e=>e.Length)>8_000_000_000L)throw new IOException("压缩包超出限制（16 个 Demo / 总计 8 GB）");
        var results=new List<string>();
        foreach(var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if(entry.Length<8||entry.Length>2_000_000_000L)throw new IOException("Demo 解压大小超出限制（2 GB）");
            // Entry names are never used as paths. No traversal or overwrite of a user download.
            var dest=System.IO.Path.Combine(CachePath,Stamp(path+file.Length+File.GetLastWriteTimeUtc(path).Ticks+entry.FullName)+".dem");
            if(!File.Exists(dest)||new FileInfo(dest).Length!=entry.Length)
            {
                var temp=dest+".tmp";
                try
                {
                    await using(var source=entry.Open())
                    await using(var target=File.Create(temp))
                    {
                        var buffer=new byte[131072];long total=0;int n;
                        while((n=await source.ReadAsync(buffer,token))>0)
                        {total+=n;if(total>entry.Length)throw new IOException("解压长度不符");await target.WriteAsync(buffer.AsMemory(0,n),token);}
                        if(total!=entry.Length)throw new IOException("Demo 尚未下载完整");
                    }
                    File.Move(temp,dest,true);
                }
                finally {if(File.Exists(temp))File.Delete(temp);}
            }
            results.Add(dest);
        }
        return results;
    }
}
