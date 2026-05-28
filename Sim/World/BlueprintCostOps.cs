using Friflo.Engine.ECS;

namespace StruggleGame.Sim.World;

// Helpers around the BlueprintCost component. Stateless: callers pass
// the blueprint Entity and we mutate its component in place.
//
// Workflow:
//   1. Designator places blueprint + posts build job, then calls
//      AttachCost(entity, ("Resources/Wood/Wood", 2), ...).
//   2. Build systems call IsFunded(entity) before advancing progress.
//   3. Haul / deposit pipeline (TBD) calls Deposit(entity, path, n)
//      when materials arrive at the tile; returns any leftover the
//      caller must re-route.
//   4. CancelJob hands deposited materials back via Outstanding(entity)
//      so the dump-on-cancel path can re-spawn stacks. (Wiring is
//      caller-side — this class only reports the numbers.)
public static class BlueprintCostOps
{
    public static void AttachCost(Entity e, params (string ItemPath, int Needed)[] reqs)
    {
        var entries = new ResourceReq[reqs.Length];
        for (int i = 0; i < reqs.Length; i++)
        {
            entries[i] = new ResourceReq
            {
                ItemPath = reqs[i].ItemPath,
                Needed = reqs[i].Needed,
                Deposited = 0,
            };
        }
        e.AddComponent(new BlueprintCost { Entries = entries });
    }

    public static bool IsFunded(Entity e)
    {
        if (!e.HasComponent<BlueprintCost>()) return true;
        ref var cost = ref e.GetComponent<BlueprintCost>();
        var entries = cost.Entries;
        if (entries is null) return true;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Deposited < entries[i].Needed) return false;
        }
        return true;
    }

    // Pours up to `amount` of `itemPath` into the first matching entry
    // that still has demand. Reservation is consumed first (delivery
    // pays back an in-flight haul); excess delivery falls through into
    // the unreserved gap. Returns the leftover the caller must re-route
    // elsewhere (0 = fully consumed).
    public static int Deposit(Entity e, string itemPath, int amount)
    {
        if (amount <= 0 || !e.HasComponent<BlueprintCost>()) return amount;
        ref var cost = ref e.GetComponent<BlueprintCost>();
        var entries = cost.Entries;
        if (entries is null) return amount;
        for (int i = 0; i < entries.Length && amount > 0; i++)
        {
            if (entries[i].ItemPath != itemPath) continue;
            int gap = entries[i].Needed - entries[i].Deposited;
            if (gap <= 0) continue;
            int take = amount < gap ? amount : gap;
            entries[i].Deposited += take;
            int relRes = take < entries[i].Reserved ? take : entries[i].Reserved;
            entries[i].Reserved -= relRes;
            amount -= take;
        }
        return amount;
    }

    // Free-need accounting: Needed - Deposited - Reserved. Used by
    // BlueprintHaulSystem to decide whether another hauler should
    // dispatch.
    public static int FreeNeed(Entity e, string itemPath)
    {
        if (!e.HasComponent<BlueprintCost>()) return 0;
        ref var cost = ref e.GetComponent<BlueprintCost>();
        var entries = cost.Entries;
        if (entries is null) return 0;
        int total = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].ItemPath != itemPath) continue;
            int gap = entries[i].Needed - entries[i].Deposited - entries[i].Reserved;
            if (gap > 0) total += gap;
        }
        return total;
    }

    // Bumps Reserved on the first matching entry that has free need.
    // Returns the amount actually reserved (<= amount).
    public static int Reserve(Entity e, string itemPath, int amount)
    {
        if (amount <= 0 || !e.HasComponent<BlueprintCost>()) return 0;
        ref var cost = ref e.GetComponent<BlueprintCost>();
        var entries = cost.Entries;
        if (entries is null) return 0;
        int reserved = 0;
        for (int i = 0; i < entries.Length && amount > 0; i++)
        {
            if (entries[i].ItemPath != itemPath) continue;
            int gap = entries[i].Needed - entries[i].Deposited - entries[i].Reserved;
            if (gap <= 0) continue;
            int take = amount < gap ? amount : gap;
            entries[i].Reserved += take;
            reserved += take;
            amount -= take;
        }
        return reserved;
    }

    // Undo a Reserve call (haul aborted). Decrements Reserved without
    // touching Deposited. Clamps at zero so double-release is safe.
    public static void ReleaseReservation(Entity e, string itemPath, int amount)
    {
        if (amount <= 0 || !e.HasComponent<BlueprintCost>()) return;
        ref var cost = ref e.GetComponent<BlueprintCost>();
        var entries = cost.Entries;
        if (entries is null) return;
        for (int i = 0; i < entries.Length && amount > 0; i++)
        {
            if (entries[i].ItemPath != itemPath) continue;
            int give = entries[i].Reserved < amount ? entries[i].Reserved : amount;
            entries[i].Reserved -= give;
            amount -= give;
        }
    }

    // Per-path "still needed" map view — what a haul planner should
    // try to bring next. Returns 0-length array when fully funded.
    public static ResourceReq[] Outstanding(Entity e)
    {
        if (!e.HasComponent<BlueprintCost>()) return System.Array.Empty<ResourceReq>();
        ref var cost = ref e.GetComponent<BlueprintCost>();
        var entries = cost.Entries;
        if (entries is null) return System.Array.Empty<ResourceReq>();
        int n = 0;
        for (int i = 0; i < entries.Length; i++)
            if (entries[i].Deposited < entries[i].Needed) n++;
        if (n == 0) return System.Array.Empty<ResourceReq>();
        var outArr = new ResourceReq[n];
        int j = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            ref var r = ref entries[i];
            if (r.Deposited >= r.Needed) continue;
            outArr[j++] = new ResourceReq
            {
                ItemPath = r.ItemPath,
                Needed = r.Needed,
                Deposited = r.Deposited,
            };
        }
        return outArr;
    }
}
