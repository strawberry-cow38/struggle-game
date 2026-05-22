namespace StruggleGame.Sim.World;

// Shared adjacency check for every "work on a blueprint tile" system
// (BuildSystem, ChopSystem, FloorSystem, DeconSystem, DoorBuildSystem).
//
// Rule (float, on world coords):
//   pawn must be within Chebyshev 1.0 of the blueprint tile center on
//   BOTH axes AND NOT standing on the blueprint tile itself.
//
// Why float instead of (int)pos.X tile coords: the old integer check
// passed pawns at world (5.9, 5.9) building a blueprint at (4, 4)
// because (int)5.9 == 5 makes dx=dy=1. But the real distance from the
// blueprint center (4.5, 4.5) is sqrt(1.4^2 + 1.4^2) = 1.98 — looks
// like the pawn is two tiles away from the work.
//
// The on-tile exclusion keeps wall/floor/door blueprints feeling like
// the pawn is "next to" the work rather than sitting on top of it.
public static class BuildAdjacency
{
    public static bool InRange(float posX, float posY, int tileX, int tileY)
    {
        float ax = MathF.Abs(posX - (tileX + 0.5f));
        float ay = MathF.Abs(posY - (tileY + 0.5f));
        if (ax > 1.0f || ay > 1.0f) return false;
        if (ax <= 0.5f && ay <= 0.5f) return false;
        return true;
    }
}
