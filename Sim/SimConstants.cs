namespace StruggleGame.Sim;

public static class SimConstants
{
    public const float TileMeters = 1.5f;

    public const int TickHz = 60;
    public const float TickSeconds = 1f / TickHz;

    public const int MapSize = 256;

    public const int PixelsPerTile = 64;

    public const float WalkTilesPerSecond = 2.0f;

    // === Ballistics (phase 2) — heights in tiles, gravity in tiles/sec². ===
    // A colonist stands ~1.1 tiles tall (1.65m at 1.5m/tile); the gun muzzle
    // and torso-center aim point sit around mid-body.
    public const float PawnBodyHeight = 1.1f;
    public const float MuzzleHeight = 0.9f;     // where a held rifle fires from
    public const float BodyAimHeight = 0.85f;   // torso-center the shot aims at
    public const float ProjectileGravity = 6.5f; // ~9.8 m/s² at 1.5 m/tile

    // Inventory caps for a colonist. Either one being exceeded stops them
    // adding more to their carry. Both default to 75 so a single wood
    // stack (also capped 75) maxes them out exactly.
    public const float MaxCarryWeight = 75f;
    public const float MaxCarryBulk = 75f;

    // Manhattan radius the haul-batching scan looks within for additional
    // items to top off the colonist's inventory mid-trip.
    public const int HaulTopoffRadius = 12;

    // Fixed-figure temperatures (°C). Outdoor = the "faux room" id 0;
    // every enclosed indoor room clamps to IndoorTempC until proper
    // per-room heat loss / gain / insulation ships.
    public const float OutdoorTempC = 21f;
    public const float IndoorTempC = 18f;
}
