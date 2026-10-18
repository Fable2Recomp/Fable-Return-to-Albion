using Godot;
using Refii.Ui;

namespace Refii.Game;

public partial class GameUi
{
    private Engine_UiMenuBox? _goldenCockView;
    private Engine_UiGuiBoxes.MenuBoxModel? _goldenCockPage;
    private int _goldenCockHighlight = -1;

    private bool DriveGoldenCockMenu(double delta)
    {
        if (GameGoldenCock.ActiveMenu is not { } dialog)
        {
            _goldenCockView?.Hide();
            _goldenCockPage = null;
            return false;
        }
        if (_presentation is null || !IsRevealed)
        {
            GameGoldenCock.CancelMenu();
            _goldenCockView?.Hide();
            _goldenCockPage = null;
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
                GameGoldenCock.CancelMenu();
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
}
