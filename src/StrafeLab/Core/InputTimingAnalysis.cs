namespace StrafeLab.Core;

/// <summary>Local timing of audited in-game opposite-key/click candidates, not braking eligibility.
/// Version 4 reports already audit live self-player GSI, edge confidence and capture continuity.
/// Demo-derived speed, pose and outcome must never change this population.</summary>
public static class InputTimingAnalysis
{
    public const string Prefix="input";
    public static bool IsEligible(ActionReview a)
    {
        bool FiniteNonnegative(double? v)=>v.HasValue&&double.IsFinite(v.Value)&&v.Value>=0;
        return a.Direction is "A→D" or "D→A" or "W→S" or "S→W" && MatchAnalysis.IsGun(a.Weapon) &&
            a.TransitionUs>0 && a.ShotUs>=a.TransitionUs &&
            double.IsFinite(a.ClickMs) && a.ClickMs is >=0 and <=CounterStrafeCohort.MaxClickDelayMs &&
            Math.Abs((a.ShotUs-a.TransitionUs)/1000d-a.ClickMs)<.001 &&
            FiniteNonnegative(a.GapMs) && a.GapMs<=500 && FiniteNonnegative(a.OverlapMs) &&
            (a.GapMs==0||a.OverlapMs==0) &&
            !a.ExclusionReasons.Contains("input_history") && !a.ExclusionReasons.Contains("movement_changed");
    }
    // These are observed keyboard modifiers, not a claim about in-game stance or speed.
    public static int Modifiers(ActionReview a)=>(a.ContextTags.Contains("input_crouch")?1:0)|
        (a.ContextTags.Contains("input_walk")?2:0)|(a.ContextTags.Contains("input_jump")?4:0);
    public static string Key(ActionReview a)=>$"{Prefix}|{a.Weapon}|{Modifiers(a)}";
    public static string ModifierLabel(ActionReview a)=>Modifiers(a)==0?"未见蹲 / 慢走 / 跳键":string.Join(" + ",
        new[]{(1,"含蹲键"),(2,"含慢走键"),(4,"含跳键")}.Where(x=>(Modifiers(a)&x.Item1)!=0).Select(x=>x.Item2));
    public static bool IsKey(string? key)=>key?.StartsWith(Prefix+"|",StringComparison.Ordinal)==true;
}
