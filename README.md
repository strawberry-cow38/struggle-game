# Struggle Game

2D top-down oblique colony sim. Godot 4 + C#.

## Conventions

- **Tile size:** 1.5 m² (matches human shoulder width).
- **Perspective:** Rimworld-style oblique top-down (thin front face on tall geometry).
- **Layout:**
  - `Game/` — Godot nodes, scenes, rendering, input.
  - `Sim/` — Headless deterministic simulation (no Godot refs).
  - `Tests/` — xUnit tests against `Sim`.
  - `scenes/` — `.tscn` files.
  - `assets/` — art, audio.
  - `data/` — game data tables.

## Build

```sh
dotnet build StruggleGame.sln
```

Open `project.godot` in Godot 4.6+ to run.
