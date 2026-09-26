using System.Text.Json.Serialization;

namespace StrafeLab.Core;

public sealed class MatchReport
{
    public const int CurrentVersion = 4;
    public int Version { get; set; } = CurrentVersion;
    public string SessionId { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public string Map { get; set; } = "";
    public string? DemoPath { get; set; }
    public string? DemoHash { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public AlignmentResult? Alignment { get; set; }
    public CalibrationResult? Calibration { get; set; }
    public int InputCount { get; set; }
    public int HallCount { get; set; }
    public int ExcludedCount { get; set; }
    public int UnassociatedShotCount { get; set; }
    public List<ActionReview> Actions { get; set; } = [];
    [JsonIgnore] public string LocalTime => StartedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
    [JsonIgnore] public int Count => Actions.Count;
    [JsonIgnore] public int Matched => Actions.Count(x => x.DemoTick.HasValue);
    [JsonIgnore] public int EligibleCount => Actions.Count(x => x.EligibleForStats);
    [JsonIgnore] public string DemoStatus => Alignment?.IsReliable == true ? $"已同步 · {Matched}/{Count}" : "等待对应 Demo";
}

public sealed class ActionReview
{
    public long TransitionUs { get; set; }
    public long ShotUs { get; set; }
    public DateTime LocalTime { get; set; }
    public int? Round { get; set; }
    public string Direction { get; set; } = "";
    public string Weapon { get; set; } = "";
    public double? GapMs { get; set; }
    public double? OverlapMs { get; set; }
    public double? HoldMs { get; set; }
    public double ClickMs { get; set; }
    public int? DemoTick { get; set; }
    public double? MatchErrorMs { get; set; }
    public double? Speed { get; set; }
    public double? MinSpeed { get; set; }
    public double? MaxSpeed { get; set; }
    public string Verdict { get; set; } = "未匹配";
    public string Detail { get; set; } = "";
    public List<string> ExclusionReasons { get; set; } = ["demo_missing"];
    public double? PriorMinSpeed { get; set; }
    public double? PriorMaxSpeed { get; set; }
    public double? MaxDuckAmount { get; set; }
    public string Stance { get; set; } = "姿态未知";
    public List<string> ContextTags { get; set; } = [];
    public double? PriorSpeed { get; set; }
    public double? AxisSpeedBefore { get; set; }
    public double? AxisSpeedAtShot { get; set; }
    public bool DemoContextChecked { get; set; }
    [JsonIgnore] public bool EligibleForStats => DemoContextChecked && ExclusionReasons.Count == 0;
    [JsonIgnore] public string WeaponGroup => CounterStrafeCohort.WeaponGroup(Weapon);
    [JsonIgnore] public string Cohort => Stance+" · "+WeaponGroup;
    [JsonIgnore] public string InitialSpeedBand => !PriorSpeed.HasValue ? "起速未知" : PriorSpeed<50 ? "起速 <50" : PriorSpeed<150 ? "起速 50–150" : "起速 ≥150";
    [JsonIgnore] public double? AxisSpeedDrop => EligibleForStats ? AxisSpeedBefore-AxisSpeedAtShot : null;
    [JsonIgnore] public string BrakeDescription => AxisSpeedDrop is >.5 ? "轴向速度下降" : AxisSpeedDrop is <-.5 ? "轴向速度增大" : AxisSpeedDrop.HasValue ? "变化很小" : "未评估";
    [JsonIgnore] public string ContextText => string.Join("；",ContextTags.Select(CounterStrafeCohort.ReasonText));
    [JsonIgnore] public string EligibilityText => EligibleForStats ? "计入" : string.Join("；", ExclusionReasons.Select(CounterStrafeCohort.ReasonText));
    [JsonIgnore] public string TimeText => LocalTime.ToString("HH:mm:ss.fff");
    [JsonIgnore] public string RoundText => Round.HasValue ? (Round + 1).ToString()! : "—";
}

/// <summary>Audits recorded edges, then joins only unique same-round/weapon fire events.
/// Speed is a position-derived tick interval estimate, never weapon accuracy.</summary>
public static class MatchAnalysis
{
    public static string Weapon(string? value)
    {
        var original=(value??"").Replace("weapon_","",StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
        // GSI/events use schema names; demoparser active_weapon_name uses display names.
        var compact=new string(original.Where(char.IsLetterOrDigit).ToArray());
        return compact switch
        {
            "m4a4"=>"m4a1", "m4a1s" or "m4a1silencer"=>"m4a1_silencer", "usps" or "uspsilencer"=>"usp_silencer",
            "deserteagle"=>"deagle", "dualberettas"=>"elite", "glock18"=>"glock", "p2000"=>"hkp2000",
            "sg553"=>"sg556", "cz75auto"=>"cz75a", "ppbizon"=>"bizon", "r8revolver"=>"revolver",
            _=>compact
        };
    }
    private static readonly HashSet<string> Guns = new(StringComparer.OrdinalIgnoreCase)
    { "ak47","m4a1","m4a1_silencer","awp","ssg08","aug","sg556","famas","galilar","scar20","g3sg1",
      "glock","hkp2000","usp_silencer","p250","deagle","revolver","elite","fiveseven","tec9","cz75a",
      "mac10","mp9","mp7","mp5sd","ump45","p90","bizon","nova","xm1014","mag7","sawedoff","m249","negev" };
    public static bool IsGun(string? weapon) => Guns.Contains(Weapon(weapon));
    public static double? Median(IEnumerable<double?> source)
    {
        var a = source.Where(x => x.HasValue && double.IsFinite(x.Value)).Select(x => x!.Value).Order().ToArray();
        return a.Length == 0 ? null : (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2;
    }
    public static MatchReport Build(SessionDocument s, DemoParseResult? demo = null, AlignmentResult? alignment = null)
    {
        var report = new MatchReport { SessionId=s.SessionId, StartedAtUtc=s.StartedAtUtc, Map=s.Map??"未知地图",
            InputCount=s.InputEvents.Count, HallCount=s.HallSamples.Count, Alignment=alignment };
        if(s.DiagnosticMode) return report;
        var edges=s.InputEvents.Where(e=>e.Action!=InputAction.Analog).OrderBy(e=>e.TimestampUs).ToArray();
        var byKey=edges.GroupBy(e=>e.Control).ToDictionary(g=>g.Key,g=>g.ToArray());
        var gs=s.GsiEvents.Select(e=>e.Snapshot).OrderBy(e=>e.ReceivedAtUs).ToArray();
        var physics=s.PhysicsSamples.OrderBy(e=>e.TimestampUs).ToArray();
        var transitions=s.Transitions.GroupBy(t=>t.TimestampUs).ToDictionary(g=>g.Key,g=>g.First());
        var seen=new HashSet<long>();
        foreach(var shot in s.Shots.OrderBy(x=>x.TimestampUs))
        {
            var context=AtOrBefore(gs,shot.TimestampUs,x=>x.ReceivedAtUs);
            bool Live(GsiSnapshot? g,long at)=>g!=null&&at-g.ReceivedAtUs<=2_000_000&&g.MapPhase=="live"&&g.RoundPhase=="live"&&
                g.IsAlive==true&&g.PlayerActivity=="playing"&&g.PlayerSteamId==s.PlayerSteamId&&g.ProviderSteamId==s.PlayerSteamId;
            if(!Live(context,shot.TimestampUs)||!IsGun(shot.WeaponName)||shot.Confidence<.75) continue;
            if(shot.RelatedTransitionUs is not long t0 || !transitions.TryGetValue(t0,out var t)) {report.UnassociatedShotCount++;continue;}
            if(seen.Contains(t0)||!Live(AtOrBefore(gs,t0,x=>x.ReceivedAtUs),t0)||t.Confidence<.75||shot.TimestampUs-t0 is <0 or >500_000) continue;
            seen.Add(t0);
            bool Edge(InputControl key,InputAction action,long at)=>byKey.TryGetValue(key,out var list)&&
                Array.Exists(list,e=>e.TimestampUs==at&&e.Action==action&&e.Confidence>=.75);
            var segment=physics.Where(p=>p.TimestampUs>=t0&&p.TimestampUs<=shot.TimestampUs).ToArray();
            bool continuous=segment.Length>0&&segment[0].TimestampUs-t0<=30_000&&shot.TimestampUs-segment[^1].TimestampUs<=30_000&&
                segment.Zip(segment.Skip(1),(a,b)=>b.TimestampUs-a.TimestampUs).All(dt=>dt<=100_000);
            if(!Edge(t.To,InputAction.Down,t0)||!Edge(InputControl.Mouse1,InputAction.Down,shot.TimestampUs)||!continuous)
            { report.ExcludedCount++; continue; }
            // Reconstruct the first target release. Never trust legacy holds filled across a pause.
            double? hold=null;
            var next=byKey[t.To].FirstOrDefault(e=>e.TimestampUs>t0);
            if(next?.Action==InputAction.Up&&next.Confidence>=.75)
            {
                var between=physics.Where(p=>p.TimestampUs>=t0&&p.TimestampUs<=next.TimestampUs).ToArray();
                if(between.Length>0&&next.TimestampUs-between[^1].TimestampUs<=30_000&&
                   between.Zip(between.Skip(1),(a,b)=>b.TimestampUs-a.TimestampUs).All(dt=>dt<=100_000)) hold=(next.TimestampUs-t0)/1000d;
            }
            bool gapValid=t.GapUs.HasValue&&(t.GapUs==0||Edge(t.From,InputAction.Up,t0-t.GapUs.Value));
            bool overlapValid=t.OverlapUs.HasValue&&(t.OverlapUs==0||
                Edge(t.From,InputAction.Up,t0+t.OverlapUs.Value)||Edge(t.To,InputAction.Up,t0+t.OverlapUs.Value));
            var row=new ActionReview{TransitionUs=t0,ShotUs=shot.TimestampUs,LocalTime=s.StartedAtUtc.ToLocalTime().AddTicks((t0-s.SessionStartUs)*10),
                Round=shot.Round,Direction=$"{t.From}→{t.To}",Weapon=Weapon(shot.WeaponName),GapMs=gapValid?t.GapUs/1000d:null,
                OverlapMs=overlapValid?t.OverlapUs/1000d:null,HoldMs=hold,ClickMs=(shot.TimestampUs-t0)/1000d,
                Detail=hold.HasValue?"输入边沿已核对":"持键终点不完整，持键时长不计入统计"};
            CounterStrafeCohort.CheckInput(row,t,edges,physics);
            report.Actions.Add(row);
        }
        if(demo==null||alignment?.IsReliable!=true) return report;
        var fires=demo.Observations.Where(d=>d.EventName=="weapon_fire"&&d.SteamId==s.PlayerSteamId&&IsGun(d.Weapon)).OrderBy(d=>d.TimeSeconds).ToArray();
        var samples=demo.Observations.Where(d=>d.Kind=="player_sample"&&d.SteamId==s.PlayerSteamId).GroupBy(d=>d.Tick).ToDictionary(g=>g.Key,g=>g.First());
        var used=new HashSet<int>();
        foreach(var row in report.Actions)
        {
            double local=(row.ShotUs-s.SessionStartUs)/1e6;
            if(local<alignment.StartLocalSeconds||local>alignment.EndLocalSeconds) {row.Detail+="；超出同步锚点覆盖范围";continue;}
            double target=local*alignment.Scale+alignment.OffsetSeconds;
            var candidates=fires.Select((f,i)=>(Fire:f,Index:i,Error:Math.Abs(f.TimeSeconds-target)))
                .Where(x=>!used.Contains(x.Index)&&x.Fire.Round==row.Round&&Weapon(x.Fire.Weapon)==row.Weapon&&x.Error<=.025).OrderBy(x=>x.Error).ToArray();
            if(candidates.Length!=1) {row.Detail+="；未找到唯一同回合/同武器的开枪事件";continue;}
            var match=candidates[0];used.Add(match.Index);row.DemoTick=match.Fire.Tick;row.MatchErrorMs=match.Error*1000;
            CounterStrafeCohort.CheckDemo(row,match.Fire,samples,demo.TickRate,alignment.Scale);
            var nearby=Enumerable.Range(match.Fire.Tick-1,3).Select(t=>samples.GetValueOrDefault(t)).ToArray();
            bool Valid(DemoObservation? d)=>d!=null&&d.IsAlive==true&&d.OnGround==true&&d.Round==row.Round&&d.MoveType==2&&
                d.VelocityX.HasValue&&d.VelocityY.HasValue&&double.IsFinite(d.VelocityX.Value)&&double.IsFinite(d.VelocityY.Value)&&
                d.VelocityZ.HasValue&&double.IsFinite(d.VelocityZ.Value);
            if(nearby.Any(d=>!Valid(d))) {row.Verdict="不确定";row.Detail+="；缺少连续接地速度样本";continue;}
            var speeds=nearby.Select(d=>Math.Sqrt(d!.VelocityX!.Value*d.VelocityX.Value+d.VelocityY!.Value*d.VelocityY.Value)).ToArray();
            if(speeds.Any(v=>v>400)) {row.Verdict="不确定";row.Detail+="；速度异常/位置跳变";continue;}
            row.Speed=speeds[1];row.MinSpeed=speeds.Min();row.MaxSpeed=speeds.Max();
            row.Verdict=row.MaxSpeed<=CounterStrafeCohort.LowSpeed?"低速":row.MinSpeed>CounterStrafeCohort.LowSpeed?"仍在移动":"边界";
            row.Detail+="；开枪 tick 及前后各一 tick 的区间速度；开枪低速标记不代表急停成功或命中率，反向前初速门槛单独判断";
        }
        return report;
    }
    private static T? AtOrBefore<T>(T[] items,long time,Func<T,long> timestamp) where T:class
    {
        int lo=0,hi=items.Length-1,result=-1;
        while(lo<=hi){int mid=(lo+hi)/2;if(timestamp(items[mid])<=time){result=mid;lo=mid+1;}else hi=mid-1;}
        return result<0?null:items[result];
    }
}
