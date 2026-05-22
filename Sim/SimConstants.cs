namespace StruggleGame.Sim;

public static class SimConstants
{
    public const float TileMeters = 1.5f;

    public const int TickHz = 60;
    public const float TickSeconds = 1f / TickHz;

    public const int MapSize = 256;

    public const int PixelsPerTile = 64;

    public const float WalkTilesPerSecond = 2.0f;

    // Inventory caps for a colonist. Either one being exceeded stops them
    // adding more to their carry. Both default to 75 so a single wood
    // stack (also capped 75) maxes them out exactly.
    public const float MaxCarryWeight = 75f;
    public const float MaxCarryBulk = 75f;

    // Manhattan radius the haul-batching scan looks within for additional
    // items to top off the colonist's inventory mid-trip.
    public const int HaulTopoffRadius = 12;
}
