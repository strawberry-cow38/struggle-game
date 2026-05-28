using Godot;

namespace StruggleGame.Game.Render;

// Cosmetic rain overlay. Spawns fat raindrop particles in screen space,
// independent of camera + sim. Intensity in [0,1] controls spawn rate;
// WindX in [-1,1] tilts horizontal velocity.
public partial class WeatherFx : CanvasLayer
{
    public float Intensity { get; set; }
    public float WindX { get; set; }

    private const int MaxParticles = 2048;
    private const float SpawnPerSecAtFullIntensity = 1400f;
    private const float DropFallSpeed = 1400f;
    private const float WindSpeed = 900f;
    private const float DropLifeSec = 0.55f;
    private const float DropLength = 14f;
    private const float DropWidth = 4.5f;

    private struct Drop
    {
        public Vector2 Pos;
        public Vector2 Vel;
        public float Life;
    }

    private readonly Drop[] _drops = new Drop[MaxParticles];
    private int _count;
    private float _spawnAccum;
    private Control _canvas = null!;
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        Layer = 80;
        _rng.Randomize();
        _canvas = new Control
        {
            Name = "RainCanvas",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _canvas.Draw += DrawDrops;
        AddChild(_canvas);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        AgeDrops(dt);
        SpawnDrops(dt);
        _canvas.QueueRedraw();
    }

    private void AgeDrops(float dt)
    {
        int write = 0;
        for (int i = 0; i < _count; i++)
        {
            ref var d = ref _drops[i];
            d.Pos += d.Vel * dt;
            d.Life -= dt;
            if (d.Life <= 0f) continue;
            if (write != i) _drops[write] = d;
            write++;
        }
        _count = write;
    }

    private void SpawnDrops(float dt)
    {
        if (Intensity <= 0f) return;
        _spawnAccum += dt * SpawnPerSecAtFullIntensity * Mathf.Clamp(Intensity, 0f, 1f);
        var vp = GetViewport().GetVisibleRect().Size;
        float horizVel = WindX * WindSpeed;
        float spawnLeftMargin = horizVel < 0f ? 200f : 0f;
        float spawnRightMargin = horizVel > 0f ? 200f : 0f;
        while (_spawnAccum >= 1f && _count < MaxParticles)
        {
            _spawnAccum -= 1f;
            float x = _rng.RandfRange(-spawnLeftMargin, vp.X + spawnRightMargin);
            float y = _rng.RandfRange(-80f, 0f);
            float speedJitter = _rng.RandfRange(0.85f, 1.15f);
            _drops[_count++] = new Drop
            {
                Pos = new Vector2(x, y),
                Vel = new Vector2(horizVel, DropFallSpeed * speedJitter),
                Life = DropLifeSec,
            };
        }
        if (_spawnAccum > 2f) _spawnAccum = 2f;
    }

    private void DrawDrops()
    {
        if (_count == 0) return;
        var col = new Color(0.72f, 0.85f, 1f, 0.78f);
        for (int i = 0; i < _count; i++)
        {
            var d = _drops[i];
            var dir = d.Vel.Normalized();
            var tail = d.Pos - dir * DropLength;
            _canvas.DrawLine(tail, d.Pos, col, DropWidth, antialiased: true);
        }
    }
}
