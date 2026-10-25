using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// THE VERBS THE IN-GAME UI ANSWERS, on the one dispatcher (<see cref="Engine_UiCommands"/>): the
/// pause book's levers (tabs, saving, quitting, the co-op rows), the title's way out, the
/// multiplayer rows, and the host screens the open-screen verb stands. Every list's press reaches
/// them through the same <see cref="Engine_UiCommands.Run"/>; one that does not apply on the list it
/// was chosen on refuses with the reason (<see cref="Engine_UiVerb.Lists"/>).
/// </summary>
public partial class GameUi
{
	/// <summary>The host book screens built so far, by id (<see cref="Engine_UiHostScreen"/>).</summary>
	private readonly Dictionary<uint, Engine_UiHostScreen> _hostScreens = new();

	/// <summary>The host book screen standing over the pause book, or null.</summary>
	private Engine_UiHostScreen? _hostScreen;

	/// <summary>Registers this UI's verbs, openers and row conditions. Once, at ready.</summary>
	private void RegisterCommands()
	{
		Engine_UiCommands.Register(Engine_UiVerbs.PauseTab, (argument, context) =>
		{
			if (!uint.TryParse(argument, out uint tab))
			{
				return Engine_UiCommandResult.Refused($"'{context.Command}' names no tab");
			}

			bool accepted = HostBridge.PauseTabSelect(tab);
			GD.Print($"[game-ui] pause tab {tab} ('{context.Row}') " +
				(accepted
					? "requested — the guest pushes the screen itself"
					: "REFUSED by the bridge — not on the pause root, or a modal is pending"));
			return Engine_UiCommandResult.From(accepted, "the bridge refused it: not on the pause root, or a modal is pending");
		});

		// Through the pump, because the guest's event method must run on ITS tick thread. The guest
		// answers with its own query box (23/24/25), which the mirror draws and the guest's own input
		// answers — the mask lifts the frame the modal id goes non-zero, so the box's A press is
		// never held back.
		Engine_UiCommands.Register(Engine_UiVerbs.PauseSaving, (_, context) =>
		{
			HostBridge.PostFrontEndAction(context.Command);
			GD.Print($"[game-ui] pause saving event '{context.Command}' ('{context.Row}') posted to the guest tick pump");
			return Engine_UiCommandResult.Done;
		});

		// Same event as the matching title row, so the same confirmation box and the same save; the
		// host adds only the latch, and the app ends when the guest reaches the front end.
		Engine_UiCommands.Register(Engine_UiVerbs.PauseQuit, (saving, context) =>
		{
			_quitToDesktopPending = true;
			_quitToDesktopWindow = QuitToDesktopWindowSeconds;
			HostBridge.PostFrontEndAction(Engine_PauseMenu.SavingCommandPrefix + saving);
			GD.Print($"[game-ui] pause entry '{context.Row}' posted '{Engine_PauseMenu.SavingCommandPrefix}{saving}' " +
				"and armed the quit to the desktop; the app ends when the guest reaches the front end");
			return Engine_UiCommandResult.Done;
		});

		// Fable 3's Leave / Kick: the pump runs the guest's own answer on its tick thread.
		Engine_UiCommands.Register(Engine_UiVerbs.PauseCoop, (argument, context) =>
		{
			string coop = argument switch
			{
				"leave" => "CoopLeave",
				"kick" => "CoopKick",
				_ => string.Empty,
			};
			if (coop.Length == 0)
			{
				return Engine_UiCommandResult.Refused($"'{context.Command}': PauseCoop: takes leave or kick");
			}

			HostBridge.PostFrontEndAction(coop);
			GD.Print($"[game-ui] pause co-op row '{context.Row}' posted '{coop}'");
			return Engine_UiCommandResult.Done;
		});

		Engine_UiCommands.Register(Engine_UiVerbs.PauseOwned, Engine_UiHostScreens.Open);

		// Host-owned: no guest branch to reach. No confirmation — the front end holds no unsaved
		// progress; in a game, PauseQuit: saves first.
		Engine_UiCommands.Register(Engine_UiVerbs.QuitToDesktop, (_, _) =>
		{
			GameExit.ToDesktop(GetTree());
			return Engine_UiCommandResult.Done;
		});

		Engine_UiCommands.Register(Engine_UiVerbs.Network, (argument, _) =>
		{
			DispatchNetwork(argument);
			return Engine_UiCommandResult.Done;
		});

		// THE MULTIPLAYER BOOK has behaviour of its own (Engine_UiListBookScreen); every other host
		// screen is a template book.
		Engine_UiHostScreens.Register(Engine_UiMultiplayerPage.ScreenId, (_, _, _) =>
			Engine_UiCommandResult.From(_multiplayer.Open(), "its art is not read yet; the next press opens it"));
		Engine_UiHostScreens.Books = OpenHostScreen;

		Engine_UiRowConditions.Register(Engine_UiRowConditions.Coop, () => CoopSession.PeerStanding || CoopSession.LocalCoop);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.CoopOnline, () => CoopSession.PeerStanding && !CoopSession.LocalCoop);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.CoopClient, () => CoopSession.IsClient);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.InGame, () => HostBridge.IsInGame);
	}

	/// <summary>
	/// Stands a template book screen over the pause book. It lives on the pause root — the drive
	/// drops it the moment the root is not what the guest says is up — so it opens from the pause
	/// list (or a mod menu over it) and refuses anywhere else.
	/// </summary>
	private Engine_UiCommandResult OpenHostScreen(uint screenId, int player, Engine_UiCommandContext context)
	{
		if (!HostBridge.IsInGame || context.List != Engine_UiCommandList.Pause)
		{
			return Engine_UiCommandResult.Refused(
				$"screen {screenId} stands over the pause book; open it from the pause list");
		}

		if (!_hostScreens.TryGetValue(screenId, out Engine_UiHostScreen? screen))
		{
			// The achievements list keeps its look: leather for earned and the folder, slate for
			// locked, no rating strip.
			bool achievements = screenId == Engine_UiAchievementPage.ScreenId;
			screen = new Engine_UiHostScreen(screenId)
			{
				LockedRowsSlate = achievements,
				HideRatingStrip = achievements,
			};
			_hostScreens[screenId] = screen;
		}

		screen.Host = _presentation;
		screen.Books = _books;
		if (_hostScreen is { IsOpen: true } standing && !ReferenceEquals(standing, screen))
		{
			standing.Close();
		}

		bool up = screen.Open(player is 1 or 2 ? player : Engine_UiModMenus.MenuPlayer);
		if (up)
		{
			_hostScreen = screen;
		}

		return Engine_UiCommandResult.From(up, "its art is not read yet; the next press opens it");
	}

	/// <summary>
	/// The standing host screen's frame, over the pause book. True when it took the frame. Dropped
	/// the moment the pause root is not standing: a tab push, a query box or the resume all close
	/// it, the same window the list itself yields in.
	/// </summary>
	private bool DriveHostScreen(bool standing, double delta)
	{
		if (_hostScreen is not { IsOpen: true } screen)
		{
			return false;
		}

		if (!standing)
		{
			screen.Close();
			return false;
		}

		_pauseView!.Visible = false;
		GameInput.PauseMenuSwallowsBack = true;

		float slabZ =
			_drawn.TryGetValue(_pauseAddress, out Engine_UiDrawnPresentation? slab) && IsInstanceValid(slab.Node)
				? slab.Node.Position.Z
				: 0f;

		// The art may still be being read; the frame is held either way rather than letting the
		// press fall through to a list the player cannot see.
		if (screen.Drive(
				GameInput.MenuDirection,
				GameInput.MenuAcceptPressed || PointerList(screen.View),
				GameInput.MenuBackPressed, delta, slabZ))
		{
			ReportMenuView(screen.View);
		}

		return true;
	}

	/// <summary>Drops every built host screen so the next open builds from the records as they stand (a remount).</summary>
	private void ForgetHostScreens()
	{
		foreach (Engine_UiHostScreen screen in _hostScreens.Values)
		{
			screen.Forget();
		}

		_hostScreens.Clear();
		_hostScreen = null;
	}
}
