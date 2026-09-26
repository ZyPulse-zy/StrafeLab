using StrafeLab.Core;
using Xunit;

namespace StrafeLab.Tests;
public sealed class CounterStrafeCohortTests
{
    [Theory][InlineData("AK-47","ak47")][InlineData("M4A4","m4a1")][InlineData("M4A1-S","m4a1_silencer")]
    [InlineData("USP-S","usp_silencer")][InlineData("Galil AR","galilar")][InlineData("SG 553","sg556")]
    [InlineData("Desert Eagle","deagle")][InlineData("Dual Berettas","elite")][InlineData("Glock-18","glock")]
    [InlineData("weapon_m4a1_silencer","m4a1_silencer")][InlineData("MAC-10","mac10")][InlineData("CZ75-Auto","cz75a")]
    public void Display_names_and_gsi_schema_names_identify_the_same_weapon(string input,string expected)
        =>Assert.Equal(expected,MatchAnalysis.Weapon(input));
    private static ActionReview Row(string weapon="ak47",double click=100)=>new()
    {Weapon=weapon,Direction="A→D",Round=0,TransitionUs=1_000_000,ShotUs=1_000_000+(long)(click*1000),ClickMs=click};
    private static void Input(ActionReview row,bool crouch=false,bool walk=false,bool missing=false,bool changed=false,bool shortTap=false)
    {
        var edges=new List<InputEvent>();
        void Edge(InputControl key,InputAction action,long time)=>edges.Add(new(){Control=key,Action=action,TimestampUs=time,Confidence=.9});
        if(!missing)Edge(InputControl.A,InputAction.Down,shortTap?980_000:700_000);
        Edge(InputControl.A,InputAction.Up,990_000);Edge(InputControl.D,InputAction.Down,1_000_000);
        if(changed)Edge(InputControl.A,InputAction.Down,1_050_000);
        CounterStrafeCohort.CheckInput(row,new(){From=InputControl.A,To=InputControl.D},edges.ToArray(),
            [new(){TimestampUs=950_000,Crouch=crouch,Walk=walk,A=true},new(){TimestampUs=row.ShotUs,D=true}]);
    }
    private static void Demo(ActionReview row,double prior=180,double shot=0,double duck=0,int? duckAt=null,bool walking=false,bool scoped=false,
        bool damaged=false,int? missingTick=null,bool unknownDuck=false,bool badDirection=false,bool priorBoundary=false,
        bool ground=true,double vertical=0,double forward=0,double? lastPriorSpeed=null)
    {
        // 100 Hz: transition tick 100; strict pre-input samples 97,98,99.
        int fire=100+(int)Math.Round(row.ClickMs/10);
        var data=new Dictionary<int,DemoObservation>();
        for(int t=70;t<=fire+2;t++)
        {
            if(t==missingTick)continue;
            data[t]=new(){Kind="player_sample",Tick=t,TimeSeconds=t/100d,Round=0,Weapon=row.Weapon,
                IsAlive=true,OnGround=ground,MoveType=2,VelocityZ=vertical,VelocityX=badDirection&&t<100?prior:forward,
                VelocityY=t<100?(t==99&&lastPriorSpeed.HasValue?lastPriorSpeed.Value:priorBoundary&&t==99?10:badDirection?0:prior):shot,
                DuckAmount=unknownDuck?null:(duckAt==null||duckAt==t?duck:0),IsWalking=walking,IsScoped=scoped,
                VelocityModifier=damaged?.7:1,Yaw=0};
        }
        CounterStrafeCohort.CheckDemo(row,new(){Tick=fire,TimeSeconds=fire/100d},data,100,1);
    }
    [Theory][InlineData(0,"低速")][InlineData(190,"仍在移动")]
    public void Slowing_and_still_fast_attempts_are_both_evaluated(double speed,string verdict)
    {
        var r=Row();Input(r);Demo(r,shot:speed);r.Verdict=verdict;
        Assert.True(r.EligibleForStats,string.Join(",",r.ExclusionReasons));
        Assert.Equal(180-speed,r.AxisSpeedDrop);
    }
    [Theory][InlineData(.03,91)][InlineData(1,110)][InlineData(.4,111)]
    public void Duck_transition_before_during_or_after_click_is_separate(double duck,int tick)
    {
        var r=Row();Input(r);Demo(r,duck:duck,duckAt:tick);
        Assert.True(r.EligibleForStats);Assert.Contains("crouch",r.ContextTags);Assert.Equal("蹲起变化",r.Stance);
    }
    [Fact] public void Local_crouch_key_is_context_not_a_reason_to_exclude_or_override_demo_stance()
    {var r=Row();Input(r,crouch:true);Demo(r);Assert.Contains("input_crouch",r.ContextTags);Assert.True(r.EligibleForStats);Assert.Equal("站姿",r.Stance);}
    [Theory][InlineData("mp9")][InlineData("mac10")][InlineData("nova")][InlineData("glock")][InlineData("awp")]
    public void All_guns_can_have_attempts_but_are_compared_in_separate_groups(string weapon)
    {var r=Row(weapon);Input(r);Demo(r);Assert.True(r.EligibleForStats);Assert.Equal(weapon,CounterStrafeCohort.Groups([r]).Single().Weapon);}
    [Fact] public void Already_stopped_before_press_is_not_a_successful_counterstrafe()
    {var r=Row();Input(r);Demo(r,prior:0,shot:10);Assert.Contains("stationary",r.ExclusionReasons);Assert.Empty(CounterStrafeCohort.Eligible([r]));Assert.Null(r.AxisSpeedDrop);}
    [Fact] public void Initial_speed_gate_uses_three_tick_median_not_minimum_or_maximum()
    {
        var r=Row();Input(r);Demo(r,priorBoundary:true);
        Assert.Equal(10,r.PriorMinSpeed);Assert.Equal(180,r.PriorSpeed);Assert.True(r.EligibleForStats);
        r=Row();Input(r);Demo(r,prior:20,lastPriorSpeed:180);
        Assert.Equal(180,r.PriorMaxSpeed);Assert.Equal(20,r.PriorSpeed);Assert.False(r.EligibleForStats);
        Assert.Contains("initial_speed_below_threshold",r.ExclusionReasons);
    }
    [Theory][InlineData(1,false)][InlineData(5,false)][InlineData(20,false)][InlineData(33.999,false)]
    [InlineData(34,true)][InlineData(34.001,true)][InlineData(80,true)]
    public void Initial_speed_boundary_applies_equally_to_standing_crouch_transition_and_walk(double speed,bool eligible)
    {
        foreach(var stance in new[]{"站姿","蹲姿","蹲起变化","慢走"})
        {
            var r=Row();Input(r);Demo(r,prior:speed,duck:stance=="蹲姿"?1:stance=="蹲起变化"?.4:0,walking:stance=="慢走");
            Assert.Equal(stance,r.Stance);Assert.Equal(speed,r.PriorSpeed!.Value,8);Assert.Equal(eligible,r.EligibleForStats);
            Assert.Equal(!eligible,r.ExclusionReasons.Contains("initial_speed_below_threshold"));
            Assert.Equal(eligible?1:0,CounterStrafeCohort.Groups([r]).Count);
        }
    }
    [Theory][InlineData(true,false,false)][InlineData(false,true,false)][InlineData(false,false,true)]
    public void Walking_scope_and_damage_are_context_not_automatic_failure(bool walk,bool scope,bool damage)
    {var r=Row();Input(r);Demo(r,walking:walk,scoped:scope,damaged:damage);Assert.True(r.EligibleForStats);Assert.Contains(walk?"walk":scope?"scoped":"damaged",r.ContextTags);}
    [Fact] public void Missing_motion_fails_closed_but_unknown_pose_has_its_own_group()
    {
        var r=Row();Input(r);Demo(r,missingTick:95);Assert.Contains("context_missing",r.ExclusionReasons);
        r=Row();Input(r);Demo(r,unknownDuck:true);Assert.True(r.EligibleForStats);Assert.Equal("姿态未知",r.Stance);Assert.Contains("pose_unknown",r.ContextTags);
        r=Row();Input(r);Assert.False(r.EligibleForStats);Assert.Contains("demo_missing",r.ExclusionReasons);
    }
    [Fact] public void Late_click_remains_but_broken_history_or_different_direction_cannot_be_attributed()
    {
        var r=Row(click:300);Input(r);Demo(r);Assert.Contains("late_click",r.ContextTags);Assert.True(r.EligibleForStats);
        r=Row();Input(r,changed:true);Demo(r);Assert.Contains("movement_changed",r.ExclusionReasons);
        r=Row();Input(r,missing:true);Demo(r);Assert.Contains("input_history",r.ExclusionReasons);
        r=Row();Input(r);Demo(r,badDirection:true);Assert.Contains("direction",r.ExclusionReasons);
    }
    [Fact] public void Grouping_preserves_crouch_other_guns_and_slow_attempts_without_mixing_them()
    {
        var good=Row();Input(good);Demo(good);good.Verdict="低速";
        var bad=Row();Input(bad);Demo(bad,shot:190);bad.Verdict="仍在移动";
        var duck=Row();Input(duck);Demo(duck,duck:1);duck.Verdict="低速";
        var smg=Row("mp9");Input(smg);Demo(smg);smg.Verdict="低速";
        var groups=CounterStrafeCohort.Groups([good,bad,duck,smg]);Assert.Equal(3,groups.Count);
        Assert.Equal(4,groups.Sum(g=>g.Count));Assert.Equal(2,groups.Single(g=>g.Weapon=="ak47"&&g.Stance=="站姿").Count);
        var slow=Row();Input(slow);Demo(slow,prior:20);
        Assert.Equal(groups.ToArray(),CounterStrafeCohort.Groups([good,bad,duck,smg,slow]).ToArray());
        Assert.Equal(4,CounterStrafeCohort.Eligible([good,bad,duck,smg,slow]).Count);
        var qualifiedSlow=Row();Input(qualifiedSlow);Demo(qualifiedSlow,prior:40);
        Assert.Equal(4,CounterStrafeCohort.Groups([good,bad,duck,smg,qualifiedSlow]).Count);
    }
    [Fact] public void Short_tap_diagonal_motion_and_grounded_ramp_are_supported()
    {
        var r=Row();Input(r,shortTap:true);Demo(r,prior:20,forward:150,vertical:30);
        Assert.True(r.EligibleForStats);Assert.Equal(20,r.AxisSpeedBefore);Assert.Equal(20,r.AxisSpeedDrop);
        r=Row();Input(r);Demo(r,ground:false);Assert.False(r.EligibleForStats);Assert.Contains("airborne",r.ExclusionReasons);
    }
    [Fact] public void Unduck_does_not_instantly_restore_model_confidence()
    {
        var sim=new MovementSimulator(new());
        var gsi=new GsiSnapshot{ReceivedAtUs=0,PlayerSteamId="self",ProviderSteamId="self",PlayerActivity="playing",IsAlive=true,WeaponName="weapon_ak47"};
        sim.AdvanceTo(0,new(){Crouch=true},gsi);
        Assert.True(sim.AdvanceTo(100_000,new(),gsi).Confidence<.75);
        for(long t=200_000;t<=900_000;t+=100_000)Assert.True(sim.AdvanceTo(t,new(),gsi).Confidence<.75);
        Assert.True(sim.AdvanceTo(1_200_000,new(),gsi).Confidence>=.75);
    }
}
