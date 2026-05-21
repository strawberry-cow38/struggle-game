namespace StruggleGame.Sim.Jobs;

public enum JobState : byte
{
    Open = 0,
    Claimed = 1,
    Completed = 2,
    Cancelled = 3,
}
