namespace StrafeLab.Core;

/// <summary>Qualify observable opposite-input attempts independently of stance, gun,
/// outcome. Initial speed must meet the analysis floor; stance is grouping context.</summary>
public static class CounterStrafeCohort
{
    public const double LowSpeed = 34; // Descriptive legacy speed marker, never eligibility/accuracy.
    public const double MinimumInitialSpeed = 34; // User-selected statistics floor, not a universal accuracy threshold.
    public const double MotionNoise = .5; // Near-zero position-difference tolerance, not a firing threshold.
    public const double MaxClickDelayMs = 500;
    public static string WeaponGroup(string? weapon) => MatchAnalysis.Weapon(weapon) switch
    {
        "ak47" or "m4a1" or "m4a1_silencer" or "famas" or "galilar" or "aug" or "sg556" => "步枪",
        "awp" or "ssg08" or "scar20" or "g3sg1" => "狙击枪",
        "mac10" or "mp9" or "mp7" or "mp5sd" or "ump45" or "p90" or "bizon" => "冲锋枪",
        "nova" or "xm1014" or "mag7" or "sawedoff" => "霰弹枪",
        "m249" or "negev" => "机枪",
        _ => MatchAnalysis.IsGun(weapon) ? "手枪" : "其他"
    };
    public static bool IsRifle(string? weapon) => WeaponGroup(weapon)=="步枪";
    public static string ReasonText(string reason) => reason switch
    {
        "demo_missing"=>"待 Demo 验证运动", "input_crouch"=>"有蹲键输入", "input_walk"=>"有慢走键输入",
        "input_jump"=>"有跳键输入", "crouch"=>"蹲伏 / 蹲起", "walk"=>"慢走", "scoped"=>"开镜",
        "pose_unknown"=>"姿态字段不完整", "airborne"=>"非连续接地 / 特殊移动",
        "damaged"=>"有受击或速度修正，减速不能单独归因于按键", "modifier_unknown"=>"速度修正字段未知",
        "stationary"=>"反向前近静止，缺少制动证据（不等于成功）",
        "initial_speed_below_threshold"=>"反向前初速中位数低于 34 u/s",
        "context_missing"=>"连续运动 / 武器证据不足", "input_history"=>"原方向输入边沿不完整",
        "late_click"=>"反向到开枪较久，可能已重新加速", "diagonal"=>"复合移动，评估反向轴分量",
        "movement_changed"=>"开枪前再次换向，不能归于该次反向", "direction"=>"未确认原方向运动分量",
        "turning"=>"期间转向，速度变化不能单独归因于按键", _=>reason
    };
    private static void Add(ActionReview row,string reason)
    {if(!row.ExclusionReasons.Contains(reason))row.ExclusionReasons.Add(reason);}
    private static void Tag(ActionReview row,string tag)
    {if(!row.ContextTags.Contains(tag))row.ContextTags.Add(tag);}

    public static void CheckInput(ActionReview row,StrafeTransition transition,InputEvent[] edges,PhysicsSample[] physics)
    {
        if(row.ClickMs>250)Tag(row,"late_click");
        var history=physics.Where(p=>p.TimestampUs>=row.TransitionUs-150_000&&p.TimestampUs<=row.ShotUs).ToArray();
        if(history.Any(p=>p.Crouch))Tag(row,"input_crouch");
        if(history.Any(p=>p.Walk))Tag(row,"input_walk");
        if(history.Any(p=>p.Jump))Tag(row,"input_jump");
        bool lateral=transition.To is InputControl.A or InputControl.D;
        if(history.Any(p=>lateral?(p.W||p.S):(p.A||p.D)))Tag(row,"diagonal");
        // Short taps are valid. Need credible edges, not an arbitrary minimum dwell time.
        var oldDown=edges.LastOrDefault(e=>e.Control==transition.From&&e.Action==InputAction.Down&&e.TimestampUs<row.TransitionUs);
        var oldUp=oldDown==null?null:edges.FirstOrDefault(e=>e.Control==transition.From&&e.Action==InputAction.Up&&e.TimestampUs>oldDown.TimestampUs);
        if(oldDown==null||oldDown.Confidence<.75||oldUp?.Confidence<.75||
            Math.Min(oldUp?.TimestampUs??row.TransitionUs,row.TransitionUs)<=oldDown.TimestampUs)Add(row,"input_history");
        if(edges.Any(e=>e.TimestampUs>row.TransitionUs&&e.TimestampUs<row.ShotUs&&e.Action==InputAction.Down&&
            e.Control is InputControl.W or InputControl.A or InputControl.S or InputControl.D))Add(row,"movement_changed");
    }

    public static void CheckDemo(ActionReview row,DemoObservation fire,IReadOnlyDictionary<int,DemoObservation> samples,double? tickRate,double clockScale)
    {
        row.ExclusionReasons.Remove("demo_missing");row.DemoContextChecked=true;
        if(tickRate is not (>=30 and <=256)||!double.IsFinite(clockScale)||clockScale<=0||row.ClickMs is <0 or >MaxClickDelayMs)
        {Add(row,"context_missing");return;}
        double transitionTick=fire.Tick-row.ClickMs/1000*clockScale*tickRate.Value;
        int before=(int)Math.Ceiling(transitionTick)-1;
        int start=(int)Math.Floor(transitionTick-.15*tickRate.Value),end=fire.Tick+1;
        if(start<0||end<start||end-start>256){Add(row,"context_missing");return;}
        var window=Enumerable.Range(start,end-start+1).Select(t=>samples.GetValueOrDefault(t)).ToArray();
        var known=window.Where(d=>d!=null).Select(d=>d!).ToArray();
        ClassifyStance(row,window);
        if(known.Any(d=>d.IsScoped==true))Tag(row,"scoped");
        if(known.Any(d=>d.VelocityModifier is <.99 or >1.01))Tag(row,"damaged");
        if(window.Any(d=>d?.VelocityModifier==null||!double.IsFinite(d.VelocityModifier.Value)))Tag(row,"modifier_unknown");
        if(known.Any(d=>d.OnGround==false||d.MoveType is not (null or 2)))Add(row,"airborne");
        bool Valid(DemoObservation? d)=>d!=null&&d.IsAlive==true&&d.Round==row.Round&&
            MatchAnalysis.Weapon(d.Weapon)==row.Weapon&&d.OnGround==true&&d.MoveType==2&&
            d.VelocityX.HasValue&&double.IsFinite(d.VelocityX.Value)&&d.VelocityY.HasValue&&double.IsFinite(d.VelocityY.Value)&&
            d.VelocityZ.HasValue&&double.IsFinite(d.VelocityZ.Value)&&Speed(d)<=400;
        // Grounded stairs/ramps may have vertical speed; do not classify them as airborne by Z velocity.
        if(window.Any(d=>!Valid(d)))Add(row,"context_missing");
        var prior=Enumerable.Range(before-2,3).Select(t=>samples.GetValueOrDefault(t)).ToArray();
        if(prior.Any(d=>!Valid(d))){Add(row,"context_missing");return;}
        var speeds=prior.Select(d=>Speed(d!)).ToArray();
        row.PriorMinSpeed=speeds.Min();row.PriorMaxSpeed=speeds.Max();row.PriorSpeed=Median(speeds);
        // Use unrounded total horizontal speed before the opposite key, not shot speed or one axis.
        if(row.PriorSpeed<MinimumInitialSpeed)Add(row,"initial_speed_below_threshold");
        if(speeds.All(v=>v<=MotionNoise))Add(row,"stationary");
        if(prior.Any(d=>!d!.Yaw.HasValue||!double.IsFinite(d.Yaw.Value))){Add(row,"context_missing");return;}
        // Freeze the original movement axis. W+D can brake D while retaining forward speed.
        double initialYaw=prior[^1]!.Yaw!.Value;
        var axis=Axis(row.Direction,initialYaw);
        var projections=prior.Select(d=>d!.VelocityX!.Value*axis.X+d.VelocityY!.Value*axis.Y).ToArray();
        row.AxisSpeedBefore=Median(projections);
        if(!row.ExclusionReasons.Contains("stationary")&&
            (projections.Count(v=>v>MotionNoise)<2||row.AxisSpeedBefore<=MotionNoise))Add(row,"direction");
        if(known.Any(d=>d.Yaw.HasValue&&Math.Abs(Math.IEEERemainder(d.Yaw.Value-initialYaw,360))>10))Tag(row,"turning");
        var aroundShot=Enumerable.Range(fire.Tick-1,3).Select(t=>samples.GetValueOrDefault(t)).ToArray();
        if(aroundShot.All(Valid))row.AxisSpeedAtShot=Median(aroundShot.Select(d=>Math.Abs(d!.VelocityX!.Value*axis.X+d.VelocityY!.Value*axis.Y)));
    }
    private static void ClassifyStance(ActionReview row,DemoObservation?[] window)
    {
        var ducks=window.Where(d=>d?.DuckAmount is >=0 and <=1).Select(d=>d!.DuckAmount!.Value).ToArray();
        row.MaxDuckAmount=ducks.Length==0?null:ducks.Max();
        if(ducks.Any(v=>v>=.01))Tag(row,"crouch");
        if(window.Any(d=>d?.IsWalking==true))Tag(row,"walk");
        if(ducks.Length!=window.Length||window.Any(d=>d?.IsWalking==null))
        {row.Stance="姿态未知";Tag(row,"pose_unknown");return;}
        row.Stance=ducks.All(v=>v>=.95)?"蹲姿":ducks.Any(v=>v>=.01)?"蹲起变化":
            window.Any(d=>d!.IsWalking==true)?"慢走":"站姿";
    }
    private static (double X,double Y) Axis(string direction,double yawDegrees)
    {
        double yaw=yawDegrees*Math.PI/180;
        return direction.Split('→')[0] switch
        {"A"=>(-Math.Sin(yaw),Math.Cos(yaw)),"D"=>(Math.Sin(yaw),-Math.Cos(yaw)),
         "W"=>(Math.Cos(yaw),Math.Sin(yaw)),"S"=>(-Math.Cos(yaw),-Math.Sin(yaw)),_=>(0,0)};
    }
    private static double Median(IEnumerable<double> values)=>MatchAnalysis.Median(values.Select(v=>(double?)v))!.Value;
    private static double Speed(DemoObservation d)=>Math.Sqrt(d.VelocityX!.Value*d.VelocityX.Value+d.VelocityY!.Value*d.VelocityY.Value);
    public static IReadOnlyList<ActionReview> Eligible(IEnumerable<ActionReview> actions)=>actions.Where(a=>a.EligibleForStats).ToArray();
    public static IReadOnlyList<ActionGroupSummary> Groups(IEnumerable<ActionReview> actions)=>Eligible(actions)
        .GroupBy(a=>(a.Weapon,a.Stance,a.InitialSpeedBand)).OrderBy(g=>g.Key.Weapon).ThenBy(g=>g.Key.Stance).ThenBy(g=>g.Min(a=>a.PriorSpeed)??double.MaxValue)
        .Select(g=>new ActionGroupSummary(g.Key.Weapon,g.Key.Stance,g.Key.InitialSpeedBand,g.Count(),
            MatchAnalysis.Median(g.Select(a=>a.PriorSpeed)),MatchAnalysis.Median(g.Select(a=>a.Speed)),
            MatchAnalysis.Median(g.Select(a=>a.AxisSpeedDrop)))).ToArray();
}
public sealed record ActionGroupSummary(string Weapon,string Stance,string InitialSpeedBand,int Count,double? Before,double? AtShot,double? AxisDrop);
