using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Ui;

/// <summary>
/// Where a HUD piece's art comes from — the shipped bank by default, a mod's file when one is
/// supplied. The override is always checked first; a piece asks by id and nothing in the HUD
/// knows whether a mod exists.
///
/// Two kinds of answer: a cloned bank piece (the artist's built subtree out of `hud.bgf`, with
/// texture, UV rect and child quads intact) or a replacement texture (one image on a plain quad
/// at the layout's size, so a mod need not reproduce a nine-slice or an atlas rect).
///
/// The bank side reads in place through the same <see cref="Engine_UiTheme"/> the menus use;
/// nothing here writes game art to disk.
/// </summary>
public sealed class Engine_HudArt
{
    /// <summary>The shipped art, catalogued from `hud.bgf` — the fallback for every piece.</summary>
    public Engine_UiTheme? Shipped { get; set; }

    /// <summary>
    /// Replacement textures by PIECE id, not by `.bgf` element name — the element name is an
    /// implementation detail of the shipped art.
    /// </summary>
    private readonly Dictionary<string, Texture2D> _overrides = new();

    /// <summary>How many pieces currently have a replacement.</summary>
    public int OverrideCount => _overrides.Count;

    /// <summary>Supplies a replacement for one piece. A null texture clears it.</summary>
    public void Override(string pieceId, Texture2D? texture)
    {
        if (texture is null)
        {
            _overrides.Remove(pieceId);
            return;
        }

        _overrides[pieceId] = texture;
    }

    /// <summary>
    /// Loads replacement textures from a folder, one file per piece id: the id IS the file name,
    /// so `<c>&lt;folder&gt;/health.bar.png</c>` replaces the piece `health.bar`
    /// (<see cref="Engine_HudLayout.Describe"/> prints them). Returns how many were taken; a
    /// file matching no piece is ignored.
    /// </summary>
    public int LoadOverrides(string folder, IEnumerable<string> pieceIds)
    {
        if (!DirAccess.DirExistsAbsolute(folder))
        {
            return 0;
        }

        int taken = 0;
        foreach (string id in pieceIds)
        {
            foreach (string extension in Extensions)
            {
                string path = $"{folder}/{id}{extension}";
                if (!FileAccess.FileExists(path))
                {
                    continue;
                }

                if (LoadImage(path) is { } texture)
                {
                    _overrides[id] = texture;
                    ++taken;
                }

                break;
            }
        }

        return taken;
    }

    /// <summary>The image formats a mod may supply.</summary>
    private static readonly string[] Extensions = { ".png", ".webp", ".jpg" };

    /// <summary>
    /// Reads one replacement off disk through <see cref="Image"/>, not <c>ResourceLoader</c>:
    /// the player's files sit outside the project with no <c>.import</c> sidecar.
    /// </summary>
    private static Texture2D? LoadImage(string path)
    {
        var image = new Image();
        return image.Load(path) == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
    }

    /// <summary>
    /// Builds the node for one piece: the mod's texture if it has one, else the shipped art.
    /// Null when neither exists — the caller reports it rather than drawing a stand-in.
    /// </summary>
    public Node3D? Build(Engine_HudLayout.Piece piece, Vector2 size)
    {
        if (_overrides.TryGetValue(piece.Id, out Texture2D? replacement))
        {
            var quad = new MeshInstance3D
            {
                Name = piece.Id.Replace('.', '_'),
                Mesh = new QuadMesh { Size = size },
                MaterialOverride = new StandardMaterial3D
                {
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    AlbedoTexture = replacement,
                },
            };

            return quad;
        }

        // The artist's scale survives the clone — a bar authored `scale.y 0.24` is 0.24 of the
        // 100-unit base quad. Only the POSITION is the layout's to overwrite, below.
        // By ORDINAL first: the name alone is ambiguous in the file (the charger's and the
        // assigner's `Spell1..5`).
        Node3D? art = piece.ElementOrdinal >= 0
            ? Shipped?.ArtForOrdinal(piece.Art, piece.ElementOrdinal, keepAuthoredBasis: true)
            : null;
        art ??= Shipped?.Art(piece.Art, 0, keepAuthoredBasis: true);
        if (art is null)
        {
            return null;
        }

        art.Name = piece.Id.Replace('.', '_');

        // A group cut whole with nothing under it is the layout naming an element whose subtree
        // the book does not hold — reported with what the book has, since the piece then draws
        // as a bare node and looks like a guest that never lit it.
        if (piece.Id.StartsWith("group.", System.StringComparison.Ordinal) && art.GetChildCount() == 0)
        {
            int bearers = Shipped?.ArtSourceNodes(piece.Art).Count ?? 0;
            string bearerChildren = string.Join(",", (Shipped?.ArtSourceNodes(piece.Art) ?? System.Array.Empty<Node3D>())
                .Select(n => $"{(n.HasMeta("ordinal") ? n.GetMeta("ordinal").AsInt32() : -1)}:{n.GetChildCount()}"));
            GD.Print($"[hud-art] group '{piece.Id}' (element {piece.ElementOrdinal}) cloned with NO children — " +
                $"{bearers} bearer(s) of '{piece.Art}' in the book, ordinal:children = [{bearerChildren}]");
        }

        // Authored text goes too: the artist leaves "Text" placeholders in the button and prompt
        // components. A piece showing live text is Drive.Text and draws its own label — except a
        // GROUP painting the guest's strings onto its own text elements (the wheel's titles, the
        // TopBox's title and body), which needs those nodes kept: stripped, the clone binding
        // found nothing to paint and the quest box drew as a bare slab.
        if (!piece.LiveText)
        {
            StripText(art);
        }

        // A container keeps its own art (the health bar's surround, the quest box's parchment)
        // but not the authored copies of bound children the HUD draws itself.
        if (piece.Strip is { Count: > 0 } strip)
        {
            StripNamed(art, strip);
        }

        // The clone is placed by the LAYOUT, so the artist's own placement is discarded.
        art.Position = Vector3.Zero;

        // Each piece owns its materials: Duplicate shares sub-resources, and the playhead scrub
        // writes opacity onto materials, which would fade the source book and every sibling.
        return art;
    }

    /// <summary>Removes authored TEXT elements from a cloned piece, by the builder's type meta.</summary>
    /// <summary>
    /// Removes the subtrees named in <paramref name="names"/> from a cloned container. By name,
    /// which is what the clone carries; the whole subtree goes.
    /// </summary>
    private static void StripNamed(Node node, IReadOnlySet<string> names)
    {
        foreach (Node child in node.GetChildren())
        {
            if (names.Contains(child.Name))
            {
                node.RemoveChild(child);
                child.QueueFree();
                continue;
            }

            StripNamed(child, names);
        }
    }

    private static void StripText(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child.HasMeta("element_type") &&
                (string)child.GetMeta("element_type") == "Text")
            {
                node.RemoveChild(child);
                child.QueueFree();
                continue;
            }

            StripText(child);
        }
    }

    /// <summary>One line for the run — how much of the HUD is shipped art and how much is not.</summary>
    public string Describe() =>
        $"[hud-art] {Shipped?.ArtCount ?? 0} shipped piece(s) catalogued, " +
        $"{_overrides.Count} replaced by mods";
}
