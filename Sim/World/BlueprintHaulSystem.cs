using Friflo.Engine.ECS;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick poster for blueprint-bound hauls. Mirrors HaulSystem but
// targets unfunded blueprints (Blueprint / FloorBlueprint / DoorBlueprint /
// BedBlueprint with a BlueprintCost) rather than stockpiles. Runs before
// HaulSystem so blueprint demand wins the race for nearby wood; whatever
// wood isn't consumed here falls through to normal stockpile hauling.
//
// The wood entity itself is the job entity — same pattern as HaulSystem,
// so the existing DummyController pickup/dropoff plumbing handles
// execution unchanged. HaulPayload carries BlueprintEntityId so
// DeliverCarrying can route per-slot Counts into BlueprintCostOps.Deposit
// rather than spawning a fresh Wood stack at the dest tile.
public sealed class BlueprintHaulSystem
{
    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    // Reused per-tick scratch.
    private readonly List<(Entity Ent, TilePos Tile, int Count)> _wood = new();
    private readonly List<(Entity Ent, TilePos Tile, int Need)> _demand = new();

    public BlueprintHaulSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        _wood.Clear();
        _demand.Clear();

        // God mode bypasses the BuildSystem funding gate, so any haul we
        // schedule here just incinerates wood into a cost ledger nobody
        // reads. Skip the whole pass so relocated stacks survive.
        if (_sim.GodModeFreeBuild) return;

        // Blueprint costs are all wood today, so the haul material is wood
        // (an ItemPile of the wood path). Carrots can't fund a wall.
        string woodPath = ItemCatalog.Wood.FullPath;
        store.Query<ItemPile>().ForEachEntity((ref ItemPile p, Entity ent) =>
        {
            if (p.ItemPath != woodPath) return;
            if (ent.HasComponent<HaulReserved>()) return;
            if (ent.HasComponent<Forbidden>()) return;
            _wood.Add((ent, p.Tile, p.Count));
        });
        if (_wood.Count == 0) return;

        CollectBlueprintDemand(store);
        if (_demand.Count == 0) return;
        foreach (var d in _demand)
        {
            int need = d.Need;
            if (need <= 0) continue;

            int bestIdx = -1;
            int bestDist = int.MaxValue;
            for (int i = 0; i < _wood.Count; i++)
            {
                var w = _wood[i];
                int dist = Math.Abs(w.Tile.X - d.Tile.X) + Math.Abs(w.Tile.Y - d.Tile.Y);
                if (dist < bestDist) { bestDist = dist; bestIdx = i; }
            }
            if (bestIdx < 0) break;

            var pick = _wood[bestIdx];
            _wood.RemoveAt(bestIdx);

            int reserve = pick.Count < need ? pick.Count : need;
            int gotReserve = BlueprintCostOps.Reserve(d.Ent, woodPath, reserve);
            if (gotReserve <= 0) continue;

            // Split the source stack down to exactly the reserved amount.
            // Leftover stays on the source tile as an unreserved Wood entity
            // — HaulSystem (or another blueprint next tick) can claim it.
            // This is the guardrail that stops a pawn from carrying more
            // than the blueprint can possibly absorb.
            int leftoverAtSource = pick.Count - gotReserve;
            int haulCount = gotReserve;
            if (leftoverAtSource > 0)
            {
                ref var srcWood = ref pick.Ent.GetComponent<ItemPile>();
                srcWood.Count = haulCount;
                var leftover = _sim.Store.CreateEntity();
                leftover.AddComponent(new ItemPile { Tile = pick.Tile, Count = leftoverAtSource, ItemPath = woodPath });
                leftover.AddComponent(new WorldPos { X = pick.Tile.X + 0.5f, Y = pick.Tile.Y + 0.5f });
            }

            pick.Ent.AddComponent(new HaulPayload
            {
                DestTile = d.Tile,
                StockpileId = 0,
                ItemPath = woodPath,
                Count = haulCount,
                BlueprintEntityId = d.Ent.Id,
            });
            var id = _jobs.Post(JobKind.Haul, pick.Tile, pick.Ent);
            if (id.IsNone)
            {
                // Restore the source stack and refund the reservation.
                // (Splitting created a sibling entity — leave it; the merge
                // pass will fold it back next tick.)
                pick.Ent.RemoveComponent<HaulPayload>();
                BlueprintCostOps.ReleaseReservation(d.Ent, woodPath, gotReserve);
                continue;
            }
            pick.Ent.AddComponent(new HaulReserved { JobId = id });
            _sim.ReserveHaulDest(d.Tile);
        }
    }

    private void CollectBlueprintDemand(EntityStore store)
    {
        string woodPath = ItemCatalog.Wood.FullPath;
        store.Query<BlueprintCost>().ForEachEntity((ref BlueprintCost _, Entity e) =>
        {
            int need = BlueprintCostOps.FreeNeed(e, woodPath);
            if (need <= 0) return;

            TilePos tile;
            if (e.HasComponent<Blueprint>()) tile = e.GetComponent<Blueprint>().Tile;
            else if (e.HasComponent<FloorBlueprint>()) tile = e.GetComponent<FloorBlueprint>().Tile;
            else if (e.HasComponent<DoorBlueprint>()) tile = e.GetComponent<DoorBlueprint>().Tile;
            else if (e.HasComponent<BedBlueprint>()) tile = e.GetComponent<BedBlueprint>().Origin;
            else return;

            _demand.Add((e, tile, need));
        });
    }
}
