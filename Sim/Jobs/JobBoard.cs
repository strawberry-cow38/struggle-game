using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Work;

namespace StruggleGame.Sim.Jobs;

// Generic registry of pending/in-progress work. Replaces the build-only
// BlueprintRegistry. WallBuild is the first JobKind; eat/sleep/haul/etc
// slot in as new kinds without touching consumers.
//
// Today: dictionaries by id + tile. Tomorrow: tier buckets (priority
// classes), spatial hash for "jobs in rect" queries, and a dirty-flag
// bus consumers subscribe to via Version.
public sealed class JobBoard
{
    private readonly Dictionary<JobId, Job> _byId = new();
    private readonly Dictionary<TilePos, JobId> _byTile = new();
    // Jobs that are Open AND not Forbidden — the only ones a pawn can claim.
    // Maintained on every state/forbidden transition so the per-pawn claim
    // scan iterates just the claimable set, not every job (most are Claimed
    // mid-work in a busy colony).
    private readonly HashSet<Job> _open = new();
    private long _nextId;

    // Spatial index of OPEN jobs by (work type, chunk) so a pawn can ring-search
    // nearby claimable jobs instead of walking the whole open set. 16x16 chunks.
    public const int ChunkShift = 4;
    private readonly Dictionary<(int wt, int cx, int cy), List<Job>> _openChunks = new();

    private void RefreshOpen(Job job)
    {
        bool want = job.State == JobState.Open && !job.Forbidden;
        bool have = _open.Contains(job);
        if (want && !have) { _open.Add(job); ChunkIndex(job, true); }
        else if (!want && have) { _open.Remove(job); ChunkIndex(job, false); }
    }

    private void ChunkIndex(Job job, bool add)
    {
        if (!WorkTypes.TryGet(job.Kind, out var wt)) return; // unclaimable kind
        var key = ((int)wt, job.Tile.X >> ChunkShift, job.Tile.Y >> ChunkShift);
        if (add)
        {
            if (!_openChunks.TryGetValue(key, out var list)) { list = new List<Job>(); _openChunks[key] = list; }
            list.Add(job);
        }
        else if (_openChunks.TryGetValue(key, out var list))
        {
            list.Remove(job);
            if (list.Count == 0) _openChunks.Remove(key);
        }
    }

    // Open jobs of a work type in one chunk (null if none). For the pawn-side
    // ring search. The returned list is live — read it, don't mutate.
    public List<Job>? OpenJobsInChunk(WorkType wt, int cx, int cy)
        => _openChunks.TryGetValue(((int)wt, cx, cy), out var l) ? l : null;

    public long Version { get; private set; }
    public int Count => _byId.Count;

    // Concrete ValueCollection (not IEnumerable<Job>) so foreach uses the
    // struct enumerator — no boxed-enumerator heap alloc per iteration. Hit per
    // idle pawn per tick (TryClaimJob) and several times per snapshot.
    public Dictionary<JobId, Job>.ValueCollection All => _byId.Values;

    // Claimable jobs only (Open + not Forbidden). Concrete HashSet so foreach
    // uses the struct enumerator (no boxed-enumerator alloc).
    public HashSet<Job> OpenJobs => _open;

    public bool HasTile(TilePos tile) => _byTile.ContainsKey(tile);

    public Job? GetByTile(TilePos tile) => _byTile.TryGetValue(tile, out var id) ? _byId[id] : null;
    public Job? Get(JobId id) => _byId.TryGetValue(id, out var j) ? j : null;

    public JobId Post(JobKind kind, TilePos tile, Entity entity)
        => Post(kind, tile, entity, null, skipTileIndex: false);

    // Multi-tile post: anchor `tile` is the tile pawns approach; extras
    // are additional tiles covered by the same job (e.g. a 3x3 roof
    // chunk). All tiles are indexed in _byTile so HasTile / GetByTile
    // see every covered tile, but the job ticks/completes as one unit.
    public JobId Post(JobKind kind, TilePos tile, Entity entity, TilePos[]? extraTiles)
        => Post(kind, tile, entity, extraTiles, skipTileIndex: false);

    // Skip-index variant: used by BlueprintClearanceSystem to post a
    // Haul on a tile that already hosts a build job. The clearance
    // haul doesn't need GetByTile / HasTile to find it (its lifecycle
    // is owned by the wood entity's HaulReserved JobId), so we just
    // omit it from the tile index entirely.
    public JobId Post(JobKind kind, TilePos tile, Entity entity, TilePos[]? extraTiles, bool skipTileIndex)
    {
        if (!skipTileIndex)
        {
            if (_byTile.ContainsKey(tile)) return JobId.None;
            if (extraTiles is not null)
            {
                foreach (var t in extraTiles)
                {
                    if (t == tile) continue;
                    if (_byTile.ContainsKey(t)) return JobId.None;
                }
            }
        }
        var id = new JobId(++_nextId);
        var job = new Job(id, kind, tile, entity, extraTiles);
        _byId[id] = job;
        RefreshOpen(job); // new jobs start Open + unforbidden → claimable
        if (!skipTileIndex)
        {
            _byTile[tile] = id;
            if (extraTiles is not null)
            {
                foreach (var t in extraTiles)
                {
                    if (t == tile) continue;
                    _byTile[t] = id;
                }
            }
        }
        Version++;
        return id;
    }

    public bool TryClaim(JobId id, Entity claimant)
    {
        if (!_byId.TryGetValue(id, out var job)) return false;
        if (job.State != JobState.Open) return false;
        if (job.Forbidden) return false;
        job.State = JobState.Claimed;
        job.Claimant = claimant;
        RefreshOpen(job);
        Version++;
        return true;
    }

    // Flip the Forbidden flag on a job. Forbidden = no future TryClaim
    // succeeds; any active claim is released so the worker re-plans.
    public bool SetForbidden(JobId id, bool forbidden)
    {
        if (!_byId.TryGetValue(id, out var job)) return false;
        if (job.Forbidden == forbidden) return false;
        job.Forbidden = forbidden;
        if (forbidden && job.State == JobState.Claimed)
        {
            job.State = JobState.Open;
            job.Claimant = default;
        }
        RefreshOpen(job);
        Version++;
        return true;
    }

    public bool SetForbiddenByTile(TilePos tile, bool forbidden)
    {
        if (!_byTile.TryGetValue(tile, out var id)) return false;
        return SetForbidden(id, forbidden);
    }

    public void Release(JobId id)
    {
        if (!_byId.TryGetValue(id, out var job)) return;
        if (job.State != JobState.Claimed) return;
        job.State = JobState.Open;
        job.Claimant = default;
        RefreshOpen(job);
        Version++;
    }

    public void Complete(JobId id)
    {
        if (!_byId.TryGetValue(id, out var job)) return;
        job.State = JobState.Completed;
        if (_open.Remove(job)) ChunkIndex(job, false);
        RemoveTileIndexIfOwned(job.Tile, id);
        if (job.ExtraTiles is not null)
            foreach (var t in job.ExtraTiles) if (t != job.Tile) RemoveTileIndexIfOwned(t, id);
        _byId.Remove(id);
        Version++;
    }

    public void Cancel(JobId id)
    {
        if (!_byId.TryGetValue(id, out var job)) return;
        job.State = JobState.Cancelled;
        if (_open.Remove(job)) ChunkIndex(job, false);
        RemoveTileIndexIfOwned(job.Tile, id);
        if (job.ExtraTiles is not null)
            foreach (var t in job.ExtraTiles) if (t != job.Tile) RemoveTileIndexIfOwned(t, id);
        _byId.Remove(id);
        Version++;
    }

    // Only clears the tile index if it actually points at the job being
    // retired — otherwise a skipTileIndex job (clearance haul, etc.)
    // would accidentally evict the unrelated build job sharing its tile.
    private void RemoveTileIndexIfOwned(TilePos tile, JobId id)
    {
        if (_byTile.TryGetValue(tile, out var owner) && owner == id)
            _byTile.Remove(tile);
    }

    public bool CancelByTile(TilePos tile)
    {
        if (!_byTile.TryGetValue(tile, out var id)) return false;
        Cancel(id);
        return true;
    }

    // Inclusive bounds, tile units. Returns the Jobs touching the rect.
    public IEnumerable<Job> InRect(int x0, int y0, int x1, int y1)
    {
        int xmin = Math.Min(x0, x1), xmax = Math.Max(x0, x1);
        int ymin = Math.Min(y0, y1), ymax = Math.Max(y0, y1);
        foreach (var job in _byId.Values)
        {
            var t = job.Tile;
            if (t.X < xmin || t.X > xmax) continue;
            if (t.Y < ymin || t.Y > ymax) continue;
            yield return job;
        }
    }
}
