using Godot;
using Refii.AndroidRuntime;

namespace Refii.Titles.Fable2;

/// <summary>`hud_half_rate` (AndroidHudRate): the bare HUD's viewport skips every other frame.</summary>
public partial class GameUi
{
    private bool _hudSkipped;
    private bool _hudRateTouched;
    private Vector3 _hudDrawnForward;

    /// <summary>Once a frame, after the UI's own process: draws or skips the UI viewport this frame.</summary>
    private void DriveHudRate()
    {
        if (_viewport is null || !IsRevealed)
        {
            _hudSkipped = false;
            return;
        }

        if (!AndroidHudRate.Enabled)
        {
            if (_hudRateTouched)
            {
                _hudRateTouched = false;
                _hudSkipped = false;
                _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            }
            return;
        }

        _hudRateTouched = true;
        Vector3 forward = GetViewport()?.GetCamera3D() is { } camera ? -camera.GlobalBasis.Z : Vector3.Zero;
        bool bare = HasInGameUi && !_pauseTreeUp && _modal is not { ScreenId: not 0 and not 0xFFFFFFFFu } &&
            _hud is { Visible: true } && GodotObject.IsInstanceValid(_hud);
        int marks = (_worldIcons?.Lit ?? 0) + (_worldIconsTwo is { Visible: true } ? _worldIconsTwo.Lit : 0) +
            (_worldSprites?.Shown ?? 0) + (_worldSpritesTwo is { Visible: true } ? _worldSpritesTwo.Shown : 0);
        bool mods = _hud?.ModPieces is { Pieces.Count: > 0 };
        bool skip = AndroidHudRate.Skip(bare, marks, mods, forward.Dot(_hudDrawnForward), _hudSkipped);
        _hudSkipped = skip;
        if (!skip)
        {
            _hudDrawnForward = forward;
        }

        SubViewport.UpdateMode want = skip ? SubViewport.UpdateMode.Disabled : SubViewport.UpdateMode.Always;
        if (_viewport.RenderTargetUpdateMode != want)
        {
            _viewport.RenderTargetUpdateMode = want;
        }
    }
}
