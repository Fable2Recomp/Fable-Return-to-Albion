using Godot;
using Refii.Core;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

public partial class GameUi
{
    private uint _quickslotHudGeneration;
    private Engine_UiQuickSlotHud? _quickslotHud;
    private Engine_UiScreen? _quickslotHudScreen;
    private bool QuickSlotHudVisible => GameQuickSlots.Enabled && GameInput.Playing && !GameInput.InOverlay && !_fullCutscene;

    private void KeepQuickSlotHudVisible(Engine_UiScreen screen, int[] slides, bool[] active, float?[] opacity)
    {
        if (!QuickSlotHudVisible || _hud?.Bindings is not { } bindings) return;
        if (_quickslotHud is null || !ReferenceEquals(_quickslotHudScreen, screen))
        {
            _quickslotHudScreen = screen;
            _quickslotHud = new Engine_UiQuickSlotHud(screen, bindings);
        }
        _quickslotHud.Apply(slides, active, opacity);
    }
    private static readonly int[] QuickSlotDirections = { 0, 2, 1, 3 };
    private readonly System.Collections.Generic.Dictionary<string, uint> _quickslotIcons = new(System.StringComparer.OrdinalIgnoreCase);
    private Engine_UiQuickSlotPrompt? _quickslotAssignHint;
    private long _quickslotExpressionRead;
    private Engine_QuickSlots.Binding _quickslotExpression;
    private int _quickslotExpressionPlayer;
    private Engine_UiExpandableMenu.Item? _quickslotBookRow;
    private bool _quickslotBookEligible;
    private long _quickslotBookRead;
    private Engine_UiOwnedBook? _quickslotPromptBook;
    private readonly Node3D?[] _quickslotPromptAnchors = new Node3D?[2];
    private readonly Node3D?[] _quickslotNativePrompts = new Node3D?[2];
    private readonly bool[] _quickslotNativeVisible = new bool[2];
    private CanvasLayer? _quickslotPickerLayer;
    private SubViewport? _quickslotPickerViewport;
    private SubViewportContainer? _quickslotPickerContainer;
    private ColorRect? _quickslotPickerBackdrop;
    private Camera3D? _quickslotPickerCamera;
    private Engine_UiQuickSlotPicker? _quickslotPicker;
    private Vector2 _quickslotPickerSize;
    private bool _quickslotUiEnabled;

    private void ResetQuickSlotPrompt()
    {
        if (_quickslotUiEnabled != GameQuickSlots.Enabled)
        {
            _quickslotUiEnabled = GameQuickSlots.Enabled;
            if (!_quickslotUiEnabled && Engine_UiHostOptionsPage.Current is Engine_UiQuickSlotOptions)
                Engine_UiOptionsPending.Leave();
            if (_screenMenu?.Resource() == true) _screenMenuView?.Refresh();
        }
        _quickslotAssignHint?.Hide();
        for (int i = 0; i < 2; i++)
            if (_quickslotNativeVisible[i] && _quickslotNativePrompts[i] is { } prompt && IsInstanceValid(prompt))
            {
                prompt.Show();
                _quickslotNativeVisible[i] = false;
            }
    }

    private bool ShowQuickSlotPrompt(Engine_QuickSlots.Binding binding, int player, uint screen = 0)
    {
        if (_presentation?.Theme is not { } theme) return false;
        if (_quickslotAssignHint is null || !IsInstanceValid(_quickslotAssignHint))
        {
            _quickslotAssignHint = new Engine_UiQuickSlotPrompt { Name = "AssignQuickslot" };
            _presentation.AddChild(_quickslotAssignHint);
        }
        bool spell = screen == 14;
        var book = screen == 0 ? null : _books.Book(screen);
        if (!ReferenceEquals(book, _quickslotPromptBook))
        {
            _quickslotPromptBook = book;
            for (int i = 0; i < 2; i++)
            {
                _quickslotPromptAnchors[i] = book?.NodeFor($"Foreground.Book.Pages.Page{i + 1}.Contents." +
                    (spell ? "Buttons.Buttons.Learn" : "PageButtons.A"));
                _quickslotNativePrompts[i] = spell ? null : book?.NodeFor($"Foreground.Book.Pages.Page{i + 1}.Contents.PageButtons.Y");
            }
        }
        for (int i = 0; i < 2; i++)
            if (_quickslotNativePrompts[i] is { } native && IsInstanceValid(native) && native.Visible)
            {
                _quickslotNativeVisible[i] = true;
                native.Hide();
            }
        Node3D? anchor = null;
        foreach (var candidate in _quickslotPromptAnchors)
            if (candidate is not null && IsInstanceValid(candidate) && candidate.IsVisibleInTree()) { anchor = candidate; break; }
        _quickslotAssignHint.SetState(theme, GameQuickSlots.MatchingSlots(binding, player), spell, screen != 0);
        var frame = Engine_UiSeats.Of(GetViewport().GetVisibleRect().Size, Engine_UiSeats.For(player, Engine_Views.Split));
        if (anchor is not null)
        {
            _quickslotAssignHint.PlaceAt(anchor, spell);
        }
        else
        {
            Vector2 at = frame.ToCanvas(new Vector2(frame.Extent.X * 0.36f, -frame.Extent.Y * 0.5f + 110));
            _quickslotAssignHint.Position = new Vector3(at.X, at.Y, 8500);
            _quickslotAssignHint.Scale = new Vector3(frame.Scale, frame.Scale, 1);
        }
        return _quickslotAssignHint.Clicked();
    }

    private bool DriveQuickSlotBookAssignment(uint screen)
    {
        if (!GameQuickSlots.Enabled) return false;
        var row = _screenMenu?.Script.Highlighted;
        var binding = Engine_UiQuickSlotAssignment.FromRow(screen, row);
        if (!binding.Assigned || GameQuickSlots.Assigning) return false;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!ReferenceEquals(row, _quickslotBookRow) || now >= _quickslotBookRead)
        {
            _quickslotBookRow = row;
            _quickslotBookRead = now + System.Diagnostics.Stopwatch.Frequency;
            _quickslotBookEligible = GameQuickSlots.CanAssign(binding);
        }
        if (!_quickslotBookEligible) return false;
        bool spell = screen == 14;
        if (spell) GameInput.ScreenMenuHoldsEquip = true;
        else GameInput.ScreenMenuHoldsFlip = true;
        bool clicked = ShowQuickSlotPrompt(binding, GameQuickSlots.MenuPlayer, screen);
        if (!clicked && !(spell ? GameInput.MenuEquipPressed : GameInput.MenuFlipPressed)) return false;
        if (!GameQuickSlots.OpenAssignment(binding, GameQuickSlots.MenuPlayer)) return false;
        _quickslotAssignHint?.Hide();
        HoldScreenStick(0);
        return true;
    }

    private bool DriveQuickSlotPicker(double delta)
    {
        if (!GameQuickSlots.Assigning || _presentation is null || !IsRevealed || _fullCutscene)
        {
            if (_quickslotPickerLayer is not null)
            {
                _quickslotPickerLayer.QueueFree();
                _quickslotPickerLayer = null;
                _quickslotPicker = null;
            }
            if (GameQuickSlots.Assigning) GameQuickSlots.Assignment!.Cancel();
            return false;
        }
        var assignment = GameQuickSlots.Assignment!;
        if (_quickslotPickerLayer is null)
        {
            var theme = _presentation.PatternBookFor(HudPresentation);
            var screen = _presentation.DocumentFor(HudPresentation);
            if (theme is null || screen is null) { assignment.Cancel(); return true; }
            _quickslotPickerLayer = new CanvasLayer { Name = "QuickslotPicker", Layer = 6 };
            AddChild(_quickslotPickerLayer);
            _quickslotPickerBackdrop = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore,
                Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Engine/ui/ui_quickslot_backdrop.gdshader") } };
            _quickslotPickerLayer.AddChild(_quickslotPickerBackdrop);
            _quickslotPickerContainer = new SubViewportContainer { Stretch = true, MouseFilter = Control.MouseFilterEnum.Ignore };
            _quickslotPickerLayer.AddChild(_quickslotPickerContainer);
            _quickslotPickerViewport = new SubViewport { OwnWorld3D = true, TransparentBg = true,
                GuiDisableInput = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            _quickslotPickerContainer.AddChild(_quickslotPickerViewport);
            _quickslotPickerCamera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal,
                Position = new Vector3(0, 0, 20000), Far = 40000, Current = true };
            _quickslotPickerViewport.AddChild(_quickslotPickerCamera);
            _quickslotPicker = new Engine_UiQuickSlotPicker();
            _quickslotPickerViewport.AddChild(_quickslotPicker);
            _quickslotPicker.Build(theme, screen, hash => Engine_UiHostTextures.TryGet(hash) ??
                (_presentation.TextureNames is { } names && names.TryResolve(hash, out string path) ? _presentation.LoadTexture(path) : null),
                QuickSlotTexture, _hudTextures);
            _quickslotPickerSize = Vector2.Zero;
        }
        Vector2 window = GetViewport().GetVisibleRect().Size;
        var seat = Engine_UiSeats.Of(window, Engine_UiSeats.For(assignment.Player, Engine_Views.Split));
        Vector2 size = seat.Window;
        Vector2 origin = seat.IsWhole ? Vector2.Zero : seat.Origin;
        if (_quickslotPickerSize != size || _quickslotPickerContainer!.Position != origin)
        {
            _quickslotPickerSize = size;
            _quickslotPickerContainer!.Position = _quickslotPickerBackdrop!.Position = origin;
            _quickslotPickerContainer.Size = _quickslotPickerBackdrop.Size = size;
            bool narrow = size.X / System.Math.Max(size.Y, 1) < 1100f / 720;
            _quickslotPickerCamera!.KeepAspect = narrow ? Camera3D.KeepAspectEnum.Width : Camera3D.KeepAspectEnum.Height;
            _quickslotPickerCamera.Size = narrow ? 1100 : 720;
        }
        int clicked = -1;
        if (Engine_UiPointer.TakeLeft())
        {
            Vector2 mouse = GetViewport().GetMousePosition() - origin;
            if (new Rect2(Vector2.Zero, size).HasPoint(mouse))
            {
                Vector3 at = _quickslotPickerCamera!.ProjectRayOrigin(mouse);
                clicked = _quickslotPicker!.SlotAt(new Vector2(at.X, at.Y));
            }
        }
        GameQuickSlots.DriveAssignment(delta, clicked);
        if (assignment.IsOpen) _quickslotPicker!.ShowAssignment(assignment);
        else _quickslotPickerLayer!.Hide();
        HoldScreenStick(0);
        return true;
    }

    private void DriveQuickSlotExpressionAssignment()
    {
        if (!GameQuickSlots.Enabled || _presentation?.Theme is null || _fullCutscene || GameQuickSlots.Assigning ||
            !GameInput.QuickWheelHeld && !GameInput.QuickWheelTwoHeld) return;
        int player = ExpressionWheelOwner();
        bool pressed = GameQuickSlots.AssignmentButtonPressed(player, 0x8000);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (pressed || player != _quickslotExpressionPlayer || now >= _quickslotExpressionRead)
        {
            _quickslotExpression = GameQuickSlots.SelectedExpression(player);
            _quickslotExpressionPlayer = player;
            _quickslotExpressionRead = now + System.Diagnostics.Stopwatch.Frequency / 10;
        }
        if (!_quickslotExpression.Assigned) return;
        bool clicked = ShowQuickSlotPrompt(_quickslotExpression, player);
        if (!pressed && !clicked) return;
        var selected = GameQuickSlots.SelectedExpression(player);
        if (!GameQuickSlots.OpenAssignment(selected, player)) return;
        HostBridge.PostFrontEndAction(player == 2 && CoopSession.LocalCoop ? "QuickWheel:2:close" : "QuickWheel:close");
        _quickslotAssignHint?.Hide();
        _quickslotExpressionRead = 0;
    }

    private uint QuickSlotTexture(string icon)
    {
        if (_quickslotIcons.TryGetValue(icon, out uint cached)) return cached;
        if (_presentation?.LoadTexture(icon) is not { } texture) return 0;
        using Image source = texture.GetImage();
        if (source is null || source.IsEmpty()) return 0;
        if (source.IsCompressed() && source.Decompress() != Error.Ok) return 0;
        source.Convert(Image.Format.Rgba8);
        var fit = Engine_UiQuickSlotAssignment.FitIcon(source.GetWidth(), source.GetHeight());
        if (fit.Width == 0) return 0;
        source.Resize(fit.Width, fit.Height, Image.Interpolation.Lanczos);
        using Image padded = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
        padded.Fill(Colors.Transparent);
        padded.BlitRect(source, new Rect2I(0, 0, fit.Width, fit.Height), new Vector2I(fit.X, fit.Y));
        padded.GenerateMipmaps();
        uint hash = Engine_UiHostTextures.Register(ImageTexture.CreateFromImage(padded));
        _quickslotIcons[icon] = hash;
        return hash;
    }

    private static bool QuickSlotIcon(int player, int direction, out string icon)
    {
        icon = "";
        if (!GameQuickSlots.Enabled || !GameInput.Playing || GameInput.InOverlay) return false;
        var binding = GameQuickSlots.Assigned(player, QuickSlotDirections[direction]);
        if (binding.Assigned)
        {
            icon = GameQuickSlots.Icon(binding);
            if (icon.Length == 0) icon = "Art\\GUI\\WheelIcons\\expressions.tex";
            return true;
        }
        return GameQuickSlots.Bank(player) != 0;
    }

    private void ApplyQuickSlotTextures(uint[] textures)
    {
        _quickslotHudGeneration = GameQuickSlots.Generation;
        if (!GameQuickSlots.Enabled) return;
        int[][] slots = DpadSlotOrdinals();
        for (int i = 0; i < slots.Length; i++)
        {
            bool replaced = QuickSlotIcon(GameQuickSlots.LocalPlayer, i, out string icon);
            uint texture = 0;
            if (icon.Length != 0) texture = QuickSlotTexture(icon);
            foreach (int ordinal in slots[i])
                if (ordinal >= 0 && ordinal < textures.Length)
                {
                    if (replaced) textures[ordinal] = texture;
                    if (textures[ordinal] == 0 && _hudScreen is { } screen && ordinal < screen.Nodes.Length)
                        textures[ordinal] = screen.Nodes[ordinal].Quad?.TextureHash ?? 0;
                }
        }
    }

    private void ApplyQuickSlotSlides(int[] slides)
    {
        if (!GameQuickSlots.Enabled) return;
        if (_hud?.Bindings is not { } bindings) return;
        for (int i = 0; i < DpadSlotToggles.Length; i++)
        {
            if (!QuickSlotIcon(GameQuickSlots.LocalPlayer, i, out string icon)) continue;
            foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideToggle, Engine_GameHash.Fnv1(DpadSlotToggles[i])))
                if (ordinal >= 0 && ordinal < slides.Length) slides[ordinal] = icon.Length != 0 ? 2 : 1;
        }
    }
}

