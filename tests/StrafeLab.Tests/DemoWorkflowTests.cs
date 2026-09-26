using System.IO.Compression;
using StrafeLab.Core;
using Xunit;
namespace StrafeLab.Tests;

public sealed class DemoWorkflowTests
{
    private static string Temp()=>Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"StrafeLabTest-"+Guid.NewGuid().ToString("N"))).FullName;
    [Fact] public void Changed_download_restarts_settle_timer_and_invalidates_previous_result()
    {
        var root=Temp();try
        {
            var path=Path.Combine(root,"match.dem");File.WriteAllBytes(path,new byte[4096]);
            var now=DateTime.UtcNow.AddMinutes(1);var job=new DemoJob{SessionStamp="old",State="已完成"};
            Assert.False(DemoLibrary.Observe(job,new FileInfo(path),now,TimeSpan.FromSeconds(10)));
            Assert.Empty(job.SessionStamp);
            Assert.False(DemoLibrary.Observe(job,new FileInfo(path),now.AddSeconds(9),TimeSpan.FromSeconds(10)));
            Assert.True(DemoLibrary.Observe(job,new FileInfo(path),now.AddSeconds(11),TimeSpan.FromSeconds(10)));
            File.AppendAllText(path,"more");
            Assert.False(DemoLibrary.Observe(job,new FileInfo(path),now.AddSeconds(12),TimeSpan.FromSeconds(10)));
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Archive_paths_cannot_escape_cache_and_non_demos_are_not_extracted()
    {
        var root=Temp();try
        {
            var archive=Path.Combine(root,"demo.zip");
            using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))
            {
                using(var stream=zip.CreateEntry("../../../escaped.dem").Open())stream.Write("PBDEMS2\0payload"u8);
                using(var stream=zip.CreateEntry("evil.exe").Open())stream.WriteByte(1);
            }
            var library=new DemoLibrary(root,[]);var files=await library.MaterializeAsync(archive,CancellationToken.None);
            Assert.Single(files);Assert.StartsWith(library.CachePath+Path.DirectorySeparatorChar,files[0]);
            Assert.Equal("PBDEMS2\0payload",File.ReadAllText(files[0]));Assert.Empty(Directory.GetFiles(root,"*.exe",SearchOption.AllDirectories));
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public void Queue_recovers_running_job_without_losing_roots_or_completed_jobs()
    {
        var root=Temp();try
        {
            var library=new DemoLibrary(root,["D:\\custom"]);
            library.State.Jobs.Add(new(){Path="one.dem",State="分析中",SessionStamp="x"});
            library.State.Jobs.Add(new(){Path="two.dem",State="已完成",SessionStamp="y"});library.Save();
            var restarted=new DemoLibrary(root,[]);
            Assert.Equal("D:\\custom",restarted.State.Roots.Single());
            Assert.Equal("待分析",restarted.State.Jobs[0].State);Assert.Empty(restarted.State.Jobs[0].SessionStamp);
            Assert.Equal("y",restarted.State.Jobs[1].SessionStamp);
        }finally{Directory.Delete(root,true);}
    }
    [Theory][InlineData("demo.dem",true)][InlineData("demo.zip",true)][InlineData("demo.dem.bz2",true)]
    [InlineData("demo.dem.info",false)][InlineData("demo.zip.crdownload",false)][InlineData("demo.dem.tmp",false)]
    public void Discovery_excludes_sidecars_and_browser_partial_downloads(string path,bool supported)=>Assert.Equal(supported,DemoLibrary.Supported(path));
    [Fact] public void Reset_does_not_close_previous_hold_or_attach_new_click_to_old_transition()
    {
        var list=new List<StrafeTransition>();var analyzer=new StrafeAnalyzer(list);
        void Edge(InputControl c,InputAction a,long t)=>analyzer.Process(new(){Control=c,Action=a,TimestampUs=t},null,null);
        Edge(InputControl.A,InputAction.Down,0);Edge(InputControl.D,InputAction.Down,10000);analyzer.Reset();
        var shot=analyzer.Process(new(){Control=InputControl.Mouse1,Action=InputAction.Down,TimestampUs=20000},null,null).Shot;
        Edge(InputControl.D,InputAction.Down,30000);Edge(InputControl.D,InputAction.Up,40000);
        Assert.Null(shot!.RelatedTransitionUs);Assert.Null(list.Single().ReverseHoldUs);
    }
    [Fact] public void No_model_samples_is_unknown_not_zero_percent()=>Assert.Null(TrendAnalyzer.Summarize(new()).FireWindowRate);
    [Fact] public async Task Two_windows_share_reports_but_only_one_can_write_the_queue()
    {
        var root=Temp();try
        {
            await using(var first=new DemoMonitor(new SessionStore(root),[]))
            {
                await first.ScanOnceAsync();first.AddRoot(Path.Combine(root,"downloads"));await first.ScanOnceAsync();
                await using var second=new DemoMonitor(new SessionStore(root),["different"]);
                await second.ScanOnceAsync();Assert.True(second.IsReadOnly);Assert.False(first.IsReadOnly);
                Assert.Equal(first.Snapshot().Roots,second.Snapshot().Roots);
            }
            await using var restarted=new DemoMonitor(new SessionStore(root),[]);
            await restarted.ScanOnceAsync();Assert.False(restarted.IsReadOnly);Assert.Single(restarted.Snapshot().Roots);
        }finally{Directory.Delete(root,true);}
    }
    private static SessionDocument Session()
    {
        var s=new SessionDocument{SessionStartUs=0,PlayerSteamId="local",Map="de_dust2",EndedAtUtc=DateTime.UtcNow};
        s.GsiEvents.Add(new(){Snapshot=new(){ReceivedAtUs=0,MapPhase="live",RoundPhase="live",Round=0,ProviderSteamId="local",PlayerSteamId="local",PlayerActivity="playing",IsAlive=true}});
        s.Transitions.Add(new(){TimestampUs=100000,From=InputControl.A,To=InputControl.D,GapUs=10000,OverlapUs=0,Confidence=.9});
        s.Shots.Add(new(){TimestampUs=200000,RelatedTransitionUs=100000,Confidence=.9,WeaponName="weapon_ak47",Round=0});
        foreach(var (c,a,t) in new[]{(InputControl.A,InputAction.Up,90000L),(InputControl.D,InputAction.Down,100000L),(InputControl.Mouse1,InputAction.Down,200000L),(InputControl.D,InputAction.Up,250000L)})
            s.InputEvents.Add(new(){Control=c,Action=a,TimestampUs=t,Confidence=.9});
        for(long t=100000;t<=250000;t+=10000)s.PhysicsSamples.Add(new(){TimestampUs=t});
        return s;
    }
    [Fact] public void Review_uses_actual_fire_identity_weapon_round_and_three_speed_intervals()
    {
        var s=Session();var d=new DemoParseResult{IsSource2=true};
        d.Observations.Add(new(){EventName="weapon_fire",SteamId="other",Weapon="ak47",Round=0,TimeSeconds=.2,Tick=12});
        var alignment=new AlignmentResult{IsReliable=true,StartLocalSeconds=0,EndLocalSeconds=1};
        Assert.Null(MatchAnalysis.Build(s,d,alignment).Actions.Single().DemoTick);
        d.Observations.Add(new(){EventName="weapon_fire",SteamId="local",Weapon="ak47",Round=0,TimeSeconds=.2,Tick=12});
        foreach(int tick in new[]{11,12,13})d.Observations.Add(new(){Kind="player_sample",SteamId="local",Tick=tick,IsAlive=true,OnGround=true,MoveType=2,Round=0,VelocityX=tick==13?35:0,VelocityY=0,VelocityZ=0});
        var row=MatchAnalysis.Build(s,d,alignment).Actions.Single();
        Assert.Equal("边界",row.Verdict);Assert.Equal(150,row.HoldMs);Assert.Equal(10,row.GapMs);Assert.Equal(12,row.DemoTick);
        // A second plausible bullet is ambiguous; never select whichever happens to be nearest.
        d.Observations.Add(new(){EventName="weapon_fire",SteamId="local",Weapon="ak47",Round=0,TimeSeconds=.21,Tick=13});
        Assert.Null(MatchAnalysis.Build(s,d,alignment).Actions.Single().DemoTick);
    }
    [Fact] public void Review_excludes_warmup_knife_and_recording_gaps()
    {
        var s=Session();s.GsiEvents.Clear();Assert.Empty(MatchAnalysis.Build(s).Actions);
        s=Session();s.PhysicsSamples.RemoveAll(p=>p.TimestampUs>100000&&p.TimestampUs<250000);Assert.Empty(MatchAnalysis.Build(s).Actions);
        Assert.False(MatchAnalysis.IsGun("weapon_knife"));Assert.False(MatchAnalysis.IsGun("weapon_hegrenade"));
    }
    [Fact] public void Unassociated_gun_click_is_counted_separately_and_cannot_change_actions()
    {
        var s=Session();var before=MatchAnalysis.Build(s);
        s.Shots.Add(new(){TimestampUs=300000,Confidence=.9,WeaponName="weapon_ak47",Round=0});
        var after=MatchAnalysis.Build(s);
        Assert.Equal(before.Count,after.Count);Assert.Equal(1,after.UnassociatedShotCount);
        Assert.Equal(0,after.EligibleCount);Assert.Empty(CounterStrafeCohort.Eligible(after.Actions));
    }
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)]
    public void New_cohort_version_hides_old_reports_and_requeues_attempted_jobs(int oldVersion)
    {
        var root=Temp();try
        {
            var library=new DemoLibrary(root,[]);library.State.Version=oldVersion;
            library.State.Jobs.Add(new(){Path="old.dem",State="已完成",SessionStamp="old",AttemptedSessions=new(){{"hash:id","old"}}});
            library.SaveReport(new(){Version=oldVersion,SessionId="old"});library.Save();
            var upgraded=new DemoLibrary(root,[]);
            Assert.Equal(MatchReport.CurrentVersion,upgraded.State.Version);Assert.Empty(upgraded.ReadReports());
            Assert.Empty(upgraded.State.Jobs[0].AttemptedSessions);Assert.Empty(upgraded.State.Jobs[0].SessionStamp);
            Assert.Equal("待分析",upgraded.State.Jobs[0].State);
        }finally{Directory.Delete(root,true);}
    }
}
