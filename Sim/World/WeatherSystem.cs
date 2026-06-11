namespace StruggleGame.Sim.World;

// Ambient weather state: rain intensity + a wind vector, both purely
// visual for now (no gameplay effects). Deterministic per seed — a
// front/clear phase machine draws its durations and peaks from a
// dedicated Random, and every value advances only inside Step(), so
// the weather ticks with sim speed and freezes with pause exactly like
// every other system.
//
// Time is WORLD seconds (SimRuntime multiplies real dt by
// SimSecondsPerRealSecond before calling Step), so "a front lasts half
// a world-hour" reads ~30 wall-clock seconds at 1x speed.
public sealed class WeatherSystem
{
    private readonly Random _rng;

    // 0..1 — 0 = clear, ~0.35 = drizzle, 1 = storm. Published verbatim
    // on the snapshot; the render side does its own display smoothing.
    public float RainIntensity { get; private set; }

    // Horizontal drift in tiles/sec (world-anchored). The rain shader
    // slants streaks by WindX/fall-speed and nudges fall by WindY.
    public float WindX { get; private set; }
    public float WindY { get; private set; }

    // Debug/harness override. While set, RainIntensity is pinned (and
    // wind strength still derives from it, so a forced storm also blows
    // hard). ClearOverride hands control back to the phase machine.
    private float? _overrideIntensity;
    private float? _overrideWindX;
    private float? _overrideWindY;

    // Phase machine: ramp toward _targetIntensity, hold until the phase
    // clock runs out, then draw the next phase. Mostly-clear bias: every
    // front is followed by a clear stretch a few times longer.
    private float _targetIntensity;
    private double _phaseRemainingSec;

    // Wind random-walk state. Direction wanders slowly; strength tracks
    // rain intensity (storm fronts blow harder) plus its own slow noise.
    private float _windAngle;
    private float _windNoise;

    // === Tuning (world seconds / tiles-per-second) ===
    private const double ClearMinSec = 30 * 60;     // clear stretch: 0.5..2 world-hours
    private const double ClearMaxSec = 120 * 60;
    private const double FrontMinSec = 15 * 60;     // a front holds 15..50 world-minutes
    private const double FrontMaxSec = 50 * 60;
    private const float RampPerSec = 1f / 300f;     // full 0→1 ramp over ~5 world-minutes
    private const float WindBaseTilesPerSec = 0.8f; // calm-weather drift
    private const float WindStormTilesPerSec = 5.0f; // extra drift at full storm
    private const float WindAngleWalkPerSec = 0.02f; // radians/sec random-walk rate
    private const float WindNoiseWalkPerSec = 0.05f;

    public WeatherSystem(int seed)
    {
        _rng = new Random(seed);
        _windAngle = (float)(_rng.NextDouble() * Math.PI * 2.0);
        // Start clear, partway into a clear stretch so worlds don't all
        // open with rain at the same wall-clock moment.
        _targetIntensity = 0f;
        _phaseRemainingSec = NextRange(ClearMinSec, ClearMaxSec) * _rng.NextDouble();
    }

    public void Step(float worldDt)
    {
        // Phase machine runs even under override so the ambient pattern
        // stays deterministic — releasing the override drops back into
        // whatever phase the walk reached, not a frozen copy.
        _phaseRemainingSec -= worldDt;
        if (_phaseRemainingSec <= 0)
        {
            if (_targetIntensity > 0f)
            {
                // Front passed — back to clear.
                _targetIntensity = 0f;
                _phaseRemainingSec = NextRange(ClearMinSec, ClearMaxSec);
            }
            else
            {
                // Next front: usually drizzle, sometimes a real storm.
                _targetIntensity = 0.2f + 0.8f * (float)(_rng.NextDouble() * _rng.NextDouble());
                _phaseRemainingSec = NextRange(FrontMinSec, FrontMaxSec);
            }
        }

        if (_overrideIntensity is float forced)
        {
            RainIntensity = forced;
        }
        else
        {
            float step = RampPerSec * worldDt;
            float delta = _targetIntensity - RainIntensity;
            if (Math.Abs(delta) <= step) RainIntensity = _targetIntensity;
            else RainIntensity += Math.Sign(delta) * step;
        }

        // Wind: slow direction wander + strength tied to rain intensity
        // (storms blow) with a little independent noise on top.
        _windAngle += (float)(_rng.NextDouble() - 0.5) * 2f * WindAngleWalkPerSec * worldDt;
        _windNoise += (float)(_rng.NextDouble() - 0.5) * 2f * WindNoiseWalkPerSec * worldDt;
        _windNoise = Math.Clamp(_windNoise, -0.3f, 0.3f);
        float strength = WindBaseTilesPerSec
            + WindStormTilesPerSec * Math.Clamp(RainIntensity + _windNoise, 0f, 1f);
        WindX = _overrideWindX ?? MathF.Cos(_windAngle) * strength;
        WindY = _overrideWindY ?? MathF.Sin(_windAngle) * strength * 0.25f;
    }

    // Pin intensity (and optionally the wind vector) — Dev-bar presets +
    // the visual harness. Snap, don't ramp: the render side already
    // fades its displayed intensity toward the snapshot value.
    public void SetOverride(float intensity, float? windX = null, float? windY = null)
    {
        _overrideIntensity = Math.Clamp(intensity, 0f, 1f);
        _overrideWindX = windX;
        _overrideWindY = windY;
    }

    public void ClearOverride()
    {
        _overrideIntensity = null;
        _overrideWindX = null;
        _overrideWindY = null;
    }

    private double NextRange(double min, double max) => min + _rng.NextDouble() * (max - min);
}
