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
    public const float BodyAimHeight = 0.85f;   // torso-center of a standing pawn
    public const float ProjectileGravity = 6.5f; // ~9.8 m/s² at 1.5 m/tile
    // Ranged weapons can't engage a target this close — too tight to bring the
    // gun to bear (melee or back off instead).
    public const float RangedMinFireRange = 1.5f;
    // Downed/unconscious pawns lie prone: a low hitbox you have to aim low for.
    public const float DownedBodyHeight = 0.35f;
    public const float DownedAimHeight = 0.18f;
    // Aim heights for the targeted-area selector (standing pawn).
    public const float AimHeadHeight = 1.05f;
    public const float AimLegsHeight = 0.3f;  // BodyAimHeight (0.85) is the torso aim
    public const float AimAutoHeight = 0.55f; // Auto = dead center of the body mass
    // Cover: a sandbag stands ~0.5 tile tall. Shots whose impact height is
    // below this (and coming from the covered side) eat the sandbag; a
    // crouched pawn tucks below it. The cover/peeking systems read this.
    public const float SandbagCoverHeight = 0.5f;
    // A crouched (tucked) pawn drops below the sandbag so high rounds clear
    // them — kept just under SandbagCoverHeight so the bag fully shields them.
    public const float CrouchBodyHeight = 0.45f;
    // How far a popped-out leaning pawn's BODY (and thus its hitbox + the
    // position it's perceived/aimed at) sits toward the peek cell, 0..1. The
    // renderer leans the sprite by the same fraction so the hitbox matches the
    // visual (the gun muzzle still reaches the full peek cell). Lower = less
    // exposed while peeking.
    public const float LeanPeekFraction = 0.6f;
    // A target in shadow is harder to hit: the shooter's dispersion cone is
    // multiplied by (1 + DarknessSpreadBonus * (1 - targetLight)), so a fully
    // dark target (light 0) gets the full bonus and a fully lit one (light 1)
    // none. Drives both live fire (FireOneShot) and the hover hit-chance.
    public const float DarknessSpreadBonus = 1.5f;
    // Snapshot aim mode: no aim time, but the dispersion cone is multiplied by
    // this (~-60% hit chance at range; negligible point-blank where it's used).
    public const float SnapshotSpreadMultiplier = 2.5f;
    // Auto aim mode snapshots a target within this fraction of weapon range
    // (very close); aims at anything farther.
    public const float SnapshotRangeFraction = 0.25f;

    // === Medical ===
    // Total wound-severity one tend/stabilize job covers (worst wounds first,
    // whole wounds; the wound that exhausts the budget is still treated fully).
    public const float TendSeverityBudget = 10f;
    public const float StabilizeSeverityBudget = 20f; // emergency: patch more, fast
    public const long TendWorkTicks = 240;   // ~4s of work to tend
    public const long StabilizeWorkTicks = 45; // ~0.75s — fast emergency patch
    public const float TendQualityStub = 0.75f; // until filth/skill/med-quality exist
    // Per-bullet hit radius around a pawn (tiles), and the fraction of it a
    // popped-out leaning pawn presents (a thin peeking sliver — harder to hit).
    public const float ProjectileHitRadius = 0.45f;
    public const float LeanHitFraction = 0.5f;

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
