namespace StrafeLab.Core;

/// <summary>Conservative, identity-bound affine synchronization and held-out model fitting.</summary>
public static class RobustDemoAnalysis
{
    private sealed record Series(string Kind,double[] Local,double[] Demo);
    private sealed record Anchor(double Local,double Demo,string Kind);
    private sealed record Pair(DemoObservation A,DemoObservation B,PhysicsSample Input,double Dt,double MaxSpeed);
    public static AlignmentResult Align(SessionDocument s,DemoParseResult demo)
    {
        var id=s.PlayerSteamId??s.GsiEvents.Select(x=>x.Snapshot.ProviderSteamId).FirstOrDefault(x=>x!=null);
        AlignmentResult Reject(string why)=>new(){SteamId=id,Explanation=why};
        if(s.DiagnosticMode||id==null||!demo.IsSource2||demo.Error!=null)return Reject("缺少本机玩家身份或完整 Demo；不用于同步/校准");
        if(s.Map!=null&&demo.MapHint!=null&&s.Map!=demo.MapHint)return Reject("Demo 地图不匹配");
        var gs=s.GsiEvents.Select(g=>g.Snapshot).Where(g=>g.ProviderSteamId==id).OrderBy(g=>g.ReceivedAtUs).ToArray();
        var series=new List<Series>();
        var clicks=s.Shots.Where(x=>x.Confidence>=.75).Select(x=>(x.TimestampUs-s.SessionStartUs)/1e6).Order().ToArray();
        var fires=demo.Observations.Where(x=>x.EventName=="weapon_fire"&&x.SteamId==id).OrderBy(x=>x.TimeSeconds).ToArray();
        // Mouse1 is a click, not each bullet in an automatic burst. Keep burst onsets.
        var onsets=fires.Where((f,i)=>i==0||f.TimeSeconds-fires[i-1].TimeSeconds>.25).Select(f=>f.TimeSeconds).ToArray();
        if(clicks.Length>0&&onsets.Length>0)series.Add(new("weapon_fire",clicks,onsets));
        for(int i=1;i<gs.Length;i++)
        {
            var a=gs[i-1];var b=gs[i];if(a.Map!=b.Map||b.Round==null)continue;
            string? kind=b.RoundPhase=="live"&&a.RoundPhase=="freezetime"?"round_freeze_end":
                b.IsAlive==true&&a.IsAlive==false&&b.PlayerSteamId==id?"player_spawn":null;
            if(kind==null)continue;
            var ds=demo.Observations.Where(d=>d.EventName==kind&&d.Round==b.Round&&(kind!="player_spawn"||d.SteamId==id)).Select(d=>d.TimeSeconds).ToArray();
            if(ds.Length>0)series.Add(new(kind+":"+b.Round,[(b.ReceivedAtUs-s.SessionStartUs)/1e6],ds));
        }
        if(series.Count==0)return Reject("没有本机玩家的开枪/回合/重生锚点");
        var offsets=new HashSet<double>();
        foreach(var set in series)
            foreach(var l in Thin(set.Local,50))foreach(var d in Thin(set.Demo,50))offsets.Add(Math.Round(d-l,3));
        var candidates=offsets.Select(offset=>{
            var m=Match(series,1,offset,.30);return (Offset:offset,Matches:m,Score:m.Sum(x=>x.Kind=="weapon_fire"?1:3));
        }).OrderByDescending(x=>x.Score).ThenBy(x=>x.Matches.Sum(a=>Math.Abs(a.Demo-a.Local-x.Offset))).ToArray();
        var best=candidates[0];double scale=1,shift=best.Offset;
        var inliers=best.Matches;
        for(int pass=0;pass<3;pass++)
        {
            var residual=inliers.Select(x=>x.Demo-(scale*x.Local+shift)).ToArray();
            double median=Median(residual),mad=Median(residual.Select(x=>Math.Abs(x-median)).ToArray());
            double cutoff=Math.Clamp(3*1.4826*mad,.04,.12);
            inliers=inliers.Where(x=>Math.Abs(x.Demo-(scale*x.Local+shift)-median)<=cutoff).ToList();
            Fit(inliers,out scale,out shift);inliers=Match(series,scale,shift,.12);
        }
        double rms=inliers.Count>0?Math.Sqrt(inliers.Average(x=>Math.Pow(x.Demo-scale*x.Local-shift,2)))*1000:9999;
        double start=inliers.Count==0?0:inliers.Min(x=>x.Local),end=inliers.Count==0?0:inliers.Max(x=>x.Local);
        int kinds=inliers.Select(x=>x.Kind.Split(':')[0]).Distinct().Count();
        var rival=candidates.FirstOrDefault(c=>Math.Abs(c.Offset-best.Offset)>.6);
        bool ambiguous=rival.Matches!=null && rival.Score>=best.Score*.9;
        bool reliable=inliers.Count>=6&&kinds>=2&&end-start>=30&&rms<=50&&Math.Abs(scale-1)<=.002&&!ambiguous;
        return new(){IsReliable=reliable,SteamId=id,Scale=scale,OffsetSeconds=shift,ResidualRmsMs=rms,
            AnchorCount=best.Matches.Count,InlierCount=inliers.Count,StartLocalSeconds=start,EndLocalSeconds=end,
            Anchors=inliers.Select(x=>x.Kind.Split(':')[0]).Distinct().ToList(),
            Explanation=reliable?$"同步 {inliers.Count} 锚点，残差 {rms:F1} ms":$"未通过同步门槛：{inliers.Count} 锚点/{kinds} 类，残差 {rms:F1} ms，时间倍率 {scale:F6}{(ambiguous?"，存在歧义":"")}"};
    }
    private static IEnumerable<double> Thin(double[] a,int max) => a.Length<=max?a:Enumerable.Range(0,max).Select(i=>a[(int)(i*(a.Length-1d)/(max-1))]);
    private static List<Anchor> Match(IEnumerable<Series> series,double scale,double offset,double tolerance)
    {
        var result=new List<Anchor>();
        foreach(var set in series)
        {
            int j=0;
            foreach(var x in set.Local)
            {
                var target=x*scale+offset;while(j<set.Demo.Length&&set.Demo[j]<target-tolerance)j++;
                if(j>=set.Demo.Length)break;
                if(j+1<set.Demo.Length&&Math.Abs(set.Demo[j+1]-target)<Math.Abs(set.Demo[j]-target))j++;
                if(Math.Abs(set.Demo[j]-target)<=tolerance)result.Add(new(x,set.Demo[j++],set.Kind));
            }
        }
        return result;
    }
    private static void Fit(List<Anchor> a,out double scale,out double offset)
    {
        scale=1;offset=0;if(a.Count==0)return;
        double x=a.Average(p=>p.Local),y=a.Average(p=>p.Demo),d=a.Sum(p=>(p.Local-x)*(p.Local-x));
        if(d>1e-6)scale=a.Sum(p=>(p.Local-x)*(p.Demo-y))/d;
        offset=y-scale*x;
    }
    private static double Median(double[] a){if(a.Length==0)return 0;Array.Sort(a);return a.Length%2==1?a[a.Length/2]:(a[a.Length/2]+a[a.Length/2-1])/2;}

    public static CalibrationResult Calibrate(SessionDocument s,DemoParseResult demo,AlignmentResult alignment)
    {
        if(s.DiagnosticMode||!alignment.IsReliable||alignment.SteamId==null||alignment.ResidualRmsMs>25)
            return new(){Explanation="同步/身份/时间精度不足，保留原模型"};
        var local=s.PhysicsSamples.OrderBy(x=>x.TimestampUs).ToArray();
        var ds=demo.Observations.Where(d=>d.Kind=="player_sample"&&d.SteamId==alignment.SteamId).OrderBy(d=>d.TimeSeconds).ToArray();
        var pairs=new List<Pair>();int rejected=0,candidates=0,j=0;
        bool Valid(DemoObservation d)=>d.OnGround==true&&d.IsAlive==true&&d.IsScoped==false&&d.IsWalking==false&&d.MoveType==2&&
            d.DuckAmount is >=0 and <.01&&d.VelocityModifier is >=.99 and <=1.01&&d.Yaw.HasValue&&
            d.VelocityX.HasValue&&d.VelocityY.HasValue&&d.VelocityZ is >-3 and <3;
        for(int i=1;i<ds.Length;i++)
        {
            var a=ds[i-1];var b=ds[i];var dt=b.TimeSeconds-a.TimeSeconds;if(dt<.006||dt>.035)continue;candidates++;
            double time=(a.TimeSeconds-alignment.OffsetSeconds)/alignment.Scale;
            if(time<alignment.StartLocalSeconds||time>alignment.EndLocalSeconds||!Valid(a)||!Valid(b)||a.Round!=b.Round||a.Weapon!=b.Weapon||AngleDelta(a.Yaw!.Value,b.Yaw!.Value)>1)
            {rejected++;continue;}
            long us=s.SessionStartUs+(long)(time*1e6);
            while(j+1<local.Length&&local[j+1].TimestampUs<=us)j++;
            if(j>=local.Length)break;var l=local[j];
            if(Math.Abs(l.TimestampUs-us)>22000||l.Confidence<.75||l.Crouch||l.Walk||l.Jump){rejected++;continue;}
            if(j+1<local.Length&&local[j+1].TimestampUs<us+dt*1e6&&
                (l.W!=local[j+1].W||l.A!=local[j+1].A||l.S!=local[j+1].S||l.D!=local[j+1].D)){rejected++;continue;}
            var max=a.MaxSpeed is >=100 and <=320?a.MaxSpeed.Value:MovementSimulator.WeaponSpeed(l.Weapon);
            var pair=new Pair(a,b,l,dt,max);
            // Reject collisions, movement modes and corrections the ground model cannot explain.
            if(Error(pair,s.MovementModel.Accelerate,s.MovementModel.Friction)>144){rejected++;continue;}
            if(Speed(a)>25)pairs.Add(pair);
        }
        if(pairs.Count>5000)pairs=pairs.Where((_,i)=>i%(int)Math.Ceiling(pairs.Count/5000d)==0).ToList();
        int coasts=pairs.Count(p=>!p.Input.W&&!p.Input.A&&!p.Input.S&&!p.Input.D);
        int accelerating=pairs.Count(p=>p.Input.W||p.Input.A||p.Input.S||p.Input.D);
        if(pairs.Count<200||coasts<50||accelerating<50)return new(){CandidateCount=candidates,UsedSampleCount=pairs.Count,RejectedLowConfidenceCount=rejected,
            Explanation=$"筛选后 {pairs.Count} 对样本（滑行 {coasts} / 有输入 {accelerating}），不足以可靠校准"};
        int split=(int)(pairs.Count*.7);var train=pairs.Take(split).ToArray();var holdout=pairs.Skip(split).ToArray();
        double Loss(Pair[] data,double a,double f)=>data.Average(p=>Math.Min(36,Error(p,a,f)));
        double bestA=s.MovementModel.Accelerate,bestF=s.MovementModel.Friction,best=Loss(train,bestA,bestF);
        for(double a=3.5;a<=7.5;a+=.1)for(double f=3.5;f<=7.5;f+=.1)
        {var loss=Loss(train,a,f);if(loss<best){best=loss;bestA=a;bestF=f;}}
        double before=Loss(holdout,s.MovementModel.Accelerate,s.MovementModel.Friction),after=Loss(holdout,bestA,bestF);
        bool applied=after<before*.90&&after<9&&Math.Abs(bestA-s.MovementModel.Accelerate)<1.5&&Math.Abs(bestF-s.MovementModel.Friction)<1.5;
        if(applied){s.MovementModel.Accelerate=bestA;s.MovementModel.Friction=bestF;s.MovementModel.CalibrationSource="Demo ground pairs; temporal holdout validated";}
        return new(){Applied=applied,CandidateCount=candidates,UsedSampleCount=pairs.Count,RejectedLowConfidenceCount=rejected,
            ObservedAcceleration=bestA,ObservedFriction=bestF,ResidualRms=Math.Sqrt(after),
            Explanation=applied?$"已校准：a={bestA:F2}, f={bestF:F2}，留出集误差 {Math.Sqrt(before):F2}→{Math.Sqrt(after):F2} u/s":
                $"{pairs.Count} 对样本验证完成，改进未达门槛；保留原参数"};
    }
    private static double AngleDelta(double a,double b)=>Math.Abs(((a-b+540)%360)-180);
    private static double Speed(DemoObservation d)=>Math.Sqrt(d.VelocityX!.Value*d.VelocityX.Value+d.VelocityY!.Value*d.VelocityY.Value);
    private static double Error(Pair p,double accelerate,double friction)
    {
        double x=p.A.VelocityX!.Value,y=p.A.VelocityY!.Value,speed=Speed(p.A),scale=speed>0?Math.Max(0,speed-Math.Max(speed,80)*friction*p.Dt)/speed:0;
        x*=scale;y*=scale;
        double side=(p.Input.D?1:0)-(p.Input.A?1:0),forward=(p.Input.W?1:0)-(p.Input.S?1:0),length=Math.Sqrt(side*side+forward*forward);
        if(length>0)
        {
            side/=length;forward/=length;double yaw=p.A.Yaw!.Value*Math.PI/180;
            double wx=Math.Cos(yaw)*forward+Math.Sin(yaw)*side,wy=Math.Sin(yaw)*forward-Math.Cos(yaw)*side;
            double add=Math.Clamp(p.MaxSpeed-x*wx-y*wy,0,accelerate*p.MaxSpeed*p.Dt);x+=wx*add;y+=wy*add;
        }
        return Math.Pow(x-p.B.VelocityX!.Value,2)+Math.Pow(y-p.B.VelocityY!.Value,2);
    }
}
