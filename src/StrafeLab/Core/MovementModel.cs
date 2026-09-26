namespace StrafeLab.Core;

public sealed class PhysicsState
{
    public double VelocityX { get; set; }
    public double VelocityY { get; set; }
    public double Speed => Math.Sqrt((VelocityX * VelocityX) + (VelocityY * VelocityY));
    public long TimestampUs { get; set; }
    public bool InPreciseFireWindow { get; set; }
    public double Confidence { get; set; } = 0.74;
}

/// <summary>
/// A conservative ground movement approximation. CS2 does not publish the full
/// movement state through GSI, so the simulator is explicitly labeled as an estimate
/// and is later calibrated only with high-confidence demo observations.
/// </summary>
public sealed class MovementSimulator
{
    private readonly MovementModelParameters _parameters;
    private readonly PhysicsState _state = new();
    private long? _lastTimestampUs;
    private InputSnapshot _lastInput = new();
    private double _maxSpeed = 250;
    private long _uncertainUntil;
    public void MarkUncertain(long nowUs, long durationUs = 2_000_000) => _uncertainUntil = Math.Max(_uncertainUntil, nowUs + durationUs);

    public MovementSimulator(MovementModelParameters parameters)
    {
        _parameters = parameters;
    }

    public MovementModelParameters Parameters => _parameters;
    public PhysicsState State => _state;

    public PhysicsSample AdvanceTo(long timestampUs, InputSnapshot input, GsiSnapshot? gsi)
    {
        if (_lastTimestampUs is null)
        {
            _lastTimestampUs = timestampUs;
            _state.TimestampUs = timestampUs;
            _lastInput = input;
            _state.InPreciseFireWindow = _state.Speed <= _parameters.FullAccuracySpeed;
            return ToSample(timestampUs, input, gsi);
        }

        if (timestampUs < _lastTimestampUs.Value) return ToSample(_lastTimestampUs.Value,input,gsi);
        _maxSpeed = WeaponSpeed(gsi?.WeaponName, _parameters.MaxGroundSpeed);
        // The input edge is not the end of CS2's duck/unduck transition.
        // Keep estimates uncertain after either edge; only Demo can verify stance.
        if(input.Crouch!=_lastInput.Crouch)MarkUncertain(timestampUs,1_000_000);
        if (input.Jump) MarkUncertain(timestampUs);
        if (timestampUs - _lastTimestampUs.Value > 500_000) MarkUncertain(timestampUs);
        var elapsedSeconds = Math.Clamp((timestampUs - _lastTimestampUs.Value) / 1_000_000d, 0, 0.5);
        Integrate(_lastInput, elapsedSeconds);
        _lastTimestampUs = timestampUs;
        _lastInput = input;
        _state.TimestampUs = timestampUs;
        _state.InPreciseFireWindow = _state.Speed <= _parameters.FullAccuracySpeed;
        return ToSample(timestampUs, input, gsi);
    }

    public PhysicsSample AdvanceForTimer(long timestampUs, GsiSnapshot? gsi)
        => AdvanceTo(timestampUs, _lastInput, gsi);

    public void Reset()
    {
        _state.VelocityX = 0;
        _state.VelocityY = 0;
        _state.TimestampUs = 0;
        _state.InPreciseFireWindow = true;
        _state.Confidence = 0.74;
        _lastTimestampUs = null;
        _lastInput = new InputSnapshot();
    }

    private void Integrate(InputSnapshot input, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0)
        {
            return;
        }

        var tickSeconds = 1d / Math.Max(1, _parameters.SimulationTickRate);
        var steps = Math.Clamp((int)Math.Ceiling(elapsedSeconds / tickSeconds), 1, 256);
        var dt = elapsedSeconds / steps;
        for (var i = 0; i < steps; i++)
        {
            StepGround(input, dt);
        }
    }

    private void StepGround(InputSnapshot input, double dt)
    {
        var wishX = input.Horizontal;
        var wishY = input.Vertical;
        var wishLength = Math.Sqrt((wishX * wishX) + (wishY * wishY));
        if (wishLength > 1)
        {
            wishX /= wishLength;
            wishY /= wishLength;
            wishLength = 1;
        }

        var speed = _state.Speed;
        if (speed > 0)
        {
            var control = Math.Max(speed, _parameters.StopSpeed);
            var drop = control * _parameters.Friction * _parameters.SurfaceFriction * dt;
            var newSpeed = Math.Max(0, speed - drop);
            var scale = newSpeed / speed;
            _state.VelocityX *= scale;
            _state.VelocityY *= scale;
        }

        if (wishLength <= 0)
        {
            return;
        }

        var wishSpeed = _maxSpeed * wishLength * (input.Crouch ? 0.34 : input.Walk ? 0.52 : 1);
        var currentSpeed = (_state.VelocityX * wishX) + (_state.VelocityY * wishY);
        var addSpeed = wishSpeed - currentSpeed;
        if (addSpeed <= 0)
        {
            return;
        }

        var accelerationSpeed = _parameters.Accelerate * wishSpeed * dt * _parameters.SurfaceFriction;
        accelerationSpeed = Math.Min(accelerationSpeed, addSpeed);
        _state.VelocityX += wishX * accelerationSpeed;
        _state.VelocityY += wishY * accelerationSpeed;
    }

    public static double WeaponSpeed(string? weapon, double fallback=250) => weapon switch
    {
        "weapon_ak47" => 215, "weapon_m4a1" => 225, "weapon_m4a1_silencer" => 225,
        "weapon_awp" => 200, "weapon_ssg08" => 230, "weapon_aug" => 220, "weapon_sg556" => 210,
        "weapon_deagle" => 230, "weapon_glock" => 240, "weapon_usp_silencer" => 240,
        "weapon_hkp2000" => 240, "weapon_p250" => 240, "weapon_fiveseven" => 240,
        "weapon_tec9" => 240, "weapon_mp9" => 240, "weapon_mac10" => 240,
        "weapon_mp7" => 220, "weapon_mp5sd" => 235, "weapon_p90" => 230, "weapon_ump45" => 230,
        "weapon_galilar" => 215, "weapon_famas" => 220, "weapon_negev" => 150, "weapon_m249" => 195,
        _ => fallback
    };
    private PhysicsSample ToSample(long timestampUs, InputSnapshot input, GsiSnapshot? gsi)
    {
        var confidence = 0.35;
        if (gsi is not null)
        {
            var age = Math.Max(0, timestampUs - gsi.ReceivedAtUs);
            confidence = age <= 1_500_000 && gsi.IsAlive == true && gsi.PlayerActivity == "playing" && gsi.PlayerSteamId == gsi.ProviderSteamId && gsi.ProviderSteamId != null ? 0.8 : 0.35;
            if (gsi.IsAlive == false)
            {
                confidence *= 0.6;
            }
        }

        if (input.Jump || input.Crouch || input.Walk || timestampUs < _uncertainUntil) confidence = Math.Min(confidence,0.35);
        if (gsi?.WeaponName is "weapon_awp" or "weapon_ssg08" or "weapon_aug" or "weapon_sg556" or "weapon_scar20" or "weapon_g3sg1")
            confidence=Math.Min(confidence,.4); // GSI does not confirm scope state in normal play.
        _state.Confidence = confidence;

        return new PhysicsSample
        {
            TimestampUs = timestampUs,
            Weapon=gsi?.WeaponName,Walk=input.Walk,Crouch=input.Crouch,Jump=input.Jump,
            VelocityX = _state.VelocityX,
            VelocityY = _state.VelocityY,
            Speed = _state.Speed,
            InPreciseFireWindow = _state.InPreciseFireWindow,
            Confidence = confidence,
            W = input.W,
            A = input.A,
            S = input.S,
            D = input.D
        };
    }
}
