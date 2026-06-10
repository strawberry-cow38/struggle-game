using System.Collections.Generic;
using Godot;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.Audio;

// Plays combat sound effects driven off the sim snapshot: a gunshot whenever a
// pawn fires (DummyState.ShotTick advancing), picking the sound by the firing
// pawn's weapon. Streams are loaded at runtime so the source-pull build needs
// no Godot .import step.
public partial class CombatSfx : Node
{
    private const string Dir = "res://Game/Assets/Audio/";

    public SimHost? Host { get; set; }

    // Per-weapon shot players, keyed by the weapon's item path.
    private readonly Dictionary<string, AudioStreamPlayer> _players = new();
    private AudioStreamPlayer? _default;
    private long _lastShotTick = 0;

    public override void _Ready()
    {
        var map = new (ItemDef weapon, string ogg)[]
        {
            (ItemCatalog.AssaultRifle, "Shot_GTEK556mm.ogg"),
            (ItemCatalog.SubmachineGun, "Shot_GTEK_MP5Type.ogg"),
            (ItemCatalog.BoltActionRifle, "Shot_GTEK762mm.ogg"),
            (ItemCatalog.Akm, "Shot_GTEK762mmSoviet.ogg"),
            (ItemCatalog.Lmg, "Shot_GTEK556mm_BeltA.ogg"),
        };
        foreach (var (weapon, ogg) in map)
        {
            var p = new AudioStreamPlayer { Name = $"Shot_{weapon.Id}", Bus = "Master" };
            AddChild(p);
            var stream = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath(Dir + ogg));
            if (stream is not null) p.Stream = stream;
            _players[weapon.FullPath] = p;
            _default ??= p; // the rifle is the fallback report
        }
    }

    public override void _Process(double delta)
    {
        if (Host is null) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;

        // Newest shot tick across all pawns + the weapon that produced it; play
        // once per new shot (simultaneous shots collapse to one report).
        long maxShot = _lastShotTick;
        string weapon = "";
        foreach (var d in snap.Dummies)
            if (d.HasRangedWeapon && d.ShotTick > maxShot) { maxShot = d.ShotTick; weapon = WeaponPathOf(d); }

        if (maxShot > _lastShotTick)
        {
            _lastShotTick = maxShot;
            var player = (weapon.Length > 0 && _players.TryGetValue(weapon, out var pl)) ? pl : _default;
            player?.Play();
        }
    }

    private static string WeaponPathOf(in DummyState d)
    {
        foreach (var eq in d.Equipped)
            if (ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var def) && def.Ranged is not null)
                return eq.ItemPath;
        return "";
    }
}
