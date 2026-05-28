using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Render;

// Cosmetic rain overlay drawn in WORLD space. Particles spawn above the
// camera's visible rect, fall in world coords, and are clipped per tile
// against the roof map — drops over roofed tiles disappear.
public partial class WeatherFx : Node2D
{
    public SimHost? Host { get; set; }
    public float Intensity { get; set; }
    public float WindX { get; set; }

    private const int MaxParticles = 6000;
    private const float SpawnPerSecPerTileAtFull = 0.35f;
    private const float DropFallSpeed = 1400f;
    private const float WindSpeed = 1100f;
    private const float DropLifeSec = 1.2f;
    private const float DropLength = 18f;
    private const float DropWidth = 5f;
    private const float SpawnAboveCameraPx = 200f;

    private struct Drop
    {
        public Vector2 Pos;
        public Vector2 Vel;
        public float Life;
    }

    private readonly Drop[] _drops = new Drop[MaxParticles];
    private int _count;
    private float _spawnAccum;
    private readonly RandomNumberGenerator _rng = new();

    private byte[]? _roofTiles;
    private int _mapWidth;
    private int _mapHeight;
    private long _lastRoofVersion = -1;

    public override void _Ready()
    {
        _rng.Randomize();
        ZIndex = 50;
    }

    public override void _Process(double delta)
    {
        if (Host is null) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        if (snap.RoofVersion != _lastRoofVersion || _roofTiles is null)
        {
            _roofTiles = Host.CopyRoofTilesForRender();
            _mapWidth = Host.Map.Width;
            _mapHeight = Host.Map.Height;
            _lastRoofVersion = snap.RoofVersion;
        }
        float dt = (float)delta;
        AgeDrops(dt);
        SpawnDrops(dt);
        QueueRedraw();
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

    private (Vector2 min, Vector2 max) GetVisibleWorldRect()
    {
        var vp = GetViewport().GetVisibleRect();
        var xform = GetCanvasTransform().AffineInverse();
        var a = xform * vp.Position;
        var b = xform * (vp.Position + vp.Size);
        return (new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y)),
                new Vector2(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y)));
    }

    private void SpawnDrops(float dt)
    {
        if (Intensity <= 0f) return;
        var (min, max) = GetVisibleWorldRect();
        float areaTiles = ((max.X - min.X) / SimConstants.PixelsPerTile)
                        * ((max.Y - min.Y) / SimConstants.PixelsPerTile);
        if (areaTiles <= 0f) return;
        float rate = areaTiles * SpawnPerSecPerTileAtFull * Mathf.Clamp(Intensity, 0f, 1f);
        _spawnAccum += dt * rate;
        float horizVel = WindX * WindSpeed;
        // Widen the spawn band against wind so drops blowing across the
        // viewport still cover the downwind edge.
        float padLeft = horizVel < 0f ? -horizVel * DropLifeSec : 0f;
        float padRight = horizVel > 0f ? horizVel * DropLifeSec : 0f;
        while (_spawnAccum >= 1f && _count < MaxParticles)
        {
            _spawnAccum -= 1f;
            float x = _rng.RandfRange(min.X - padLeft, max.X + padRight);
            float y = _rng.RandfRange(min.Y - SpawnAboveCameraPx, min.Y);
            float speedJitter = _rng.RandfRange(0.85f, 1.15f);
            _drops[_count++] = new Drop
            {
                Pos = new Vector2(x, y),
                Vel = new Vector2(horizVel, DropFallSpeed * speedJitter),
                Life = DropLifeSec,
            };
        }
        if (_spawnAccum > 4f) _spawnAccum = 4f;
    }

    public override void _Draw()
    {
        if (_count == 0) return;
        var col = new Color(0.72f, 0.85f, 1f, 0.82f);
        int w = _mapWidth, h = _mapHeight;
        var roof = _roofTiles;
        for (int i = 0; i < _count; i++)
        {
            var d = _drops[i];
            int tx = Mathf.FloorToInt(d.Pos.X / SimConstants.PixelsPerTile);
            int ty = Mathf.FloorToInt(d.Pos.Y / SimConstants.PixelsPerTile);
            if (roof is not null && (uint)tx < (uint)w && (uint)ty < (uint)h && roof[ty * w + tx] != 0) continue;
            var dir = d.Vel.Normalized();
            var tail = d.Pos - dir * DropLength;
            DrawLine(tail, d.Pos, col, DropWidth, antialiased: true);
        }
    }
}
