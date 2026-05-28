using Friflo.Engine.ECS;

namespace StruggleGame.Sim.World;

// Ticks SleepNeed every sim tick: -1.0 over 16 sim-hours while awake,
// +1.0 over 8 sim-hours while the pawn has a Sleeping component. Wake
// + bed-claim decisions live in DummyController; this system is pure
// accounting so the math is testable in isolation.
public sealed class SleepSystem
{
    // 16h awake = full → empty; 8h asleep = empty → full.
    public const double AwakeSecToEmpty = 16.0 * 3600.0;
    public const double SleepSecToFull = 8.0 * 3600.0;

    public void Step(EntityStore store, float dt)
    {
        float drainPerSimSec = (float)(1.0 / AwakeSecToEmpty);
        float gainPerSimSec = (float)(1.0 / SleepSecToFull);
        float simDt = (float)(SimRuntime.SimSecondsPerRealSecond * dt);
        float drain = drainPerSimSec * simDt;
        float gain = gainPerSimSec * simDt;

        store.Query<SleepNeed>().ForEachEntity((ref SleepNeed need, Entity ent) =>
        {
            if (ent.HasComponent<Sleeping>())
            {
                need.Level += gain;
                if (need.Level > 1f) need.Level = 1f;
            }
            else
            {
                need.Level -= drain;
                if (need.Level < 0f) need.Level = 0f;
            }
        });
    }
}
