using System;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.UI;

// Max hit points per selectable "thing", by type. Current HP isn't tracked yet
// (nothing damages buildings/trees/items), so panels show max/max for now —
// the bar is wired to current/max and will reflect damage once a source exists.
public static class ThingHp
{
    public static int Wall(WallType t) => t switch
    {
        WallType.Stone => 300,
        _ => 200,
    };

    public const int Door = 150;
    public const int Bed = 90;
    public const int Lamp = 40;
    public const int Stove = 120;
    public const int UrBoard = 100;
    public const int Item = 50;

    // Saplings are flimsy; a mature tree is sturdier.
    public static int Tree(float growthStage) => 50 + (int)Math.Round(70.0 * Math.Clamp(growthStage, 0.0, 1.0));
}
