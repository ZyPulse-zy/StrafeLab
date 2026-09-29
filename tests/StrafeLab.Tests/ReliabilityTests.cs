using System.Text.Json;
using System.IO.Compression;
using StrafeLab.Core;
using Xunit;

namespace StrafeLab.Tests;

public sealed class ReliabilityTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"StrafeLabReliability-"+Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now=new(2026,9,27,0,0,0,DateTimeKind.Utc);
    public ReliabilityTests()=>Directory.CreateDirectory(_root);
    public void Dispose()=>Directory.Delete(_root,true);
    private DemoLibrary Library()=>new(_root,[]);
    private SessionDocument Session()=>new(){StartedAtUtc=Now.AddHours(-1),EndedAtUtc=Now,PlayerSteamId="local",Map="de_dust2"};
    private static DemoJob Job(string path,params string[] hashes)=>new(){Path=path,ContentHashes=hashes.ToList(),State="未匹配"};

    [Theory]
    [InlineData("event failed\nSTRAFELAB_ERROR {\"code\":\"missing_motion_fields\"}\nmore", "missing_motion_fields")]
    [InlineData("KeyError: Column not found: X", "missing_motion_fields")]
    [InlineData("End of Central Directory record could not be found.", "invalid_archive")]
    [InlineData("Demo clock is irregular; synchronization rejected", "invalid_clock")]
    [InlineData("demo parse failed: unsupported schema", "parser_error")]
    [InlineData("不是 Source 2 Demo", "invalid_demo")]
    public void Deterministic_failures_do_not_schedule_infinite_retries(string message,string code)
    {
        var j=Job("broken.zip");j.Attempts=27;
        DemoFailures.Apply(j,new IOException(message),Now);
        Assert.Equal("需处理",j.State);Assert.Equal(code,j.FailureCode);Assert.Equal(1,j.FailureCount);
        Assert.False(DemoFailures.IsPending(j,Now.AddYears(10)));Assert.Equal(default,j.NextRetryUtc);
        Assert.Equal(message,j.FailureDiagnostic);
    }
    [Fact] public void Transient_failure_limit_is_consecutive_not_lifetime_attempts()
    {
        var j=Job("busy.dem");j.Attempts=99;
        DemoFailures.Apply(j,new IOException("file busy"),Now);
        Assert.Equal("失败",j.State);Assert.Equal(Now.AddMinutes(2),j.NextRetryUtc);
        Assert.False(DemoFailures.IsPending(j,Now));Assert.True(DemoFailures.IsPending(j,Now.AddMinutes(2)));
        DemoFailures.Apply(j,new IOException("file busy"),Now);Assert.Equal(Now.AddMinutes(4),j.NextRetryUtc);
        DemoFailures.Apply(j,new IOException("file busy"),Now);Assert.Equal("需处理",j.State);
        DemoFailures.Reset(j);Assert.Equal(0,j.FailureCount);Assert.Equal("待分析",j.State);
    }
    [Fact] public void Only_changed_file_or_explicit_reset_revives_a_blocked_job()
    {
        var path=Path.Combine(_root,"broken.dem");File.WriteAllText(path,"bad demo bytes");
        var j=Job(path,"hash");var info=new FileInfo(path);
        j.Length=info.Length;j.WrittenUtc=info.LastWriteTimeUtc;j.StableSinceUtc=Now.AddMinutes(-1);
        DemoFailures.Apply(j,new InvalidDataException("invalid"),Now);
        DemoLibrary.Observe(j,info,DateTime.UtcNow.AddMinutes(1),TimeSpan.Zero);
        Assert.Equal("需处理",j.State);
        j.UserIgnored=true;File.AppendAllText(path,"changed");
        Assert.False(DemoLibrary.Observe(j,new(path),Now,TimeSpan.Zero));
        Assert.False(j.UserIgnored);Assert.Empty(j.ContentHashes);Assert.Equal("等待下载完成",j.State);
        Assert.Equal(0,j.FailureCount);Assert.Empty(j.FailureCode);
    }
    [Fact] public void Queue_upgrade_preserves_directories_matches_and_ignored_choices()
    {
        var state=new DemoLibraryState{Roots=["watch"],ManualFiles=["manual.dem"],Jobs=[
            new(){Path="bad.zip",State="失败",Attempts=32,Detail="Central Directory missing"},
            new(){Path="ok.dem",State="已完成",ContentHashes=["ok"]},
            new(){Path="ignored.zip",State="已忽略",UserIgnored=true}]};
        DemoLibrary.Atomic(Path.Combine(_root,"demo-library.json"),state);
        var library=Library();Assert.Equal(2,library.State.QueueVersion);
        Assert.Equal(state.Roots,library.State.Roots);Assert.Equal(state.ManualFiles,library.State.ManualFiles);
        Assert.Equal("需处理",library.State.Jobs[0].State);Assert.Equal("已完成",library.State.Jobs[1].State);
        Assert.True(library.State.Jobs[2].UserIgnored);
        var j=library.State.Jobs[0];j.FailureCode="missing_motion_fields";j.FailureRevision="old";
        DemoFailures.Migrate(j);Assert.Equal("待分析",j.State);
        j.State="需处理";j.FailureCode="invalid_archive";DemoFailures.Migrate(j);Assert.Equal("需处理",j.State);
    }
    [Fact] public void Malformed_diagnostics_never_crash_failure_handling()
    {
        Assert.Equal("temporary_io",DemoFailures.Classify(new IOException("STRAFELAB_ERROR {\"code\":123}")).Code);
        var j=Job("x");DemoFailures.Apply(j,new IOException(new string('x',2000)),Now);
        Assert.Equal(1600,j.FailureDiagnostic.Length);
    }
    [Fact] public void Same_contents_merge_with_all_searchable_copies_and_specific_reason()
    {
        var a=Job("one.zip","same");var b=Job("download/copy.dem","same");
        a.Contents["same"]=new(){Map="de_inferno",ReasonCode="identity",Detail="missing identity"};
        var group=Assert.Single(DemoContentGroups.Build([a,b],[]));
        Assert.Equal(2,group.FileCount);Assert.Equal("未匹配",group.State);
        Assert.Equal("录像中未找到本机玩家",group.Reason);Assert.True(group.Matches("copy.dem"));
        Assert.Equal(2,group.Files.Count);Assert.Contains("未配对录制中的可信按键",group.Detail);
    }
    [Fact] public void Overlapping_multi_demo_archives_are_not_transitively_merged_or_all_marked_matched()
    {
        var a=Job("a.zip","x","y");a.State="已完成";
        var b=Job("b.zip","y","z");var c=Job("c.dem","x");
        var report=new MatchReport{SessionId="one",DemoHash="x",Alignment=new(){IsReliable=true}};
        var groups=DemoContentGroups.Build([a,b,c],[report]);Assert.Equal(3,groups.Count);
        Assert.Equal("已完成",groups.Single(g=>g.Key=="x").State);
        Assert.Equal("未匹配",groups.Single(g=>g.Key=="y").State);
        Assert.Equal(2,groups.Single(g=>g.Key=="y").FileCount);
        Assert.Single(groups.Single(g=>g.Key=="z").Files);
    }
    [Fact] public void Unhashed_files_and_broken_content_are_distinct_from_unmatched_games()
    {
        var a=Job("a.zip");var b=Job("b.zip");DemoFailures.Apply(a,new InvalidDataException("broken"),Now);
        var groups=DemoContentGroups.Build([a,b],[]);Assert.Equal(2,groups.Count);
        var failed=groups.Single(g=>g.Files[0]==a);Assert.Equal("需处理",failed.State);Assert.Equal("压缩包无法读取",failed.Reason);
        Assert.Contains("自动重试已停止",failed.Detail);
        a.UserIgnored=true;a.State="已忽略";Assert.Equal("已忽略",DemoContentGroups.Build([a],[])[0].State);
    }
    [Fact] public void Cold_index_preserves_existing_validated_report_and_restart_loads_no_raw_sessions()
    {
        var store=new SessionStore(_root);var s=Session();store.Save(s);var library=Library();
        var report=MatchAnalysis.Build(s);report.Alignment=new(){IsReliable=true};report.DemoHash="evidence";library.SaveReport(report);
        var reportPath=Path.Combine(library.ReportsPath,s.SessionId+".json");var before=File.ReadAllBytes(reportPath);
        var timestamp=File.GetLastWriteTimeUtc(reportPath);var reports=library.ReadReports().ToDictionary(r=>r.SessionId);
        var first=new SessionCatalog(_root);Assert.Single(first.Refresh(store,library,reports,default));Assert.Equal(1,first.LastLoadedCount);
        Assert.Equal(before,File.ReadAllBytes(reportPath));Assert.Equal(timestamp,File.GetLastWriteTimeUtc(reportPath));
        var restart=new SessionCatalog(_root);Assert.Single(restart.Refresh(store,library,reports,default,_=>throw new Exception("Raw JSON reread")));
        Assert.Equal(0,restart.LastLoadedCount);Assert.True(reports[s.SessionId].Alignment?.IsReliable);
        Assert.DoesNotContain("inputEvents",File.ReadAllText(Path.Combine(_root,"session-index.json")));
    }
    [Fact] public void Changed_recording_invalidates_old_demo_evidence_and_missing_report_is_rebuilt()
    {
        var store=new SessionStore(_root);var s=Session();store.Save(s);var library=Library();var reports=new Dictionary<string,MatchReport>();
        var catalog=new SessionCatalog(_root);catalog.Refresh(store,library,reports,default);
        reports[s.SessionId].Alignment=new(){IsReliable=true};library.SaveReport(reports[s.SessionId]);
        s.Revision++;s.Map="de_inferno";store.Save(s);catalog.Refresh(store,library,reports,default);
        Assert.Equal(1,catalog.LastLoadedCount);Assert.Null(reports[s.SessionId].Alignment);Assert.Equal("de_inferno",reports[s.SessionId].Map);
        File.Delete(Path.Combine(library.ReportsPath,s.SessionId+".json"));reports.Clear();
        new SessionCatalog(_root).Refresh(store,library,reports,default);Assert.Single(reports);
    }
    [Theory] [InlineData("broken")] [InlineData("{\"version\":1,\"reportVersion\":4,\"entries\":null}")]
    [InlineData("{\"version\":0,\"entries\":{}}")]
    public void Corrupt_or_old_index_is_disposable(string json)
    {
        var store=new SessionStore(_root);var s=Session();store.Save(s);
        File.WriteAllText(Path.Combine(_root,"session-index.json"),json);
        var catalog=new SessionCatalog(_root);Assert.Single(catalog.Refresh(store,Library(),new Dictionary<string,MatchReport>(),default));
        Assert.Equal(1,catalog.LastLoadedCount);Assert.NotEqual(json,File.ReadAllText(Path.Combine(_root,"session-index.json")));
    }
    [Fact] public void Active_and_diagnostic_sessions_are_indexed_but_not_analyzed_and_deleted_entries_prune()
    {
        var store=new SessionStore(_root);var active=Session();active.EndedAtUtc=null;store.Save(active);
        var diagnostic=Session();diagnostic.DiagnosticMode=true;store.Save(diagnostic);
        var reports=new Dictionary<string,MatchReport>();var catalog=new SessionCatalog(_root);var library=Library();
        Assert.Empty(catalog.Refresh(store,library,reports,default));Assert.Equal(2,catalog.LastLoadedCount);Assert.Empty(reports);
        Assert.Empty(new SessionCatalog(_root).Refresh(store,library,reports,default,_=>throw new Exception("Unchanged file reread")));
        File.Delete(store.GetPath(diagnostic.SessionId));catalog.Refresh(store,library,reports,default);
        var index=JsonSerializer.Deserialize<SessionCatalogState>(File.ReadAllText(Path.Combine(_root,"session-index.json")),DemoLibrary.Json)!;
        Assert.Single(index.Entries);
        active.EndedAtUtc=Now;active.Revision++;store.Save(active);Assert.Single(catalog.Refresh(store,library,reports,default));Assert.Single(reports);
    }
    [Fact] public void A_recording_modified_during_load_is_not_cached_or_published()
    {
        var store=new SessionStore(_root);var s=Session();store.Save(s);var reports=new Dictionary<string,MatchReport>();var catalog=new SessionCatalog(_root);
        var result=catalog.Refresh(store,Library(),reports,default,id=>{s.Map="de_inferno";s.Revision++;store.Save(s);return s;});
        Assert.Empty(result);Assert.Empty(reports);
        Assert.Single(catalog.Refresh(store,Library(),reports,default));Assert.Equal(1,catalog.LastLoadedCount);
    }
    [Fact] public void Identical_report_write_preserves_bytes_and_original_timestamp()
    {
        var report=MatchAnalysis.Build(Session());var library=Library();Assert.True(library.SaveReportIfChanged(report));
        var path=Path.Combine(library.ReportsPath,report.SessionId+".json");var before=File.ReadAllBytes(path);var written=File.GetLastWriteTimeUtc(path);
        report.UpdatedAtUtc=DateTime.UtcNow.AddMinutes(1);Assert.False(library.SaveReportIfChanged(report));
        Assert.Equal(before,File.ReadAllBytes(path));Assert.Equal(written,File.GetLastWriteTimeUtc(path));
    }
    [Fact] public async Task Queue_commands_restore_selected_files_without_deleting_them_and_noop_scan_does_not_write()
    {
        var path=Path.Combine(_root,"file.dem");File.WriteAllText(path,"source bytes remain");
        var library=Library();library.State.Enabled=false;library.State.Jobs.Add(Job(path,"hash"));library.Save();
        await using var monitor=new DemoMonitor(new SessionStore(_root),[],defer:()=>false);
        monitor.Ignore([path]);await monitor.ScanOnceAsync();Assert.True(monitor.Snapshot().Jobs[0].UserIgnored);
        monitor.Retry([path]);await monitor.ScanOnceAsync();var job=monitor.Snapshot().Jobs[0];Assert.False(job.UserIgnored);Assert.Equal("待分析",job.State);
        var queue=Path.Combine(_root,"demo-library.json");var before=File.GetLastWriteTimeUtc(queue);
        await monitor.ScanOnceAsync();Assert.Equal(before,File.GetLastWriteTimeUtc(queue));Assert.Equal("source bytes remain",File.ReadAllText(path));
    }
    [Fact] public async Task A_bad_demo_in_an_archive_does_not_hide_other_contents_or_loop_on_restart()
    {
        var downloads=Path.Combine(_root,"downloads");Directory.CreateDirectory(downloads);
        var zipPath=Path.Combine(downloads,"mixed.zip");
        using(var zip=ZipFile.Open(zipPath,ZipArchiveMode.Create))
        {
            using(var w=new StreamWriter(zip.CreateEntry("bad.dem").Open()))w.Write("not a demo at all");
            using(var w=new StreamWriter(zip.CreateEntry("good.dem").Open()))w.Write("PBDEMS2\0de_inferno");
        }
        var archiveBytes=File.ReadAllBytes(zipPath);
        await using(var monitor=new DemoMonitor(new SessionStore(_root),[downloads],defer:()=>false))
        {
            await monitor.ScanOnceAsync();await Task.Delay(10_100);await monitor.ScanOnceAsync();
            var j=Assert.Single(monitor.Snapshot().Jobs);Assert.Equal("需处理",j.State);Assert.Equal(2,j.ContentHashes.Count);
            var groups=DemoContentGroups.Build([j],[]);Assert.Equal(2,groups.Count);
            Assert.Single(groups,g=>g.State=="需处理"&&g.Reason=="不是 CS2 录像");
            Assert.Single(groups,g=>g.State=="未匹配"&&g.Reason=="缺少本地录制");
            for(int i=0;i<3;i++)await monitor.ScanOnceAsync();Assert.Equal(1,monitor.Snapshot().Jobs[0].Attempts);
        }
        await using(var restart=new DemoMonitor(new SessionStore(_root),[downloads],defer:()=>false))
        {await restart.ScanOnceAsync();Assert.Equal(1,restart.Snapshot().Jobs[0].Attempts);}
        Assert.False(BackgroundDemoWorker.Fingerprint(_root)!.Value.Pending);
        Assert.Equal(archiveBytes,File.ReadAllBytes(zipPath));
    }
    [Fact] public async Task New_recordings_requeue_remaining_unmatched_files_across_bounded_worker_batches()
    {
        var downloads=Path.Combine(_root,"downloads");Directory.CreateDirectory(downloads);
        foreach(var name in new[]{"one.dem","two.dem"})File.WriteAllText(Path.Combine(downloads,name),"PBDEMS2\0de_dust2 "+name);
        await using var monitor=new DemoMonitor(new SessionStore(_root),[downloads],defer:()=>false);
        await monitor.ScanOnceAsync();await Task.Delay(10_100);await monitor.ScanOnceAsync();await monitor.ScanOnceAsync();
        Assert.All(monitor.Snapshot().Jobs,j=>Assert.Equal("未匹配",j.State));
        // Add a different-map recording: no real parser is needed, but the set of local recordings changes.
        var store=new SessionStore(_root);var session=Session();session.Map="de_inferno";store.Save(session);
        await monitor.ScanOnceAsync();
        Assert.Single(monitor.Snapshot().Jobs,j=>j.State=="待分析");Assert.True(BackgroundDemoWorker.Fingerprint(_root)!.Value.Pending);
        await monitor.ScanOnceAsync();Assert.All(monitor.Snapshot().Jobs,j=>Assert.Equal("未匹配",j.State));
        Assert.False(BackgroundDemoWorker.Fingerprint(_root)!.Value.Pending);
    }
}
