using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Models;

/// <summary>The mobile lite variant's model shaders (Engine_ShaderVariant, Engine_ShaderSpecialisation).</summary>
public static partial class Engine_MdlMaterial
{
    /// <summary>
    /// What the lite variant needs to know about a built material to pick its shader build — the
    /// specular map, the cube, authored PBR — as bits (1, 2, 4), and for its specialised builds
    /// (Engine_ShaderSpecialisation) the flags it sets once and never again: <see cref="FactNormal"/>,
    /// <see cref="FactEmission"/>, <see cref="FactOrm"/>, <see cref="FactPremultiplied"/>,
    /// <see cref="FactGrassBend"/>, kept per pass. Meta rather than a parameter
    /// read: an unset parameter's read is a round trip to the render thread. Set only under
    /// <c>shader_variant = "lite"</c>, and copied with the material into a translucent second pass.
    /// </summary>
    private static StringName LiteFactsMeta => LiteFactsMetaHolder.Name;

    private static class LiteFactsMetaHolder
    {
        public static readonly StringName Name = "lite_variant_facts";
    }

    private const int FactNormal = 8, FactEmission = 16, FactOrm = 32, FactPremultiplied = 64, FactGrassBend = 128;

    /// <summary>The vegetation flags `lite_vegetation_folds` folds (Engine_ShaderSpecialisation.VegetationDefines).</summary>
    private const int FactFoliage = 256, FactGrass = 512, FactFoliageNormals = 1024;

    /// <summary>A character's two-sided pass drawn with back faces culled (AndroidCharacterCull).</summary>
    private const int FactCullBack = 2048;

    /// <summary>A character's pass on its own build (AndroidCharacterShading).</summary>
    private const int FactCharacter = 4096;

    /// <summary>The authored-PBR bit, the third of the shader-build bits above.</summary>
    private const int FactPbr = 4;

    /// <summary>Sets or clears fact bits on a lite pass (a no-op off lite).</summary>
    private static void SetFacts(ShaderMaterial pass, int bits, bool on)
    {
        if (pass.HasMeta(LiteFactsMeta))
        {
            int facts = pass.GetMeta(LiteFactsMeta).AsInt32();
            pass.SetMeta(LiteFactsMeta, on ? facts | bits : facts & ~bits);
        }
    }

    /// <summary>
    /// Starts a fresh modern material's facts from the maps it binds, under the lite variant; the
    /// ORM's bits follow through <see cref="SetFacts"/> where one is bound. A no-op under "full".
    /// </summary>
    private static void CaptureLiteFacts(
        ShaderMaterial result, Texture? specular, Texture? envmap, Texture? normal, Texture? emissive)
    {
        if (Engine_ShaderVariant.Lite)
        {
            result.SetMeta(LiteFactsMeta,
                (specular is not null ? 1 : 0) | (envmap is not null ? 2 : 0) |
                (normal is not null ? FactNormal : 0) | (emissive is not null ? FactEmission : 0));
        }
    }

    /// <summary>
    /// The fallback's lite build of <paramref name="family"/>, or null under "full". No textures: no specular colour, so the
    /// lite set's no-specular build draws it exactly.
    /// </summary>
    private static Shader? LiteFallbackShader(string family) =>
        Engine_ShaderVariant.ModelPath(family, false, false, false, false) is { } lite
            ? Engine_ShaderSpecialisation.Load(lite, null, LoadShared(lite))
            : null;

    /// <summary>Loads the lite model shaders this process can use (WarmShaders); nothing under "full".</summary>
    private static void WarmLiteShaders()
    {
        if (Engine_ShaderVariant.Lite)
        {
            foreach (string lite in Engine_ShaderVariant.ActiveVariantPaths)
            {
                if (!Engine_ShaderVariant.IsTerrainSplat(lite))
                {
                    LoadShared(lite);
                }
            }
        }
    }

    /// <summary>
    /// Moves a finished modern material, and its next passes, onto the lite build of the shader it
    /// ended up with (after the wind, tree and vegetation swaps). A no-op under "full".
    /// </summary>
    private static void ApplyShaderVariant(Material built, Refii.Models.MdlInfo info)
    {
        if (!Engine_ShaderVariant.Lite)
        {
            return;
        }

        bool vegetation = IsVegetationDrawPath(info);
        bool foliage = vegetation && info.MeshKind == 1;
        bool grass = vegetation && info.MeshKind == 4;
        for (Material? pass = built; pass is ShaderMaterial shaded; pass = shaded.NextPass)
        {
            if (shaded.Shader is not { } current || !shaded.HasMeta(LiteFactsMeta))
            {
                continue;
            }

            int facts = shaded.GetMeta(LiteFactsMeta).AsInt32();
            if (Refii.AndroidRuntime.AndroidCharacterCull.For(info) &&
                current.ResourcePath is Engine_ShaderFamily.StandardPath or Engine_ShaderFamily.StandardTranslucentPath)
            {
                SetFacts(shaded, FactCullBack, true);
            }

            if (Refii.AndroidRuntime.AndroidCharacterShading.For(info) &&
                current.ResourcePath is Engine_ShaderFamily.StandardPath or Engine_ShaderFamily.StandardCulledPath
                    or Engine_ShaderFamily.StandardTranslucentPath)
            {
                SetFacts(shaded, FactCharacter, true);
            }

            if (Engine_ShaderVariant.ModelPath(
                    current.ResourcePath,
                    specularMap: (facts & 1) != 0 && !grass,
                    envmap: (facts & 2) != 0,
                    pbr: (facts & 4) != 0,
                    foliage) is { } path &&
                LoadShared(path) is { } lite)
            {
                shaded.Shader = lite;
            }
        }
    }

    /// <summary>
    /// Moves a finished lite material's passes onto their specialised builds
    /// (Engine_ShaderSpecialisation): the same wrapper with code compiled out that this material,
    /// or this process's settings, prove dead. A no-op unless a specialisation key is on.
    /// </summary>
    private static void ApplySpecialisation(Material built)
    {
        // The opaque pass's draw order class (AndroidOpaqueOrder); a no-op while its key is 0.
        Refii.AndroidRuntime.AndroidOpaqueOrder.Model(built);
        if (!Engine_ShaderVariant.Lite || !Engine_ShaderSpecialisation.Any)
        {
            return;
        }

        for (Material? pass = built; pass is ShaderMaterial shaded; pass = shaded.NextPass)
        {
            if (shaded.Shader is not { } current || !shaded.HasMeta(LiteFactsMeta))
            {
                continue;
            }

            string path = Engine_ShaderSpecialisation.PathOf(current);
            if (Engine_ShaderVariant.FullPathOf(path) == path)
            {
                continue;
            }

            if (Engine_ShaderSpecialisation.Load(path, SpecialisationDefines(shaded, path), LoadShared(path)) is { } special)
            {
                shaded.Shader = special;
            }
        }
    }

    /// <summary>The defines this material's own facts allow (Engine_ShaderSpecialisation.FoldDefines).</summary>
    private static IEnumerable<string> SpecialisationDefines(ShaderMaterial pass, string path)
    {
        int facts = pass.GetMeta(LiteFactsMeta).AsInt32();
        string[] vegetation = Engine_ShaderSpecialisation.VegetationFolds
            ? Engine_ShaderSpecialisation.VegetationDefines(
                foliage: (facts & FactFoliage) != 0,
                grass: (facts & FactGrass) != 0,
                foliageNormals: (facts & FactFoliageNormals) != 0,
                tree: Engine_ShaderVariant.FullPathOf(path) == Engine_ShaderFamily.StandardTreePath)
            : Array.Empty<string>();
        if ((facts & FactCullBack) != 0)
        {
            vegetation = [.. vegetation, Refii.AndroidRuntime.AndroidCharacterCull.Define];
        }

        // A character's build (`android_character_lite` and its neighbours), and with the exact set at least the
        // folds that split no build: a character's builds are its own already.
        Engine_ShaderSpecialisation.FoldScope scope = Engine_ShaderSpecialisation.MaterialFoldScope;
        if ((facts & FactCharacter) != 0)
        {
            vegetation = [.. vegetation, .. Refii.AndroidRuntime.AndroidCharacterShading.Defines(path)];
            scope = Refii.AndroidRuntime.AndroidCharacterShading.FoldScopeFor(scope, Refii.AndroidRuntime.AndroidCharacterShading.On);
        }

        if (scope == Engine_ShaderSpecialisation.FoldScope.Off)
        {
            return vegetation;
        }

        return vegetation.Concat(Engine_ShaderSpecialisation.ScopeFolds(scope, path, Engine_ShaderSpecialisation.FoldDefines(
            pbr: (facts & 4) != 0,
            orm: (facts & FactOrm) != 0,
            emission: (facts & FactEmission) != 0,
            premultiplied: (facts & FactPremultiplied) != 0,
            normalMap: (facts & FactNormal) != 0,
            grassBend: (facts & FactGrassBend) != 0)));
    }
}
