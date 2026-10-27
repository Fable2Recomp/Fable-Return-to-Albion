using Godot;
using Refii.Ui;

namespace Refii.Game;

public partial class GameUi
{
    private Engine_UiMenuBox? _goldenCockView;
    private Engine_UiGuiBoxes.MenuBoxModel? _goldenCockPage;
    private int _goldenCockHighlight = -1;

    /// <summary>
    /// The host's menu box — the Golden Cock's flow, else a mod's <c>menu_box</c>
    /// (<see cref="Engine_UiModMenuBox"/>) — drawn in the game's menu box art, on the front end and
    /// in game, taking the pad while it stands. One view serves both: the same dialog drive
    /// (<see cref="Engine_UiMenuBoxDialog"/>) and the same art.
    /// </summary>
    private bool DriveGoldenCockMenu(double delta)
    {
        Engine_UiMenuBoxDialog? dialog = GameGoldenCock.ActiveMenu ?? (Engine_UiMenuBoxDialog?)Engine_UiModMenuBox.Active;
        if (dialog is null)
        {
            _goldenCockView?.Hide();
            _goldenCockPage = null;
            return false;
        }
        if (_presentation is null || !IsRevealed)
        {
            _goldenCockView?.Hide();
            _goldenCockPage = null;

            // A mod's box waits for the screen to come back; the Golden Cock's flow ends.
            if (dialog is Engine_UiModMenuBox)
            {
                return false;
            }

            CancelHostMenuBox(dialog);
            return true;
        }
        if (_goldenCockView is null)
        {
            var book = _presentation.PatternBookFor(Engine_UiMenuBox.Presentation);
            var view = Engine_UiMenuBox.FromPatternBook(book, "GoldenCockMenu");
            if (!view.HasShippedArt)
            {
                GD.PushWarning("[golden-cock] Fable 2 menu art is unavailable: " + view.LastReport);
                view.Free();
                CancelHostMenuBox(dialog);
                return true;
            }
            _goldenCockView = view;
            view.Position = new Vector3(0, 0, Engine_UiSurface<Engine_UiMenuBox>.ModalDepth);
            _presentation.AddChild(view);
            if (book is not null && _presentation.DocumentFor(Engine_UiMenuBox.Presentation) is { } document)
            {
                var binding = new Engine_UiCloneBinding();
                binding.Bind(view, document);
                new Engine_UiFrameCloning(book).Dress(document, binding);
            }
        }
        // The whole pad is the box's while it stands: this drive returns before the per-frame
        // flag resets, so the flag holds until the box is gone and the resets run again.
        GameInput.HostUiOwnsPad = true;
        bool clicked = Engine_UiPointer.TakeLeft();
        int pointed = (clicked || Engine_UiPointer.Moved) && Engine_UiPointer.Canvas is { } at
            ? _goldenCockView.OptionAt(at) : -1;
        int wheel = Engine_UiPointer.TakeWheel();
        dialog.Drive(delta, wheel != 0 ? System.Math.Sign(wheel) : GameInput.MenuDirection,
            GameInput.MenuAcceptPressed, GameInput.MenuBackPressed || clicked && pointed == -2, pointed, clicked);
        if (!dialog.IsOpen || dialog.Menu is not { } page)
        {
            _goldenCockView.Hide();
            _goldenCockPage = null;
            return true;
        }
        if (!ReferenceEquals(_goldenCockPage, page) || _goldenCockHighlight != page.Highlight)
        {
            var labels = new string?[Engine_UiMenuBox.MaxOptions];
            uint enabled = 0;
            for (int i = 0; i < page.Options.Count; i++)
            {
                labels[i] = page.Options[i].Label;
                if (page.Options[i].Enabled) enabled |= 1u << i;
            }
            _goldenCockView.ShowStated(page.Text, labels, page.Options.Count, page.Highlight, enabled, page.Cancelable);
            _goldenCockPage = page;
            _goldenCockHighlight = page.Highlight;
        }
        return true;
    }

    /// <summary>Takes a host menu box down unanswered: the Golden Cock's closes its flow, a mod's answers -1.</summary>
    private static void CancelHostMenuBox(Engine_UiMenuBoxDialog dialog)
    {
        if (dialog is Engine_UiModMenuBox mod)
        {
            mod.Cancel();
        }
        else
        {
            GameGoldenCock.CancelMenu();
        }
    }
}
