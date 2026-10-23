#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.EditorTools;
using Refii.Models;
using Refii.Mods;

namespace Refii.Assets.UI;

/// <summary>
/// The Mod Studio's Modding Guide tab: the pages of <see cref="ModGuidePages"/> (cards, plus each page's
/// own widgets: the GDB jump row, the socket table, the events catalog), and the Mod API Guide page,
/// which draws the markdown files under <see cref="ModGuide.ResFolder"/> — the same files the SDK ships.
///
/// Every page fits any width: the navigation and button rows wrap (an HBox of the eight long captions was
/// ~1,700 px wide, so on a narrower editor the last pages' buttons were off the edge and could not be
/// clicked), and each page scrolls on its own.
/// </summary>
public partial class ModdingGuideView : MarginContainer
{
    private readonly List<Button> _subTabButtons = new();
    private readonly List<Control> _subTabPages = new();
    private Label? _statusToastLabel;

    public ModdingGuideView()
    {
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        SetAnchorsPreset(Control.LayoutPreset.FullRect);

        AddThemeConstantOverride("margin_left", 6);
        AddThemeConstantOverride("margin_top", 6);
        AddThemeConstantOverride("margin_right", 6);
        AddThemeConstantOverride("margin_bottom", 6);

        BuildLayout();
    }

    private void BuildLayout()
    {
        var rootVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        rootVBox.AddThemeConstantOverride("separation", 8);
        AddChild(rootVBox);

        // Top Banner + Sub-Tab Navigation Bar
        var topPanel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var topStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.11f, 0.15f),
            BorderColor = new Color(0.22f, 0.36f, 0.54f),
            BorderWidthLeft = 2,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        topPanel.AddThemeStyleboxOverride("panel", topStyle);

        var topVBox = new VBoxContainer();
        topVBox.AddThemeConstantOverride("separation", 6);

        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 10);

        var titleLbl = new Label
        {
            Text = "📖 Fable II Mod Studio — Creator Guide & Reference",
            Modulate = new Color(0.55f, 0.84f, 1.0f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        titleLbl.AddThemeFontSizeOverride("font_size", 16);
        titleRow.AddChild(titleLbl);

        _statusToastLabel = new Label
        {
            Text = "",
            Modulate = new Color(0.45f, 0.95f, 0.65f),
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _statusToastLabel.AddThemeFontSizeOverride("font_size", 13);
        titleRow.AddChild(_statusToastLabel);

        var openToolsBtn = new Button
        {
            Text = "🧩 Blender Plugin",
            TooltipText = "Open res://modding_tools/, the folder holding refii_blender_tools.py"
        };
        openToolsBtn.AddThemeFontSizeOverride("font_size", 13);
        openToolsBtn.Pressed += OnOpenModdingToolsFolder;
        titleRow.AddChild(openToolsBtn);

        topVBox.AddChild(titleRow);

        // The page buttons WRAP: in one row they are wider than most editor layouts.
        var subNavRow = new HFlowContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        subNavRow.AddThemeConstantOverride("h_separation", 6);
        subNavRow.AddThemeConstantOverride("v_separation", 4);

        for (int i = 0; i < ModGuidePages.All.Count; i++)
        {
            AddSubTabButton(subNavRow, ModGuidePages.All[i].Button, i);
        }

        topVBox.AddChild(subNavRow);
        topPanel.AddChild(topVBox);
        rootVBox.AddChild(topPanel);

        // Each page fills this host and scrolls on its own.
        var pageHost = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };

        foreach (ModGuidePage page in ModGuidePages.All)
        {
            Control built = page.Key == ModGuidePages.GuideKey ? BuildGuidePage() : WrapInScroll(BuildCardPage(page));
            built.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            built.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            _subTabPages.Add(built);
            pageHost.AddChild(built);
        }

        rootVBox.AddChild(pageHost);

        SwitchSubTab(0);
    }

    private void AddSubTabButton(Container bar, string label, int index)
    {
        var btn = new Button
        {
            Text = label,
            ToggleMode = true,
            CustomMinimumSize = new Vector2(0, 28)
        };
        btn.AddThemeFontSizeOverride("font_size", 13);
        btn.Pressed += () => SwitchSubTab(index);
        _subTabButtons.Add(btn);
        bar.AddChild(btn);
    }

    public void SwitchSubTab(int index)
    {
        for (int i = 0; i < _subTabButtons.Count; i++)
        {
            _subTabButtons[i].ButtonPressed = (i == index);
        }

        for (int i = 0; i < _subTabPages.Count; i++)
        {
            _subTabPages[i].Visible = (i == index);
        }

        if (index >= 0 && index < ModGuidePages.All.Count && ModGuidePages.All[index].Key == ModGuidePages.GuideKey && !_guideLoaded)
        {
            LoadGuide();
        }
    }

    /// <summary>The Mod API Guide page on <paramref name="file"/> (a guide file name), at <paramref name="anchor"/>.</summary>
    public void OpenModApiGuide(string file = ModGuide.IndexFile, string anchor = "")
    {
        SwitchSubTab(ModGuidePages.IndexOf(ModGuidePages.GuideKey));
        OpenGuideFile(file, anchor, remember: true);
    }

    private static ScrollContainer WrapInScroll(Control content)
    {
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(content);
        return scroll;
    }

    private void ShowToast(string text)
    {
        if (_statusToastLabel is not null)
        {
            _statusToastLabel.Text = text;
        }
    }

    private void OnOpenModdingToolsFolder()
    {
        string toolsDir = ProjectSettings.GlobalizePath("res://modding_tools");
        Directory.CreateDirectory(toolsDir);
        string pluginFile = Path.Combine(toolsDir, "refii_blender_tools.py");
        EditorModExporter.RevealInExplorer(File.Exists(pluginFile) ? pluginFile : toolsDir);
        ShowToast("Opened res://modding_tools/ in File Explorer.");
    }

    // =========================================================================
    // THE CARD PAGES: ModGuidePages' cards, plus each page's own widgets
    // =========================================================================
    private Control BuildCardPage(ModGuidePage page)
    {
        var column = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        column.AddThemeConstantOverride("separation", 10);

        if (page.Key == ModGuidePages.EventsKey)
        {
            _eventsEntries.Clear();
            column.AddChild(BuildEventsToolbar());
        }

        foreach (ModGuideCard card in page.Cards)
        {
            column.AddChild(CreateSectionCard(card));
        }

        switch (page.Key)
        {
            case ModGuidePages.OverviewKey:
                column.AddChild(BuildOverviewActions());
                break;
            case ModGuidePages.GdbKey:
                column.AddChild(BuildGdbJumpRow());
                break;
            case ModGuidePages.ModelsKey:
                column.AddChild(BuildSocketTable());
                break;
            case ModGuidePages.EventsKey:
                AddEventsCatalog(column);
                break;
        }

        foreach (ModGuideCard card in page.Closing)
        {
            column.AddChild(CreateSectionCard(card));
        }

        return column;
    }

    private Control BuildOverviewActions()
    {
        var actionRow = new HFlowContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        actionRow.AddThemeConstantOverride("h_separation", 8);
        actionRow.AddThemeConstantOverride("v_separation", 4);

        var guideBtn = new Button
        {
            Text = "📘 Read the Mod API Guide →",
            TooltipText = "Every event, call and file a mod can use: " + ModGuide.ProjectFolder + "/ in the SDK"
        };
        guideBtn.Pressed += () => OpenModApiGuide();
        actionRow.AddChild(guideBtn);

        var goGdbBtn = new Button
        {
            Text = "🗂️ Open GDB Hierarchy & Components Tab"
        };
        goGdbBtn.Pressed += () => ModdingHubPanel.Instance?.SwitchTab(2);
        actionRow.AddChild(goGdbBtn);

        var goModelsGuideBtn = new Button
        {
            Text = "🎨 Read the 3D Models, Textures & .gmd Sockets Guide →"
        };
        goModelsGuideBtn.Pressed += () => SwitchSubTab(ModGuidePages.IndexOf(ModGuidePages.ModelsKey));
        actionRow.AddChild(goModelsGuideBtn);
        return actionRow;
    }

    private static Control BuildGdbJumpRow()
    {
        var jumpRow = new HFlowContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        jumpRow.AddThemeConstantOverride("h_separation", 6);
        jumpRow.AddThemeConstantOverride("v_separation", 4);

        var jumpLbl = new Label
        {
            Text = "Quick Jump to GDB Domain:",
            Modulate = new Color(0.75f, 0.82f, 0.92f)
        };
        jumpLbl.AddThemeFontSizeOverride("font_size", 13);
        jumpRow.AddChild(jumpLbl);

        foreach (string domain in new[] { "Weapons", "Clothing", "Potions", "Furniture & Props", "Creatures & NPCs" })
        {
            string captured = domain;
            var btn = new Button { Text = $"Open {captured}" };
            btn.AddThemeFontSizeOverride("font_size", 13);
            btn.Pressed += () => ModdingHubPanel.Instance?.OpenGdbDomain(captured);
            jumpRow.AddChild(btn);
        }

        return jumpRow;
    }

    /// <summary>The standard .mdl.gmd socket names, each with a copy button.</summary>
    private Control BuildSocketTable()
    {
        var tableCard = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var cardStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.10f, 0.14f),
            BorderColor = new Color(0.24f, 0.34f, 0.48f),
            BorderWidthLeft = 3,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
        tableCard.AddThemeStyleboxOverride("panel", cardStyle);

        var tableVBox = new VBoxContainer();
        tableVBox.AddThemeConstantOverride("separation", 6);

        var tableHeader = new Label
        {
            Text = "📋 Standard Fable II .mdl.gmd Socket Names (Click Copy to paste into Blender):",
            Modulate = new Color(0.95f, 0.82f, 0.42f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        tableHeader.AddThemeFontSizeOverride("font_size", 15);
        tableVBox.AddChild(tableHeader);

        foreach (var sock in Engine_GmdWriter.StandardSockets)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);

            var catLbl = new Label
            {
                Text = $"[{sock.Category}]",
                CustomMinimumSize = new Vector2(115, 0),
                Modulate = new Color(0.55f, 0.80f, 0.98f)
            };
            catLbl.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(catLbl);

            var nameBox = new LineEdit
            {
                Text = sock.Name,
                Editable = false,
                CustomMinimumSize = new Vector2(235, 0)
            };
            nameBox.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(nameBox);

            var descLbl = new Label
            {
                Text = sock.Description,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(0.75f, 0.80f, 0.88f)
            };
            descLbl.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(descLbl);

            string capturedName = sock.Name;
            var copyBtn = new Button
            {
                Text = "📋 Copy",
                CustomMinimumSize = new Vector2(74, 24)
            };
            copyBtn.AddThemeFontSizeOverride("font_size", 12);
            copyBtn.Pressed += () =>
            {
                DisplayServer.ClipboardSet(capturedName);
                ShowToast($"Copied '{capturedName}' to clipboard!");
            };
            row.AddChild(copyBtn);

            tableVBox.AddChild(row);
        }

        tableCard.AddChild(tableVBox);
        return tableCard;
    }

    // =========================================================================
    // THE EVENTS PAGE — generated from the catalog (Engine_ModApi)
    // =========================================================================
    private void AddEventsCatalog(VBoxContainer page)
    {
        foreach (ModApiEvent e in ModApi.Events)
        {
            page.AddChild(BuildEventCard(e));
        }

        page.AddChild(BuildHelpersCard(
            "🧰 Native helpers", new Color(0.60f, 0.90f, 0.60f),
            "What a handler (or a hook) calls to read and change the game. All in core/refii_mod.h; C linkage, usable from any mod.",
            ModApi.Helpers, EventsEntryKind.NativeHelper));

        page.AddChild(BuildHelpersCard(
            "📜 Lua helpers", new Color(0.85f, 0.65f, 1.0f),
            "Composed ahead of every mod's init.lua. Entities are HANDLES in Lua (small integers for an object this session; the game's Lua number is a 32-bit float): compare them, key tables by them, pass them to Refii.* calls. Hashes (definitions, text) are \"0x…\" strings. Assigning a writable field in a handler (e.damage, e.delta, e.amount, e.level, e.type, e.relation) changes what the game gets.",
            ModApi.LuaHelpers, EventsEntryKind.LuaHelper));

        page.AddChild(BuildHelpersCard(
            "🎮 Godot script helpers", new Color(0.55f, 0.80f, 1.0f),
            "Engine.get_singleton(\"RefiiHost\") in a mod's godot/*.gd: one signal per event (its Lua name) and the calls below. Entities are HANDLES, as in Lua. A query answers on a later frame on reply(id, result); match it by id. hero(1) is the host's hero on both machines — this machine's hero is local_hero().",
            ModApi.GodotHelpers, EventsEntryKind.GodotHelper));

        ApplyEventsFilter();
    }

    private enum EventsEntryKind { Event, NativeHelper, LuaHelper, GodotHelper }

    /// <summary>One filterable node of the events page: a card or a helper row, with the text the search matches.</summary>
    private sealed record EventsEntry(Control Node, EventsEntryKind Kind, string Haystack, bool Cancellable, bool Writable, bool Lua);

    private readonly List<EventsEntry> _eventsEntries = new();
    private LineEdit? _eventsSearch;
    private CheckBox? _eventsCancellableChip;
    private CheckBox? _eventsWritableChip;
    private CheckBox? _eventsLuaChip;
    private Label? _eventsCountLabel;
    private Control? _nativeHelpersCard;
    private Control? _luaHelpersCard;
    private Control? _godotHelpersCard;

    /// <summary>The search box, the filter chips, the count line and the guide button; the rows wrap at narrow widths.</summary>
    private Control BuildEventsToolbar()
    {
        var bar = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        bar.AddThemeConstantOverride("separation", 6);

        var searchRow = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        searchRow.AddThemeConstantOverride("separation", 8);

        _eventsSearch = new LineEdit
        {
            PlaceholderText = "Search events, fields, helpers…",
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(160, 0)
        };
        _eventsSearch.AddThemeFontSizeOverride("font_size", 13);
        _eventsSearch.TextChanged += _ => ApplyEventsFilter();
        searchRow.AddChild(_eventsSearch);

        var guideBtn = new Button
        {
            Text = "📘 Mod API Guide",
            TooltipText = "The events reference in the guide (" + ModGuide.ProjectFolder + "/reference-events.md)"
        };
        guideBtn.AddThemeFontSizeOverride("font_size", 13);
        guideBtn.Pressed += () => OpenModApiGuide("reference-events.md");
        searchRow.AddChild(guideBtn);

        bar.AddChild(searchRow);

        var chipRow = new HFlowContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        chipRow.AddThemeConstantOverride("h_separation", 12);
        chipRow.AddThemeConstantOverride("v_separation", 4);

        _eventsCancellableChip = CreateFilterChip(chipRow, "Cancellable", "Events a handler can refuse");
        _eventsWritableChip = CreateFilterChip(chipRow, "Has writable fields", "Events with a field a native handler can change");
        _eventsLuaChip = CreateFilterChip(chipRow, "Lua", "Events whose table carries at least one non-pointer field");

        _eventsCountLabel = new Label
        {
            Modulate = new Color(0.75f, 0.80f, 0.88f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Right,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _eventsCountLabel.AddThemeFontSizeOverride("font_size", 13);
        chipRow.AddChild(_eventsCountLabel);

        bar.AddChild(chipRow);
        return bar;
    }

    private CheckBox CreateFilterChip(Container row, string text, string tooltip)
    {
        var chip = new CheckBox
        {
            Text = text,
            TooltipText = tooltip
        };
        chip.AddThemeFontSizeOverride("font_size", 13);
        chip.Toggled += _ => ApplyEventsFilter();
        row.AddChild(chip);
        return chip;
    }

    /// <summary>One event's card, with its template copy buttons, registered for the filter.</summary>
    private Control BuildEventCard(ModApiEvent e)
    {
        Control card = CreateSectionCard(ModGuidePages.EventCard(e));

        var buttons = new HFlowContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        buttons.AddThemeConstantOverride("h_separation", 8);
        buttons.AddChild(CreateCopyButton("📋 Copy native", $"Copied the {e.Name} native handler", () => ModApi.NativeTemplate(e, "my_mod")));
        buttons.AddChild(CreateCopyButton("📋 Copy Lua", $"Copied the \"{e.LuaName}\" Lua handler", () => ModApi.LuaTemplate(e)));
        CardBody(card).AddChild(buttons);

        var haystack = new System.Text.StringBuilder();
        haystack.Append(e.Name).Append('\n').Append(e.LuaName).Append('\n').Append(e.Summary).Append('\n')
            .Append(e.Seam).Append('\n').Append(e.Cancel).Append('\n').Append(e.Relayed).Append('\n');
        foreach (ModApiField f in e.Fields)
        {
            haystack.Append(f.Name).Append('\n').Append(f.Type).Append('\n').Append(f.Meaning).Append('\n');
        }

        _eventsEntries.Add(new EventsEntry(
            card, EventsEntryKind.Event, haystack.ToString().ToLowerInvariant(),
            e.Cancellable,
            e.Fields.Any(f => f.Writable),
            e.Fields.Any(f => !f.Type.Contains('*'))));
        return card;
    }

    /// <summary>A helpers card: one row per helper (each registered for the filter) and a copy of the signature list.</summary>
    private Control BuildHelpersCard(string title, Color accent, string intro, IReadOnlyList<ModApiHelper> helpers, EventsEntryKind kind)
    {
        Control card = CreateSectionCard(title, accent, intro);
        VBoxContainer vbox = CardBody(card);

        foreach (ModApiHelper h in helpers)
        {
            var row = new Label
            {
                Text = $"  {h.Signature}\n      {h.Meaning}",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(0.84f, 0.88f, 0.94f)
            };
            row.AddThemeFontSizeOverride("font_size", 13);
            vbox.AddChild(row);
            _eventsEntries.Add(new EventsEntry(
                row, kind, (h.Signature + "\n" + h.Meaning).ToLowerInvariant(),
                Cancellable: false, Writable: false, Lua: kind == EventsEntryKind.LuaHelper));
        }

        var buttons = new HFlowContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        buttons.AddChild(CreateCopyButton("📋 Copy", $"Copied the {title.Substring(title.IndexOf(' ') + 1).ToLowerInvariant()} signatures",
            () => string.Join("\n", helpers.Select(h => h.Signature)) + "\n"));
        vbox.AddChild(buttons);

        switch (kind)
        {
            case EventsEntryKind.LuaHelper:
                _luaHelpersCard = card;
                break;
            case EventsEntryKind.GodotHelper:
                _godotHelpersCard = card;
                break;
            default:
                _nativeHelpersCard = card;
                break;
        }

        return card;
    }

    private Button CreateCopyButton(string text, string flash, Func<string> content)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 24)
        };
        btn.AddThemeFontSizeOverride("font_size", 12);
        btn.Pressed += () =>
        {
            DisplayServer.ClipboardSet(content());
            FlashToast(flash);
        };
        return btn;
    }

    /// <summary>The column CreateSectionCard lays its title and body in; extra rows go after the body.</summary>
    private static VBoxContainer CardBody(Control card) => card.GetChild<VBoxContainer>(0);

    /// <summary>
    /// Hides every entry the search or the chips exclude. The search is a case-insensitive substring
    /// match of each whitespace-separated word over an event's name, Lua name, summary, seam, cancel text,
    /// field names, types and meanings, and over a helper's signature and meaning. The chips apply to
    /// events only; a helpers card hides when the search leaves it no row.
    /// </summary>
    private void ApplyEventsFilter()
    {
        string[] words = (_eventsSearch?.Text ?? string.Empty)
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool cancellable = _eventsCancellableChip?.ButtonPressed ?? false;
        bool writable = _eventsWritableChip?.ButtonPressed ?? false;
        bool lua = _eventsLuaChip?.ButtonPressed ?? false;

        int events = 0, eventsShown = 0, native = 0, nativeShown = 0, luaHelpers = 0, luaShown = 0, godot = 0, godotShown = 0;
        foreach (EventsEntry entry in _eventsEntries)
        {
            bool show = words.All(w => entry.Haystack.Contains(w, StringComparison.Ordinal));
            if (entry.Kind == EventsEntryKind.Event)
            {
                show &= (!cancellable || entry.Cancellable) && (!writable || entry.Writable) && (!lua || entry.Lua);
            }

            entry.Node.Visible = show;
            switch (entry.Kind)
            {
                case EventsEntryKind.Event: events++; if (show) eventsShown++; break;
                case EventsEntryKind.NativeHelper: native++; if (show) nativeShown++; break;
                case EventsEntryKind.GodotHelper: godot++; if (show) godotShown++; break;
                default: luaHelpers++; if (show) luaShown++; break;
            }
        }

        if (_nativeHelpersCard is not null) _nativeHelpersCard.Visible = nativeShown != 0;
        if (_luaHelpersCard is not null) _luaHelpersCard.Visible = luaShown != 0;
        if (_godotHelpersCard is not null) _godotHelpersCard.Visible = godotShown != 0;
        if (_eventsCountLabel is not null)
        {
            _eventsCountLabel.Text = $"{eventsShown} of {events} events  ·  {nativeShown} of {native} native helpers  ·  {luaShown} of {luaHelpers} Lua helpers  ·  {godotShown} of {godot} Godot helpers";
        }
    }

    /// <summary>Shows a toast that clears itself after a moment, unless another has replaced it.</summary>
    private void FlashToast(string text)
    {
        ShowToast(text);
        if (!IsInsideTree())
        {
            return;
        }

        GetTree().CreateTimer(1.8).Timeout += () =>
        {
            if (_statusToastLabel is not null && _statusToastLabel.Text == text)
            {
                _statusToastLabel.Text = string.Empty;
            }
        };
    }

    // =========================================================================
    // THE MOD API GUIDE — the shipped markdown under res://docs/modding/
    // =========================================================================
    private bool _guideLoaded;
    private string _guideFolder = string.Empty;
    private readonly Dictionary<string, ModGuideDocument> _guideDocs = new(StringComparer.Ordinal);
    private string _guideFile = string.Empty;
    private ModGuideRendered? _guideRendered;
    private readonly Stack<string> _guideHistory = new();
    private Tree? _guideToc;
    private RichTextLabel? _guideText;
    private Label? _guideStatus;
    private Button? _guideBack;
    private bool _guideTocQuiet;

    private Control BuildGuidePage()
    {
        var split = new HSplitContainer();

        var side = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(230, 0),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        side.AddThemeConstantOverride("separation", 6);

        var tools = new HFlowContainer();
        tools.AddThemeConstantOverride("h_separation", 6);
        tools.AddThemeConstantOverride("v_separation", 4);

        _guideBack = new Button { Text = "◀ Back", Disabled = true, TooltipText = "The page you came from" };
        _guideBack.Pressed += OnGuideBack;
        tools.AddChild(_guideBack);

        var reload = new Button { Text = "↻ Reload", TooltipText = "Read the guide's files again" };
        reload.Pressed += () =>
        {
            LoadGuide();
            FlashToast("Reloaded the Mod API Guide.");
        };
        tools.AddChild(reload);

        var folder = new Button { Text = "📂 Folder", TooltipText = "Open the folder the guide is read from" };
        folder.Pressed += () =>
        {
            if (Directory.Exists(_guideFolder))
            {
                OS.ShellOpen(_guideFolder);
            }
        };
        tools.AddChild(folder);
        side.AddChild(tools);

        _guideToc = new Tree
        {
            HideRoot = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _guideToc.ItemSelected += OnGuideTocSelected;
        side.AddChild(_guideToc);
        split.AddChild(side);

        var main = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        main.AddThemeConstantOverride("separation", 4);

        _guideStatus = new Label
        {
            Modulate = new Color(0.62f, 0.68f, 0.78f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _guideStatus.AddThemeFontSizeOverride("font_size", 12);
        main.AddChild(_guideStatus);

        // The label scrolls itself, so a long reference never depends on an outer container's size.
        _guideText = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            SelectionEnabled = true,
            ContextMenuEnabled = true,
            FitContent = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _guideText.AddThemeFontSizeOverride("normal_font_size", 14);
        _guideText.AddThemeFontSizeOverride("bold_font_size", 14);
        _guideText.AddThemeFontSizeOverride("mono_font_size", 13);
        _guideText.MetaClicked += meta => OnGuideMeta(meta.AsString());
        main.AddChild(_guideText);
        split.AddChild(main);

        return split;
    }

    /// <summary>Reads every guide file again and rebuilds the contents tree; keeps the open file when it is still there.</summary>
    private void LoadGuide()
    {
        _guideLoaded = true;
        _guideDocs.Clear();
        _guideRendered = null;
        _guideFolder = ProjectSettings.GlobalizePath(ModGuide.ResFolder);
        IReadOnlyList<string> files;
        try
        {
            files = ModGuide.Files(_guideFolder);
            foreach (string file in files)
            {
                _guideDocs[file] = ModGuide.Load(_guideFolder, file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = Array.Empty<string>();
            _guideDocs.Clear();
            GD.PushWarning($"[mod guide] reading {_guideFolder}: {ex.Message}");
        }

        BuildGuideToc(files);
        if (_guideDocs.Count == 0)
        {
            if (_guideText is not null)
            {
                _guideText.Text = "[b]The Mod API Guide's files are not here:[/b]\n" + ModGuide.Escape(_guideFolder) +
                                  "\n\nThe guide ships in the SDK as " + ModGuide.ProjectFolder + "/.";
            }

            SetGuideStatus(_guideFolder + " — no guide files found.", string.Empty);
            return;
        }

        IReadOnlyList<string> problems = ModGuide.Problems(_guideFolder);
        SetGuideStatus(
            problems.Count == 0
                ? $"{files.Count} files · {ModGuide.ProjectFolder}/"
                : $"{files.Count} files · {ModGuide.ProjectFolder}/ · {problems.Count} broken link(s) — hover for the list",
            string.Join("\n", problems));
        string open = _guideDocs.ContainsKey(_guideFile) ? _guideFile : ModGuide.IndexFile;
        OpenGuideFile(_guideDocs.ContainsKey(open) ? open : files[0], string.Empty, remember: false);
    }

    private void SetGuideStatus(string text, string tooltip)
    {
        if (_guideStatus is not null)
        {
            _guideStatus.Text = text;
            _guideStatus.TooltipText = tooltip;
        }
    }

    /// <summary>One item per file (its title), its level-2 headings under it and their level-3 headings under those.</summary>
    private void BuildGuideToc(IReadOnlyList<string> files)
    {
        if (_guideToc is null)
        {
            return;
        }

        _guideToc.Clear();
        TreeItem root = _guideToc.CreateItem();
        foreach (string file in files)
        {
            if (!_guideDocs.TryGetValue(file, out ModGuideDocument? doc))
            {
                continue;
            }

            TreeItem fileItem = _guideToc.CreateItem(root);
            fileItem.SetText(0, doc.Title);
            fileItem.SetTooltipText(0, file);
            fileItem.SetMetadata(0, ModGuide.LinkMeta(file, string.Empty));
            fileItem.Collapsed = true;
            TreeItem? section = null;
            foreach (ModGuideHeading heading in doc.Headings)
            {
                if (heading.Level == 2)
                {
                    section = _guideToc.CreateItem(fileItem);
                    section.SetText(0, heading.Text);
                    section.SetMetadata(0, ModGuide.LinkMeta(file, heading.Anchor));
                    section.Collapsed = true;
                }
                else if (heading.Level == 3 && section is not null)
                {
                    TreeItem sub = _guideToc.CreateItem(section);
                    sub.SetText(0, heading.Text);
                    sub.SetMetadata(0, ModGuide.LinkMeta(file, heading.Anchor));
                }
            }
        }
    }

    private void OnGuideTocSelected()
    {
        if (_guideTocQuiet || _guideToc?.GetSelected() is not TreeItem item)
        {
            return;
        }

        if (ModGuide.TryParseLinkMeta(item.GetMetadata(0).AsString(), out string file, out string anchor))
        {
            OpenGuideFile(file, anchor, remember: true);
        }
    }

    private void OnGuideMeta(string meta)
    {
        if (ModGuide.TryParseLinkMeta(meta, out string file, out string anchor))
        {
            OpenGuideFile(file, anchor, remember: true);
        }
        else if (meta.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || meta.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            OS.ShellOpen(meta);
        }
    }

    private void OnGuideBack()
    {
        if (_guideHistory.Count != 0 && ModGuide.TryParseLinkMeta(_guideHistory.Pop(), out string file, out string anchor))
        {
            OpenGuideFile(file, anchor, remember: false);
        }

        if (_guideBack is not null)
        {
            _guideBack.Disabled = _guideHistory.Count == 0;
        }
    }

    /// <summary>Shows <paramref name="file"/> and scrolls to <paramref name="anchor"/> (the top when empty).</summary>
    private void OpenGuideFile(string file, string anchor, bool remember)
    {
        if (!_guideLoaded)
        {
            LoadGuide();
        }

        if (!_guideDocs.TryGetValue(file, out ModGuideDocument? doc) || _guideText is null)
        {
            FlashToast($"The guide has no {file}.");
            return;
        }

        if (remember && _guideFile.Length != 0)
        {
            _guideHistory.Push(ModGuide.LinkMeta(_guideFile, string.Empty));
            if (_guideBack is not null)
            {
                _guideBack.Disabled = false;
            }
        }

        if (file != _guideFile || _guideRendered is null)
        {
            _guideRendered = ModGuide.ToBbCode(doc);
            _guideText.Text = _guideRendered.BbCode;
            _guideFile = file;
        }

        SelectGuideTocItem(ModGuide.LinkMeta(file, anchor));
        int paragraph = anchor.Length != 0 && _guideRendered.AnchorParagraphs.TryGetValue(anchor, out int p) ? p : 0;
        ScrollGuideTo(paragraph);
    }

    /// <summary>Scrolls once the label has laid the text out (the frame after it was set).</summary>
    private async void ScrollGuideTo(int paragraph)
    {
        if (!IsInsideTree() || _guideText is null)
        {
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_guideText is null || !IsInstanceValid(_guideText))
        {
            return;
        }

        _guideText.GetContentHeight();
        _guideText.ScrollToParagraph(paragraph);
    }

    private void SelectGuideTocItem(string meta)
    {
        if (_guideToc?.GetRoot() is not TreeItem root)
        {
            return;
        }

        TreeItem? match = Find(root, meta);
        if (match is null)
        {
            return;
        }

        for (TreeItem? up = match.GetParent(); up is not null && up != root; up = up.GetParent())
        {
            up.Collapsed = false;
        }

        _guideTocQuiet = true;
        try
        {
            match.Select(0);
            _guideToc.ScrollToItem(match);
        }
        finally
        {
            _guideTocQuiet = false;
        }

        static TreeItem? Find(TreeItem item, string wanted)
        {
            for (TreeItem? child = item.GetFirstChild(); child is not null; child = child.GetNext())
            {
                if (child.GetMetadata(0).AsString() == wanted)
                {
                    return child;
                }

                if (Find(child, wanted) is TreeItem deeper)
                {
                    return deeper;
                }
            }

            return null;
        }
    }

    // =========================================================================
    // CARDS
    // =========================================================================
    private static Control CreateSectionCard(ModGuideCard card) =>
        CreateSectionCard(card.Title, new Color(card.R, card.G, card.B), card.Body);

    private static Control CreateSectionCard(string title, Color accentColor, string bodyText)
    {
        var card = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.10f, 0.14f),
            BorderColor = accentColor,
            BorderWidthLeft = 3,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
        card.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        vbox.AddThemeConstantOverride("separation", 6);

        var titleLbl = new Label
        {
            Text = title,
            Modulate = accentColor,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        titleLbl.AddThemeFontSizeOverride("font_size", 15);
        vbox.AddChild(titleLbl);

        var bodyLbl = new Label
        {
            Text = bodyText,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.84f, 0.88f, 0.94f)
        };
        bodyLbl.AddThemeFontSizeOverride("font_size", 13);
        vbox.AddChild(bodyLbl);

        card.AddChild(vbox);
        return card;
    }
}
#endif
