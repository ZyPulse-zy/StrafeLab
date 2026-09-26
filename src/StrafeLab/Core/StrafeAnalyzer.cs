namespace StrafeLab.Core;
public sealed class AnalyzerUpdate
{
    public InputSnapshot Snapshot { get; init; } = new();
    public StrafeTransition? Transition { get; init; }
    public ShotEvent? Shot { get; init; }
}
/// <summary>Actual digital edges; Hall depth never substitutes for RT key state.</summary>
public sealed class StrafeAnalyzer
{
    private readonly HashSet<InputControl> _down = [];
    private readonly Dictionary<InputControl, long> _lastUp = [];
    private readonly List<StrafeTransition> _transitions;
    private int _segmentStart;
    public StrafeAnalyzer(List<StrafeTransition> transitions) => _transitions = transitions;
    public InputSnapshot Snapshot => new() {W=_down.Contains(InputControl.W),A=_down.Contains(InputControl.A),
        S=_down.Contains(InputControl.S),D=_down.Contains(InputControl.D),Walk=_down.Contains(InputControl.Walk),
        Crouch=_down.Contains(InputControl.Crouch),Jump=_down.Contains(InputControl.Jump)};
    public bool IsHeld(InputControl control)=>_down.Contains(control);
    public void Reset() { _segmentStart=_transitions.Count; _down.Clear(); _lastUp.Clear(); foreach(var t in _transitions.Where(t=>t.OverlapPending)) {t.OverlapPending=false;t.OverlapUs=null;} }
    public AnalyzerUpdate Process(InputEvent input, GsiSnapshot? gsi, PhysicsState? physics)
    {
        if (input.Action == InputAction.Analog || (input.Action == InputAction.Down) == _down.Contains(input.Control))
            return new() {Snapshot=Snapshot};
        bool pressed = input.Action == InputAction.Down;
        if (pressed) _down.Add(input.Control); else _down.Remove(input.Control);
        if (input.Control == InputControl.Mouse1)
        {
            if (!pressed) return new() {Snapshot=Snapshot};
            var t = _transitions.Skip(_segmentStart).LastOrDefault(t => input.TimestampUs >= t.TimestampUs && input.TimestampUs - t.TimestampUs <= 500_000);
            long? delta = t == null ? null : input.TimestampUs - t.TimestampUs;
            if (t != null) t.ShotDeltaUs ??= delta;
            return new() {Snapshot=Snapshot, Shot=new() {TimestampUs=input.TimestampUs,Confidence=input.Confidence,
                ModelConfidence=physics?.Confidence??0,WeaponName=gsi?.WeaponName,Round=gsi?.Round,Map=gsi?.Map,
                RelatedTransitionUs=t?.TimestampUs,DeltaFromTransitionUs=delta,EstimatedSpeed=physics?.Speed,
                IsWithinFireWindow=physics?.InPreciseFireWindow==true}};
        }
        if (input.Control is not (InputControl.W or InputControl.A or InputControl.S or InputControl.D)) return new() {Snapshot=Snapshot};
        if (!pressed)
        {
            _lastUp[input.Control]=input.TimestampUs;
            foreach (var t in _transitions.Skip(_segmentStart).Reverse())
            {
                if (t.TimestampUs > input.TimestampUs) continue;
                if (t.To == input.Control && t.TargetHoldUs == null)
                    t.ReverseHoldUs = t.TargetHoldUs = input.TimestampUs-t.TimestampUs;
                if (t.From == input.Control && t.OppositeReleasedAtUs == null) t.OppositeReleasedAtUs=input.TimestampUs;
                if (t.OverlapPending && (t.To==input.Control || t.From==input.Control))
                { t.OverlapUs=input.TimestampUs-t.TimestampUs;t.OverlapPending=false; }
            }
            return new() {Snapshot=Snapshot};
        }
        var opposite = input.Control switch {InputControl.A=>InputControl.D,InputControl.D=>InputControl.A,InputControl.W=>InputControl.S,_=>InputControl.W};
        bool overlap = _down.Contains(opposite);
        long gap = _lastUp.TryGetValue(opposite,out var up) ? input.TimestampUs-up : long.MaxValue;
        if (!overlap && (gap < 0 || gap > 500_000)) return new() {Snapshot=Snapshot};
        var transition = new StrafeTransition {TimestampUs=input.TimestampUs,
            Axis=input.Control is InputControl.A or InputControl.D ? StrafeAxis.Horizontal : StrafeAxis.Vertical,
            From=opposite,To=input.Control,GapUs=overlap?0:gap,OverlapUs=overlap?null:0,OverlapPending=overlap,
            OppositeReleasedAtUs=overlap?null:up,Confidence=input.Confidence,EstimatedSpeed=physics?.Speed,
            IsWithinFireWindow=physics?.InPreciseFireWindow==true,Map=gsi?.Map,Round=gsi?.Round,WeaponName=gsi?.WeaponName};
        _lastUp.Remove(opposite); _transitions.Add(transition);
        return new() {Snapshot=Snapshot,Transition=transition};
    }
}
