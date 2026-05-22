namespace StruggleGame.Sim.Map;

// Tiles are stacked: terrain (always present) → flooring (built over
// terrain) → wall (vertical, blocks walk) → roof (overhead cover).
// Each layer is its own byte-sized enum; the map keeps four parallel
// arrays so a wall over dirt remembers it sat on dirt, and tearing
// the wall leaves the dirt intact.

public enum TerrainType : byte
{
    Grass = 0,
}

public enum FlooringType : byte
{
    None = 0,
    Wood = 1,
}

public enum WallType : byte
{
    None = 0,
    Stone = 1,
}

public enum RoofType : byte
{
    None = 0,
}

// Layer selector for cross-layer APIs (render copy, designators, etc.)
public enum MapLayer : byte
{
    Terrain = 0,
    Flooring = 1,
    Wall = 2,
    Roof = 3,
}
