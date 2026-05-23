using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;

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
    private long _nextId;

    public long Version { get; private set; }
    public int Count => _byId.Count;

    public IEnumerable<Job> All => _byId.Values;

    public bool HasTile(TilePos tile) => _byTile.ContainsKey(tile);

    public Job? GetByTile(TilePos tile) => _byTile.TryGetValue(tile, out var id) ? _byId[id] : null;
    public Job? Get(JobId id) => _byId.TryGetValue(id, out var j) ? j : null;

    public JobId Post(JobKind kind, TilePos tile, Entity entity)
        => Post(kind, tile, entity, null);

    // Multi-tile post: anchor `tile` is the tile pawns approach; extras
    // are additional tiles covered by the same job (e.g. a 3x3 roof
    // chunk). All tiles are indexed in _byTile so HasTile / GetByTile
    // see every covered tile, but the job ticks/completes as one unit.
    public JobId Post(JobKind kind, TilePos tile, Entity entity, TilePos[]? extraTiles)
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
        var id = new JobId(++_nextId);
        var job = new Job(id, kind, tile, entity, extraTiles);
        _byId[id] = job;
        _byTile[tile] = id;
        if (extraTiles is not null)
        {
            foreach (var t in extraTiles)
            {
                if (t == tile) continue;
                _byTile[t] = id;
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
        Version++;
    }

    public void Complete(JobId id)
    {
        if (!_byId.TryGetValue(id, out var job)) return;
        job.State = JobState.Completed;
        _byTile.Remove(job.Tile);
        if (job.ExtraTiles is not null)
            foreach (var t in job.ExtraTiles) if (t != job.Tile) _byTile.Remove(t);
        _byId.Remove(id);
        Version++;
    }

    public void Cancel(JobId id)
    {
        if (!_byId.TryGetValue(id, out var job)) return;
        job.State = JobState.Cancelled;
        _byTile.Remove(job.Tile);
        if (job.ExtraTiles is not null)
            foreach (var t in job.ExtraTiles) if (t != job.Tile) _byTile.Remove(t);
        _byId.Remove(id);
        Version++;
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
