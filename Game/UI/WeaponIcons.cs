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
    private static readonly Dictionary<string, ImageTexture?> _highlights = new();

    // Bright-pixels-only copy of an item's art (whitened, dim pixels dropped),
    // softly fanned out to fake a tiny bloom. Null if the item has no art.
    public static ImageTexture? Highlights(string itemPath)
    {
        if (string.IsNullOrEmpty(itemPath)) return null;
        if (_highlights.TryGetValue(itemPath, out var cached)) return cached;
        ImageTexture? hi = null;
        if (Texture(itemPath) is { } tex)
        {
            var img = tex.GetImage();
            img.Convert(Image.Format.Rgba8);
            int w = img.GetWidth(), h = img.GetHeight();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = img.GetPixel(x, y);
                    float lum = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
                    // keep only the brighter metal, ramped above the threshold
                    float a = c.A > 0f ? Mathf.Clamp((lum - 0.40f) / 0.35f, 0f, 1f) : 0f;
                    img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            img.GenerateMipmaps();
            hi = ImageTexture.CreateFromImage(img);
        }
        _highlights[itemPath] = hi;
        return hi;
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

    // Eight unit directions for the bloom fan.
    private static readonly (float dx, float dy)[] BloomDirs =
    {
        (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f),
        (0.7f, 0.7f), (-0.7f, 0.7f), (0.7f, -0.7f), (-0.7f, -0.7f),
    };

    // An icon control that fills its parent rect (inset by pad): a TextureRect
    // when the item has art, otherwise the vector WeaponGlyph for the kind.
    // bloom layers faint, slightly-offset copies of the bright highlights over
    // the sprite so its metal gives off a tiny light bleed.
    public static Control Make(string itemPath, WeaponGlyph.Kind kind, int pad = 4, bool bloom = false)
    {
        var tex = Texture(itemPath);
        if (tex is not null && bloom && Highlights(itemPath) is { } hi)
        {
            var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            holder.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var sprite = TexRect(tex);
            Inset(sprite, pad);
            holder.AddChild(sprite);
            // Faint white highlight copies fanned out 1-2px = soft bloom.
            foreach (float r in new[] { 1f, 2f })
                foreach (var (dx, dy) in BloomDirs)
                {
                    var b = TexRect(hi);
                    b.Modulate = new Color(1f, 1f, 1f, 0.10f);
                    Inset(b, pad, dx * r, dy * r);
                    holder.AddChild(b);
                }
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
