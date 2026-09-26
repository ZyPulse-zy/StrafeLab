using StrafeLab.Core;
using StrafeLab.Platform;
using Xunit;

namespace StrafeLab.Tests;
public sealed class BackgroundCollectionTests
{
    private sealed class Capture : ICaptureSession
    {
        public int Disposals;
        public string State=>"recording";
        public string Gsi=>"connected";
        public string Hall=>"verified";
        public int InputCount=>12;
        public ValueTask DisposeAsync(){Disposals++;return ValueTask.CompletedTask;}
    }
    [Fact] public async Task Idle_never_opens_input_and_repeated_game_checks_do_not_duplicate_capture()
    {
        int starts=0;var capture=new Capture();
        await using var lifecycle=new CaptureLifecycle(()=>{starts++;return Task.FromResult<ICaptureSession>(capture);});
        await lifecycle.StepAsync(false,false);await lifecycle.StepAsync(false,false);Assert.Equal(0,starts);
        await lifecycle.StepAsync(true,false);await lifecycle.StepAsync(true,false);
        Assert.Equal(1,starts);Assert.True(lifecycle.Active);Assert.Equal(12,lifecycle.InputCount);
        await lifecycle.StepAsync(false,false);Assert.Equal(1,capture.Disposals);Assert.False(lifecycle.Active);
    }
    [Fact] public async Task Pause_flushes_once_and_resume_creates_a_new_capture()
    {
        var instances=new List<Capture>();
        await using var lifecycle=new CaptureLifecycle(()=>{var c=new Capture();instances.Add(c);return Task.FromResult<ICaptureSession>(c);});
        await lifecycle.StepAsync(true,false);await lifecycle.StepAsync(true,true);await lifecycle.StepAsync(true,true);
        Assert.Equal(1,instances[0].Disposals);Assert.False(lifecycle.Active);
        await lifecycle.StepAsync(true,false);Assert.Equal(2,instances.Count);
        await lifecycle.DisposeAsync();await lifecycle.DisposeAsync();Assert.Equal(1,instances[1].Disposals);
    }
    [Fact] public async Task Failed_initialization_can_retry_without_claiming_to_be_running()
    {
        int attempts=0;
        await using var lifecycle=new CaptureLifecycle(()=>++attempts==1?throw new IOException("device busy"):Task.FromResult<ICaptureSession>(new Capture()));
        await Assert.ThrowsAsync<IOException>(()=>lifecycle.StepAsync(true,false));Assert.False(lifecycle.Active);
        await lifecycle.StepAsync(true,false);Assert.True(lifecycle.Active);
    }
    [Fact] public async Task Game_deferral_keeps_directory_commands_but_does_not_load_sessions()
    {
        var root=Path.Combine(Path.GetTempPath(),"StrafeLabBackground-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new SessionStore(root);store.Save(new(){EndedAtUtc=DateTime.UtcNow});
            bool playing=true;
            await using(var monitor=new DemoMonitor(store,[],defer:()=>playing))
            {
                monitor.AddRoot(Path.Combine(root,"downloads"));await monitor.ScanOnceAsync();
                Assert.Single(monitor.Snapshot().Roots);Assert.Empty(monitor.Reports());Assert.Contains("CS2",monitor.Status);
                playing=false;await monitor.ScanOnceAsync();Assert.Single(monitor.Reports());
            }
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Local_control_handles_commands_and_exits_cleanly()
    {
        string role="test-"+Guid.NewGuid().ToString("N");
        await using(var server=new LocalControlServer(role,c=>Task.FromResult(c=="ping"?"ready":"invalid")))
        {
            Assert.Equal("ready",await LocalControlServer.SendAsync(role,"ping"));
            Assert.Equal("invalid",await LocalControlServer.SendAsync(role,"unknown"));
            Assert.Equal("ready",await LocalControlServer.SendAsync(role,"ping"));
        }
    }
    [Fact] public void Startup_command_quotes_paths_and_rejects_unsafe_or_too_long_paths()
    {
        Assert.Equal("\"C:\\Users\\my name\\StrafeLab.exe\" --collector",StartupRegistration.Command(@"C:\Users\my name\StrafeLab.exe"));
        Assert.Throws<ArgumentException>(()=>StartupRegistration.Command("relative.exe"));
        Assert.Throws<ArgumentException>(()=>StartupRegistration.Command("C:\\bad\"name.exe"));
        Assert.Throws<ArgumentException>(()=>StartupRegistration.Command("C:\\"+new string('a',260)+".exe"));
    }
    [Fact] public void Worker_discovery_uses_metadata_and_not_session_contents()
    {
        var root=Path.Combine(Path.GetTempPath(),"StrafeLabFingerprint-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new SessionStore(root);var library=new DemoLibrary(root,[]);library.Save();
            var path=Path.Combine(store.SessionsDirectory,"broken.json");File.WriteAllText(path,"not valid json");
            var first=BackgroundDemoWorker.Fingerprint(root);Assert.NotNull(first);Assert.False(first.Value.Pending);
            File.AppendAllText(path,"more");Assert.NotEqual(first.Value.Stamp,BackgroundDemoWorker.Fingerprint(root)!.Value.Stamp);
            library.State.Enabled=false;library.Save();Assert.Null(BackgroundDemoWorker.Fingerprint(root));
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public void Streaming_saves_keep_final_revision_and_preserve_roundtrip()
    {
        var root=Path.Combine(Path.GetTempPath(),"StrafeLabStream-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new SessionStore(root);var session=new SessionDocument{Map="de_dust2",EndedAtUtc=DateTime.UtcNow,Revision=3};
            session.InputEvents.Add(new(){Control=InputControl.A,Action=InputAction.Down,TimestampUs=12345});store.Save(session);
            var loaded=store.Load(session.SessionId);Assert.Equal(12345,loaded!.InputEvents[0].TimestampUs);
            session.Revision=2;session.Map="wrong";store.Save(session);Assert.Equal("de_dust2",store.Load(session.SessionId)!.Map);
            Assert.Empty(Directory.GetFiles(store.SessionsDirectory,"*.tmp"));
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public void Status_roundtrips_and_rejects_stale_or_reused_process_identity()
    {
        var root=Path.Combine(Path.GetTempPath(),"StrafeLabStatus-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            using var current=System.Diagnostics.Process.GetCurrentProcess();
            var status=new CollectorStatus(current.Id,current.StartTime.ToUniversalTime(),DateTime.UtcNow,false,"waiting","idle","idle",0,123,456);
            var path=Path.Combine(root,"collector-status.json");DemoLibrary.Atomic(path,status);
            Assert.Equal("waiting",CollectorStatus.Read(root)?.State);
            DemoLibrary.Atomic(path,status with{UpdatedUtc=DateTime.UtcNow.AddMinutes(-1)});Assert.Null(CollectorStatus.Read(root));
            DemoLibrary.Atomic(path,status with{ProcessStartedUtc=DateTime.UtcNow.AddYears(-1)});Assert.Null(CollectorStatus.Read(root));
        }
        finally{Directory.Delete(root,true);}
    }
}
