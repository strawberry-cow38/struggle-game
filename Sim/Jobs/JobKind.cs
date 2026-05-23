namespace StruggleGame.Sim.Jobs;

// Tag for the kind of work a job represents. New verbs (haul, eat, sleep,
// deconstruct, repair, harvest…) add an entry here and a handler system.
public enum JobKind : byte
{
    WallBuild = 1,
    ChopTree = 2,
    Deconstruct = 3,
    FloorBuild = 4,
    DoorBuild = 5,
    Haul = 6,
    FloorDeconstruct = 7,
    DoorDeconstruct = 8,
    CutPlants = 9,
    Harvest = 10,
    Sow = 11,
    RoofBuild = 12,
    RoofRemove = 13,
}
