using System.Collections.Generic;
using Godot;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Shared weapon icon resolver. A pixel-art PNG (assets/items/<file>.png) when
// the item has art, otherwise the vector WeaponGlyph for the weapon kind.
// Textures load once at runtime via Image.Load (no Godot import — same path as
// the ground texture). Used by both the Pocket Sand grid and the colonist bar.
public static class WeaponIcons
{
    private static readonly Dictionary<string, ImageTexture?> _cache = new();

    // Art texture for an item full-path, or null if it has no pixel art.
    public static ImageTexture? Texture(string itemPath)
    {
        if (string.IsNullOrEmpty(itemPath)) return null;
        if (_cache.TryGetValue(itemPath, out var cached)) return cached;
        // ItemsByPath is keyed by FullPath ("Equipment/AssaultRifle"); match on
        // the item Id so category nesting doesn't matter.
        string? id = ItemCatalog.ItemsByPath.TryGetValue(itemPath, out var def) ? def.Id : null;
        string? file = id switch { "AssaultRifle" => "m16", _ => null };
        ImageTexture? tex = null;
        if (file is not null)
        {
            var img = new Image();
            if (img.Load(ProjectSettings.GlobalizePath($"res://assets/items/{file}.png")) == Error.Ok)
                tex = ImageTexture.CreateFromImage(img);
        }
        _cache[itemPath] = tex;
        return tex;
    }

    // An icon control that fills its parent rect (inset by pad): a TextureRect
    // when the item has art, otherwise the vector WeaponGlyph for the kind.
    public static Control Make(string itemPath, WeaponGlyph.Kind kind, int pad = 4)
    {
        Control icon = Texture(itemPath) is { } tex ? TexRect(tex)
            : (Control)new WeaponGlyph { Glyph = kind, MouseFilter = Control.MouseFilterEnum.Ignore };
        Inset(icon, pad);
        return icon;
    }

    private static TextureRect TexRect(Texture2D tex) => new()
    {
        Texture = tex,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        // Bilinear: the sprite is downscaled heavily (256px -> ~20px), so
        // nearest aliases. Linear smooths it.
        TextureFilter = CanvasItem.TextureFilterEnum.Linear,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static void Inset(Control c, int pad, float dx = 0f, float dy = 0f)
    {
        c.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        c.OffsetLeft = pad + dx; c.OffsetRight = -pad + dx;
        c.OffsetTop = pad + dy; c.OffsetBottom = -pad + dy;
    }

    // The weapon a colonist should show: equipped ranged first, then melee,
    // else unarmed. Returns the item full-path ("" = unarmed) and glyph kind.
    public static (string path, WeaponGlyph.Kind kind) PickEquipped(in DummyState d)
    {
        string? ranged = null, melee = null;
        foreach (var eq in d.Equipped)
        {
            if (!ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var def)) continue;
            if (def.IsRangedWeapon) ranged ??= eq.ItemPath;
            else if (def.IsWeapon) melee ??= eq.ItemPath;
        }
        if (ranged is not null) return (ranged, WeaponGlyph.Kind.Ranged);
        if (melee is not null) return (melee, WeaponGlyph.Kind.Melee);
        return ("", WeaponGlyph.Kind.Unarmed);
    }
}
