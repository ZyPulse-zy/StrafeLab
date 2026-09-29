using StrafeLab.Core;
using Xunit;

namespace StrafeLab.Tests;

public sealed class InputTimingAnalysisTests
{
    private static ActionReview Action(long at=100_000)=>new()
    {TransitionUs=at,ShotUs=at+80_000,ClickMs=80,Weapon="ak47",Direction="A→D",GapMs=12,OverlapMs=0};
    private static MatchReport Report(string id,params ActionReview[] actions)=>new()
    {SessionId=id,StartedAtUtc=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc),Actions=actions.ToList()};
    private static ProgressSnapshot Local(params MatchReport[] reports)=>ProgressAnalysis.Build(reports,InputTimingAnalysis.Key(Action()),new(),ProgressEvidence.LocalInput);

    [Fact] public void Unmatched_input_is_visible_without_entering_validated_statistics()
    {
        var report=Report("one",Action());var local=Local(report);
        Assert.Equal(1,local.Count);Assert.Equal(1,local.WithoutReliableDemo);Assert.Equal(-12,local.Handoff.Median);
        Assert.Equal(ProgressEvidence.LocalInput,local.Evidence);Assert.Empty(local.Profiles);
        Assert.Empty(ProgressAnalysis.Cohorts([report]));Assert.False(report.Actions[0].EligibleForStats);
        Assert.Contains("未配对",local.Matches[0].Scheme);
    }
    [Fact] public void Local_population_and_groups_stay_identical_when_demo_adds_low_speed_or_pose()
    {
        var a=Action();var report=Report("one",a);var before=Local(report);
        report.Alignment=new(){IsReliable=true};a.DemoContextChecked=true;a.DemoTick=12;
        a.PriorSpeed=10;a.Stance="蹲姿";a.ExclusionReasons=["initial_speed_below_threshold"];
        a.ContextTags.Add("crouch");
        var after=Local(report);
        Assert.Equal(before.Handoff,after.Handoff);Assert.Equal(before.Cohort,after.Cohort);
        Assert.Equal(0,after.WithoutReliableDemo);Assert.Empty(ProgressAnalysis.Cohorts([report]));
    }
    [Fact] public void Known_profiles_cannot_turn_local_input_into_a_parameter_comparison()
    {
        var report=Report("one",Action());var history=new KeyboardProfileHistory
        {Profiles=[new("A","Baseline",report.StartedAtUtc.AddDays(-1),.5,.85,.11,.11,"")]};
        var local=ProgressAnalysis.Build([report],InputTimingAnalysis.Key(Action()),history,ProgressEvidence.LocalInput);
        Assert.Single(local.Matches);Assert.Empty(local.Profiles);Assert.Null(local.Matches[0].SchemeId);
    }
    [Fact] public void Modifier_groups_use_only_local_keyboard_tags_and_keep_weapons_separate()
    {
        var a=Action();var b=Action(200_000);b.ContextTags=["input_crouch","input_walk"];
        var c=Action(300_000);c.Weapon="glock";
        var groups=ProgressAnalysis.Cohorts([Report("one",a,b,c)],ProgressEvidence.LocalInput);
        Assert.Equal(3,groups.Count);Assert.Contains(groups,g=>g.Label.Contains("含蹲键 + 含慢走键"));
        Assert.Equal("input|ak47|3",InputTimingAnalysis.Key(b));
    }
    [Theory]
    [InlineData("input_history")][InlineData("movement_changed")]
    public void Unreliable_local_history_is_not_rescued_by_successful_demo_matching(string reason)
    {
        var a=Action();a.ExclusionReasons=[reason];a.DemoContextChecked=true;
        var report=Report("one",a);report.Alignment=new(){IsReliable=true};Assert.Empty(Local(report).Matches);
    }
    [Fact] public void Missing_or_invalid_edges_and_unrelated_clicks_are_excluded()
    {
        var missing=Action();missing.GapMs=null;Assert.False(InputTimingAnalysis.IsEligible(missing));
        var nan=Action();nan.OverlapMs=double.NaN;Assert.False(InputTimingAnalysis.IsEligible(nan));
        var both=Action();both.OverlapMs=2;Assert.False(InputTimingAnalysis.IsEligible(both));
        var negative=Action();negative.GapMs=-1;Assert.False(InputTimingAnalysis.IsEligible(negative));
        var late=Action();late.ClickMs=501;late.ShotUs=late.TransitionUs+501_000;Assert.False(InputTimingAnalysis.IsEligible(late));
        var inconsistent=Action();inconsistent.ShotUs++;Assert.False(InputTimingAnalysis.IsEligible(inconsistent));
        var knife=Action();knife.Weapon="knife";Assert.False(InputTimingAnalysis.IsEligible(knife));
        var unrelated=Action();unrelated.Direction="A→W";Assert.False(InputTimingAnalysis.IsEligible(unrelated));
        var old=Report("one",Action());old.Version=3;Assert.Empty(Local(old).Matches);
        var zero=Action();zero.GapMs=0;Assert.True(InputTimingAnalysis.IsEligible(zero));Assert.Equal(0,Local(Report("zero",zero)).Handoff.Median);
    }
    [Fact] public void Refreshes_deduplicate_a_recording_and_a_transition_without_mutating_reports()
    {
        var first=Report("one",Action());first.UpdatedAtUtc=DateTime.UtcNow.AddMinutes(-1);
        var refreshed=Report("one",Action(),Action());refreshed.Alignment=new(){IsReliable=true};
        var local=Local(first,refreshed);
        Assert.Equal(1,local.Count);Assert.Single(local.Matches);Assert.Equal(2,refreshed.Actions.Count);
        Assert.Equal(0,local.WithoutReliableDemo);
    }
    private static SessionDocument Session()
    {
        var s=new SessionDocument{SessionId="fixture",SessionStartUs=0,PlayerSteamId="local",Map="de_dust2",EndedAtUtc=DateTime.UtcNow};
        s.GsiEvents.Add(new(){Snapshot=new(){ReceivedAtUs=0,MapPhase="live",RoundPhase="live",Round=0,ProviderSteamId="local",PlayerSteamId="local",PlayerActivity="playing",IsAlive=true}});
        s.Transitions.Add(new(){TimestampUs=100_000,From=InputControl.A,To=InputControl.D,GapUs=10_000,OverlapUs=0,Confidence=.9});
        s.Shots.Add(new(){TimestampUs=180_000,RelatedTransitionUs=100_000,Confidence=.9,WeaponName="weapon_ak47",Round=0});
        foreach(var (key,action,time) in new[]{(InputControl.A,InputAction.Down,10_000L),(InputControl.A,InputAction.Up,90_000L),
            (InputControl.D,InputAction.Down,100_000L),(InputControl.Mouse1,InputAction.Down,180_000L),(InputControl.D,InputAction.Up,250_000L)})
            s.InputEvents.Add(new(){Control=key,Action=action,TimestampUs=time,Confidence=.9});
        for(long t=90_000;t<=250_000;t+=10_000)s.PhysicsSamples.Add(new(){TimestampUs=t});
        return s;
    }
    [Fact] public void Existing_report_audit_accepts_complete_in_game_input_before_demo_arrives()
    {
        var s=Session();var before=MatchAnalysis.Build(s);Assert.Equal(1,Local(before).Count);
        var demo=new DemoParseResult{IsSource2=true,TickRate=64,Observations=[new(){EventName="weapon_fire",SteamId="local",Weapon="ak47",Round=0,TimeSeconds=.18,Tick=12}]};
        var after=MatchAnalysis.Build(s,demo,new(){IsReliable=true,Scale=1,StartLocalSeconds=0,EndLocalSeconds=1});
        Assert.Equal(Local(before).Handoff,Local(after).Handoff);Assert.Empty(ProgressAnalysis.Cohorts([after]));
    }
    [Fact] public void Capture_diagnostics_spectating_low_confidence_and_missing_edges_never_enter_local_trends()
    {
        var s=Session();s.DiagnosticMode=true;Assert.Equal(0,Local(MatchAnalysis.Build(s)).Count);
        s=Session();s.GsiEvents[0]=new(){Snapshot=new(){ReceivedAtUs=0,MapPhase="live",RoundPhase="live",Round=0,ProviderSteamId="local",PlayerSteamId="other",PlayerActivity="playing",IsAlive=true}};
        Assert.Equal(0,Local(MatchAnalysis.Build(s)).Count);
        s=Session();s.InputEvents[0]=s.InputEvents[0] with{Confidence=.2};Assert.Equal(0,Local(MatchAnalysis.Build(s)).Count);
        s=Session();s.InputEvents.RemoveAt(1);Assert.Equal(0,Local(MatchAnalysis.Build(s)).Count);
        s=Session();s.PhysicsSamples.Clear();Assert.Equal(0,Local(MatchAnalysis.Build(s)).Count);
        s=Session();s.InputEvents.Add(new(){Control=InputControl.W,Action=InputAction.Down,TimestampUs=150_000,Confidence=.9});
        Assert.Equal(0,Local(MatchAnalysis.Build(s)).Count);
    }
}
