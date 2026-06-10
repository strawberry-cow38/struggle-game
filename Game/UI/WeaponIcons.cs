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

    // Solid white silhouette of an item's art (opaque pixels -> white, alpha
    // kept), tinted via Modulate to build a drop shadow. Null if no art.
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
            img.GenerateMipmaps();
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
            {
                // Mipmaps so the heavy 256px->~24px downscale stays clean
                // (trilinear) instead of muddy/aliased.
                img.GenerateMipmaps();
                tex = ImageTexture.CreateFromImage(img);
            }
        }
        _cache[itemPath] = tex;
        return tex;
    }

    // Cluster of offsets (centered on a down-right base) for a fat soft shadow.
    private static readonly (float dx, float dy)[] ShadowDirs =
    {
        (0f, 0f), (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f),
        (0.7f, 0.7f), (-0.7f, 0.7f), (0.7f, -0.7f), (-0.7f, -0.7f),
    };

    // An icon control that fills its parent rect (inset by pad): a TextureRect
    // when the item has art, otherwise the vector WeaponGlyph for the kind.
    // shadow lays a fat soft dark drop shadow (down-right) behind the sprite.
    public static Control Make(string itemPath, WeaponGlyph.Kind kind, int pad = 4, bool shadow = false)
    {
        var tex = Texture(itemPath);
        if (tex is not null && shadow && Silhouette(itemPath) is { } sil)
        {
            const float baseX = 2.5f, baseY = 3.0f, spread = 1.8f;
            var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            holder.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            foreach (var (dx, dy) in ShadowDirs)
            {
                var s = TexRect(sil);
                s.Modulate = new Color(0f, 0f, 0f, 0.16f); // soft dark, stacks to fatten
                Inset(s, pad, baseX + dx * spread, baseY + dy * spread);
                holder.AddChild(s);
            }
            var sprite = TexRect(tex);
            Inset(sprite, pad);
            holder.AddChild(sprite);
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
        // Trilinear: the sprite downscales heavily (256px -> ~24px). Mipmaps +
        // linear keep it clean instead of muddy/aliased.
        TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
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
