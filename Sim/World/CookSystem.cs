using Friflo.Engine.ECS;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Drives bill scheduling + cook progress on stoves.
//   1. For each idle stove (CurrentBillIndex == -1, no live Cook job):
//      pick first eligible bill (repeat mode + world inventory check)
//      and post a Cook job at the stove's standing tile.
//   2. For each stove with a bound cook (ActiveCookEntityId != 0)
//      and a Cooking pawn in phase=1 standing on the standing tile:
//      advance CookProgressTicks. On completion spawn the meal output,
//      decrement the bill, free the cook, and CompleteJob.
public sealed class CookSystem
{
    public const int TicksPerSecond = 60;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public CookSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    private readonly List<JobId> _completed = new();
    private readonly List<(int stoveId, int billIdx)> _toPost = new();
    private readonly List<CookFinish> _finished = new();

    private ArchetypeQuery<Stove, BillsBoard>? _stoveBillsQ;
    private ArchetypeQuery<Cooking, WorldPos>? _cookingPosQ;
    private ArchetypeQuery<ItemPile>? _itemPileQ;

    // Map-wide item count by path, built at most ONCE per tick (lazily, only if
    // a stove actually evaluates a bill) and shared across every stove/input —
    // replaces a full ItemPile scan per stove per input check. _pathTotals
    // counts every pile (output-count checks: hauled-to-stockpile output still
    // exists); _unreservedTotals excludes stacks already promised to an
    // in-flight haul job (ingredient checks: don't start a cook on carrots the
    // haul system is about to cart off elsewhere).
    private readonly Dictionary<string, int> _pathTotals = new();
    private readonly Dictionary<string, int> _unreservedTotals = new();
    private bool _totalsReady;
    private void EnsureTotals(EntityStore store)
    {
        if (_totalsReady) return;
        _totalsReady = true;
        _pathTotals.Clear();
        _unreservedTotals.Clear();
        (_itemPileQ ??= store.Query<ItemPile>()).ForEachEntity((ref ItemPile p, Entity e) =>
        {
            _pathTotals[p.ItemPath] = _pathTotals.GetValueOrDefault(p.ItemPath) + p.Count;
            if (!e.HasComponent<HaulReserved>())
                _unreservedTotals[p.ItemPath] = _unreservedTotals.GetValueOrDefault(p.ItemPath) + p.Count;
        });
    }

    private readonly struct CookFinish
    {
        public readonly int StoveEntityId;
        public readonly int CookEntityId;
        public readonly int BillIndex;
        public readonly TilePos OutputTile;
        public readonly TilePos StandingTile;
        public readonly string OutputItemPath;
        public readonly int OutputCount;
        public CookFinish(int stoveId, int cookId, int billIdx, TilePos output, TilePos standing, string itemPath, int count)
        { StoveEntityId = stoveId; CookEntityId = cookId; BillIndex = billIdx; OutputTile = output; StandingTile = standing; OutputItemPath = itemPath; OutputCount = count; }
    }

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        _toPost.Clear();
        _finished.Clear();
        _totalsReady = false; // rebuilt lazily this tick only if a bill needs it

        // 1. Scan stoves for bill scheduling.
        var stoveQuery = _stoveBillsQ ??= store.Query<Stove, BillsBoard>();
        stoveQuery.ForEachEntity((ref Stove stove, ref BillsBoard board, Entity ent) =>
        {
            if (stove.CurrentBillIndex != -1) return;
            if (board.Bills is null || board.Bills.Count == 0) return;
            // First-eligible-wins.
            for (int i = 0; i < board.Bills.Count; i++)
            {
                var bill = board.Bills[i];
                var recipe = Recipes.Get(bill.Recipe);
                if (!IsBillSatisfied(bill, recipe, store)) continue;
                if (!HasIngredientsOnMap(recipe, store)) continue;
                _toPost.Add((ent.Id, i));
                break;
            }
        });
        // Apply outside the iteration.
        foreach (var (stoveId, billIdx) in _toPost)
        {
            if (!store.TryGetEntityById(stoveId, out var stoveEnt)) continue;
            if (!stoveEnt.HasComponent<Stove>()) continue;
            ref var stove = ref stoveEnt.GetComponent<Stove>();
            if (stove.CurrentBillIndex != -1) continue;
            var standing = StoveOrientations.StandingTile(stove.Origin, stove.Orientation);
            if (_jobs.HasTile(standing)) continue;
            var id = _jobs.Post(JobKind.Cook, standing, stoveEnt);
            if (id.IsNone) continue;
            stove.CurrentBillIndex = billIdx;
        }

        // 2. Advance cook progress.
        var cookers = _cookingPosQ ??= store.Query<Cooking, WorldPos>();
        cookers.ForEachEntity((ref Cooking cooking, ref WorldPos pos, Entity pawn) =>
        {
            if (cooking.Phase != 1) return;
            if (!store.TryGetEntityById(cooking.StoveEntityId, out var stoveEnt)) return;
            if (!stoveEnt.HasComponent<Stove>() || !stoveEnt.HasComponent<BillsBoard>()) return;
            ref var stove = ref stoveEnt.GetComponent<Stove>();
            if (stove.ActiveCookEntityId != pawn.Id) return;
            var standing = StoveOrientations.StandingTile(stove.Origin, stove.Orientation);
            int px = (int)pos.X, py = (int)pos.Y;
            if (px != standing.X || py != standing.Y) return;
            var board = stoveEnt.GetComponent<BillsBoard>();
            if (board.Bills is null) return;
            if (stove.CurrentBillIndex < 0 || stove.CurrentBillIndex >= board.Bills.Count) return;
            var bill = board.Bills[stove.CurrentBillIndex];
            var recipe = Recipes.Get(bill.Recipe);

            stove.CookProgressTicks += dt * TicksPerSecond * HealthMods.WorkSpeed(pawn);
            if (stove.CookProgressTicks < recipe.WorkTicks) return;

            // Defer all structural changes (entity spawn, component removal,
            // bill mutation, job close) to after the query loop.
            _finished.Add(new CookFinish(
                stoveEnt.Id, pawn.Id, stove.CurrentBillIndex,
                standing, standing,
                recipe.Output.ItemPath, recipe.Output.Count));
        });

        foreach (var f in _finished)
        {
            if (!store.TryGetEntityById(f.StoveEntityId, out var stoveEnt)) continue;
            if (!stoveEnt.HasComponent<Stove>() || !stoveEnt.HasComponent<BillsBoard>()) continue;
            ref var stove = ref stoveEnt.GetComponent<Stove>();
            var board = stoveEnt.GetComponent<BillsBoard>();

            // SpecificStockpile routing isn't wired into completion yet (output
            // always drops at the workbench), but a stale target id — the
            // stockpile was deleted after the bill was configured — shouldn't
            // linger silently. Validate it here, fall back to DropAtWorkbench,
            // and surface the downgrade in the watcher feed.
            if (board.Bills is not null && f.BillIndex >= 0 && f.BillIndex < board.Bills.Count)
            {
                var b = board.Bills[f.BillIndex];
                if (b.OutputDest == BillOutputDest.SpecificStockpile && !StockpileExists(b.StockpileEntityId))
                {
                    _sim.Watcher.RecordStaleBillTarget(_sim.Tick, f.StoveEntityId,
                        $"bill {b.Recipe} target stockpile {b.StockpileEntityId} gone -> drop at workbench");
                    b.OutputDest = BillOutputDest.DropAtWorkbench;
                    b.StockpileEntityId = 0;
                    board.Bills[f.BillIndex] = b;
                }
            }

            // Spawn output now that the query is done.
            _sim.SpawnItemPile(f.OutputTile, f.OutputItemPath, f.OutputCount);

            // Decrement DoXTimes; remove if zero. Leave Forever/DoUntilCount alone.
            if (board.Bills is not null && f.BillIndex >= 0 && f.BillIndex < board.Bills.Count)
            {
                var updated = board.Bills[f.BillIndex];
                if (updated.RepeatMode == BillRepeatMode.DoXTimes)
                {
                    updated.RemainingCount = Math.Max(0, updated.RemainingCount - 1);
                }
                board.Bills[f.BillIndex] = updated;
                if (updated.RepeatMode == BillRepeatMode.DoXTimes && updated.RemainingCount <= 0)
                {
                    board.Bills.RemoveAt(f.BillIndex);
                }
            }

            stove.CookProgressTicks = 0f;
            stove.CurrentBillIndex = -1;
            stove.ActiveCookEntityId = 0;

            if (store.TryGetEntityById(f.CookEntityId, out var cookEnt))
            {
                if (cookEnt.HasComponent<Cooking>()) cookEnt.RemoveComponent<Cooking>();
                if (cookEnt.HasComponent<BuildTarget>()) cookEnt.RemoveComponent<BuildTarget>();
            }

            var cookJob = _jobs.GetByTile(f.StandingTile);
            if (cookJob is not null && cookJob.Kind == JobKind.Cook
                && (cookJob.State == JobState.Open || cookJob.State == JobState.Claimed))
            {
                _completed.Add(cookJob.Id);
            }
        }
        foreach (var id in _completed) _sim.CompleteJob(id);
    }

    private bool StockpileExists(int stockpileId)
    {
        foreach (var sp in _sim.Stockpiles)
            if (sp.Id == stockpileId) return true;
        return false;
    }

    private bool IsBillSatisfied(Bill bill, Recipe recipe, EntityStore store)
    {
        switch (bill.RepeatMode)
        {
            case BillRepeatMode.Forever:
                return true;
            case BillRepeatMode.DoXTimes:
                return bill.RemainingCount > 0;
            case BillRepeatMode.DoUntilCount:
            {
                EnsureTotals(store);
                return _pathTotals.GetValueOrDefault(recipe.Output.ItemPath) < bill.TargetCount;
            }
            default: return false;
        }
    }

    private bool HasIngredientsOnMap(Recipe recipe, EntityStore store)
    {
        EnsureTotals(store);
        foreach (var input in recipe.Inputs)
            if (_unreservedTotals.GetValueOrDefault(input.ItemPath) < input.Count) return false;
        return true;
    }
}
