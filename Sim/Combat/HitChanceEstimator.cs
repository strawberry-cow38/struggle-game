using System;
using StruggleGame.Sim.Items;

namespace StruggleGame.Sim.Gunnery;

// Cover state on the firing line, for the hover readout.
public enum HitCover : byte { None = 0, Sandbag = 1, WallBlocked = 2 }

// Single-shot hit-chance breakdown for the UI. All probabilities 0..1.
public readonly record struct HitChanceResult(
    float Chance,        // final P(hit) for one shot
    float Distance,      // tiles, shooter -> target
    float ConeDeg,       // dispersion half-angle (spread + recoil)
    float ScatterRadius, // miss disc radius at the target plane, tiles
    float PHorizontal,   // horizontal connect probability
    float PVertical,     // vertical connect probability
    HitCover Cover,      // cover sitting on the line
    bool InRange);

// Pure, allocation-free estimator that mirrors the firing model in
// DummyController.FireOneShot + SimRuntime.ResolveArcImpact: a shot scatters
// into a cone (steady spread + accumulated recoil); the scatter radius at the
// target is tan(cone) * distance; the round connects if its lateral miss is
// inside the body capsule (ProjectileHitRadius) AND its height lands on the
// body, after any wall/sandbag on the line eats it. We solve those two
// independent probabilities analytically instead of Monte-Carlo so the hover
// readout is stable (no jitter) and the per-factor terms are inspectable.
public static class HitChanceEstimator
{
    // Must track SimRuntime.ProjectileHitRadius / RangedMinShotDist.
    public const float HitRadius = 0.45f;
    private const float MinShotDist = 1.5f;

    // isWall / isSandbag sample the map along the firing line (integer tiles).
    public static HitChanceResult Estimate(
        RangedSpec spec, float recoilDeg,
        float fromX, float fromY, float toX, float toY,
        float targetBodyH, float aimHeight,
        Func<int, int, bool> isWall, Func<int, int, bool> isSandbag)
    {
        float ddx = toX - fromX, ddy = toY - fromY;
        float dist = MathF.Sqrt(ddx * ddx + ddy * ddy);
        float coneDeg = spec.SpreadDegrees + MathF.Max(0f, recoilDeg);

        if (dist > spec.Range)
            return new HitChanceResult(0f, dist, coneDeg, 0f, 0f, 0f, HitCover.None, false);

        // Scatter disc radius at the target plane (same as FireOneShot).
        float aimDist = MathF.Max(dist, MinShotDist);
        float coneRad = coneDeg * (MathF.PI / 180f);
        float R = MathF.Tan(coneRad) * aimDist;

        // --- Cover on the line (wall fully blocks; sandbag raises the floor) ---
        HitCover cover = HitCover.None;
        if (dist > 1e-4f)
        {
            int samples = Math.Max(2, (int)(dist / 0.2f));
            for (int k = 1; k <= samples; k++)
            {
                float f = (float)k / samples;
                int cx = (int)(fromX + ddx * f), cy = (int)(fromY + ddy * f);
                if (cx == (int)toX && cy == (int)toY) break; // reached the target tile
                if (isWall(cx, cy)) { cover = HitCover.WallBlocked; break; }
                if (isSandbag(cx, cy)) cover = HitCover.Sandbag; // keep scanning for a wall past it
            }
        }
        if (cover == HitCover.WallBlocked)
            return new HitChanceResult(0f, dist, coneDeg, R, 0f, 0f, cover, true);

        // --- Horizontal: P(lateral miss < HitRadius), disc of radius R ---
        float pH = DiscStripFraction(HitRadius, R);

        // --- Vertical: aim height +/- R must land on the exposed body band.
        // A sandbag on the line raises the lower edge to its crest, so only
        // rounds that pass above it connect. ---
        float lo = (cover == HitCover.Sandbag) ? SimConstants.SandbagCoverHeight : 0f;
        float hi = targetBodyH;
        float pV;
        if (R < 1e-4f)
            pV = (aimHeight >= lo && aimHeight <= hi) ? 1f : 0f;
        else
        {
            float overlap = MathF.Min(aimHeight + R, hi) - MathF.Max(aimHeight - R, lo);
            pV = Math.Clamp(overlap / (2f * R), 0f, 1f);
        }

        float chance = Math.Clamp(pH * pV, 0f, 1f);
        return new HitChanceResult(chance, dist, coneDeg, R, pH, pV, cover, true);
    }

    // Fraction of a uniform disc of radius R that lies within the vertical
    // strip |x| < halfWidth — i.e. P(one axis of a uniform-disc sample is
    // inside the body's lateral half-width). R <= halfWidth => the whole disc
    // is inside => 1.
    private static float DiscStripFraction(float halfWidth, float R)
    {
        if (R <= 1e-4f) return 1f;
        float t = halfWidth / R;
        if (t >= 1f) return 1f;
        return (2f / MathF.PI) * (MathF.Asin(t) + t * MathF.Sqrt(1f - t * t));
    }
}
