using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Save;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

public partial class GameGoldenCock : Node
{
    private static readonly HashSet<Engine_GoldenCock> Actors = new();
    private static GameGoldenCock? _current;
    private Engine_NewGameDialog? _dialog;
    /// <summary>The dialog's hold on the guest: let go while the transition runs, taken again if that fails.</summary>
    private GameGuestHold? _hold;
    private bool _attempted;
    private bool _waiting;
    private ulong _sequence;
    private uint _generation;
    private double _remaining;
    private uint _interaction;
    private double _pollRemaining;

    public static bool OwnsInput => _current?._dialog is { IsOpen: true };
    public static Engine_NewGameDialog? ActiveMenu => OwnsInput ? _current!._dialog : null;
    public static void CancelMenu() => _current?.Close();

    /// <summary>
    /// For a host that steps its guest without a <see cref="GameSession"/> (Android's own session
    /// thread): holds the guest still while true, as <see cref="GameSession.Paused"/> does. Null on
    /// the desktop, where the session is held.
    /// </summary>
    public Action<bool>? HoldGuest { get; set; }

    /// <summary>
    /// Whether nothing holds the guest still: no paused session on the desktop, and no session at
    /// all on a host that steps its own guest (Android), where photo mode is what holds it.
    /// </summary>
    public static bool GuestRuns => GameSession.Current is not { Paused: true } && !GamePhotoMode.Active;

    public static bool MenuAllowsInteraction(Engine_UiModel.PauseState? state) =>
        state is null || state is { ScreenId: (uint)Engine_UiModel.InGameScreen.None, ModalMode: 1 };

    public static bool SessionAllowsInteraction(CoopSession.Phase phase, CoopSession.Role role, bool localCoop) =>
        !localCoop && role != CoopSession.Role.Client && phase != CoopSession.Phase.PeerJoined;

    public static void Register(Engine_GoldenCock actor) => Actors.Add(actor);
    public static void Unregister(Engine_GoldenCock actor)
    {
        Actors.Remove(actor);
    }

    public static bool ObserveGuest(HostBridge.GuestEntity entity)
    {
        if (entity.RecordHash != Engine_GoldenCockRuntime.Record) return false;
        foreach (var actor in Actors) actor.SetHighlight(entity.Highlight);
        return true;
    }

    public static bool HasColliderNear(Vector3 position)
    {
        foreach (var actor in Actors)
        {
            if (!GodotObject.IsInstanceValid(actor) || !actor.IsInsideTree() || !actor.IsVisibleInTree()) continue;
            var collider = actor.GetNodeOrNull<CollisionShape3D>("Body/Cube");
            if (collider is not { Disabled: false, Shape: BoxShape3D box }) continue;
            float reach = (collider.GlobalTransform.Basis.Scale.Abs() * box.Size).Length() * 0.5f + 4f;
            if (collider.GlobalPosition.DistanceSquaredTo(position) <= reach * reach) return true;
        }
        return false;
    }

    public override void _Ready() => _current = this;

    private void PollInteraction()
    {
        var state = Fable2Bridge.GetNewGameState();
        uint interaction = Fable2Bridge.GetGoldenCockInteraction();
        if (_generation != state.Generation)
        {
            Close();
            _generation = state.Generation;
            _attempted = false;
            _interaction = interaction;
            return;
        }
        if (_interaction == interaction) return;
        _interaction = interaction;
        bool available = !OwnsInput && !_attempted && Actors.Count > 0 && GameUi.WorldInteractionAllowed &&
            GuestRuns && HostBridge.IsInGame &&
            !Fable2Input.ScreenMenuOwnsInput && !Fable2Input.ModalBoxOwnsInput && !Fable2Input.MenuBoxOwnsInput &&
            !Fable2Input.ReceiveItemsOwnsInput && !Fable2Input.DisplayMessageBoxUp && !GameDevMenu.KeyboardCaptured &&
            SessionAllowsInteraction(CoopSession.State, CoopSession.Side, CoopSession.LocalCoop) &&
            MenuAllowsInteraction(HostBridge.GetPauseState());
        if (available) Open();
    }

    public override void _Process(double delta)
    {
        _pollRemaining -= delta;
        if (_pollRemaining <= 0)
        {
            _pollRemaining = 0.1;
            PollInteraction();
        }
        if (_dialog is { IsOpen: true })
        {
            if (!HostBridge.IsInGame) { Close(); return; }
            if (_waiting)
            {
                _remaining -= delta;
                ulong result = Fable2Bridge.GetNewGameState().Result;
                if ((result >> 1) > _sequence)
                {
                    _waiting = false;
                    if ((result & 1) != 0) { Close(); return; }
                    TransitionFailed("The childhood transition was refused or failed. Reload this hero before trying again; see the game log for details.");
                }
                else if (_remaining <= 0)
                {
                    _waiting = false;
                    TransitionFailed("The game did not confirm the transition. Check the game state before continuing; reload this hero before retrying.");
                }
            }
            return;
        }
    }

    /// <summary>Holds the guest still and opens the dialog: the session's pause on the desktop, the host's own hold elsewhere.</summary>
    private void Open()
    {
        if (GameGuestHold.Take(HoldGuest, GameSession.Current) is not { } hold) return;
        _hold = hold;
        _dialog ??= new Engine_NewGameDialog();
        _dialog.Open(Choose);
    }

    private void Choose(NewGameSelection? selection)
    {
        if (selection is null) { Close(); return; }
        if (!Engine_NewGameRuntime.PrepareLive(selection, out string error))
        {
            _dialog!.ShowError(error);
            return;
        }
        _sequence = Fable2Bridge.GetNewGameState().Result >> 1;
        if (HostBridge.PostFrontEndAction("RefiiNewGame") == 0)
        {
            _dialog!.ShowError("The game could not queue the transition.");
            return;
        }
        _attempted = true;
        _waiting = true;
        _remaining = 30;
        _dialog!.ShowWorking();
        _hold?.Set(false);
    }

    private void TransitionFailed(string message)
    {
        _hold?.Set(true);
        _dialog!.ShowTransitionFailure(message);
    }

    private void Close()
    {
        _dialog?.Dismiss();
        _hold?.Release();
        _hold = null;
        _waiting = false;
    }

    public override void _ExitTree()
    {
        Close();
        if (_current == this) _current = null;
    }
}
