using StrafeLab.Core;
using Xunit;
namespace StrafeLab.Tests;
public sealed class ProgressAnalysisTests
{
    private static readonly DateTime Epoch=new(2026,1,1,0,0,0,DateTimeKind.Utc);
    private static ActionReview Action(double overlap=10,double gap=0,string weapon="ak47",string stance="站姿",double speed=180)=>new()
    {Weapon=weapon,Stance=stance,Direction="A→D",PriorSpeed=speed,ClickMs=80,OverlapMs=overlap,GapMs=gap,DemoContextChecked=true,ExclusionReasons=[]};
    private static MatchReport Report(int day,params ActionReview[] rows)=>new()
    {SessionId="match"+day,StartedAtUtc=Epoch.AddDays(day),Alignment=new(){IsReliable=true},Actions=rows.ToList()};
    private static KeyboardProfile Profile(string id,int day)=>new(id,id,Epoch.AddDays(day),.5,.85,.11,.11,"");
    [Fact] public void Trends_separate_weapon_stance_and_speed_and_keep_the_34_gate()
    {
        var invalid=Action();invalid.ExclusionReasons.Add("context_missing");
        var report=Report(1,Action(),Action(weapon:"awp"),Action(stance:"慢走"),Action(speed:40),Action(speed:33.999),invalid);
        var groups=ProgressAnalysis.Cohorts([report]);Assert.Equal(4,groups.Count);Assert.Equal(4,groups.Sum(g=>g.Actions));
        var snapshot=ProgressAnalysis.Build([report],ProgressAnalysis.Key(report.Actions[0]),new());
        Assert.Equal(1,snapshot.Count);Assert.Equal(1,snapshot.UnknownProfileActions);
        var old=Report(2,Action());old.Version=1;var pending=Report(3,Action());pending.Alignment=null;
        Assert.Empty(ProgressAnalysis.Cohorts([old,pending]));
    }
    [Fact] public void Typical_ranges_and_handoff_do_not_replace_missing_edges_with_zero()
    {
        var d=ProgressAnalysis.Distribution([0,10,20,30,null,double.NaN]);
        Assert.Equal(4,d.Count);Assert.Equal(15,d.Median);Assert.Equal(7.5,d.Q25);Assert.Equal(22.5,d.Q75);
        Assert.Equal(-12,ProgressAnalysis.Handoff(Action(overlap:0,gap:12)));
        Assert.Equal(12,ProgressAnalysis.Handoff(Action(overlap:12)));
        var a=Action();a.GapMs=null;Assert.Null(ProgressAnalysis.Handoff(a));
        Assert.Null(ProgressAnalysis.Distribution([]).Spread);
    }
    [Fact] public void Recording_a_profile_never_retroactively_claims_old_matches()
    {
        var history=new KeyboardProfileHistory{Profiles=[Profile("A",2),Profile("B",4)]};
        Assert.Null(history.For(Report(1)));Assert.Equal("A",history.For(Report(3))?.Id);
        Assert.Equal("B",history.For(Report(5))?.Id);
        history.SessionAssignments["match1"]="A";Assert.Equal("A",history.For(Report(1))?.Id);
        history.SessionAssignments["match5"]="";Assert.Null(history.For(Report(5)));
    }
    [Fact] public void Handoff_order_distribution_preserves_missing_and_exact_timestamp_edges()
    {
        var unknown=Action();unknown.GapMs=null;
        var report=Report(1,Action(10),Action(0,7),Action(0,0),unknown,Action(20,speed:33.9));
        var s=ProgressAnalysis.Build([report],ProgressAnalysis.Key(Action()),new());
        Assert.Equal(4,s.Count);Assert.Equal(1,s.ReverseFirst);Assert.Equal(1,s.ReleaseFirst);Assert.Equal(1,s.SameTimestamp);Assert.Equal(1,s.UnknownHandoff);
    }
    [Fact] public void Known_mid_match_profile_change_excludes_automatic_scheme_attribution()
    {
        var a=Action();a.LocalTime=Epoch.AddDays(3).AddMinutes(20);
        var history=new KeyboardProfileHistory{Profiles=[Profile("A",1),Profile("B",3) with{AppliedAtUtc=Epoch.AddDays(3).AddMinutes(10)}]};
        Assert.Null(history.For(Report(3,a)));
    }
    [Fact] public void Profile_comparison_excludes_unknown_history_but_keeps_it_in_progress()
    {
        var reports=new[]{Report(1,Action()),Report(3,Action()),Report(5,Action())};
        var history=new KeyboardProfileHistory{Profiles=[Profile("A",2),Profile("B",4)]};
        var s=ProgressAnalysis.Build(reports,ProgressAnalysis.Key(Action()),history);
        Assert.Equal(3,s.Count);Assert.Equal(1,s.UnknownProfileActions);Assert.Equal(2,s.Profiles.Count);
        Assert.Contains("先别",ProgressAnalysis.Compare(s.Profiles[0],s.Profiles[1]));
        Assert.False(s.Ready);
    }
    [Fact] public void Trend_requires_two_matches_with_enough_timing_edges()
    {
        var a=Report(1,Enumerable.Range(0,5).Select(i=>Action(i*10)).ToArray());
        var b=Report(2,Enumerable.Range(0,5).Select(i=>Action(i*2)).ToArray());
        var s=ProgressAnalysis.Build([a,b],ProgressAnalysis.Key(Action()),new());
        Assert.Contains("缩小 16 ms",ProgressAnalysis.Trend(s));Assert.Contains("不能说明",ProgressAnalysis.Trend(s));
        b.Actions.RemoveAt(0);s=ProgressAnalysis.Build([a,b],ProgressAnalysis.Key(Action()),new());
        Assert.Contains("至少需要两局",ProgressAnalysis.Trend(s));
    }
    [Fact] public void Ready_comparisons_report_observed_changes_without_declaring_best_RT()
    {
        var before=new ProfileComparison("A","A",30,3,"",new(30,20,10,30),new(30,0,0,0),new(30,80,60,100),new(30,20,10,30));
        var after=before with{Id="B",Name="B",Handoff=new(30,10,5,15)};
        var text=ProgressAnalysis.Compare(before,after);Assert.Contains("小 10 ms",text);Assert.Contains("不据此宣布最佳 RT",text);
    }
    [Fact] public void Long_trend_uses_disjoint_equal_match_weight_windows_and_resists_one_outlier()
    {
        var reports=Enumerable.Range(0,6).Select(i=>Report(i,Enumerable.Range(0,5).Select(j=>Action(j*(i<3?10:i==5?500:2))).ToArray())).ToArray();
        var s=ProgressAnalysis.Build(reports,ProgressAnalysis.Key(Action()),new());
        var text=ProgressAnalysis.Trend(s);
        Assert.Contains("较早 3 局",text);Assert.Contains("最近 3 局",text);Assert.Contains("20 → 4 ms",text);Assert.Contains("两组不重叠",text);
    }
    [Fact] public void Profile_changes_persist_and_backup_without_modifying_sessions()
    {
        var root=Path.Combine(Path.GetTempPath(),"StrafeLabProfileTest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var store=new KeyboardProfileStore(root);Assert.Empty(store.Load().Profiles);
            Assert.Throws<ArgumentException>(()=>store.Add("A",double.NaN,.85,.11,.11,"",Epoch));
            var a=store.Add("A",.5,.85,.11,.11,"baseline",Epoch);
            store.Assign("match1",a.Id);Assert.Equal(a.Id,new KeyboardProfileStore(root).Load().For(Report(1))?.Id);
            store.Assign("match1",null);Assert.Null(store.Load().For(Report(1)));
            Assert.True(File.Exists(Path.Combine(root,"keyboard-profiles.json.bak")));
            File.WriteAllText(Path.Combine(root,"keyboard-profiles.json"),"broken");
            Assert.ThrowsAny<Exception>(()=>store.Add("B",.5,.85,.1,.1,"",Epoch));
            Assert.Equal("broken",File.ReadAllText(Path.Combine(root,"keyboard-profiles.json")));
        }
        finally{Directory.Delete(root,true);}
    }
}
