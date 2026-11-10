using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;
using Refii.Textures;

namespace Refii.Hero;

public sealed class TextureMorphEntry
{
    public uint MorphHash { get; init; }

    public uint Layer { get; init; }

    public float Weight { get; init; }

    public TextureMorphBlend Blend { get; init; }
}

/// <summary>Composites the guest's stated appearance layers while retaining reusable decoded sources.</summary>
public static partial class Engine_HeroTextureMorph
{
    /// <summary>Composites kept before the cache is dropped; a dragged slider makes one per value.</summary>
    public const int CompositedLimit = 64;

    private sealed class DecodedTexture
    {
        public int Width { get; set; }

        public int Height { get; set; }

        public bool HasAlpha { get; set; }

        public bool Ok { get; set; }

        public byte[] Rgba { get; set; } = Array.Empty<byte>();

        public long LastUsed { get; set; }
    }

    private static readonly object MorphLock = new();

    // Work started by a departing entity may finish after the region's cache references retire.
    // Its result remains usable by that caller, but must not root the departed region again.
    private static long _textureEpoch;

    private static readonly Dictionary<string, ImageTexture> Composited =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, DecodedTexture> DecodedCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Composites past <see cref="CompositedLimit"/>, for as long as something else still holds them.
    /// The limit bounds what this cache alone keeps alive; a texture a live material draws with
    /// costs the same whether it is listed here or not, so a body asking for it again takes that
    /// one rather than a second copy.
    /// </summary>
    private static readonly Dictionary<string, WeakReference<ImageTexture>> OverLimit =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Flight> CompositeFlights =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Flight> DecodeFlights =
        new(StringComparer.OrdinalIgnoreCase);

    private static Func<string, byte[]?>? _resolver;

    private static List<TextureMorphPair>? _pairs;

    /// <summary>One consistent observation of retained cache references, excluding active callers and native GPU allocation.</summary>
    /// <param name="DecodedEntries">Decoded-source entries, including failed decodes with empty payloads.</param>
    /// <param name="DecodedRgbaBytes">Managed top-level RGBA bytes currently referenced by the decoded dictionary.</param>
    /// <param name="CompositedEntries">Texture references currently held by the composite dictionary, not all live material references.</param>
    /// <param name="CompositesBuilt">Cumulative construction count, including composites no longer held by the dictionary.</param>
    public readonly record struct CacheSnapshot(
        int DecodedEntries, long DecodedRgbaBytes, int CompositedEntries, long CompositesBuilt);

    /// <summary>Samples current retained RGBA and texture-reference counts without decoding, evicting or querying Godot.</summary>
    /// <remarks>This is a cache footprint, not live VRAM or native Image memory. Active callers may retain
    /// values after a clear, and live materials may retain textures absent from the dictionary.</remarks>
    public static CacheSnapshot CacheMetrics
    {
        get
        {
            lock (MorphLock)
            {
                long bytes = 0;
                foreach (DecodedTexture decoded in DecodedCache.Values)
                    bytes += decoded.Rgba.LongLength;
                return new(DecodedCache.Count, bytes, Composited.Count, CompositesBuilt);
            }
        }
    }

    /// <summary>Replaces the catalogue resolver and invalidates appearance sources and textures.</summary>
    /// <param name="resolver">Resolver for the current mounted game, or null when unmounted.</param>
    public static void SetResolver(Func<string, byte[]?>? resolver)
    {
        lock (MorphLock)
        {
            _resolver = resolver;
            _pairs = null;
            ++_textureEpoch;
            Composited.Clear();
            OverLimit.Clear();
            DecodedCache.Clear();
            _decodedBytes = 0;
        }
    }

    /// <summary>Invalidates the catalogue and texture references without disposing borrowers.</summary>
    public static void Invalidate()
    {
        lock (MorphLock)
        {
            _pairs = null;
            ++_textureEpoch;
            Composited.Clear();
            OverLimit.Clear();
            DecodedCache.Clear();
            _decodedBytes = 0;
        }
    }

    /// <summary>Drops retired-region RGBA and composite cache references while preserving the mounted morph catalogue.</summary>
    /// <remarks>Live materials and active callers retain their borrowed textures. An epoch rejects cache
    /// publication by work already in flight, avoiding a second root after the retirement boundary.</remarks>
    public static void ReleaseRegionTextures()
    {
        lock (MorphLock)
        {
            ++_textureEpoch;
            Composited.Clear();
            OverLimit.Clear();
            DecodedCache.Clear();
            _decodedBytes = 0;
        }

        // The ORMs packed from those composites go with them, so a release frees what it did before.
        Engine_TexLibrary.ForgetComposedOrms();
    }

    /// <summary>
    /// Drops cached raw RGBA layer arrays while keeping compiled GPU composite textures intact,
    /// reclaiming LOH buffers once initial crowd composites finish building during level load.
    /// </summary>
    public static void TrimDecodedSources()
    {
        lock (MorphLock)
        {
            DecodedCache.Clear();
            DecodedCache.TrimExcess();
            _decodedBytes = 0;
        }
    }

    public static List<TextureMorphPair> Pairs()
    {
        lock (MorphLock)
        {
            if (_pairs is not null)
            {
                return _pairs;
            }

            if (_resolver is null)
            {
                _pairs = new List<TextureMorphPair>();
                return _pairs;
            }
        }

        var blobs = Refii.Gdb.Engine_GameGdb.LoadSupplementalBlobs(_resolver, includeTu: true);
        List<TextureMorphPair> built = Engine_GdbTextureMorphs.Build(GdbMulti.Build(blobs));
        GD.Print($"[morph] {built.Count} texture morph pair(s), " +
            $"{Engine_TexPathIndex.Count} texture(s) indexed");

        lock (MorphLock)
        {
            _pairs = built;
        }

        return built;
    }

    /// <summary>
    /// The live layers per original texture, in the guest's order: <c>Layer</c> ascending, then
    /// the morph texture (<c>sub_825BE008</c>). Type 1 is always on, type 19 (scars) is not a
    /// stat morph, and a weight at or below 1e-4 is dropped (<c>sub_8220DE88</c>, 0x8220EE6C).
    /// </summary>
    public static Dictionary<uint, List<TextureMorphEntry>> Active(HeroMorphStats stats) =>
        Active(type => Engine_HeroMorphState.Weight(stats, type));

    /// <summary>Composited textures built; run total.</summary>
    public static long CompositesBuilt => System.Threading.Interlocked.Read(ref _compositesBuilt);

    private static long _compositesBuilt;

    /// <summary>Run totals: builds and decodes that waited for the same one in flight, composites found
    /// still alive past the limit or kept on disk, and the sources decoded.</summary>
    public static (long CompositesJoined, long CompositesRevived, long CompositesFromDisk, long DecodesJoined, long SourcesDecoded) SavedWork => (
        System.Threading.Interlocked.Read(ref _compositesJoined),
        System.Threading.Interlocked.Read(ref _compositesRevived),
        System.Threading.Interlocked.Read(ref _compositesFromDisk),
        System.Threading.Interlocked.Read(ref _decodesJoined),
        System.Threading.Interlocked.Read(ref _sourcesDecoded));

    private static long _compositesJoined;

    private static long _compositesRevived;

    private static long _compositesFromDisk;

    private static long _decodesJoined;

    private static long _sourcesDecoded;

    /// <summary>One build under way, for the workers that want the same result to wait for.</summary>
    /// <remarks>Character builds run on several workers and crowds share textures, so without this
    /// two bodies arriving together each decode, blend and encode the same image, and the
    /// duplicate stays in video memory for as long as its material does.</remarks>
    private sealed class Flight(long epoch)
    {
        public readonly long Epoch = epoch;

        public readonly System.Threading.ManualResetEventSlim Landed = new(false);

        public object? Result;

        public bool Answered;
    }

    /// <summary>Under <see cref="MorphLock"/>: starts a flight and returns it, or sets <paramref name="running"/> to the one already up.</summary>
    /// <remarks>A flight from before a retirement is not joined: its result must not serve the new region's first request.</remarks>
    private static Flight? Board(Dictionary<string, Flight> flights, string key, long epoch, out Flight? running)
    {
        if (flights.TryGetValue(key, out running) && running.Epoch == epoch)
        {
            return null;
        }

        running = null;
        var mine = new Flight(epoch);
        flights[key] = mine;
        return mine;
    }

    /// <summary>Waits for another worker's build. False when the caller has to build for itself: the
    /// main thread never waits on a worker, and a build that threw gave no answer.</summary>
    private static bool Await(Flight running, ref long joined, out object? result)
    {
        result = null;
        if (OS.GetThreadCallerId() == OS.GetMainThreadId())
        {
            return false;
        }

        running.Landed.Wait();
        if (!running.Answered)
        {
            return false;
        }

        System.Threading.Interlocked.Increment(ref joined);
        result = running.Result;
        return true;
    }

    private static void Land(Dictionary<string, Flight> flights, string key, Flight mine)
    {
        lock (MorphLock)
        {
            if (flights.TryGetValue(key, out Flight? listed) && ReferenceEquals(listed, mine))
            {
                flights.Remove(key);
            }
        }

        mine.Landed.Set();
    }

    /// <summary>Under <see cref="MorphLock"/>: the composite under this key if it is still alive past the limit.</summary>
    private static ImageTexture? Revive(string key)
    {
        if (!OverLimit.TryGetValue(key, out WeakReference<ImageTexture>? held) ||
            !held.TryGetTarget(out ImageTexture? texture) || !GodotObject.IsInstanceValid(texture))
        {
            return null;
        }

        System.Threading.Interlocked.Increment(ref _compositesRevived);
        if (Composited.Count < CompositedLimit)
        {
            OverLimit.Remove(key);
            Composited[key] = texture;
        }

        return texture;
    }

    /// <summary>Under <see cref="MorphLock"/>: at the limit the list starts again, and what it held stays findable while alive.</summary>
    private static void MakeRoomForComposite()
    {
        if (Composited.Count < CompositedLimit)
        {
            return;
        }

        var gone = new List<string>();
        foreach ((string key, WeakReference<ImageTexture> held) in OverLimit)
        {
            if (!held.TryGetTarget(out _))
            {
                gone.Add(key);
            }
        }

        foreach (string key in gone)
        {
            OverLimit.Remove(key);
        }

        foreach ((string key, ImageTexture texture) in Composited)
        {
            OverLimit[key] = new WeakReference<ImageTexture>(texture);
        }

        Composited.Clear();
    }

    /// <summary>
    /// Serialises <c>Duplicate()</c> of the SHARED model materials. Composites build on
    /// several workers at once (GameEntities.QueueCompositeBuild), and Godot's
    /// <c>ShaderMaterial::_get</c>, which <c>Resource::duplicate</c> calls per property, fills a
    /// lazy remap cache on the source material, so concurrent duplicates race (a null deref in
    /// <c>ShaderMaterial::_getv</c>).
    /// </summary>
    private static readonly object DuplicateGate = new();

    /// <summary>
    /// Replaces, on a private copy of each material, every texture the guest stated a composite
    /// for (<c>sub_825B38B0</c>) — whichever slot holds it — and lays the overlays it stated
    /// for the material's diffuse (<c>sub_8222AAC8</c>, command type 14) over diffuse and normal.
    /// </summary>
    public static void ApplyTo(
        ArrayMesh mesh, IReadOnlyList<Refii.Host.HostBridge.GuestTextureComposite> composites,
        float tan = 0.5f)
    {
        if (composites.Count == 0 && tan == 0.5f)
        {
            return;
        }

        const int DiffuseSlot = 0;
        const int NormalSlot = 1;
        const int SpecularSlot = 2;

        var byOriginal = new Dictionary<uint, Refii.Host.HostBridge.GuestTextureComposite>();
        var overlays = new Dictionary<uint, Refii.Host.HostBridge.GuestTextureComposite>();

        // WHOLE-ENTITY SWAPS, by our slot: the guest states them as every diffuse (0), specular
        // (1) or normal (2) map, and states no composite beside them (`sub_8220DE88` skips that
        // path, 0x8220eb44).
        var everyMap = new Refii.Host.HostBridge.GuestTextureLayer[]?[3];
        foreach (Refii.Host.HostBridge.GuestTextureComposite composite in composites)
        {
            if (composite.Wildcard)
            {
                int slot = composite.Original switch
                {
                    0u => DiffuseSlot,
                    1u => SpecularSlot,
                    2u => NormalSlot,
                    _ => -1,
                };
                if (slot >= 0)
                {
                    everyMap[slot] = composite.Layers;
                }

                continue;
            }

            (composite.Overlay ? overlays : byOriginal)[composite.Original] = composite;
        }

        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is not ShaderMaterial material ||
                !material.HasMeta(Refii.Models.Engine_MdlMaterial.TextureNamesMeta))
            {
                continue;
            }

            string[] names = material.GetMeta(
                Refii.Models.Engine_MdlMaterial.TextureNamesMeta).AsStringArray();
            ShaderMaterial? own = null;

            Refii.Host.HostBridge.GuestTextureLayer[]? over = null;
            if (names.Length > DiffuseSlot && names[DiffuseSlot].Length != 0 &&
                overlays.TryGetValue(
                    Engine_TexPathIndex.PathHash(names[DiffuseSlot]),
                    out Refii.Host.HostBridge.GuestTextureComposite stated))
            {
                over = stated.Layers;
            }

            bool packedSourceChanged = false;
            var slotReplacements = new List<(int Slot, Texture2D Texture)>(3);
            Texture2D? morphedSpecTex = null;
            Texture2D? morphedNormTex = null;
            for (int slot = 0;
                 slot < names.Length && slot < Refii.Models.Engine_MdlMaterial.TextureParameters.Length;
                 slot++)
            {
                if (names[slot].Length == 0)
                {
                    continue;
                }

                uint original = Engine_TexPathIndex.PathHash(names[slot]);
                byOriginal.TryGetValue(
                    original, out Refii.Host.HostBridge.GuestTextureComposite composite);
                if (slot < everyMap.Length && everyMap[slot] is { } swapped)
                {
                    composite = new Refii.Host.HostBridge.GuestTextureComposite(original, swapped);
                }

                Refii.Host.HostBridge.GuestTextureLayer[]? slotOver =
                    slot == DiffuseSlot || slot == NormalSlot ? over : null;

                var layers = slot == DiffuseSlot && everyMap[DiffuseSlot] is null
                    ? SkinLayers(names[slot], composite.Layers, tan) : composite.Layers;
                if (Composite(original, layers, slotOver, slot == NormalSlot)
                    is not Texture2D composited)
                {
                    continue;
                }

                slotReplacements.Add((slot, composited));
                if (slot == SpecularSlot)
                {
                    morphedSpecTex = composited;
                }
                else if (slot == NormalSlot)
                {
                    morphedNormTex = composited;
                }

                packedSourceChanged |= slot == NormalSlot || slot == SpecularSlot;
            }

            if (slotReplacements.Count == 0)
            {
                continue;
            }

            bool hasOrm = false;
            float specPowerMin = 0f;
            float specPowerMax = 0f;
            Texture2D? baseSpecTex = null;
            Texture2D? baseNormTex = null;
            own = Refii.Core.Engine_ServerSyncs.RunGpuUpload(4096L, () =>
            {
                ShaderMaterial copy;
                lock (DuplicateGate)
                {
                    copy = material.HasMeta(Refii.Models.Engine_ClassicMaterial.ClassicMeta)
                        ? Refii.Models.Engine_ClassicMaterial.InstanceCopy(material)
                        : Refii.Models.Engine_MaterialCopy.Shallow(material);
                }

                for (int i = 0; i < slotReplacements.Count; i++)
                {
                    (int replacedSlot, Texture2D composited) = slotReplacements[i];
                    Refii.Models.Engine_ClassicMaterial.SetOnPasses(copy,
                        Refii.Models.Engine_ClassicMaterial.TextureParameter(copy, replacedSlot), composited);
                }

                if (packedSourceChanged && Engine_TexSettings.OrmEnabled &&
                    copy.GetShaderParameter("has_orm").AsBool())
                {
                    hasOrm = true;
                    specPowerMin = copy.GetShaderParameter("spec_power_min").AsSingle();
                    specPowerMax = copy.GetShaderParameter("spec_power_max").AsSingle();
                    baseSpecTex = morphedSpecTex ?? copy.GetShaderParameter("specular_tex").As<Texture2D>();
                    baseNormTex = morphedNormTex ?? copy.GetShaderParameter("normal_tex").As<Texture2D>();
                }

                return copy;
            });

            // THE ORM FOLLOWS THE MAPS IT WAS PACKED FROM. Roughness and metallic come from the
            // specular, occlusion and height from the normal (Engine_TexOrm); a material whose
            // composite replaced either would otherwise draw with the unmorphed pack beside the
            // morphed map — the rule Engine_MdlMaterial already applies to an overridden source.
            ImageTexture? orm = null;
            bool hasHeight = false;
            if (hasOrm)
            {
                orm = Engine_TexLibrary.ComposedOrm(
                    baseSpecTex,
                    baseNormTex,
                    specPowerMin,
                    specPowerMax,
                    Engine_TexSettings.OrmAoStrength,
                    Engine_TexSettings.OrmMetallicStrength,
                    Engine_TexSettings.OrmHeight,
                    out hasHeight);
            }

            int surfaceIdx = s;
            ShaderMaterial finalMat = own;
            Refii.Core.Engine_ServerSyncs.RunGpuUpload(4096L, () =>
            {
                if (orm is not null)
                {
                    finalMat.SetShaderParameter("orm_tex", orm);
                    finalMat.SetShaderParameter("has_height", hasHeight);
                }

                mesh.SurfaceSetMaterial(surfaceIdx, finalMat);
            });
        }
    }

    public static Refii.Host.HostBridge.GuestTextureLayer[]? SkinLayers(
        string texture, Refii.Host.HostBridge.GuestTextureLayer[]? layers, float tan)
    {
        if (!float.IsFinite(tan) || tan == 0.5f) return layers;
        string path = texture.Replace('\\', '/').ToLowerInvariant();
        const string root = "art/characters/heros/unclothed ";
        string sex;
        string prefix;
        string suffix;
        if (path.StartsWith(root + "male/pictures/_sharedtextures/", StringComparison.Ordinal))
        {
            sex = "male";
            prefix = "mhero_";
            suffix = "_01.tex";
        }
        else if (path.StartsWith(root + "female/pictures/_sharedtextures/", StringComparison.Ordinal))
        {
            sex = "female";
            prefix = "fhero_";
            suffix = "_02.tex";
        }
        else return layers;

        string name = path[(path.LastIndexOf('/') + 1)..];
        string part = name == prefix + "face" + suffix ? "face" :
            name == prefix + "torso" + suffix ? "torso" :
            name == prefix + "legs" + suffix ? "legs" : string.Empty;
        if (part.Length == 0) return layers;
        string target = root + sex + "/pictures/_sharedtextures/" + prefix + part +
            (tan < 0.5f ? "_pale_01.tex" : "_tanned_01.tex");
        uint hash = Engine_TexPathIndex.PathHash(target);
        if (Engine_TexPathIndex.Resolve(hash) is null) return layers;

        int count = layers?.Length ?? 0;
        var result = new Refii.Host.HostBridge.GuestTextureLayer[Math.Max(1, count) + 1];
        result[0] = count > 0 ? layers![0] :
            new(Engine_TexPathIndex.PathHash(texture), 0, 1, false, 0, 0);
        result[1] = new(hash, 1, Math.Abs(Math.Clamp(tan, 0, 1) - 0.5f) * 2, false, 0, 0);
        if (count > 1) Array.Copy(layers!, 1, result, 2, count - 1);
        return result;
    }

    /// <summary>
    /// The guest's composite: layer 0 opaque, then each layer over it by its blend mode with
    /// the layer's alpha scaled by its weight (<c>sub_82A7EA28</c>); then the overlays, each
    /// blended by its own diffuse's alpha times its weight. On the normal slot an overlay
    /// contributes its bump, masked by that same diffuse alpha. Null when the result is the
    /// original texture itself.
    /// </summary>
    /// <param name="original">Guest hash of the material's original texture.</param>
    /// <param name="layers">Ordered guest composite layers, or null for no replacement layers.</param>
    /// <param name="overlays">Guest overlays whose diffuse alpha masks colour or normal contributions.</param>
    /// <param name="normalSlot">Whether to composite bump sources under the diffuse coverage mask.</param>
    /// <returns>A borrowed texture, or null when no valid composite is needed. Retirement never disposes this borrow.</returns>
    public static Texture2D? Composite(
        uint original,
        Refii.Host.HostBridge.GuestTextureLayer[]? layers,
        Refii.Host.HostBridge.GuestTextureLayer[]? overlays,
        bool normalSlot)
    {
        layers ??= Array.Empty<Refii.Host.HostBridge.GuestTextureLayer>();
        overlays ??= Array.Empty<Refii.Host.HostBridge.GuestTextureLayer>();

        bool layered = layers.Length > 1 ||
            (layers.Length == 1 && (layers[0].Texture != original || layers[0].HasTint));

        bool overlaid = false;
        foreach (Refii.Host.HostBridge.GuestTextureLayer overlay in overlays)
        {
            if (overlay.Weight > Engine_HeroMorph.WeightFloor &&
                (normalSlot ? overlay.Bump != 0 : overlay.Texture != 0))
            {
                overlaid = true;
            }
        }

        if (!layered && !overlaid)
        {
            return null;
        }

        var key = new System.Text.StringBuilder(normalSlot ? "N" : "L");
        key.Append(original.ToString("x8"));
        foreach (Refii.Host.HostBridge.GuestTextureLayer layer in layers)
        {
            AppendKey(key, layer);
        }

        key.Append('+');
        foreach (Refii.Host.HostBridge.GuestTextureLayer overlay in overlays)
        {
            AppendKey(key, overlay);
        }

        string cacheKey = key.ToString();
        long epoch;
        Flight? mine;
        Flight? running;
        lock (MorphLock)
        {
            epoch = _textureEpoch;
            if (Composited.TryGetValue(cacheKey, out ImageTexture? cached))
            {
                return cached;
            }

            if (Revive(cacheKey) is { } alive)
            {
                return alive;
            }

            mine = Board(CompositeFlights, cacheKey, epoch, out running);
        }

        if (running is not null && Await(running, ref _compositesJoined, out object? theirs))
        {
            return theirs as Texture2D;
        }

        if (mine is null)
        {
            return BuildComposite(original, layers, overlays, normalSlot, cacheKey, epoch);
        }

        try
        {
            ImageTexture? built = BuildComposite(original, layers, overlays, normalSlot, cacheKey, epoch);
            mine.Result = built;
            mine.Answered = true;
            return built;
        }
        finally
        {
            Land(CompositeFlights, cacheKey, mine);
        }
    }

    private static ImageTexture? BuildComposite(
        uint original,
        Refii.Host.HostBridge.GuestTextureLayer[] layers,
        Refii.Host.HostBridge.GuestTextureLayer[] overlays,
        bool normalSlot,
        string cacheKey,
        long epoch)
    {
        if (TryStoredComposite(cacheKey, original, layers, overlays, out string? stored) is { } kept)
        {
            System.Threading.Interlocked.Increment(ref _compositesFromDisk);
            return Publish(cacheKey, epoch, kept);
        }

        string? basePath = Engine_TexPathIndex.Resolve(layers.Length != 0 ? layers[0].Texture : original);
        DecodedTexture? source = basePath is null ? null : Decode(basePath, epoch);
        if (source is null || !source.Ok)
        {
            return null;
        }

        // THE TARGET IS THE LARGEST LAYER, not the base. `sub_82A5BCE8` sizes the composite from
        // 64 x 64 up to the widest and tallest of ALL its layers (texture +148 / +152); a smaller
        // layer is sampled up to it. The Will lines are a larger `ILL_*_will` map over a 32 x 32
        // black `ILL_*` base: composited at the base's size they were soft blobs of glow. The guest
        // then halves past 512 (`dword_8331AB00/04`); that hardware budget is not kept, so no
        // composite comes out softer than its sharpest layer.
        var decoded = new DecodedTexture?[layers.Length];
        int width = Math.Max(MinimumCompositeSize, source.Width);
        int height = Math.Max(MinimumCompositeSize, source.Height);
        for (int i = 1; i < layers.Length; i++)
        {
            string? path = Engine_TexPathIndex.Resolve(layers[i].Texture);
            decoded[i] = path is null ? null : Decode(path, epoch);
            if (decoded[i] is { Ok: true } layerTexture)
            {
                width = Math.Max(width, layerTexture.Width);
                height = Math.Max(height, layerTexture.Height);
            }
        }

        // Pooled when it would otherwise be a copy of the source: Image.CreateFromData copies it
        // again, and a fresh array per composite is large-object garbage only a gen2 returns.
        byte[] output = Fit(source, width, height).Rgba;
        byte[]? pooled = null;
        if (ReferenceEquals(output, source.Rgba))
        {
            pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(width * height * 4);
            Buffer.BlockCopy(output, 0, pooled, 0, width * height * 4);
            output = pooled;
        }

        try
        {
            return BlendAndUpload(output, width, height, original, layers, decoded, overlays, normalSlot, cacheKey, epoch, stored);
        }
        finally
        {
            if (pooled is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pooled);
            }
        }
    }

    private static ImageTexture BlendAndUpload(
        byte[] output,
        int width,
        int height,
        uint original,
        Refii.Host.HostBridge.GuestTextureLayer[] layers,
        DecodedTexture?[] decoded,
        Refii.Host.HostBridge.GuestTextureLayer[] overlays,
        bool normalSlot,
        string cacheKey,
        long epoch,
        string? stored)
    {
        // Whether every layer, mask and bump was there; a composite built without one is not kept.
        bool complete = true;
        if (layers.Length != 0 && layers[0].HasTint)
        {
            new Colourise(layers[0].Tint, layers[0].Brightness).ApplyAll(output, output, width * height);
        }

        for (int i = 1; i < layers.Length; i++)
        {
            Refii.Host.HostBridge.GuestTextureLayer layer = layers[i];
            if (decoded[i] is { Ok: true } over)
            {
                over = Fit(over, width, height);
                Blend(output, width, height, over, over, layer.Mode, layer.Weight,
                    layer.HasTint ? new Colourise(layer.Tint, layer.Brightness) : null);
            }
            else
            {
                // Built without a layer: shown, but never kept, as the next build may have it.
                complete = false;
            }
        }

        foreach (Refii.Host.HostBridge.GuestTextureLayer overlay in overlays)
        {
            if (overlay.Weight <= Engine_HeroMorph.WeightFloor)
            {
                continue;
            }

            string? maskPath = Engine_TexPathIndex.Resolve(overlay.Texture);
            DecodedTexture? mask = maskPath is null ? null : Decode(maskPath, epoch);
            if (mask is null || !mask.Ok)
            {
                complete = false;
                continue;
            }

            DecodedTexture? colour = mask;
            if (normalSlot)
            {
                string? bumpPath = Engine_TexPathIndex.Resolve(overlay.Bump);
                colour = bumpPath is null ? null : Decode(bumpPath, epoch);
            }

            if (colour is not null && colour.Ok)
            {
                Blend(output, width, height, Fit(colour, width, height), Fit(mask, width, height), 1, overlay.Weight, null);
            }
            else
            {
                complete = false;
            }
        }

        string? keep = complete ? KeepAs(stored, cacheKey, original, layers, overlays) : null;
        System.Threading.Interlocked.Increment(ref _compositesBuilt);
        return Publish(cacheKey, epoch, ToTexture(output, width, height, normalSlot, keep));
    }

    private static ImageTexture Publish(string cacheKey, long epoch, ImageTexture texture)
    {
        // Every composite reaches a material through here, and none is rewritten after: the only
        // in-place writers (Engine_TexLibrary.Refresh and its enhancement swaps, Engine_ModRemount,
        // Engine_UiKeyGlyphs) touch their own textures, never one made here.
        Engine_TexLibrary.MarkUnchanging(texture);
        lock (MorphLock)
        {
            if (epoch != _textureEpoch)
                return texture;
            MakeRoomForComposite();
            Composited[cacheKey] = texture;
        }

        return texture;
    }

    private static Image? DecodeToImage(string path, long epoch)
    {
        DecodedTexture decoded = Decode(path, epoch);
        if (!decoded.Ok || decoded.Width <= 0 || decoded.Height <= 0 || decoded.Rgba.Length < decoded.Width * decoded.Height * 4)
        {
            return null;
        }

        return Image.CreateFromData(decoded.Width, decoded.Height, false, Image.Format.Rgba8, decoded.Rgba);
    }

    private static void AppendKey(
        System.Text.StringBuilder key, Refii.Host.HostBridge.GuestTextureLayer layer) =>
        key.Append(layer.Texture.ToString("x8")).Append(layer.Mode)
            .Append(BitConverter.SingleToUInt32Bits(layer.Weight).ToString("x8"))
            .Append(layer.HasTint ? layer.Tint.ToString("x8") : "-")
            .Append(BitConverter.SingleToUInt32Bits(layer.Brightness).ToString("x8"))
            .Append(layer.Bump.ToString("x8"));

    /// <summary>The composite's floor: `sub_82A5BCE8` starts its max over the layers from 64 x 64.</summary>
    private const int MinimumCompositeSize = 64;

    /// <summary>
    /// <paramref name="texture"/> at the target's size: itself when it already is, else sampled
    /// bilinearly between texel centres, as the guest's composite pass filters a smaller layer.
    /// </summary>
    private static DecodedTexture Fit(DecodedTexture texture, int width, int height)
    {
        if (texture.Width == width && texture.Height == height)
        {
            return texture;
        }

        var rgba = new byte[width * height * 4];
        int sw = texture.Width;
        int sh = texture.Height;
        byte[] src = texture.Rgba;
        var columns = new (int X0, int X1, float T)[width];
        for (int x = 0; x < width; x++)
        {
            float fx = Math.Clamp((((x + 0.5f) * sw) / width) - 0.5f, 0f, sw - 1f);
            int x0 = (int)fx;
            columns[x] = (x0, Math.Min(x0 + 1, sw - 1), fx - x0);
        }

        for (int y = 0; y < height; y++)
        {
            float fy = Math.Clamp((((y + 0.5f) * sh) / height) - 0.5f, 0f, sh - 1f);
            int y0 = (int)fy;
            int y1 = Math.Min(y0 + 1, sh - 1);
            float ty = fy - y0;
            for (int x = 0; x < width; x++)
            {
                (int x0, int x1, float tx) = columns[x];
                int a = ((y0 * sw) + x0) * 4;
                int b = ((y0 * sw) + x1) * 4;
                int c = ((y1 * sw) + x0) * 4;
                int d = ((y1 * sw) + x1) * 4;
                int o = ((y * width) + x) * 4;
                for (int k = 0; k < 4; k++)
                {
                    float top = src[a + k] + ((src[b + k] - src[a + k]) * tx);
                    float bottom = src[c + k] + ((src[d + k] - src[c + k]) * tx);
                    rgba[o + k] = (byte)Math.Clamp(top + ((bottom - top) * ty) + 0.5f, 0f, 255f);
                }
            }
        }

        return new DecodedTexture { Width = width, Height = height, HasAlpha = texture.HasAlpha, Ok = true, Rgba = rgba };
    }

    /// <summary>One layer over the target: colour from one texture, coverage from another's alpha, both already at its size (<see cref="Fit"/>).</summary>
    private static void Blend(
        byte[] output, int width, int height, DecodedTexture colour, DecodedTexture coverage,
        int mode, float weight, Colourise? tint)
    {
        int pixels = width * height;
        if (tint is null)
        {
            Engine_HeroTextureKernels.Blend(output, colour.Rgba, coverage.Rgba, pixels, mode, weight);
            return;
        }

        byte[] dyed = System.Buffers.ArrayPool<byte>.Shared.Rent(pixels * 4);
        try
        {
            tint.ApplyAll(colour.Rgba, dyed, pixels);
            Engine_HeroTextureKernels.Blend(output, dyed, coverage.Rgba, pixels, mode, weight);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(dyed);
        }
    }

    /// <summary>
    /// The dye: the layer is reduced to intensity, tone-curved by Brightness, and mapped black
    /// to the dye's hue to white with the pivot at the dye's own luminance. Constants as
    /// <c>sub_82A7E288</c> computes them; the per-pixel arithmetic is the shipped
    /// <c>texture_morph_colourise</c> pixel shader's.
    /// </summary>
    private sealed class Colourise
    {
        private static readonly byte[,] BrightnessRows =
        {
            { 0x00, 0x10, 0x40, 0x90, 0xFF },
            { 0x00, 0x28, 0x60, 0xA8, 0xFF },
            { 0x00, 0x40, 0x80, 0xC0, 0xFF },
            { 0x00, 0x60, 0x9A, 0xCE, 0xFF },
            { 0x00, 0x80, 0xB4, 0xDC, 0xFF },
        };

        private readonly float[] _colour = new float[3];
        private readonly float _colourAlpha;
        private readonly float[] _toIntensity = new float[3];
        private readonly float _pivot;
        private readonly float[] _curve = new float[5];

        public Colourise(uint tint, float brightness)
        {
            int b = (int)((tint >> 24) & 0xFF);
            int g = (int)((tint >> 16) & 0xFF);
            int r = (int)((tint >> 8) & 0xFF);
            int a = (int)(tint & 0xFF);

            int max = Math.Max(r, Math.Max(g, b));
            if (max == 0)
            {
                r = g = b = 1;
                max = 1;
            }

            _colour[0] = r / (float)max;
            _colour[1] = g / (float)max;
            _colour[2] = b / (float)max;
            _colourAlpha = a / 255.0f;

            float wr = MathF.Pow(0.212671f, 0.4545f);
            float wg = MathF.Pow(0.71516f, 0.4545f);
            float wb = MathF.Pow(0.072169f, 0.4545f);
            float sum = wr + wg + wb;
            wr /= sum;
            wg /= sum;
            wb /= sum;

            float scale = max / 255.0f;
            _toIntensity[0] = wr * scale;
            _toIntensity[1] = wg * scale;
            _toIntensity[2] = wb * scale;
            _pivot = (_colour[0] * wr) + (_colour[1] * wg) + (_colour[2] * wb);

            float t = (Math.Clamp(brightness, -1.0f, 1.0f) + 1.0f) * 2.0f;
            int row = Math.Min(4, (int)t);
            int next = Math.Min(4, row + 1);
            float f = t - row;
            for (int i = 0; i < 5; i++)
            {
                _curve[i] = ((BrightnessRows[row, i] * (1.0f - f)) +
                    (BrightnessRows[next, i] * f)) / 255.0f;
            }
        }

        /// <summary>Dyes every pixel of <paramref name="from"/> into <paramref name="to"/>, which may be the same array.</summary>
        public void ApplyAll(byte[] from, byte[] to, int pixels) =>
            Engine_HeroTextureKernels.Colourise(from, to, pixels, _colour, _colourAlpha, _toIntensity, _pivot, _curve);
    }

    public static Dictionary<uint, List<TextureMorphEntry>> Active(Func<uint, float> weightOfType)
    {
        var active = new Dictionary<uint, List<TextureMorphEntry>>();

        foreach (TextureMorphPair pair in Pairs())
        {
            if (pair.MorphType == Engine_HeroMorphState.TypeScar)
            {
                continue;
            }

            float weight = pair.MorphType == Engine_HeroMorphState.TypeAlways
                ? 1.0f
                : weightOfType(pair.MorphType);

            if (weight <= Engine_HeroMorph.WeightFloor)
            {
                continue;
            }

            if (!active.TryGetValue(pair.OriginalTextureHash, out List<TextureMorphEntry>? list))
            {
                list = new List<TextureMorphEntry>();
                active[pair.OriginalTextureHash] = list;
            }

            list.Add(new TextureMorphEntry
            {
                MorphHash = pair.MorphTextureHash,
                Layer = pair.Layer,
                Weight = weight,
                Blend = pair.Blend,
            });
        }

        foreach (List<TextureMorphEntry> list in active.Values)
        {
            list.Sort((a, b) =>
                a.Layer != b.Layer
                    ? a.Layer.CompareTo(b.Layer)
                    : a.MorphHash.CompareTo(b.MorphHash));
        }

        return active;
    }

    /// <summary>Builds or reuses a texture for catalogued swap/additive/alpha appearance layers.</summary>
    /// <param name="textureName">The original virtual texture path.</param>
    /// <param name="active">Ordered active morph entries keyed by original texture hash.</param>
    /// <returns>A borrowed cached texture, or null when no composite is required or its base cannot decode.</returns>
    public static Texture2D? Resolve(
        string textureName, Dictionary<uint, List<TextureMorphEntry>> active)
    {
        if (textureName.Length == 0 || active.Count == 0)
        {
            return null;
        }

        uint hash = Engine_TexPathIndex.PathHash(textureName);
        if (!active.TryGetValue(hash, out List<TextureMorphEntry>? entries))
        {
            return null;
        }

        string baseName = textureName;
        var layers = new List<(string Path, TextureMorphEntry Entry)>();

        foreach (TextureMorphEntry entry in entries)
        {
            string? target = Engine_TexPathIndex.Resolve(entry.MorphHash);
            if (target is null)
            {
                continue;
            }

            if (entry.Blend == TextureMorphBlend.Swap)
            {
                baseName = target;
                continue;
            }

            layers.Add((target, entry));
        }

        if (layers.Count == 0 &&
            baseName.Equals(textureName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = new System.Text.StringBuilder();
        key.Append(Engine_TexPathIndex.PathHash(baseName).ToString("x8"));
        foreach ((string path, TextureMorphEntry entry) in layers)
        {
            key.Append('_')
                .Append(Engine_TexPathIndex.PathHash(path).ToString("x8"))
                .Append(entry.Blend == TextureMorphBlend.Additive ? 'a' : 'o')
                .Append(BitConverter.SingleToUInt32Bits(entry.Weight).ToString("x8"));
        }

        string cacheKey = key.ToString();
        long epoch;
        lock (MorphLock)
        {
            epoch = _textureEpoch;
            if (Composited.TryGetValue(cacheKey, out ImageTexture? cached))
            {
                return cached;
            }
        }

        DecodedTexture source = Decode(baseName, epoch);
        if (!source.Ok)
        {
            return null;
        }

        // Sized as `sub_82A5BCE8` sizes a composite: the largest layer, 64 x 64 at least.
        var decodedLayers = new List<(DecodedTexture Texture, TextureMorphEntry Entry)>(layers.Count);
        int width = Math.Max(MinimumCompositeSize, source.Width);
        int height = Math.Max(MinimumCompositeSize, source.Height);
        foreach ((string path, TextureMorphEntry entry) in layers)
        {
            DecodedTexture decodedLayer = Decode(path, epoch);
            if (decodedLayer.Ok)
            {
                decodedLayers.Add((decodedLayer, entry));
                width = Math.Max(width, decodedLayer.Width);
                height = Math.Max(height, decodedLayer.Height);
            }
        }

        byte[] output = Fit(source, width, height).Rgba;
        if (ReferenceEquals(output, source.Rgba))
        {
            output = (byte[])output.Clone();
        }

        foreach ((DecodedTexture decodedLayer, TextureMorphEntry entry) in decodedLayers)
        {
            DecodedTexture over = Fit(decodedLayer, width, height);
            bool additive = entry.Blend == TextureMorphBlend.Additive;

            for (int y = 0; y < height; y++)
            {
                int sy = over.Height == height
                    ? y
                    : Math.Min(over.Height - 1, y * over.Height / height);

                for (int x = 0; x < width; x++)
                {
                    int sx = over.Width == width
                        ? x
                        : Math.Min(over.Width - 1, x * over.Width / width);

                    int src = ((sy * over.Width) + sx) * 4;
                    float alpha = (over.Rgba[src + 3] / 255.0f) * entry.Weight;
                    if (alpha <= 0.0f)
                    {
                        continue;
                    }

                    int dst = ((y * width) + x) * 4;
                    for (int k = 0; k < 3; k++)
                    {
                        float value = additive
                            ? output[dst + k] + (over.Rgba[src + k] * alpha)
                            : (output[dst + k] * (1.0f - alpha)) + (over.Rgba[src + k] * alpha);

                        output[dst + k] = (byte)Math.Clamp(value + 0.5f, 0.0f, 255.0f);
                    }
                }
            }
        }

        System.Threading.Interlocked.Increment(ref _compositesBuilt);
        string stem = System.IO.Path.GetFileName(baseName);
        return Publish(cacheKey, epoch, ToTexture(
            output, width, height, stem.StartsWith("norm", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The finished composite as a texture, WITH A FULL MIP CHAIN like every other texture the
    /// material binds. The samplers are mipmapped; a top level alone is minified by
    /// point-picking texels, and on a normal map and a self-illumination map that is a skin
    /// full of sparkling highlights and stray glowing dots. A normal map is renormalised down
    /// the chain and block-compressed the way the library compresses its own (BC5 for a
    /// normal, BC1/BC3 otherwise).
    /// </summary>
    /// <param name="rgba">Composite pixel bytes, at least width x height x 4, possibly pooled; copied here.</param>
    /// <param name="width">Composite width in pixels.</param>
    /// <param name="height">Composite height in pixels.</param>
    /// <param name="normalMap">Whether mip generation renormalizes the normal vectors.</param>
    /// <param name="stored">Where to keep the upload for next time (<see cref="StoredUpload"/>), if anywhere.</param>
    /// <returns>The uploaded texture using the Android format fallback when needed.</returns>
    private static ImageTexture ToTexture(byte[] rgba, int width, int height, bool normalMap, string? stored = null)
    {
        Image image = Image.CreateFromData(
            width, height, false, Image.Format.Rgba8, new ReadOnlySpan<byte>(rgba, 0, width * height * 4));
        image.GenerateMipmaps(normalMap);
        Engine_TexImage.CompressBlocks(image, normalMap);
        ImageTexture texture = StoredUpload(image, stored) ?? Refii.Textures.Engine_TextureUpload.CreateTexture(image, world: true);
        Engine_TexLibrary.RegisterCpuImage(texture, image);
        return texture;
    }

    /// <summary>Retains top-level managed RGBA while releasing the newly decoded native Image after extraction.</summary>
    /// <param name="path">Virtual path identifying the reusable source.</param>
    /// <param name="epoch">Texture-cache generation in which the borrowing composite began.</param>
    /// <returns>A borrowed decoded value, including a cached failed decode.</returns>
    private static DecodedTexture Decode(string path, long epoch)
    {
        string key = path.Replace('\\', '/');
        Flight? mine = null;
        Flight? running = null;

        lock (MorphLock)
        {
            if (epoch == _textureEpoch)
            {
                if (DecodedCache.TryGetValue(key, out DecodedTexture? cached))
                {
                    cached.LastUsed = ++_decodedClock;
                    return cached;
                }

                mine = Board(DecodeFlights, key, epoch, out running);
            }
        }

        if (running is not null && Await(running, ref _decodesJoined, out object? theirs) && theirs is DecodedTexture landed)
        {
            return landed;
        }

        if (mine is null)
        {
            return DecodeSource(path, key, epoch);
        }

        try
        {
            DecodedTexture value = DecodeSource(path, key, epoch);
            mine.Result = value;
            mine.Answered = true;
            return value;
        }
        finally
        {
            Land(DecodeFlights, key, mine);
        }
    }

    private static DecodedTexture DecodeSource(string path, string key, long epoch)
    {
        System.Threading.Interlocked.Increment(ref _sourcesDecoded);
        var value = new DecodedTexture();
        byte[]? bytes = Refii.Models.Engine_BnkMeshLoader.ReadBytes(path);

        if (bytes is not null)
        {
            try
            {
                using Image? image = Engine_TexImage.TryCreateImage(bytes, out bool hasAlpha, out _);

                if (image is not null)
                {
                    value.HasAlpha = hasAlpha;
                    Fill(value, image);
                }
            }
            catch (Exception)
            {
                value.Ok = false;
            }
        }

        lock (MorphLock)
        {
            if (epoch == _textureEpoch)
            {
                value.LastUsed = ++_decodedClock;
                if (SourceBudgetBytes > 0 && DecodedCache.TryGetValue(key, out DecodedTexture? replaced))
                {
                    _decodedBytes -= replaced.Rgba.LongLength;
                }

                DecodedCache[key] = value;
                if (SourceBudgetBytes > 0)
                {
                    _decodedBytes += value.Rgba.LongLength;
                    TrimDecodedTo(SourceBudgetBytes, value);
                }
            }
        }

        return value;
    }

    /// <summary>
    /// `hero_texture_sources_mb` (default 0, unbounded): the decoded sources kept for the next
    /// composite, in MB. Past it the longest unused go first, and a composite that wants one of
    /// those decodes it again. Read once. On a phone 0 can mean a bound (<see cref="UnsetSourceMegabytes"/>).
    /// </summary>
    /// <remarks>
    /// Each source is the RGBA top mip of a base, layer, mask or bump texture, 1-4 MB and on the
    /// large object heap, and within a region every distinct source any layered character used
    /// stays (a crowded market holds many). They save a later composite the decode, on the
    /// entity build's worker, and nothing else: the composite itself is uploaded and cached apart.
    /// </remarks>
    private static readonly long SourceBudgetBytes = ReadSourceBudget();

    /// <summary>Bytes in the decoded sources since they were last cleared, when budgeted.</summary>
    private static long _decodedBytes;

    private const int DefaultSourceBudgetMb = 64;

    /// <summary>Stamps each source as it is used, for <see cref="TrimDecodedTo"/>.</summary>
    private static long _decodedClock;

    /// <summary>Under <see cref="MorphLock"/>: drops the longest unused sources until the rest fit.</summary>
    /// <remarks>Dropping them all, as this once did, also dropped the ones every body is about to
    /// ask for again, the shared skin maps above all.</remarks>
    private static void TrimDecodedTo(long budget, DecodedTexture keep)
    {
        while (_decodedBytes > budget)
        {
            string? oldest = null;
            long oldestUse = long.MaxValue;
            foreach ((string key, DecodedTexture source) in DecodedCache)
            {
                if (!ReferenceEquals(source, keep) && source.LastUsed < oldestUse && source.Rgba.LongLength != 0)
                {
                    oldest = key;
                    oldestUse = source.LastUsed;
                }
            }

            if (oldest is null)
            {
                return;
            }

            _decodedBytes -= DecodedCache[oldest].Rgba.LongLength;
            DecodedCache.Remove(oldest);
        }
    }

    private static long ReadSourceBudget()
    {
        try
        {
            int unset = UnsetSourceMegabytes();
            int megabytes = Refii.Core.Engine_RefiiConfig.Int(Refii.Core.Engine_RefiiConfig.Read(out _), "hero_texture_sources_mb", 0);
            return Math.Max(0, megabytes == 0 ? unset : megabytes) * 1048576L;
        }
        catch (Exception)
        {
            return DefaultSourceBudgetMb * 1048576L;
        }
    }

    private static void Fill(DecodedTexture value, Image image)
    {
        // Only the top level is composited: the rest of the chain would be decoded, copied out to
        // a managed array and thrown away with it.
        image.ClearMipmaps();

        if (image.IsCompressed())
        {
            image.Decompress();
        }

        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        int width = image.GetWidth();
        int height = image.GetHeight();
        if (width <= 0 || height <= 0)
        {
            return;
        }

        byte[] data = image.GetData();
        int needed = width * height * 4;
        if (data.Length < needed)
        {
            return;
        }

        value.Width = width;
        value.Height = height;
        value.Rgba = data.Length == needed ? data : data[..needed];
        value.Ok = true;
    }
}
