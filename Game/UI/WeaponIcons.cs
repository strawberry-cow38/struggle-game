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
    // "unarmed" is keyed separately (no item path) in these same maps.
    private const string UnarmedKey = "\0unarmed";

    // Load assets/items/<file>.png with mipmaps (trilinear keeps the heavy
    // downscale clean). No Godot import — same path as the ground texture.
    private static ImageTexture? LoadIcon(string file)
    {
        var img = new Image();
        if (img.Load(ProjectSettings.GlobalizePath($"res://assets/items/{file}.png")) != Error.Ok)
            return null;
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    // Solid white silhouette (opaque pixels -> white, alpha kept), tinted via
    // Modulate to build a drop shadow.
    private static ImageTexture BuildSilhouette(ImageTexture src)
    {
        var img = src.GetImage();
        img.Convert(Image.Format.Rgba8);
        int w = img.GetWidth(), h = img.GetHeight();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float a = img.GetPixel(x, y).A;
                if (a > 0f) img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    // Drop-shadow silhouette of an item's art, or null if no art.
    public static ImageTexture? Silhouette(string itemPath)
    {
        if (string.IsNullOrEmpty(itemPath)) return null;
        if (_silhouette.TryGetValue(itemPath, out var cached)) return cached;
        var sil = Texture(itemPath) is { } tex ? BuildSilhouette(tex) : null;
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
        string? file = id switch
        {
            "AssaultRifle" => "m16",
            "SubmachineGun" => "mp5",
            "BoltActionRifle" => "m700",
            "AKM" => "akm",
            "AUG" => "aug",
            _ => null,
        };
        var tex = file is not null ? LoadIcon(file) : null;
        _cache[itemPath] = tex;
        return tex;
    }

    // The unarmed icon (a fist) + its shadow silhouette, shown in place of the
    // vector glyph everywhere a pawn is empty-handed.
    private static ImageTexture? UnarmedTexture()
    {
        if (_cache.TryGetValue(UnarmedKey, out var cached)) return cached;
        var tex = LoadIcon("fist");
        _cache[UnarmedKey] = tex;
        return tex;
    }

    private static ImageTexture? UnarmedSilhouette()
    {
        if (_silhouette.TryGetValue(UnarmedKey, out var cached)) return cached;
        var sil = UnarmedTexture() is { } tex ? BuildSilhouette(tex) : null;
        _silhouette[UnarmedKey] = sil;
        return sil;
    }

    // Cluster of offsets (centered on a down-right base) for a fat soft shadow.
    private static readonly (float dx, float dy)[] ShadowDirs =
    {
        (0f, 0f), (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f),
        (0.7f, 0.7f), (-0.7f, 0.7f), (0.7f, -0.7f), (-0.7f, -0.7f),
    };

    // An icon control that fills its parent rect (inset by pad): a TextureRect
    // when the item has art (or the fist for unarmed), otherwise the vector
    // WeaponGlyph. shadow lays a fat soft dark drop shadow behind the sprite.
    public static Control Make(string itemPath, WeaponGlyph.Kind kind, int pad = 4, bool shadow = false)
    {
        var tex = Texture(itemPath);
        var sil = shadow ? Silhouette(itemPath) : null;
        if (tex is null && kind == WeaponGlyph.Kind.Unarmed)
        {
            tex = UnarmedTexture();
            sil = shadow ? UnarmedSilhouette() : null;
        }

        if (tex is not null)
            return sil is not null ? ShadowedIcon(tex, sil, pad) : InsetTex(tex, pad);

        var glyph = new WeaponGlyph { Glyph = kind, MouseFilter = Control.MouseFilterEnum.Ignore };
        Inset(glyph, pad);
        return glyph;
    }

    private static Control InsetTex(ImageTexture tex, int pad)
    {
        var t = TexRect(tex);
        Inset(t, pad);
        return t;
    }

    // Sprite over a fat soft drop shadow (dark silhouette copies offset
    // down-right, clustered to soften).
    private static Control ShadowedIcon(ImageTexture tex, ImageTexture sil, int pad)
    {
        const float baseX = 2.5f, baseY = 3.0f, spread = 1.8f;
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        holder.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var (dx, dy) in ShadowDirs)
        {
            var s = TexRect(sil);
            s.Modulate = new Color(0f, 0f, 0f, 0.16f);
            Inset(s, pad, baseX + dx * spread, baseY + dy * spread);
            holder.AddChild(s);
        }
        var sprite = TexRect(tex);
        Inset(sprite, pad);
        holder.AddChild(sprite);
        return holder;
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
