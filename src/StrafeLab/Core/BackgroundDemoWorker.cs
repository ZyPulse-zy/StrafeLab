using System.IO;
using System.Text.Json;
using StrafeLab.Platform;

namespace StrafeLab.Core;
public static class BackgroundDemoWorker
{
    // Metadata only. The small collector never deserializes complete match recordings.
    public static (string Stamp,bool Pending)? Fingerprint(string root)
    {
        try
        {
            var path=Path.Combine(root,"demo-library.json");
            var state=File.Exists(path)?JsonSerializer.Deserialize<DemoLibraryState>(File.ReadAllText(path),DemoLibrary.Json):null;
            if(state?.Enabled==false)return null;
            var roots=state?.Roots??new SteamLocator().FindDemoRoots().ToList();
            var files=DemoLibrary.Enumerate(roots).Concat(state?.ManualFiles??[])
                .Concat(Directory.EnumerateFiles(Path.Combine(root,"sessions"),"*.json"))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
            var entries=new List<string>{string.Join('|',roots)};
            foreach(var file in files)
            {
                try{var info=new FileInfo(file);if(info.Exists)entries.Add($"{file}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            bool pending=state?.Jobs.Any(j=>j.State is "待分析" or "等待下载完成" || j.State=="失败"&&j.NextRetryUtc<=DateTime.UtcNow)==true;
            return (DemoLibrary.Stamp(string.Join('\n',entries)),pending);
        }
        catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(JsonException){return null;}
    }
    public static async Task RunAsync(CancellationToken cancellationToken=default)
    {
        using var instance=new Mutex(true,@"Local\StrafeLab-worker",out bool created);
        if(!created||GamePresence.IsRunning())return;
        await using var monitor=new DemoMonitor(new SessionStore(),new SteamLocator().FindDemoRoots());
        // Two observations separated by the download-settle window; then drain a bounded batch.
        for(int i=0;i<8&&!cancellationToken.IsCancellationRequested&&!GamePresence.IsRunning();i++)
        {
            await monitor.ScanOnceAsync(cancellationToken);
            if(monitor.IsReadOnly)return;
            if(i==0)await Task.Delay(TimeSpan.FromSeconds(11),cancellationToken);
            else if(!monitor.Snapshot().Jobs.Any(j=>j.State=="待分析"))break;
        }
    }
}
