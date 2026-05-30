using Godot;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.Audio;

// Plays combat sound effects driven off the sim snapshot. Right now: a
// gunshot whenever a colonist fires (detected via DummyState.ShotTick
// advancing). Loaded at runtime so the source-pull build doesn't need a
// Godot .import step for the asset.
public partial class CombatSfx : Node
{
    private const string ShotPath = "res://Game/Assets/Audio/Shot_GTEK556mm.ogg";

    public SimHost? Host { get; set; }

    private AudioStreamPlayer _shotPlayer = null!;
    private long _lastShotTick = 0;

    public override void _Ready()
    {
        _shotPlayer = new AudioStreamPlayer { Name = "ShotPlayer", Bus = "Master" };
        AddChild(_shotPlayer);
        var stream = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath(ShotPath));
        if (stream is not null) _shotPlayer.Stream = stream;
    }

    public override void _Process(double delta)
    {
        if (Host is null || _shotPlayer.Stream is null) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;

        // Find the most recent shot tick across all colonists; play once per
        // new shot tick (simultaneous shots collapse to one report).
        long maxShot = _lastShotTick;
        foreach (var d in snap.Dummies)
            if (d.HasRangedWeapon && d.ShotTick > maxShot) maxShot = d.ShotTick;
        // (ShotTick is 0 until a pawn first fires, so startup never triggers.)

        if (maxShot > _lastShotTick)
        {
            _lastShotTick = maxShot;
            _shotPlayer.Play();
        }
    }
}
