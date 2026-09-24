using StrafeLab.Core;
using StrafeLab.Core.Gsi;
using StrafeLab.Input;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using Xunit;
namespace StrafeLab.Tests;
public sealed class CoreAnalysisTests
{
    private static InputEvent Edge(InputControl c,InputAction a,long t)=>new(){Control=c,Action=a,TimestampUs=t,Source=InputSourceKind.RawInput,Confidence=.92};
    [Fact] public void Gap_hold_and_click_are_causal()
    {
        var list=new List<StrafeTransition>();var a=new StrafeAnalyzer(list);
        a.Process(Edge(InputControl.A,InputAction.Down,0),null,null);
        a.Process(Edge(InputControl.A,InputAction.Up,100000),null,null);
        a.Process(Edge(InputControl.D,InputAction.Down,125000),null,null);
        var shot=a.Process(Edge(InputControl.Mouse1,InputAction.Down,140000),null,new(){VelocityX=12,InPreciseFireWindow=true}).Shot;
        a.Process(Edge(InputControl.D,InputAction.Up,225000),null,null);
        Assert.Equal(25000,list[0].GapUs);Assert.Equal(100000,list[0].ReverseHoldUs);Assert.Equal(15000,shot!.DeltaFromTransitionUs);
    }
    [Theory][InlineData(true)][InlineData(false)] public void Overlap_ends_at_first_release(bool previousFirst)
    {
        var list=new List<StrafeTransition>();var a=new StrafeAnalyzer(list);
        a.Process(Edge(InputControl.A,InputAction.Down,10000),null,null);
        a.Process(Edge(InputControl.D,InputAction.Down,30000),null,null);
        Assert.Null(list[0].OverlapUs);
        a.Process(Edge(previousFirst?InputControl.A:InputControl.D,InputAction.Up,42000),null,null);
        a.Process(Edge(previousFirst?InputControl.D:InputControl.A,InputAction.Up,80000),null,null);
        Assert.Equal(12000,list[0].OverlapUs);Assert.Equal(previousFirst?50000:12000,list[0].ReverseHoldUs);
    }
    [Fact] public void Analog_is_not_an_RT_edge_and_repeat_does_not_add_transitions()
    {
        var list=new List<StrafeTransition>();var a=new StrafeAnalyzer(list);
        a.Process(new(){Control=InputControl.A,Action=InputAction.Analog,Value=1},null,null);Assert.False(a.Snapshot.A);
        a.Process(Edge(InputControl.A,InputAction.Down,1),null,null);
        a.Process(Edge(InputControl.D,InputAction.Down,2),null,null);
        a.Process(Edge(InputControl.D,InputAction.Down,3),null,null);Assert.Single(list);
        a.Reset();Assert.False(a.Snapshot.A);Assert.False(a.Snapshot.D);
    }
    [Fact] public void Raw_mouse_matches_win32_union_layout()
    {Assert.Equal(24,Marshal.SizeOf<RawInputSource.RawMouse>());Assert.Equal(new IntPtr(4),Marshal.OffsetOf<RawInputSource.RawMouse>(nameof(RawInputSource.RawMouse.ButtonFlags)));}
    [Fact] public void Ground_model_counter_input_stops_earlier_than_release()
    {
        double Stop(bool reverse)
        {
            var m=new MovementSimulator(new());m.AdvanceTo(0,new(){A=true},null);
            for(long t=8000;t<=1000000;t+=8000)m.AdvanceTo(t,new(){A=true},null);
            m.AdvanceTo(1000000,new(){D=reverse},null);
            for(long t=1008000;t<=1500000;t+=8000){m.AdvanceTo(t,new(){D=reverse},null);if(m.State.Speed<=34)return(t-1000000)/1000d;}
            return 500;
        }
        Assert.True(Stop(true)<Stop(false));Assert.InRange(Stop(true),70,180);
    }
    [Fact] public void Unknown_game_state_never_becomes_high_confidence()
    {var m=new MovementSimulator(new());var p=m.AdvanceTo(0,new(),null);Assert.True(p.Confidence<.75);}
    [Fact] public void Gsi_menu_missing_player_is_valid()
    {var p=GsiSnapshotParser.Parse("{\"provider\":{\"timestamp\":50}}",10);Assert.Null(p.WeaponName);Assert.Equal(50000,p.GameTimeMs);Assert.Null(p.IsAlive);}
    [Fact] public void Gsi_weapon_and_local_identity_are_parsed()
    {
        var p=GsiSnapshotParser.Parse("""{"provider":{"steamid":"local"},"player":{"steamid":"local","state":{"health":100},"weapons":{"weapon_0":{"name":"weapon_ak47","state":"active","ammo_clip":27}}}}""",1);
        Assert.Equal("weapon_ak47",p.WeaponName);Assert.Equal(27,p.AmmoClip);Assert.Equal(p.PlayerSteamId,p.ProviderSteamId);
        Assert.Contains("\"player_weapons\"",new GsiConfigInstaller(new()).BuildConfig(3000,"test"));
    }
    [Fact] public void Real_Hall_packets_decode_all_four_keys_and_mm()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","ace68-air-0117.json")));
        var keys=new HashSet<InputControl>();
        foreach(var row in doc.RootElement.EnumerateArray())
        {
            var payload=Convert.FromHexString(row.GetProperty("hex").GetString()!);
            Assert.True(Ace68Protocol.TryDecode(payload,1,out var sample));
            Assert.Equal(row.GetProperty("travel").GetInt32()/100d,sample!.Millimeters,8);keys.Add(sample.Control);
            Assert.True(Ace68Protocol.TryDecode(new byte[]{0}.Concat(payload).ToArray(),1,out _));
        }
        Assert.Equal(4,keys.Count);Assert.False(Ace68Protocol.TryDecode(new byte[65],1,out _));
    }
    [Fact] public void Hall_requests_match_observed_official_packets()
    {
        var p=Ace68Protocol.Command(5,64,56);Assert.Equal("005505007838400000",Convert.ToHexString(p[..9]));
        Assert.Throws<ArgumentOutOfRangeException>(()=>Ace68Protocol.Command(0xA8,0,1));
    }
    private static (SessionDocument,DemoParseResult) Synced()
    {
        const string id="76561198000000001";var s=new SessionDocument{SessionStartUs=0,PlayerSteamId=id,Map="de_mirage"};
        var d=new DemoParseResult{IsSource2=true,MapHint="de_mirage"};
        foreach(double t in new[]{11,23.3,41.8,58.3,80.7,90.8,110.1,131.4})
        {s.Shots.Add(new(){TimestampUs=(long)(t*1e6),Confidence=.9});d.Observations.Add(new(){EventName="weapon_fire",SteamId=id,TimeSeconds=t*1.0001+2});}
        foreach(var (t,r) in new[]{(8d,0),(70d,1),(120d,2)})
        {
            s.GsiEvents.Add(new(){Snapshot=new(){ProviderSteamId=id,PlayerSteamId=id,Round=r,RoundPhase="freezetime",ReceivedAtUs=(long)((t-3)*1e6)}});
            s.GsiEvents.Add(new(){Snapshot=new(){ProviderSteamId=id,PlayerSteamId=id,Round=r,RoundPhase="live",ReceivedAtUs=(long)(t*1e6)}});
            d.Observations.Add(new(){EventName="round_freeze_end",Round=r,TimeSeconds=t*1.0001+2});
        }
        return(s,d);
    }
    [Fact] public void Robust_sync_handles_drift_and_foreign_shot_outliers()
    {
        var (s,d)=Synced();d.Observations.Add(new(){EventName="weapon_fire",SteamId="other",TimeSeconds=999});
        var fit=DemoService.Align(s,d);Assert.True(fit.IsReliable,fit.Explanation);Assert.InRange(fit.OffsetSeconds,1.999,2.001);Assert.InRange(fit.Scale,1.00009,1.00011);
    }
    [Fact] public void No_identity_or_airborne_data_can_calibrate()
    {
        var(s,d)=Synced();s.PlayerSteamId="unknown";Assert.False(DemoService.Align(s,d).IsReliable);
        var result=DemoService.Calibrate(s,d,new(){IsReliable=false});Assert.False(result.Applied);Assert.Equal(5.5,s.MovementModel.Accelerate);
    }
    [Fact] public async Task Gsi_loopback_checks_auth_method_and_menu_payload()
    {
        await using var server=new GsiServer("unit-test-token");Assert.True(await server.StartAsync(33271));
        using var client=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{server.Port}")};
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsync("/gsi",new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsync("/gsi",new StringContent("{\"auth\":{\"token\":\"unit-test-token\"}}"))).StatusCode);Assert.NotNull(server.Latest);
    }
    [Fact] public void Atomic_store_rejects_older_revision()
    {
        var root=Path.Combine(Path.GetTempPath(),"StrafeLabTest-"+Guid.NewGuid().ToString("N"));
        try{var store=new SessionStore(root);var s=new SessionDocument{Revision=2,Map="new"};store.Save(s);store.Save(new(){SessionId=s.SessionId,Revision=1,Map="old"});Assert.Equal("new",store.Load(s.SessionId)!.Map);}
        finally{Directory.Delete(root,true);}
    }
    private static (SessionDocument,DemoParseResult,AlignmentResult) AnalyticGroundRun(double confidence,bool grounded)
    {
        // Independent closed-form dv/dt = a*wishSpeed - f*v, while v remains above
        // stop speed; this is not the model's discrete stepping implementation.
        const string id="76561198000000001";const double dt=1d/64;
        var s=new SessionDocument{SessionStartUs=0,PlayerSteamId=id};var d=new DemoParseResult{IsSource2=true};
        double v=180;
        for(int i=0;i<=2200;i++)
        {
            bool pressed=i%20<12;double t=i*dt;
            s.PhysicsSamples.Add(new(){TimestampUs=(long)(t*1e6),W=pressed,Confidence=confidence,Weapon="weapon_knife"});
            d.Observations.Add(new(){Kind="player_sample",SteamId=id,TimeSeconds=t,VelocityX=v,VelocityY=0,VelocityZ=0,
                OnGround=grounded,IsAlive=true,IsScoped=false,IsWalking=false,MoveType=2,DuckAmount=0,Yaw=0,Round=0,
                VelocityModifier=1,MaxSpeed=250,Weapon="knife"});
            var decay=Math.Exp(-4.8*dt);v=Math.Min(250,v*decay+(pressed?6*250/4.8*(1-decay):0));
        }
        return(s,d,new(){IsReliable=true,SteamId=id,Scale=1,ResidualRmsMs=2,StartLocalSeconds=0,EndLocalSeconds=35});
    }
    [Fact] public void Calibration_improves_independent_analytic_run_on_temporal_holdout()
    {
        var(s,d,a)=AnalyticGroundRun(.95,true);var r=DemoService.Calibrate(s,d,a);
        Assert.True(r.Applied,r.Explanation);Assert.True(r.UsedSampleCount>=200);Assert.InRange(s.MovementModel.Friction,4.4,4.9);Assert.InRange(s.MovementModel.Accelerate,5.6,6.1);
    }
    [Theory][InlineData(.4,true)][InlineData(.95,false)] public void Calibration_excludes_uncertain_or_airborne_pairs(double confidence,bool grounded)
    {
        var(s,d,a)=AnalyticGroundRun(confidence,grounded);var r=DemoService.Calibrate(s,d,a);
        Assert.False(r.Applied);Assert.Equal(0,r.UsedSampleCount);Assert.True(r.RejectedLowConfidenceCount>0);
    }
}
