using StrafeLab.Core;
using Xunit;

namespace StrafeLab.Tests;
public sealed class DirectionTrendTests
{
    private static ActionReview Action(string direction,long time,double overlap)=>new(){TransitionUs=time,ShotUs=time+80_000,ClickMs=80,
        Direction=direction,Weapon="ak47",Stance="站姿",PriorSpeed=180,OverlapMs=overlap,GapMs=0,DemoContextChecked=true,ExclusionReasons=[]};
    private static readonly DateTime At=new(2026,9,1,0,0,0,DateTimeKind.Utc);
    private static MatchReport Report()=>new(){SessionId="one",StartedAtUtc=At,Alignment=new(){IsReliable=true},Actions=[
        Action("A→D",100_000,10),Action("A→D",200_000,20),Action("D→A",300_000,100)]};
    [Theory] [InlineData(ProgressEvidence.LocalInput)] [InlineData(ProgressEvidence.DemoValidated)]
    public void Direction_scopes_counts_matches_distributions_and_cohorts_consistently(ProgressEvidence evidence)
    {
        var report=Report();var group=Assert.Single(ProgressAnalysis.Cohorts([report],evidence,"A→D"));Assert.Equal(2,group.Actions);
        var snapshot=ProgressAnalysis.Build([report],group.Key,new(),evidence,"A→D");
        Assert.Equal("A→D",snapshot.Direction);Assert.Equal(2,snapshot.Count);Assert.Equal(15,snapshot.Handoff.Median);
        Assert.Equal(2,Assert.Single(snapshot.Matches).Count);Assert.Equal("A→D",Assert.Single(snapshot.Directions).Direction);
        Assert.Empty(ProgressAnalysis.Cohorts([report],evidence,"S→W"));
        Assert.Equal(0,ProgressAnalysis.Build([report],group.Key,new(),evidence,"S→W").Count);
    }
    [Fact] public void Profile_comparisons_use_the_same_direction_and_strict_evidence_gates()
    {
        var report=Report();var low=Action("A→D",400_000,1000);low.PriorSpeed=33.99;report.Actions.Add(low);
        var history=new KeyboardProfileHistory{Profiles=[new("A","Baseline",At.AddDays(-1),.5,.85,.11,.11,"")]};
        var snapshot=ProgressAnalysis.Build([report],ProgressAnalysis.Key(report.Actions[0]),history,ProgressEvidence.DemoValidated,"A→D");
        Assert.Equal(2,snapshot.Count);var profile=Assert.Single(snapshot.Profiles);Assert.Equal(2,profile.Count);Assert.Equal(15,profile.Handoff.Median);
        var local=ProgressAnalysis.Build([report],InputTimingAnalysis.Key(report.Actions[0]),history,ProgressEvidence.LocalInput,"A→D");
        Assert.Equal(3,local.Count);Assert.Empty(local.Profiles);
    }
    [Fact] public void Invalid_direction_cannot_silently_fall_back_to_all_data()
    {
        Assert.Throws<ArgumentException>(()=>ProgressAnalysis.Cohorts([Report()],direction:"A→W"));
        Assert.Throws<ArgumentException>(()=>ProgressAnalysis.Build([Report()],null,new(),direction:"unknown"));
        Assert.Equal(3,Assert.Single(ProgressAnalysis.Cohorts([Report()])).Actions);
    }
}
