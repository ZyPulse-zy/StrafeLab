using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Win32;

namespace StrafeLab.Platform;

public static class GamePresence
{
    private static readonly object Gate = new();
    private static long _checked;
    private static bool _running;
    public static bool IsRunning()
    {
        if (Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE") == "1") return false;
        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (now - _checked < 1500) return _running;
            var processes = Process.GetProcessesByName("cs2");
            _running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            _checked = now;
            return _running;
        }
    }
}

public static class StartupRegistration
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "StrafeLab";
    public static string Command(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') || executable.Contains('\n'))
            throw new ArgumentException("需要完整的程序路径");
        string command = $"\"{executable}\" --collector";
        if (command.Length > 260) throw new ArgumentException("启动路径过长，请将程序放到较短的目录");
        return command;
    }
    public static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string;
    }
    public static bool IsEnabled => Read() != null;
    public static void Set(bool enabled, string executable, string dataRoot)
    {
        if (Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE") == "1")
            throw new InvalidOperationException("测试模式不允许改自启动");
        string? before = Read();
        string? desired = enabled ? Command(executable) : null;
        if (before == desired) return;
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "startup-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff") + ".json"),
            JsonSerializer.Serialize(new { key = KeyPath, name = ValueName, previous = before }));
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled) key.SetValue(ValueName, desired!, RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
    }
}

/// <summary>Current-user, same-session IPC; only small fixed commands, never executable payloads.</summary>
public sealed class LocalControlServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public static string Name(string role) => $"StrafeLab-{role}-{Process.GetCurrentProcess().SessionId}";
    public LocalControlServer(string role, Func<string, Task<string>> handler) => _loop = RunAsync(Name(role), handler);
    private async Task RunAsync(string name, Func<string, Task<string>> handler)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                // Bound the input instead of reading an arbitrarily long line.
                var chars = new char[64]; int count = 0;
                while (count < chars.Length)
                {
                    int n = await reader.ReadAsync(chars.AsMemory(count, 1), timeout.Token);
                    if (n == 0 || chars[count] == '\n') break;
                    count++;
                }
                string response = count >= chars.Length ? "invalid" : await handler(new string(chars, 0, count).Trim());
                await writer.WriteLineAsync(response.AsMemory(), timeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
    public static async Task<string?> SendAsync(string role, string command)
    {
        using var timeout = new CancellationTokenSource(1800);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", Name(role), PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
            return await reader.ReadLineAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return null; }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _loop; _stop.Dispose(); }
}

public sealed record CollectorStatus(int Pid, DateTime ProcessStartedUtc, DateTime UpdatedUtc, bool Paused,
    string State, string Gsi, string Hall, int Inputs, long WorkingSetBytes, long PrivateBytes, bool HallEnabled=true)
{
    public static CollectorStatus? Read(string root)
    {
        try
        {
            var status = JsonSerializer.Deserialize<CollectorStatus>(File.ReadAllText(Path.Combine(root, "collector-status.json")), StrafeLab.Core.DemoLibrary.Json);
            if (status == null || DateTime.UtcNow - status.UpdatedUtc > TimeSpan.FromSeconds(20)) return null;
            using var process = Process.GetProcessById(status.Pid);
            return Math.Abs((process.StartTime.ToUniversalTime() - status.ProcessStartedUtc).TotalSeconds) < 1 ? status : null;
        }
        catch { return null; }
    }
}

public static class AppLauncher
{
    public static Process? Start(string arguments) => Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments)
    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory });
    public static async Task EnsureCollectorAsync()
    {
        if (Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE") == "1") return;
        if (await LocalControlServer.SendAsync("collector", "ping") == null) Start("--collector")?.Dispose();
    }
}
