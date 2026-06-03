namespace StruggleGame.Sim;

// Stable placeholder names for colonists, derived from the entity id so a
// given colonist always reads the same. Swap for a real naming/backstory
// system later; until then this beats bare "#126".
public static class ColonistNames
{
    private static readonly string[] First =
    {
        "Vael", "Bryn", "Sora", "Kade", "Mira", "Tovi", "Resa", "Jarl",
        "Nima", "Orin", "Lux", "Pell", "Wren", "Cass", "Dott", "Elga",
        "Faro", "Gwen", "Hale", "Ivo", "Juno", "Kovu", "Lenn", "Marn",
    };

    private static readonly string[] Last =
    {
        "Brokk", "Vance", "Holt", "Reyes", "Frost", "Marsh", "Quill", "Ashby",
        "Crane", "Dunn", "Vega", "Lowe", "Stark", "Pyke", "Roone", "Sable",
    };

    public static string For(int entityId)
    {
        int id = entityId < 0 ? -entityId : entityId;
        return $"{First[id % First.Length]} {Last[(id / First.Length) % Last.Length]}";
    }
}
