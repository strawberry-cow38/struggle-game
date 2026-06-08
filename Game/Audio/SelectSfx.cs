using Godot;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Audio;

// Plays the UI select sound whenever the player makes a new (non-empty)
// selection of anything — colonist, enemy, tree, building, zone, etc. Detects
// changes by hashing all of Host's selection sets each frame; a short cooldown
// stops drag-select from machine-gunning the sound. Stream loaded at runtime so
// the source-pull build needs no Godot .import step.
public partial class SelectSfx : Node
{
    private const string Dir = "res://Game/Assets/Audio/";

    public SimHost? Host { get; set; }

    private AudioStreamPlayer _player = null!;
    private long _lastSig = long.MinValue;
    private double _cooldown;

    public override void _Ready()
    {
        _player = new AudioStreamPlayer { Name = "SelectPlayer", Bus = "Master" };
        AddChild(_player);
        var stream = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath(Dir + "Select.ogg"));
        if (stream is not null) _player.Stream = stream;
    }

    public override void _Process(double delta)
    {
        if (Host is null) return;
        if (_cooldown > 0) _cooldown -= delta;

        long sig = 17;
        bool any = false;
        void AddId(int? v) { sig = sig * 31 + (v ?? -1); if (v is not null) any = true; }
        void AddIds(int[] a) { sig = sig * 31 + a.Length; if (a.Length > 0) { sig = sig * 31 + a[0]; any = true; } }
        void AddTiles(TilePos[] a) { sig = sig * 31 + a.Length; if (a.Length > 0) { sig = sig * 31 + a[0].GetHashCode(); any = true; } }

        AddId(Host.SelectedDummyId);
        AddId(Host.SelectedStockpileId);
        AddId(Host.SelectedGrowZoneId);
        AddIds(Host.SelectedDummyIds);
        AddIds(Host.SelectedTreeIds);
        AddIds(Host.SelectedWoodIds);
        AddIds(Host.SelectedCropIds);
        AddTiles(Host.SelectedWallTiles);
        AddTiles(Host.SelectedDoorTiles);
        AddTiles(Host.SelectedBlueprintTiles);
        AddTiles(Host.SelectedLampTiles);
        AddTiles(Host.SelectedBedTiles);
        AddTiles(Host.SelectedUrBoardTiles);
        AddTiles(Host.SelectedStoveTiles);

        if (sig != _lastSig)
        {
            bool wasInitialized = _lastSig != long.MinValue;
            _lastSig = sig;
            if (wasInitialized && any && _cooldown <= 0)
            {
                _player.Play();
                _cooldown = 0.07;
            }
        }
    }
}
