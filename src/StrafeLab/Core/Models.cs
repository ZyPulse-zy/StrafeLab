using System.Text.Json.Serialization;

namespace StrafeLab.Core;

public enum InputControl
{
    W,
    A,
    S,
    D,
    Mouse1,
    Walk,
    Crouch,
    Jump
}

public enum InputAction
{
    Down,
    Up,
    Analog
}

public enum InputSourceKind
{
    Ace68Hall,
    RawInput,
    Synthetic
}

public enum ConfidenceBand
{
    Low,
    Medium,
    High
}

public enum StrafeAxis
{
    Horizontal,
    Vertical
}

public sealed record InputEvent
{
    public long TimestampUs { get; init; }
    public InputControl Control { get; init; }
    public InputAction Action { get; init; }
    public double Value { get; init; }
    public InputSourceKind Source { get; init; }
    public double Confidence { get; init; } = 0.9;
    public string? DevicePath { get; init; }
}

public sealed class InputSnapshot
{
    public bool Walk { get; init; }
    public bool Crouch { get; init; }
    public bool Jump { get; init; }
    public bool W { get; init; }
    public bool A { get; init; }
    public bool S { get; init; }
    public bool D { get; init; }

    public double Horizontal => (D ? 1d : 0d) - (A ? 1d : 0d);
    public double Vertical => (W ? 1d : 0d) - (S ? 1d : 0d);

    public bool IsMovingInput => W || A || S || D;
}

public sealed class GsiSnapshot
{
    public long ReceivedAtUs { get; init; }
    public string? Map { get; init; }
    public string? MapPhase { get; init; }
    public int? Round { get; init; }
    public string? RoundPhase { get; init; }
    public string? PlayerSteamId { get; init; }
    public string? ProviderSteamId { get; init; }
    public int? AmmoClip { get; init; }
    public string? PlayerName { get; init; }
    public string? PlayerActivity { get; init; }
    public string? WeaponName { get; init; }
    public string? WeaponState { get; init; }
    public int? Health { get; init; }
    public int? Armor { get; init; }
    public bool? IsAlive { get; init; }
    public double? PhaseCountdownSeconds { get; init; }
    public long? GameTimeMs { get; init; }
    public string? RawHash { get; init; }
}

public sealed record StrafeTransition
{
    public long TimestampUs { get; init; }
    public StrafeAxis Axis { get; init; }
    public InputControl From { get; init; }
    public InputControl To { get; init; }
    public long? GapUs { get; set; }
    public long? OverlapUs { get; set; }
    public bool OverlapPending { get; set; }
    public long? OppositeReleasedAtUs { get; set; }
    public long? ReverseHoldUs { get; set; }
    public long? TargetHoldUs { get; set; }
    public long? ShotDeltaUs { get; set; }
    public double? EstimatedSpeed { get; set; }
    public bool IsWithinFireWindow { get; set; }
    public double Confidence { get; init; }
    public string? WeaponName { get; init; }
    public int? Round { get; init; }
    public string? Map { get; init; }

    [JsonIgnore]
    public double GapMs => (GapUs ?? 0) / 1000d;

    [JsonIgnore]
    public double OverlapMs => (OverlapUs ?? 0) / 1000d;
}

public sealed class ShotEvent
{
    public double ModelConfidence { get; init; }
    public long TimestampUs { get; init; }
    public double Confidence { get; init; }
    public string? WeaponName { get; init; }
    public int? Round { get; init; }
    public string? Map { get; init; }
    public long? RelatedTransitionUs { get; init; }
    public long? DeltaFromTransitionUs { get; init; }
    public double? EstimatedSpeed { get; init; }
    public bool IsWithinFireWindow { get; init; }
}

public sealed class PhysicsSample
{
    public string? Weapon { get; init; }
    public bool Walk { get; init; }
    public bool Crouch { get; init; }
    public bool Jump { get; init; }
    public long TimestampUs { get; init; }
    public double VelocityX { get; init; }
    public double VelocityY { get; init; }
    public double Speed { get; init; }
    public bool InPreciseFireWindow { get; init; }
    public double Confidence { get; init; }
    public bool W { get; init; }
    public bool A { get; init; }
    public bool S { get; init; }
    public bool D { get; init; }
}

public sealed class GsiEventRecord
{
    public GsiSnapshot Snapshot { get; init; } = new();
}

public sealed class DemoObservation
{
    public double? VelocityModifier { get; init; }
    public bool? IsWalking { get; init; }
    public double? MaxSpeed { get; init; }
    public bool? IsAlive { get; init; }
    public bool? OnGround { get; init; }
    public bool? IsScoped { get; init; }
    public double? DuckAmount { get; init; }
    public double? Yaw { get; init; }
    public int? Round { get; init; }
    public int? MoveType { get; init; }
    public int Tick { get; init; }
    public double TimeSeconds { get; init; }
    public string Kind { get; init; } = "tick";
    public string? EventName { get; init; }
    public string? Map { get; init; }
    public double? TickRate { get; init; }
    public string? SteamId { get; init; }
    public int? EntityIndex { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public double? VelocityX { get; init; }
    public double? VelocityY { get; init; }
    public double? VelocityZ { get; init; }
    public string? Weapon { get; init; }
}

public sealed class DemoParseResult
{
    public string FilePath { get; init; } = string.Empty;
    public bool IsSource2 { get; init; }
    public string? MapHint { get; init; }
    public int? PlaybackTicks { get; init; }
    public double? TickRate { get; init; }
    public string Parser { get; init; } = "header-only";
    public string? Error { get; init; }
    public List<DemoObservation> Observations { get; init; } = [];
}

public sealed class DemoCandidate
{
    public string FilePath { get; init; } = string.Empty;
    public long LengthBytes { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public int Score { get; init; }
    public bool IsSource2 { get; init; }
    public string? MapHint { get; init; }
}

public sealed class AlignmentResult
{
    public bool IsReliable { get; init; }
    public string? SteamId { get; init; }
    public double StartLocalSeconds { get; init; }
    public double EndLocalSeconds { get; init; }
    public double OffsetSeconds { get; init; }
    public double Scale { get; init; } = 1.0;
    public double ResidualRmsMs { get; init; }
    public int AnchorCount { get; init; }
    public int InlierCount { get; init; }
    public List<string> Anchors { get; init; } = [];
    public string Explanation { get; init; } = string.Empty;
}

public sealed class CalibrationResult
{
    public bool Applied { get; init; }
    public int CandidateCount { get; init; }
    public int UsedSampleCount { get; init; }
    public int RejectedLowConfidenceCount { get; init; }
    public double? ObservedAcceleration { get; init; }
    public double? ObservedFriction { get; init; }
    public double? ResidualRms { get; init; }
    public string Explanation { get; init; } = string.Empty;
}

public sealed class MovementModelParameters
{
    public double MaxGroundSpeed { get; set; } = 250.0;
    public double Accelerate { get; set; } = 5.5;
    public double Friction { get; set; } = 5.2;
    public double StopSpeed { get; set; } = 80.0;
    public double SurfaceFriction { get; set; } = 1.0;
    public double FullAccuracySpeed { get; set; } = 34.0; // conservative low-speed heuristic, not total weapon accuracy
    public int SimulationTickRate { get; set; } = 128;
    public string CalibrationSource { get; set; } = "default CS2 ground model";
}

public sealed class SessionDocument
{
    public int SchemaVersion { get; init; } = 2;
    public long Revision { get; set; }
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public long SessionStartUs { get; init; } = TimeUtil.NowMicroseconds();
    public DateTime? EndedAtUtc { get; set; }
    public string InputSource { get; set; } = "Raw Input";
    public string? DeviceName { get; set; }
    public int? DeviceVendorId { get; set; }
    public int? DeviceProductId { get; set; }
    public string? Map { get; set; }
    public bool DiagnosticMode { get; set; }
    public string? PlayerSteamId { get; set; }
    public List<StrafeLab.Input.HallSample> HallSamples { get; init; } = [];
    public string? DemoPath { get; set; }
    public AlignmentResult? DemoAlignment { get; set; }
    public CalibrationResult? Calibration { get; set; }
    public MovementModelParameters MovementModel { get; set; } = new();
    public List<InputEvent> InputEvents { get; init; } = [];
    public List<StrafeTransition> Transitions { get; init; } = [];
    public List<ShotEvent> Shots { get; init; } = [];
    public List<PhysicsSample> PhysicsSamples { get; init; } = [];
    public List<GsiEventRecord> GsiEvents { get; init; } = [];

    [JsonIgnore]
    public int HighConfidenceTransitionCount => Transitions.Count(x => x.Confidence >= 0.75);
}

public sealed class SessionSummary
{
    public string SessionId { get; init; } = string.Empty;
    public DateTime StartedAtUtc { get; init; }
    public string Map { get; init; } = "未知地图";
    public int TransitionCount { get; init; }
    public int ShotCount { get; init; }
    public double AverageGapMs { get; init; }
    public double AverageOverlapMs { get; init; }
    public double FireWindowRate { get; init; }
    public double ConfidenceRate { get; init; }
}

public sealed class LiveMetrics
{
    public int ModelShotCount { get; set; }
    public string Status { get; set; } = "就绪";
    public string InputSource { get; set; } = "等待探测";
    public string GsiStatus { get; set; } = "未连接";
    public string DeviceStatus { get; set; } = "未探测";
    public string DemoStatus { get; set; } = "未匹配";
    public double AverageGapMs { get; set; }
    public double AverageOverlapMs { get; set; }
    public double AverageShotDeltaMs { get; set; }
    public double AverageSpeedAtShot { get; set; }
    public double FireWindowRate { get; set; }
    public double ConfidenceRate { get; set; }
    public int TransitionCount { get; set; }
    public int ShotCount { get; set; }
    public int PhysicsSampleCount { get; set; }
    public double CurrentSpeed { get; set; }
    public string CurrentWeapon { get; set; } = "—";
    public string CurrentRound { get; set; } = "—";
    public string CurrentMap { get; set; } = "—";
    public List<double> SpeedHistory { get; } = [];
}

public static class TimeUtil
{
    public static long StopwatchToMicroseconds(long timestamp)
        => (long)(timestamp * (1_000_000d / System.Diagnostics.Stopwatch.Frequency));

    public static long NowMicroseconds()
        => StopwatchToMicroseconds(System.Diagnostics.Stopwatch.GetTimestamp());
}
