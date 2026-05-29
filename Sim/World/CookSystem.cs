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

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        _toPost.Clear();

        // 1. Scan stoves for bill scheduling.
        var stoveQuery = store.Query<Stove, BillsBoard>();
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
        var cookers = store.Query<Cooking, WorldPos>();
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

            stove.CookProgressTicks += dt * TicksPerSecond;
            if (stove.CookProgressTicks < recipe.WorkTicks) return;

            // Completion: spawn output, decrement bill, reset stove state.
            var outputTile = standing; // drop at standing tile for now (worker stands on stove output)
            // If output dest is stockpile-routed, drop at standing then haul system will move it.
            _sim.SpawnItemPile(outputTile, recipe.Output.ItemPath, recipe.Output.Count);

            // Decrement DoXTimes; remove if zero. Leave Forever/DoUntilCount alone.
            var updated = bill;
            if (updated.RepeatMode == BillRepeatMode.DoXTimes)
            {
                updated.RemainingCount = Math.Max(0, updated.RemainingCount - 1);
            }
            board.Bills[stove.CurrentBillIndex] = updated;
            if (updated.RepeatMode == BillRepeatMode.DoXTimes && updated.RemainingCount <= 0)
            {
                board.Bills.RemoveAt(stove.CurrentBillIndex);
            }

            // Reset stove state and the cook.
            int cookId = stove.ActiveCookEntityId;
            stove.CookProgressTicks = 0f;
            stove.CurrentBillIndex = -1;
            stove.ActiveCookEntityId = 0;
            if (store.TryGetEntityById(cookId, out var cookEnt))
            {
                if (cookEnt.HasComponent<Cooking>()) cookEnt.RemoveComponent<Cooking>();
                if (cookEnt.HasComponent<BuildTarget>()) cookEnt.RemoveComponent<BuildTarget>();
            }

            // Close the Cook job.
            // Look up by stove tile (standing). Skip if already closed.
            var cookJob = _jobs.GetByTile(standing);
            if (cookJob is not null && cookJob.Kind == JobKind.Cook
                && (cookJob.State == JobState.Open || cookJob.State == JobState.Claimed))
            {
                _completed.Add(cookJob.Id);
            }
        });
        foreach (var id in _completed) _sim.CompleteJob(id);
    }

    private static bool IsBillSatisfied(Bill bill, Recipe recipe, EntityStore store)
    {
        switch (bill.RepeatMode)
        {
            case BillRepeatMode.Forever:
                return true;
            case BillRepeatMode.DoXTimes:
                return bill.RemainingCount > 0;
            case BillRepeatMode.DoUntilCount:
            {
                // Count world piles matching the output ItemPath.
                int have = 0;
                var q = store.Query<ItemPile>();
                q.ForEachEntity((ref ItemPile p, Entity _) =>
                {
                    if (p.ItemPath == recipe.Output.ItemPath) have += p.Count;
                });
                return have < bill.TargetCount;
            }
            default: return false;
        }
    }

    private static bool HasIngredientsOnMap(Recipe recipe, EntityStore store)
    {
        foreach (var input in recipe.Inputs)
        {
            int have = 0;
            var q = store.Query<ItemPile>();
            q.ForEachEntity((ref ItemPile p, Entity _) =>
            {
                if (p.ItemPath == input.ItemPath) have += p.Count;
            });
            if (have < input.Count) return false;
        }
        return true;
    }
}
