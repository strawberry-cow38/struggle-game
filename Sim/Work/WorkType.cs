using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.Work;

// Coarse work categories the player toggles per colonist in the work
// tab. JobKinds collapse into these buckets — within a category the
// existing nearest-job picker still runs, but the controller iterates
// categories in the pawn's per-category priority order and skips any
// category the pawn is forbidden from doing.
public enum WorkType : byte
{
    Construct = 0,
    Demolish = 1,
    Plants = 2,
    Farm = 3,
    Haul = 4,
}

public static class WorkTypes
{
    public const int Count = 5;

    public static readonly string[] Names = { "Construct", "Demolish", "Plants", "Farm", "Haul" };

    public static bool TryGet(JobKind kind, out WorkType type)
    {
        switch (kind)
        {
            case JobKind.WallBuild:
            case JobKind.FloorBuild:
            case JobKind.DoorBuild:
            case JobKind.RoofBuild:
            case JobKind.LampBuild:
            case JobKind.BedBuild:
                type = WorkType.Construct; return true;
            case JobKind.Deconstruct:
            case JobKind.FloorDeconstruct:
            case JobKind.DoorDeconstruct:
            case JobKind.RoofRemove:
            case JobKind.LampDeconstruct:
            case JobKind.BedDeconstruct:
                type = WorkType.Demolish; return true;
            case JobKind.ChopTree:
            case JobKind.CutPlants:
                type = WorkType.Plants; return true;
            case JobKind.Harvest:
            case JobKind.Sow:
                type = WorkType.Farm; return true;
            case JobKind.Haul:
                type = WorkType.Haul; return true;
            default:
                type = default; return false;
        }
    }
}
