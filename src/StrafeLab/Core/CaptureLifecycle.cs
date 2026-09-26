namespace StrafeLab.Core;

public interface ICaptureSession : IAsyncDisposable
{
    string State { get; }
    string Gsi { get; }
    string Hall { get; }
    int InputCount { get; }
    Task SetHallEnabledAsync(bool enabled) => Task.CompletedTask;
    Task CheckpointAsync() => Task.CompletedTask;
}

/// <summary>Only one caller steps this state machine. Idle and paused states own no input source.</summary>
public sealed class CaptureLifecycle(Func<Task<ICaptureSession>> start) : IAsyncDisposable
{
    private ICaptureSession? _session;
    public bool Active => _session != null;
    public string State { get; private set; } = "等待 CS2";
    public string Gsi => _session?.Gsi ?? "游戏启动后连接";
    public string Hall => _session?.Hall ?? "休眠 · 未占用设备";
    public int InputCount => _session?.InputCount ?? 0;
    public Task SetHallEnabledAsync(bool enabled) => _session?.SetHallEnabledAsync(enabled) ?? Task.CompletedTask;
    public Task CheckpointAsync()=>_session?.CheckpointAsync()??Task.CompletedTask;
    public async Task StepAsync(bool gameRunning, bool paused)
    {
        if ((!gameRunning || paused) && _session != null)
        {
            var ended = _session; _session = null;
            await ended.DisposeAsync(); // Finish and save the active match before releasing it.
        }
        if (gameRunning && !paused && _session == null) _session = await start();
        State = paused ? "采集已暂停" : _session?.State ?? "等待 CS2 · 空闲休眠";
    }
    public async ValueTask DisposeAsync()
    {
        if (_session == null) return;
        var ended = _session; _session = null; await ended.DisposeAsync();
    }
}
