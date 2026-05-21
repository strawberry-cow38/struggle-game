namespace StruggleGame.Sim.Map;

public readonly record struct TilePos(int X, int Y)
{
    public override string ToString() => $"({X},{Y})";
}
