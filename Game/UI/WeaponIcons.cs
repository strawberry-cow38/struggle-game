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
    private static readonly Dictionary<string, ImageTexture?> _silhouette = new();

    // White silhouette of an item's art (opaque pixels recolored white, alpha
    // kept) for building a glow. Modulate can only darken, so a real white
    // texture is needed to brighten a dark sprite. Null if the item has no art.
    public static ImageTexture? Silhouette(string itemPath)
    {
        if (string.IsNullOrEmpty(itemPath)) return null;
        if (_silhouette.TryGetValue(itemPath, out var cached)) return cached;
        ImageTexture? sil = null;
        if (Texture(itemPath) is { } tex)
        {
            var img = tex.GetImage();
            img.Convert(Image.Format.Rgba8);
            int w = img.GetWidth(), h = img.GetHeight();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float a = img.GetPixel(x, y).A;
                    if (a > 0f) img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            sil = ImageTexture.CreateFromImage(img);
        }
        _silhouette[itemPath] = sil;
        return sil;
    }

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

    // Eight-direction offsets for the glow halo.
    private static readonly (float dx, float dy)[] GlowDirs =
    {
        (1.5f, 0f), (-1.5f, 0f), (0f, 1.5f), (0f, -1.5f),
        (1.1f, 1.1f), (-1.1f, 1.1f), (1.1f, -1.1f), (-1.1f, -1.1f),
    };

    // An icon control that fills its parent rect (inset by pad): a TextureRect
    // when the item has art, otherwise the vector WeaponGlyph for the kind.
    // glow adds a soft white halo behind the sprite (PNG only) so a dark sprite
    // stands off a dark background.
    public static Control Make(string itemPath, WeaponGlyph.Kind kind, int pad = 4, bool glow = false)
    {
        var tex = Texture(itemPath);
        if (tex is not null && glow)
        {
            // White glow: a white silhouette of the sprite fanned out in eight
            // directions behind the real one, so a dark gun reads on a dark bg.
            var sil = Silhouette(itemPath) ?? tex;
            var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            holder.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            foreach (var (dx, dy) in GlowDirs)
            {
                var halo = TexRect(sil);
                halo.Modulate = new Color(0.55f, 0.55f, 0.58f, 0.6f); // muted gray glow
                Inset(halo, pad, dx, dy);
                holder.AddChild(halo);
            }
            var top = TexRect(tex);
            Inset(top, pad);
            holder.AddChild(top);
            return holder;
        }

        Control icon = tex is not null ? TexRect(tex)
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
