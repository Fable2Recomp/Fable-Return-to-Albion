using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Fx;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Launcher.Tools;

/// <summary>
/// Does the particle data actually reach the effects, and where does the chain break?
///
/// The binding IS stated in the data — `sub_82587A08` reads "ParticleEffect" off the entity's
/// component definition, `sub_82767C70` and `sub_82569B08` read "ParticleEmitter" — so
/// <see cref="Engine_LevelFx"/> reads it rather than matching keywords. The failure mode that
/// makes this check worth having is that a keyword rule produces a plausible number of fires
/// in a region whether or not one binding was read: a plausible count is not evidence. So the
/// count is per route, with the misses named.
///
/// Nothing is written and nothing is drawn. It reads the shipped banks in place.
///
///   godot --path Godot --headless res://debug/fx_check.tscn
///
/// `REFII_FX_LEVEL` picks the level by substring; without it every level is walked, which is
/// the run that matters — one region resolving well says nothing about the set.
/// `REFII_FX_MISSES` sets how many unresolved placements to name per level (default 12).
/// </summary>
public partial class FxCheck : Node
{
    public override void _Ready()
    {
        string dataRoot = Engine_GameData.Resolve();
        if (dataRoot.Length == 0)
        {
            GD.PrintErr("[fx-check] no game data; set REFII_GAME_DATA.");
            GetTree().Quit(1);
            return;
        }

        using BnkFileSystem fs = BnkFileSystem.Mount(dataRoot);
        GD.Print($"[fx-check] mounted {dataRoot}");

        byte[]? Read(string path) =>
            fs.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;

        // THE BANK FIRST, and through FableFx.SharedBank rather than a reader of our own.
        // Every count below is meaningless if the bank failed, and a bank that does not load
        // looks exactly like data that states no effects — so this has to exercise the same
        // acquisition the game does, including its fallbacks.
        //
        // Note particle_bank.bnk is a CONTAINER, not a file inside one, so it is not reachable
        // through the mounted VFS by virtual path; SharedBank's disk fallback off
        // Engine_GameData.Resolve() is what actually finds it.
        FxBank? bank = FableFx.SharedBank();
        if (bank is null)
        {
            GD.PrintErr("[fx-check] particle bank unavailable — see the [fx] line above");
            GetTree().Quit(1);
            return;
        }

        GD.Print(
            $"[fx-check] bank v{bank.Version}: {bank.Effects.Count} effects, " +
            $"{bank.Systems.Count} systems, {bank.Emitters.Count} emitters");

        int sustaining = bank.Effects.Count(e => Engine_FxSystem.EffectSustains(bank, e.NameHash));
        GD.Print(
            $"[fx-check] {sustaining} of {bank.Effects.Count} effects sustain; the rest are " +
            "one-shots and are never placed as level ambience");

        // WHICH effects the sustain test rejects, by name. The test is a host invention — it
        // simulates for 12s and asks whether anything is still spawning — so if it is wrong it
        // is wrong silently, and a whole class of effect simply never appears. Names make that
        // visible: a list full of impacts and hits is the test working, a list with fires and
        // torches in it is the test eating the level's ambience.
        // The bank is hash-keyed and carries no names, so recover them the way the engine does
        // — Engine_FxNames scans the shipped .gdb/.lua/.save/.list for strings that hash back.
        var allHashes = new HashSet<uint>(bank.Effects.Select(e => e.NameHash));
        Dictionary<uint, string> names = Engine_FxNames.Harvest(
            allHashes, fs.EnumerateAllFiles().Select(f => f.VirtualPath), Read);
        GD.Print($"[fx-check] recovered {names.Count} of {allHashes.Count} effect names");
        EffectNames = names;

        var rejected = new HashSet<uint>(
            bank.Effects.Where(e => !Engine_FxSystem.EffectSustains(bank, e.NameHash))
                .Select(e => e.NameHash));

        foreach (string probe in new[]
                 { "fire", "torch", "brazier", "candle", "lantern", "lamp", "flame", "water",
                   "fall", "splash", "steam", "smoke", "dust" })
        {
            var matching = names
                .Where(kv => kv.Value.Contains(probe, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matching.Count == 0)
            {
                continue;
            }

            int hit = matching.Count(kv => rejected.Contains(kv.Key));
            GD.Print($"[fx-check]     '{probe}': {hit} of {matching.Count} rejected as one-shot");
            if (hit != 0)
            {
                GD.Print(
                    "[fx-check]         e.g. " + string.Join(", ", matching
                        .Where(kv => rejected.Contains(kv.Key))
                        .Select(kv => kv.Value).Take(6)));
            }
        }

        string want = OS.GetEnvironment("REFII_FX_LEVEL");
        int missCap = 12;
        if (int.TryParse(OS.GetEnvironment("REFII_FX_MISSES"), out int parsed) && parsed >= 0)
        {
            missCap = parsed;
        }

        List<LevelEntry> levels = Engine_LevelIndex.Discover(fs);
        if (want.Length != 0)
        {
            levels = levels
                .Where(l => l.Name.Contains(want, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (levels.Count == 0)
        {
            GD.PrintErr($"[fx-check] no level matched '{want}'.");
            GetTree().Quit(1);
            return;
        }

        List<GdbView> supplemental = Engine_LevelProps.LoadSupplementalViews(Read);
        GD.Print($"[fx-check] {supplemental.Count} supplemental gdb view(s)");

        var totals = new Totals();
        foreach (LevelEntry level in levels)
        {
            Walk(level, Read, bank, supplemental, missCap, totals);
        }

        DumpDummyNames(fs, Read);
        Classify(levels, Read, bank, supplemental);
        CheckTextures(bank, fs);

        GD.Print(
            $"[fx-check] TOTAL over {levels.Count} level(s): {totals.Placements} placements, " +
            $"{totals.Resolved} resolved ({totals.ByAuthored} authored, {totals.ByName} name, " +
            $"{totals.ByBinding} binding), {totals.NotInBank} resolved to a hash the bank does " +
            $"not hold, {totals.Unresolved} unresolved; {totals.Spawned} ACTUALLY SPAWNED " +
            "after the one-shot and dedup gates");

        GetTree().Quit(0);
    }

    /// <summary>
    /// WHICH FORM is each stated particle field in, and does its value resolve?
    ///
    /// IDA says ParticleEmitter is read two ways and they mean different things: as TYPE 4 the
    /// value is a string hash the guest resolves to a name and looks up in particle_bank, and
    /// as TYPE 6 it is an entryRef the guest resolves to another GDB ENTRY. Our walk currently
    /// treats both as a bank key, which can only work for the first — a type-6 value looked up
    /// in the bank is an entry hash being asked of a name table, and it misses by construction.
    ///
    /// This buckets the raw fields so the miss count above can be attributed rather than
    /// guessed at.
    /// </summary>
    private static void Classify(
        List<LevelEntry> levels,
        Func<string, byte[]?> read,
        FxBank bank,
        List<GdbView> supplemental)
    {
        var byForm = new Dictionary<string, int>();
        void Bump(string key) => byForm[key] = byForm.TryGetValue(key, out int n) ? n + 1 : 1;

        var seenViews = new List<GdbView>(supplemental);
        foreach (LevelEntry level in levels)
        {
            byte[]? gdbBytes = Engine_LevelGdb.TryRead(level, read, out _);
            if (gdbBytes is not null &&
                GdbView.TryParse(gdbBytes, out GdbView view, out _))
            {
                seenViews.Add(view);
            }
        }

        foreach (GdbView view in seenViews)
        {
            for (int record = 0; record < view.RecordCount; record++)
            {
                foreach ((uint hash, GdbFieldType type, uint raw) in view.RawFields(record))
                {
                    if (hash != Engine_LevelFx.ParticleEmitter &&
                        hash != Engine_LevelFx.ParticleEffect)
                    {
                        continue;
                    }

                    string field = hash == Engine_LevelFx.ParticleEmitter
                        ? "ParticleEmitter" : "ParticleEffect";

                    if (raw == 0 || raw == Engine_LevelFx.Null)
                    {
                        Bump($"{field} type={type} EMPTY");
                        continue;
                    }

                    bool inBank = bank.FindByHash(raw) is not null;
                    bool isEntry = false;
                    foreach (GdbView probe in seenViews)
                    {
                        if (probe.TryLookup(raw, out _))
                        {
                            isEntry = true;
                            break;
                        }
                    }

                    string verdict = inBank
                        ? "-> BANK EFFECT"
                        : isEntry ? "-> gdb entry (needs another hop)" : "-> UNRESOLVED";
                    Bump($"{field} type={type} {verdict}");
                }
            }
        }

        // EVERY field name the data uses that mentions Particle. The walk has to be built from
        // these, not from a graph search — a search wanders out of the entity it started on and
        // comes back with somebody else's effect.
        var particleNames = new Dictionary<string, int>();
        foreach (GdbView view in seenViews)
        {
            for (int record = 0; record < view.RecordCount; record++)
            {
                foreach (GdbField f in view.Fields(record))
                {
                    if (f.Name.Length == 0 ||
                        (!f.Name.Contains("Particle", StringComparison.OrdinalIgnoreCase) &&
                         !f.Name.Contains("Dummy", StringComparison.OrdinalIgnoreCase) &&
                         !f.Name.Contains("Attach", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    string key = $"{f.Name} ({f.Type}) e.g. {f.Describe()}";
                    particleNames[key] = particleNames.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }
        }

        var dummyValues = new Dictionary<string, int>();
        foreach (GdbView v in seenViews)
        {
            for (int rec = 0; rec < v.RecordCount; rec++)
            {
                foreach (GdbField f in v.Fields(rec))
                {
                    if (f.Hash == Engine_LevelFx.DummyObject && f.Text.Length != 0)
                    {
                        dummyValues[f.Text] =
                            dummyValues.TryGetValue(f.Text, out int dn) ? dn + 1 : 1;
                    }
                }
            }
        }

        GD.Print("[fx-check] --- every distinct DummyObject value -> bone name ---");
        foreach ((string val, int n) in dummyValues.OrderByDescending(kv => kv.Value))
        {
            string bone = Engine_LevelFx.DummyBoneName(val);
            string mark = bone.Length == 0 ? "  *** NO MAPPING ***" : string.Empty;
            GD.Print($"[fx-check]     x{n,-4} {val,-52} -> {bone}{mark}");
        }

        GD.Print("[fx-check] --- every field name mentioning 'Particle' ---");
        foreach ((string key, int count) in particleNames.OrderByDescending(kv => kv.Value))
        {
            GD.Print($"[fx-check]     {count,6}  {key}");
        }

        GD.Print("[fx-check] --- stated particle fields, by form ---");
        foreach ((string key, int count) in byForm.OrderByDescending(kv => kv.Value))
        {
            GD.Print($"[fx-check]     {count,6}  {key}");
        }

        // WHAT IS ACTUALLY ON THE OTHER END of a reference-typed particle field. Knowing the
        // hop exists is not the same as knowing what it lands on, and following it blind is
        // how the walk ended up returning entry hashes to a name lookup in the first place.
        int shown = 0;
        foreach (GdbView view in seenViews)
        {
            for (int record = 0; record < view.RecordCount && shown < 4; record++)
            {
                foreach ((uint hash, GdbFieldType type, uint raw) in view.RawFields(record))
                {
                    if ((hash != Engine_LevelFx.ParticleEmitter &&
                         hash != Engine_LevelFx.ParticleEffect) ||
                        type is not (GdbFieldType.Reference or GdbFieldType.ResourceReference) ||
                        raw == 0 || raw == Engine_LevelFx.Null)
                    {
                        continue;
                    }

                    foreach (GdbView probe in seenViews)
                    {
                        if (!probe.TryLookup(raw, out int target))
                        {
                            continue;
                        }

                        string owner = hash == Engine_LevelFx.ParticleEmitter
                            ? "ParticleEmitter" : "ParticleEffect";
                        GD.Print($"[fx-check] === chain from {owner} ({type}) 0x{raw:X8}");
                        Follow(seenViews, bank, raw, 0, new HashSet<uint>());
                        shown++;
                        break;
                    }

                    if (shown >= 4)
                    {
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Do the effects' textures exist? A particle with no texture is invisible, and an effect
    /// that resolves, spawns and then draws nothing is indistinguishable from one that never
    /// resolved at all — except that the counts above call it a success.
    /// </summary>
    private static void CheckTextures(FxBank bank, BnkFileSystem fs)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FxSystemDef system in bank.Systems)
        {
            foreach (FxVisual visual in system.Visuals)
            {
                if (visual.Texture.Length != 0)
                {
                    wanted.Add(visual.Texture);
                }
            }
        }

        int found = 0;
        var missing = new List<string>();
        foreach (string texture in wanted)
        {
            string leaf = texture.Replace('\\', '/');
            int slash = leaf.LastIndexOf('/');
            if (slash >= 0)
            {
                leaf = leaf[(slash + 1)..];
            }

            bool hit = fs.TryGetFile(FableFx.TextureFolder + leaf, out _) ||
                       fs.TryGetFile(texture.Replace('\\', '/'), out _);
            if (hit)
            {
                found++;
            }
            else if (missing.Count < 10)
            {
                missing.Add(texture);
            }
        }

        // WHICH D3D9 BLEND PAIRS the bank actually asks for. The host collapses these to one
        // bool, and whether that is right depends on which pairs are present — a SRCALPHA:ONE
        // additive consumes the alpha the pixel shader writes, a ONE:ONE additive ignores it.
        var blends = new Dictionary<(int Src, int Dst), int>();
        foreach (FxSystemDef system in bank.Systems)
        {
            foreach (FxVisual visual in system.Visuals)
            {
                var key = (visual.SrcBlend, visual.DstBlend);
                blends[key] = blends.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }

        GD.Print("[fx-check] --- blend pairs the bank asks for (Xenos factors; additive = dst ONE) ---");
        foreach (((int src, int dst), int count) in blends.OrderByDescending(kv => kv.Value))
        {
            GD.Print(
                $"[fx-check]     {count,5}  src={D3dBlendName(src),-16} dst={D3dBlendName(dst)}");
        }

        GD.Print(
            $"[fx-check] textures: {found} of {wanted.Count} named by the bank resolve in the " +
            $"mounted data");
        if (missing.Count != 0)
        {
            GD.Print($"[fx-check]     missing sample: {string.Join(", ", missing)}");
        }
    }

    /// <summary>
    /// The bank's blend enum, named as what it actually is.
    ///
    /// NOT D3DBLEND. The guest maps the value through `Render_MapBlendFactorToD3D` (0x822D76F8,
    /// an identity switch) into `g_BlendFactorMapD3D` at 0x8331701C — u32 big-endian
    /// [0, 1, 4, 5, 6, 7, 10, 11, 8, 9, 12, 13, 16] — and writes the result straight into
    /// RB_BLENDCONTROL, so the mapped value is a XENOS hardware blend factor. Absent means the
    /// loader's default, which is ONE for source and INV_SRC_ALPHA for destination.
    /// </summary>
    private static string D3dBlendName(int value) => value switch
    {
        -1 => "(default)",
        0 => "ZERO",
        1 => "ONE",
        2 => "SRC_COLOR",
        3 => "INV_SRC_COLOR",
        4 => "SRC_ALPHA",
        5 => "INV_SRC_ALPHA",
        6 => "DEST_ALPHA",
        7 => "INV_DEST_ALPHA",
        8 => "DEST_COLOR",
        9 => "INV_DEST_COLOR",
        10 => "SRC_ALPHA_SAT",
        _ => $"?{value}",
    };

    /// <summary>
    /// THE FALSIFIABLE TEST for how a DummyObject key is matched.
    ///
    /// IDA says the guest CRC-32s the unmodified dotted string (poly 0xEDB88320, init 0, no
    /// final XOR) and binary-searches the .gmd node table with it — no dotted-path parsing and
    /// no "_DummyObject" concatenation anywhere in the code. But the node names are FILE data,
    /// so the binary cannot say which literal form they take. This prints what our reader
    /// actually sees for a model, so the two candidate keys can be compared against real names.
    /// </summary>
    private static void DumpDummyNames(BnkFileSystem fs, Func<string, byte[]?> read)
    {
        string want = OS.GetEnvironment("REFII_FX_MODEL");
        if (want.Length == 0)
        {
            return;
        }

        foreach (BnkFile file in fs.EnumerateAllFiles())
        {
            if (!file.VirtualPath.EndsWith(".mdl.gmd", StringComparison.OrdinalIgnoreCase) ||
                !file.VirtualPath.Contains(want, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[]? bytes = read(file.VirtualPath);
            if (bytes is null)
            {
                continue;
            }

            GD.Print($"[fx-check] === {file.VirtualPath} ({bytes.Length} bytes)");
            var run = new System.Text.StringBuilder();
            foreach (byte b in bytes)
            {
                if (b >= 0x20 && b < 0x7F)
                {
                    run.Append((char)b);
                    continue;
                }

                string text = run.ToString();
                run.Clear();
                if (text.Length >= 4)
                {
                    GD.Print($"[fx-check]     crc32=0x{Crc32(text):X8}  {text}");
                }
            }

            return;
        }

        GD.Print($"[fx-check] no .mdl.gmd matched '{want}'");
    }

    /// <summary>CRC-32, poly 0xEDB88320, init 0, NO final XOR — the guest's sub_823B0A38.</summary>
    private static uint Crc32(string s)
    {
        uint crc = 0;
        foreach (char c in s)
        {
            crc ^= (byte)c;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return crc;
    }

    /// <summary>Walks one chain to the bottom, printing every hop it takes.</summary>
    private static void Follow(
        List<GdbView> views, FxBank bank, uint hash, int depth, HashSet<uint> seen)
    {
        if (depth > 6 || hash == 0 || hash == Engine_LevelFx.Null || !seen.Add(hash))
        {
            return;
        }

        string pad = new string(' ', 6 + (depth * 2));
        foreach (GdbView view in views)
        {
            if (!view.TryLookup(hash, out int record))
            {
                continue;
            }

            foreach (GdbField f in view.Fields(record))
            {
                bool interesting =
                    f.Hash is Engine_LevelFx.ParticleEmitter or Engine_LevelFx.ParticleEffect ||
                    (f.Name.Length != 0 && f.Name.Contains("Particle", StringComparison.Ordinal)) ||
                    f.Hash == ParentHash;

                if (!interesting)
                {
                    continue;
                }

                string inBank = bank.FindByHash(f.Raw) is not null ? "  <== IN BANK" : string.Empty;
                GD.Print(
                    $"[fx-check] {pad}{f.Hash:X8} {f.Type,-18} " +
                    $"{(f.Name.Length != 0 ? f.Name : "?"),-26} {f.Describe()}{inBank}");

                if (f.IsReference)
                {
                    Follow(views, bank, f.Raw, depth + 1, seen);
                }
            }

            return;
        }

        GD.Print($"[fx-check] {pad}0x{hash:X8} NOT FOUND in any view");
    }

    /// <summary>FNV-1 of "parent" — the inheritance link GDBEntry_FindPropWalkParents follows.</summary>
    private const uint ParentHash = 0x5F6317D5;

    /// <summary>Hash -> name, recovered once so every report line can say what it is.</summary>
    private static Dictionary<uint, string> EffectNames = new();

    private sealed class Totals
    {
        public int Placements;
        public int Resolved;
        public int ByAuthored;
        public int ByName;
        public int ByBinding;
        public int NotInBank;
        public int Unresolved;
        public int Spawned;
    }

    private static void Walk(
        LevelEntry level,
        Func<string, byte[]?> read,
        FxBank bank,
        List<GdbView> supplemental,
        int missCap,
        Totals totals)
    {
        byte[]? gdbBytes = Engine_LevelGdb.TryRead(level, read, out string gdbPath);
        if (gdbBytes is null)
        {
            GD.Print($"[fx-check] {level}: no level gdb");
            return;
        }

        if (!GdbView.TryParse(gdbBytes, out GdbView view, out string why))
        {
            GD.Print($"[fx-check] {level}: level gdb unreadable ({why})");
            return;
        }

        string savePath = Engine_LevelGdb.SiblingWithExtension(gdbPath, ".save");
        List<KeyValuePair<uint, string>> saveMap =
            Engine_GdbSaveMap.Parse(read(savePath), out _);
        List<GdbPlacement> placements = Engine_GdbPlacements.Parse(view, saveMap);
        if (placements.Count == 0)
        {
            return;
        }

        var views = new List<GdbView>(supplemental.Count + 1) { view };
        views.AddRange(supplemental);

        int authored = 0, byName = 0, byBinding = 0, notInBank = 0, unresolved = 0, attached = 0;
        var dummyNames = new Dictionary<string, int>();
        var missNames = new List<string>();
        var hitEffects = new Dictionary<uint, int>();

        foreach (GdbPlacement placement in placements)
        {
            // The routes in the order Engine_LevelFx.Spawn takes them, so the numbers here
            // and the numbers it prints at build time describe the same thing.
            uint fx = Engine_LevelFx.HuntFxHash(placement.Hash, views, out string dummy);
            int route = fx != 0 && fx != Engine_LevelFx.Null ? 1 : 0;
            if (route == 1 && dummy.Length != 0)
            {
                attached++;
                dummyNames[dummy] = dummyNames.TryGetValue(dummy, out int dn) ? dn + 1 : 1;
            }

            if (route == 0)
            {
                string name = placement.EntityName ?? string.Empty;
                if (name.Length != 0 && bank.FindByName(name) is not null)
                {
                    fx = FxBank.Fnv1Lower(name);
                    route = 2;
                }
            }

            if (route == 0)
            {
                unresolved++;
                string missed = placement.EntityName ?? string.Empty;
                if (missNames.Count < missCap && missed.Length != 0)
                {
                    missNames.Add(missed);
                }

                continue;
            }

            if (bank.FindByHash(fx) is null)
            {
                // The data DID state something and the bank does not hold it. That is a
                // different failure from stating nothing, and conflating the two is how a
                // broken bank read passes for empty data.
                notInBank++;
                continue;
            }

            switch (route)
            {
                case 1: authored++; break;
                case 2: byName++; break;
            }

            hitEffects[fx] = hitEffects.TryGetValue(fx, out int n) ? n + 1 : 1;
        }

        int resolved = authored + byName + byBinding;
        totals.Placements += placements.Count;
        totals.Resolved += resolved;
        totals.ByAuthored += authored;
        totals.ByName += byName;
        totals.ByBinding += byBinding;
        totals.NotInBank += notInBank;
        totals.Unresolved += unresolved;

        // AND WHAT ACTUALLY SPAWNS. Resolution is not placement: Spawn drops one-shots, which
        // are most of the bank, and dedups repeats within 2.5m. Counting only what resolves
        // would report a level as full of effects that puts nothing in the world.
        // NOTE: no model map, so the "on a bone" figure in the [fx] line below is always zero
        // here. Attachment needs the placement->model resolution that lives in
        // Engine_LevelProps.Build, and duplicating that ladder to measure it would be the very
        // second implementation the FX resolver exists to avoid. The real numbers come from a
        // game run; this tool answers resolution and spawn counts, not placement.
        var container = new Node3D();
        int spawned = Engine_LevelFx.Spawn(container, view, supplemental, placements);
        totals.Spawned += spawned;
        container.QueueFree();

        if (resolved == 0 && notInBank == 0)
        {
            return;
        }

        GD.Print(
            $"[fx-check] {level}: {placements.Count} placements -> {resolved} resolved " +
            $"({authored} authored, {byName} name), {notInBank} not in bank, " +
            $"{hitEffects.Count} distinct effect(s); {attached} state a DummyObject bone");
        foreach ((string bone, int n) in dummyNames.OrderByDescending(kv => kv.Value).Take(4))
        {
            GD.Print($"[fx-check]     bone '{bone}' x{n}");
        }

        // DOES IT PRODUCE ANYTHING? Spawning a node is not the same as drawing a particle, and
        // an effect that simulates to zero alive is invisible in exactly the way a missing one
        // is. Drive each distinct effect for two seconds and say what came out.
        foreach ((uint hash, int count) in hitEffects.OrderByDescending(kv => kv.Value).Take(8))
        {
            var probe = new Engine_FxSystem();
            var placement = new FxPlacement { EffectHash = hash };
            probe.Build(bank, new[] { placement });

            int alive = 0;
            if (placement.Resolved)
            {
                for (int i = 0; i < 60; i++)
                {
                    probe.Update(1f / 30f);
                }

                alive = probe.AliveCount();
            }

            string name = EffectNames.TryGetValue(hash, out string? n) ? n : "?";
            string verdict = !placement.Resolved
                ? "UNRESOLVED in bank"
                : alive == 0 ? "*** 0 ALIVE after 2s — draws nothing ***" : $"{alive} alive";
            GD.Print($"[fx-check]     {hash:X8} x{count,-4} {name,-34} {verdict}");
        }

        if (missNames.Count != 0)
        {
            GD.Print($"[fx-check]     unresolved sample: {string.Join(", ", missNames)}");
        }
    }
}
