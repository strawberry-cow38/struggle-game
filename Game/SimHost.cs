using System.Diagnostics;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Diagnostics;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game;

// Owns the SimRuntime and runs it on a dedicated thread at the fixed
// tick rate. Publishes the latest immutable SimSnapshot via Volatile.Write
// so the Godot main thread can read without locking. Game code must NEVER
// touch SimRuntime.Store directly.
public sealed class SimHost : IDisposable
{
    // Swapped wholesale by Reroll; loop reads it each tick. Reference
    // writes are atomic in the CLR, but we also take _swapLock during
    // Step() so a reroll never lands mid-tick.
    private SimRuntime _sim;
    private readonly object _swapLock = new();
    private readonly Thread _thread;
    private volatile bool _running;
    private volatile bool _paused;
    private volatile int _tickHz = SimConstants.TickHz;
    private SimSnapshot? _latest;
    private volatile float _actualTps;
    // Pawn selection. The sim-snapshot path/order display reads the
    // _selectedDummyId field (first selected pawn); the multi-array
    // _selectedDummyIds is what panels + Bootstrap shortcuts iterate
    // to apply commands across the whole selection.
    private int _selectedDummyId = -1;
    private int[] _selectedDummyIds = Array.Empty<int>();

    // Tree selection set. Game thread writes via SelectedTreeIds setter
    // (replaces atomically); sim thread reads via Volatile each tick.
    private int[] _selectedTreeIds = Array.Empty<int>();

    public TileMap Map => _sim.Map;
    public SimWatcher Watcher => _sim.Watcher;

    public int TickHz => _tickHz;

    public float ActualTps => _actualTps;

    public SimHost() : this(1337) { }

    public SimHost(int seed)
    {
        _sim = new SimRuntime(seed);
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sim" };
        _thread.Start();
    }

    // Wipe the current sim and spin up a fresh one with a new seed. All
    // pawn/tile/tree selections are cleared because their entity ids and
    // tile contents are about to belong to a different world. Pause state
    // is preserved.
    public void Reroll(int seed)
    {
        lock (_swapLock)
        {
            _sim = new SimRuntime(seed);
            Volatile.Write(ref _selectedDummyId, -1);
            Volatile.Write(ref _selectedDummyIds, Array.Empty<int>());
            Volatile.Write(ref _selectedTreeIds, Array.Empty<int>());
            Volatile.Write(ref _selectedWoodIds, Array.Empty<int>());
            Volatile.Write(ref _selectedStockpileId, -1);
            Volatile.Write(ref _selectedWallTiles, Array.Empty<TilePos>());
            Volatile.Write(ref _selectedDoorTiles, Array.Empty<TilePos>());
            Volatile.Write(ref _selectedBlueprintTiles, Array.Empty<TilePos>());
            Volatile.Write(ref _selectedLampTiles, Array.Empty<TilePos>());
            Volatile.Write(ref _selectedBedTiles, Array.Empty<TilePos>());
            Volatile.Write(ref _selectedStoveTiles, Array.Empty<TilePos>());
            Volatile.Write(ref _latest, _sim.BuildSnapshot(null, null, null, null));
        }
    }

    public SimSnapshot? LatestSnapshot => Volatile.Read(ref _latest);

    // Switch tick rate at runtime. Loop reads _tickHz each iteration so the
    // change picks up next tick boundary without a restart.
    public void SetTickHz(int hz)
    {
        if (hz < 1) hz = 1;
        _tickHz = hz;
    }

    // Pause/resume the sim loop. Used by the harness to hold the sim
    // still while Godot finishes warming up (loading textures, JITting,
    // etc.) so the first few captured frames aren't a giant catch-up
    // burst.
    public void SetPaused(bool paused) => _paused = paused;

    public bool IsPaused => _paused;

    // Step the sim directly from the caller's thread (intended for the
    // Godot main thread in harness/video-capture mode). Pair with
    // SetPaused(true) on the sim loop so it doesn't race the manual
    // stepper. Publishes a snapshot exactly like the loop would.
    public void StepManual(float dt)
    {
        lock (_swapLock)
        {
            _sim.Step(dt);
            int sel = Volatile.Read(ref _selectedDummyId);
            var ids = Volatile.Read(ref _selectedDummyIds);
            var trees = Volatile.Read(ref _selectedTreeIds);
            var woods = Volatile.Read(ref _selectedWoodIds);
            Volatile.Write(ref _latest, _sim.BuildSnapshot(ids.Length == 1 ? ids[0] : (int?)null, ids.Length > 0 ? ids : null, trees.Length > 0 ? trees : null, woods.Length > 0 ? woods : null));
        }
    }

    public int? SelectedDummyId
    {
        get { int v = Volatile.Read(ref _selectedDummyId); return v >= 0 ? v : null; }
        set
        {
            Volatile.Write(ref _selectedDummyId, value ?? -1);
            Volatile.Write(ref _selectedDummyIds, value is int id ? new[] { id } : Array.Empty<int>());
        }
    }

    public int[] SelectedDummyIds
    {
        get => Volatile.Read(ref _selectedDummyIds);
        set
        {
            var arr = value ?? Array.Empty<int>();
            Volatile.Write(ref _selectedDummyIds, arr);
            Volatile.Write(ref _selectedDummyId, arr.Length > 0 ? arr[0] : -1);
        }
    }

    public int[] SelectedTreeIds
    {
        get => Volatile.Read(ref _selectedTreeIds);
        set => Volatile.Write(ref _selectedTreeIds, value ?? Array.Empty<int>());
    }

    // -1 = no selection. Game thread reads + writes (panel UI); sim thread
    // doesn't read it (no per-tick effect), so a plain int + Volatile pair
    // is enough.
    private int _selectedStockpileId = -1;
    public int? SelectedStockpileId
    {
        get { int v = Volatile.Read(ref _selectedStockpileId); return v >= 0 ? v : null; }
        set { Volatile.Write(ref _selectedStockpileId, value ?? -1); }
    }

    private int _selectedGrowZoneId = -1;
    public int? SelectedGrowZoneId
    {
        get { int v = Volatile.Read(ref _selectedGrowZoneId); return v >= 0 ? v : null; }
        set { Volatile.Write(ref _selectedGrowZoneId, value ?? -1); }
    }

    private int[] _selectedWoodIds = Array.Empty<int>();
    public int[] SelectedWoodIds
    {
        get => Volatile.Read(ref _selectedWoodIds);
        set => Volatile.Write(ref _selectedWoodIds, value ?? Array.Empty<int>());
    }

    private int[] _selectedCropIds = Array.Empty<int>();
    public int[] SelectedCropIds
    {
        get => Volatile.Read(ref _selectedCropIds);
        set => Volatile.Write(ref _selectedCropIds, value ?? Array.Empty<int>());
    }

    // Tile selections (walls / doors / blueprints) are multi-select. The
    // *Tiles array is the source of truth; the singular *Tile property
    // is "first element or null" for callers that only need one and for
    // the snapshot/path code that wasn't built for multi yet.
    private TilePos[] _selectedWallTiles = Array.Empty<TilePos>();
    public TilePos[] SelectedWallTiles
    {
        get => Volatile.Read(ref _selectedWallTiles);
        set => Volatile.Write(ref _selectedWallTiles, value ?? Array.Empty<TilePos>());
    }
    public TilePos? SelectedWallTile
    {
        get { var a = Volatile.Read(ref _selectedWallTiles); return a.Length > 0 ? a[0] : null; }
        set => Volatile.Write(ref _selectedWallTiles, value is TilePos t ? new[] { t } : Array.Empty<TilePos>());
    }

    private TilePos[] _selectedDoorTiles = Array.Empty<TilePos>();
    public TilePos[] SelectedDoorTiles
    {
        get => Volatile.Read(ref _selectedDoorTiles);
        set => Volatile.Write(ref _selectedDoorTiles, value ?? Array.Empty<TilePos>());
    }
    public TilePos? SelectedDoorTile
    {
        get { var a = Volatile.Read(ref _selectedDoorTiles); return a.Length > 0 ? a[0] : null; }
        set => Volatile.Write(ref _selectedDoorTiles, value is TilePos t ? new[] { t } : Array.Empty<TilePos>());
    }

    private TilePos[] _selectedBlueprintTiles = Array.Empty<TilePos>();
    public TilePos[] SelectedBlueprintTiles
    {
        get => Volatile.Read(ref _selectedBlueprintTiles);
        set => Volatile.Write(ref _selectedBlueprintTiles, value ?? Array.Empty<TilePos>());
    }
    public TilePos? SelectedBlueprintTile
    {
        get { var a = Volatile.Read(ref _selectedBlueprintTiles); return a.Length > 0 ? a[0] : null; }
        set => Volatile.Write(ref _selectedBlueprintTiles, value is TilePos t ? new[] { t } : Array.Empty<TilePos>());
    }

    private TilePos[] _selectedLampTiles = Array.Empty<TilePos>();
    public TilePos[] SelectedLampTiles
    {
        get => Volatile.Read(ref _selectedLampTiles);
        set => Volatile.Write(ref _selectedLampTiles, value ?? Array.Empty<TilePos>());
    }

    private TilePos[] _selectedBedTiles = Array.Empty<TilePos>();
    public TilePos[] SelectedBedTiles
    {
        get => Volatile.Read(ref _selectedBedTiles);
        set => Volatile.Write(ref _selectedBedTiles, value ?? Array.Empty<TilePos>());
    }

    private TilePos[] _selectedUrBoardTiles = Array.Empty<TilePos>();
    public TilePos[] SelectedUrBoardTiles
    {
        get => Volatile.Read(ref _selectedUrBoardTiles);
        set => Volatile.Write(ref _selectedUrBoardTiles, value ?? Array.Empty<TilePos>());
    }

    private TilePos[] _selectedStoveTiles = Array.Empty<TilePos>();
    public TilePos[] SelectedStoveTiles
    {
        get => Volatile.Read(ref _selectedStoveTiles);
        set => Volatile.Write(ref _selectedStoveTiles, value ?? Array.Empty<TilePos>());
    }

    // Read-only accessor for the WallInfoPanel: is the wall at this
    // tile player-built (deconstructable)?
    public bool IsPlayerWall(TilePos tile) => _sim.PlayerWalls.Contains(tile);

    // Bed placement legality check for the designator's preview overlay.
    // Synchronizes against the sim loop so the dict/set reads can't race
    // mid-Step(). Only called while the bed tool is active.
    public bool CanPlaceBed(TilePos origin, StruggleGame.Sim.World.BedOrientation orientation)
    {
        lock (_swapLock) { return _sim.CanPlaceBed(origin, orientation); }
    }

    public bool CanPlaceUrBoard(TilePos tile)
    {
        lock (_swapLock) { return _sim.CanPlaceUrBoard(tile); }
    }

    public bool CanPlaceSandbag(TilePos tile)
    {
        lock (_swapLock) { return _sim.CanPlaceSandbag(tile); }
    }

    public bool CanPlaceStove(TilePos origin, StruggleGame.Sim.World.StoveOrientation orientation)
    {
        lock (_swapLock) { return _sim.CanPlaceStove(origin, orientation); }
    }

    // Game→Sim command submission. Drained at the start of every tick.
    public void QueueCommand(ISimCommand cmd) => _sim.QueueCommand(cmd);

    // Threadsafe map snapshot for rebuilding the wall overlay texture
    // when MapVersion changes.
    public byte[] CopyLayerForRender(MapLayer layer) => _sim.CopyLayerForRender(layer);

    // Per-tile room ids (0 = barrier/wall/door, 1..n = room id). Recomputed
    // only on wall/door changes; pull when SimSnapshot.RoomVersion bumps.
    public int[] CopyRoomTilesForRender() => _sim.CopyRoomTilesForRender();

    // Per-tile room lookups for the HUD hover readout. Reads run from
    // the render thread against arrays mutated only on the sim thread
    // during DoRecomputeRooms; the worst-case race is a one-tick stale
    // read of room id / temperature, which is fine for a UI display.
    public int RoomIdAt(StruggleGame.Sim.Map.TilePos tile) => _sim.RoomIdAt(tile);
    public float RoomTempC(int roomId) => _sim.RoomTempC(roomId);
    public float TileTempC(StruggleGame.Sim.Map.TilePos tile) => _sim.TileTempC(tile);

    // Roof + no-roof bytemaps for the render overlays. Pull when
    // SimSnapshot.RoofVersion bumps; updates fire on auto-roof, on
    // any of the roof designator commands, and on the no-roof toggle.
    public byte[] CopyRoofTilesForRender() => _sim.CopyRoofTilesForRender();
    public byte[] CopyNoRoofTilesForRender() => _sim.CopyNoRoofTilesForRender();
    public byte[] CopyLightRgbForRender() => _sim.CopyLightRgbForRender();

    // 0..1 light fraction at a tile. Stub today: 1.0 unroofed, 0 roofed.
    public float LightAt(StruggleGame.Sim.Map.TilePos tile) => _sim.LightAt(tile);

    private static bool SelectionArrayEquals(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long nextTick = sw.ElapsedTicks;
        long windowStartMs = sw.ElapsedMilliseconds;
        int ticksThisWindow = 0;

        while (_running)
        {
            int hz = _tickHz;
            long tickStride = Stopwatch.Frequency / hz;
            // Speed control: dt is fixed at the canonical tick step, so a
            // higher tickHz fires more sim ticks per wall-second and the
            // sim advances faster than realtime. Using 1f/hz here would
            // make every speed setting feel identical because the extra
            // ticks would each cover proportionally less sim time.
            float dt = SimConstants.TickSeconds;

            if (_paused)
            {
                // Pause freezes the systems but not the player. Drain any
                // queued designations / assignments / toggles so they take
                // effect immediately, then republish the snapshot so the
                // UI reflects the new state (forbid X marks, blueprint
                // tiles, draft state, etc.).
                lock (_swapLock)
                {
                    bool needPublish = _sim.ApplyQueuedCommands();
                    int sel = Volatile.Read(ref _selectedDummyId);
                    var ids = Volatile.Read(ref _selectedDummyIds);
                    var trees = Volatile.Read(ref _selectedTreeIds);
                    var woods = Volatile.Read(ref _selectedWoodIds);
                    var crops = Volatile.Read(ref _selectedCropIds);
                    // Also republish if selection changed while paused so
                    // pawn rings + tree/wood/crop rings + selected-path
                    // update without needing a tick to fire.
                    var cur = Volatile.Read(ref _latest);
                    if (!needPublish && cur is not null)
                    {
                        int? selBoxed = sel >= 0 ? sel : null;
                        if (cur.SelectedDummyId != selBoxed
                            || !SelectionArrayEquals(cur.SelectedDummyIds, ids)
                            || !SelectionArrayEquals(cur.SelectedTreeIds, trees)
                            || !SelectionArrayEquals(cur.SelectedWoodIds, woods)
                            || !SelectionArrayEquals(cur.SelectedCropIds, crops))
                        {
                            needPublish = true;
                        }
                    }
                    if (needPublish)
                    {
                        Volatile.Write(ref _latest, _sim.BuildSnapshot(ids.Length == 1 ? ids[0] : (int?)null, ids.Length > 0 ? ids : null, trees.Length > 0 ? trees : null, woods.Length > 0 ? woods : null, crops.Length > 0 ? crops : null));
                    }
                }
                Thread.Sleep(5);
                nextTick = sw.ElapsedTicks + tickStride;
                continue;
            }

            long now = sw.ElapsedTicks;
            if (now >= nextTick)
            {
                lock (_swapLock)
                {
                    _sim.Step(dt);
                    int sel = Volatile.Read(ref _selectedDummyId);
                    var ids = Volatile.Read(ref _selectedDummyIds);
                    var trees = Volatile.Read(ref _selectedTreeIds);
                    var woods = Volatile.Read(ref _selectedWoodIds);
                    var crops = Volatile.Read(ref _selectedCropIds);
                    Volatile.Write(ref _latest, _sim.BuildSnapshot(ids.Length == 1 ? ids[0] : (int?)null, ids.Length > 0 ? ids : null, trees.Length > 0 ? trees : null, woods.Length > 0 ? woods : null, crops.Length > 0 ? crops : null));
                }
                ticksThisWindow++;
                nextTick += tickStride;
                // If we fell badly behind (paused breakpoint, hz bump, etc.),
                // don't try to catch up by spamming — resync to now.
                if (nextTick < now - tickStride * 4) nextTick = now + tickStride;
            }
            else
            {
                long sleepTicks = nextTick - now;
                long sleepMs = sleepTicks * 1000 / Stopwatch.Frequency;
                if (sleepMs > 1) Thread.Sleep(1); else Thread.SpinWait(64);
            }

            long elapsedMs = sw.ElapsedMilliseconds;
            long windowMs = elapsedMs - windowStartMs;
            if (windowMs >= 500)
            {
                _actualTps = ticksThisWindow * 1000f / windowMs;
                ticksThisWindow = 0;
                windowStartMs = elapsedMs;
            }
        }
    }
}
