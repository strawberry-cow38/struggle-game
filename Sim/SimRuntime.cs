using Friflo.Engine.ECS;

namespace StruggleGame.Sim;

public sealed class SimRuntime
{
    public EntityStore World { get; } = new();

    public long Tick { get; private set; }

    public void Step()
    {
        Tick++;
    }
}
