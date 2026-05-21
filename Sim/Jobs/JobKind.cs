namespace StruggleGame.Sim.Jobs;

// Tag for the kind of work a job represents. New verbs (haul, eat, sleep,
// deconstruct, repair, harvest…) add an entry here and a handler system.
public enum JobKind : byte
{
    WallBuild = 1,
}
