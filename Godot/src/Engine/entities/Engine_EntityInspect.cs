using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;
using Refii.Text;

namespace Refii.Entities;

public sealed class EntityInspectData
{
    public Dictionary<uint, EntityContentsInfo> Contents { get; init; } = new();

    public Dictionary<uint, ReadableInfo> Readables { get; init; } = new();

    public Dictionary<uint, PropertyInfo> Properties { get; init; } = new();

    public Dictionary<uint, Vector3> Positions { get; init; } = new();

    public HashSet<uint> BoundToNodes { get; } = new();

    public bool FullMetadata { get; init; } = true;

    public bool HasAny =>
        Contents.Count != 0 || Readables.Count != 0 || Properties.Count != 0;

    public bool HasFor(uint hash) =>
        hash != 0 &&
        (Contents.ContainsKey(hash) || Readables.ContainsKey(hash) ||
         Properties.ContainsKey(hash));
}

public static class Engine_EntityInspect
{
    public const string MarkersNodeName = "Containers";

    public static EntityInspectData? Build(
        LevelEntry level,
        Func<string, byte[]?> read,
        bool fullMetadata = true,
        IReadOnlyList<GdbView>? preloadedSupplemental = null)
    {
        byte[]? levelGdb = Engine_LevelGdb.TryRead(level, read, out string gdbPath);
        if (levelGdb is null ||
            !GdbView.TryParseShared(levelGdb, out GdbView levelView, out _))
        {
            return null;
        }

        List<KeyValuePair<uint, string>> saveMap = Engine_GdbSaveMap.Parse(
            read(Engine_LevelGdb.SiblingWithExtension(gdbPath, ".save")));
        if (saveMap.Count == 0)
        {
            return null;
        }

        GdbMulti multi;
        if (preloadedSupplemental is { Count: > 0 })
        {
            var views = new List<GdbView>(1 + preloadedSupplemental.Count) { levelView };
            views.AddRange(preloadedSupplemental);
            multi = GdbMulti.Build(views);
        }
        else if (Engine_GameGdb.MatchesActiveResolver(read))
        {
            List<GdbView> cached = Engine_GameGdb.GetSupplementalViews();
            var views = new List<GdbView>(1 + cached.Count) { levelView };
            views.AddRange(cached);
            multi = GdbMulti.Build(views);
        }
        else
        {
            var views = new List<GdbView>(1 + Engine_LevelProps.SupplementalGdbPaths.Length) { levelView };
            foreach (string path in Engine_LevelProps.SupplementalGdbPaths)
            {
                byte[]? bytes = read(path) ?? read("data/" + path);
                if (bytes is not null && GdbView.TryParseShared(bytes, out GdbView supView, out _))
                {
                    views.Add(supView);
                }
            }
            multi = GdbMulti.Build(views);
        }

        var data = new EntityInspectData
        {
            Contents = Engine_EntityContents.Extract(multi, saveMap, includeItemDetails: fullMetadata),
            Readables = Engine_EntityReadables.Extract(multi, saveMap),
            Properties = Engine_PropertyDetails.Extract(multi, saveMap),
            FullMetadata = fullMetadata,
        };

        if (fullMetadata)
        {
            foreach (GdbPlacement placement in Engine_GdbPlacements.Parse(levelView, saveMap))
            {
                if (placement.Hash != 0)
                {
                    data.Positions.TryAdd(
                        placement.Hash,
                        new Vector3(placement.X, placement.Z, placement.Y));
                }
            }

            TextBank bank = Refii.Gdb.Engine_BnkTextBankLoader.Shared ?? TextBank.Load(read);
            if (bank.Loaded)
            {
                foreach (EntityContentsInfo info in data.Contents.Values)
                {
                    ResolveItemNames(bank, info.InitialItems);
                    ResolveItemNames(bank, info.PotentialItems);
                }

                foreach (ReadableInfo readable in data.Readables.Values)
                {
                    foreach (uint tag in readable.TagHashes)
                    {
                        if (bank.TryLookup(tag, out string text))
                        {
                            readable.ResolvedTags.Add(tag);
                            readable.Texts.Add(text);
                        }
                    }
                }

                foreach (PropertyInfo property in data.Properties.Values)
                {
                    if (property.BuildingNameTag != 0 &&
                        bank.TryLookup(property.BuildingNameTag, out string name))
                    {
                        property.DisplayName = name;
                    }

                    if (property.BuildingAddressTag != 0 &&
                        bank.TryLookup(property.BuildingAddressTag, out string address))
                    {
                        property.Address = address;
                    }

                    if (property.BuildingAnecdotesTag != 0 &&
                        bank.TryLookup(property.BuildingAnecdotesTag, out string anecdotes))
                    {
                        property.Anecdotes = anecdotes;
                    }

                    if (property.BuildingBenefitsTag != 0 &&
                        bank.TryLookup(property.BuildingBenefitsTag, out string benefits))
                    {
                        property.Benefits = benefits;
                    }
                }
            }

            if (!Refii.Core.Engine_AppRoot.IsTestHost)
            {
                GD.Print(
                    $"[inspect] {level}: containers={data.Contents.Count} " +
                    $"readables={data.Readables.Count} properties={data.Properties.Count} " +
                    $"babel={(bank.Loaded ? bank.TagCount.ToString() : "missing")}");
            }
        }
        else if (!Refii.Core.Engine_AppRoot.IsTestHost)
        {
            GD.Print(
                $"[inspect] {level}: containers={data.Contents.Count} " +
                $"readables={data.Readables.Count} properties={data.Properties.Count} (gameplay lean)");
        }

        return data.HasAny ? data : null;
    }

    public static void Annotate(GodotObject node, uint hash, EntityInspectData data)
    {
        if (!data.FullMetadata)
        {
            return;
        }
        bool bound = false;

        if (data.Contents.TryGetValue(hash, out EntityContentsInfo? contents))
        {
            node.SetMeta("container_type", contents.TypeLabel);
            if (contents.SilverKeysNeeded >= 0)
            {
                node.SetMeta("silver_keys_needed", contents.SilverKeysNeeded);
            }

            if (contents.InitialItems.Count != 0)
            {
                node.SetMeta("contents", ItemLines(contents.InitialItems, false));
            }

            if (contents.PotentialItems.Count != 0)
            {
                node.SetMeta(
                    "potential_contents", ItemLines(contents.PotentialItems, true));
            }

            if (contents.CanBeStolenFrom >= 0)
            {
                node.SetMeta("stealable", contents.CanBeStolenFrom != 0);
            }

            if (contents.CanRespawnItemsOnce >= 0)
            {
                node.SetMeta("respawns_once", contents.CanRespawnItemsOnce != 0);
            }

            if (contents.CanRespawnItemsRepeatedly >= 0)
            {
                node.SetMeta(
                    "respawns_repeatedly", contents.CanRespawnItemsRepeatedly != 0);
            }

            if (contents.ChanceOfRespawning >= 0f)
            {
                node.SetMeta("respawn_chance", contents.ChanceOfRespawning);
            }

            if (contents.DogCanLeadTo)
            {
                node.SetMeta("dog_can_lead_here", true);
            }

            bound = true;
        }

        data.Properties.TryGetValue(hash, out PropertyInfo? ownedProperty);

        if (data.Readables.TryGetValue(hash, out ReadableInfo? readable) &&
            readable.Texts.Count != 0)
        {
            var texts = new List<string>(readable.Texts.Count);
            for (int i = 0; i < readable.Texts.Count; i++)
            {
                uint tag = i < readable.ResolvedTags.Count ? readable.ResolvedTags[i] : 0;
                bool duplicatesProperty = ownedProperty is not null && tag != 0 &&
                    (tag == ownedProperty.BuildingNameTag ||
                     tag == ownedProperty.BuildingAddressTag ||
                     tag == ownedProperty.BuildingAnecdotesTag ||
                     tag == ownedProperty.BuildingBenefitsTag);
                if (!duplicatesProperty)
                {
                    texts.Add(readable.Texts[i]);
                }
            }

            if (texts.Count != 0)
            {
                node.SetMeta("readable_text", texts.ToArray());
                bound = true;
            }
        }

        if (data.Properties.TryGetValue(hash, out PropertyInfo? property))
        {
            var details = new Godot.Collections.Dictionary();
            if (property.DisplayName.Length != 0)
            {
                details["Name"] = property.DisplayName;
            }

            if (property.Address.Length != 0)
            {
                details["Address"] = property.Address;
            }

            if (property.BuildingTypeName.Length != 0)
            {
                details["Type"] = property.BuildingTypeName;
            }

            if (property.BasicSalePrice >= 0)
            {
                details["Sale price"] = property.BasicSalePrice;
            }

            if (property.CanRent >= 0)
            {
                details["Can rent"] = property.CanRent != 0 ? "Yes" : "No";
            }

            if (property.Benefits.Length != 0)
            {
                details["Benefits"] = property.Benefits;
            }

            if (property.Anecdotes.Length != 0)
            {
                details["Anecdotes"] = property.Anecdotes;
            }

            details["Building"] = property.BuildingEntityName;
            foreach (KeyValuePair<string, string> field in property.Fields)
            {
                details[field.Key] = field.Value;
            }

            node.SetMeta("property", details);
            GC.KeepAlive(details);
            bound = true;
        }

        if (bound)
        {
            data.BoundToNodes.Add(hash);
        }
    }

    public static Node3D? BuildMarkers(EntityInspectData data)
    {
        var container = new Engine_EditorMarkers { Name = MarkersNodeName };
        var used = new HashSet<string>();

        foreach (KeyValuePair<uint, EntityContentsInfo> pair in data.Contents)
        {
            EntityContentsInfo info = pair.Value;
            if (!info.IsDigSpot && !info.IsDiveSpot)
            {
                continue;
            }

            if (data.BoundToNodes.Contains(pair.Key) ||
                !data.Positions.TryGetValue(pair.Key, out Vector3 position))
            {
                continue;
            }

            Color colour = info.IsDigSpot
                ? new Color(0.55f, 0.38f, 0.20f)
                : new Color(0.25f, 0.55f, 0.95f);

            var marker = new MeshInstance3D
            {
                Name = UniqueName(used,
                    info.EntityName.Length != 0 ? info.EntityName : info.TypeLabel),
                Mesh = new BoxMesh { Size = new Vector3(0.45f, 0.45f, 0.45f) },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = colour,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                },
                Position = position,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };

            Annotate(marker, pair.Key, data);
            container.AddChild(marker);
        }

        if (container.GetChildCount() == 0)
        {
            container.QueueFree();
            return null;
        }

        return container;
    }

    private static void ResolveItemNames(TextBank bank, List<ContentsItem> items)
    {
        foreach (ContentsItem item in items)
        {
            if (item.NameTagHash != 0 &&
                bank.TryLookup(item.NameTagHash, out string name) &&
                name.Length != 0)
            {
                item.DisplayName = name;
            }

            if (item.RandomChoices.Count != 0)
            {
                ResolveItemNames(bank, item.RandomChoices);
            }
        }
    }

    private static string[] ItemLines(List<ContentsItem> items, bool weighted)
    {
        var lines = new List<string>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            ContentsItem item = items[i];
            string label = item.DisplayName.Length != 0
                ? item.DisplayName
                : PrettyTag(item.NameTag.Length != 0 ? item.NameTag : item.EntryLabel);
            if (item.Money >= 0)
            {
                label += $" ({item.Money} gold)";
            }

            if (weighted)
            {
                var extras = new List<string>();
                if (item.Weight >= 0f)
                {
                    extras.Add($"weight {item.Weight:0.###}");
                }

                if (item.MinChapter >= 0f || item.MaxChapter >= 0f)
                {
                    extras.Add($"chapter {Math.Max(item.MinChapter, 0f):0.#}-" +
                               (item.MaxChapter >= 0f
                                   ? item.MaxChapter.ToString("0.#")
                                   : "?"));
                }

                if (item.MinFurnitureRating >= 0f || item.MaxFurnitureRating >= 0f)
                {
                    extras.Add(
                        $"furniture {Math.Max(item.MinFurnitureRating, 0f):0.#}-" +
                        (item.MaxFurnitureRating >= 0f
                            ? item.MaxFurnitureRating.ToString("0.#")
                            : "?"));
                }

                if (extras.Count != 0)
                {
                    label += " [" + string.Join(", ", extras) + "]";
                }
            }

            lines.Add(label);

            foreach (ContentsItem choice in item.RandomChoices)
            {
                string choiceLabel = choice.DisplayName.Length != 0
                    ? choice.DisplayName
                    : PrettyTag(choice.NameTag.Length != 0
                        ? choice.NameTag
                        : choice.EntryLabel);
                if (choice.Money >= 0)
                {
                    choiceLabel += $" ({choice.Money} gold)";
                }

                if (choice.Weight >= 0f)
                {
                    choiceLabel += $" [weight {choice.Weight:0.###}]";
                }

                lines.Add("    - " + choiceLabel);
            }
        }

        return lines.ToArray();
    }

    public static string PrettyTag(string tag)
    {
        foreach (string prefix in new[] { "INV_ITEM_", "OBJECT_", "TEXT_" })
        {
            if (tag.Length > prefix.Length && tag.StartsWith(prefix, StringComparison.Ordinal))
            {
                tag = tag[prefix.Length..];
                break;
            }
        }

        if (tag.EndsWith("_NAME", StringComparison.Ordinal))
        {
            tag = tag[..^5];
        }

        bool needsTitleCase = tag.Contains('_');
        if (!needsTitleCase)
        {
            needsTitleCase = true;
            foreach (char c in tag)
            {
                if (char.IsLower(c))
                {
                    needsTitleCase = false;
                    break;
                }
            }
        }

        if (!needsTitleCase)
        {
            return tag;
        }

        var chars = tag.ToCharArray();
        bool wordStart = true;
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '_')
            {
                chars[i] = ' ';
                wordStart = true;
            }
            else
            {
                chars[i] = wordStart
                    ? char.ToUpperInvariant(chars[i])
                    : char.ToLowerInvariant(chars[i]);
                wordStart = false;
            }
        }

        return new string(chars);
    }

    private static string UniqueName(HashSet<string> used, string name)
    {
        foreach (char c in new[] { '.', ':', '@', '/', '"', '%' })
        {
            name = name.Replace(c, '_');
        }

        if (name.Length == 0)
        {
            name = "Container";
        }

        string candidate = name;
        int suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{name}_{suffix++}";
        }

        return candidate;
    }
}
