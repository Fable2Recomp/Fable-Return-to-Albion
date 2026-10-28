using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using GdUnit4;
using Refii.Game;
using Refii.Save;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The Golden Cock on Android, which has no GameSession: the host creates its interaction and the
/// prop's collision, and its dialog holds the host's own guest thread where the desktop pauses the
/// session. Its menu box is the host menu box every menu context already taps.
/// </summary>
[TestSuite]
public sealed class AndroidGoldenCockTests
{
    private static string Source(string relative, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../src", relative)));

    private static readonly string[] Hosts = { "game/GameSession.cs", "android/AndroidGameHost.cs" };

    /// <summary>Both game hosts create the interaction and the collision; Android's holds its own guest.</summary>
    [TestCase]
    public void EveryHostAddsTheGoldenCock()
    {
        foreach (string host in Hosts)
        {
            string text = Source(host);
            AssertThat(text.Contains("new GameGoldenCock {")).OverrideFailureMessage(host + " has no Golden Cock").IsTrue();
            AssertThat(text.Contains("new GameGoldenCockCollision {")).OverrideFailureMessage(host + " has no Golden Cock collision").IsTrue();
        }

        AssertThat(Source("android/AndroidGameHost.cs").Contains("HoldGuest = held => guest.Held = held });")).IsTrue();
    }

    /// <summary>With no session and no photo mode, nothing holds the guest: the cock may open and its prop collides.</summary>
    [TestCase]
    public void NoSessionIsNoPause()
    {
        AssertThat(GameSession.Current).IsNull();
        AssertThat(GameGoldenCock.GuestRuns).IsTrue();
    }

    /// <summary>
    /// The dialog's hold: taken as it opens, let go while adulthood begins and taken again if that
    /// fails, and returned once on close; a second close lets go nothing photo mode might hold.
    /// </summary>
    [TestCase]
    public void TheDialogHoldsTheGuestUntilItCloses()
    {
        var holds = new List<bool>();
        var hold = new GameGoldenCock.GuestHold(holds.Add, false);
        AssertThat(holds).ContainsExactly(true);
        hold.Set(false);
        hold.Set(true);
        hold.Release();
        hold.Release();
        hold.Set(true);
        AssertThat(holds).ContainsExactly(true, false, true, false);

        // The desktop's session, paused before the dialog opened, stays paused after it.
        var paused = new List<bool>();
        new GameGoldenCock.GuestHold(paused.Add, true).Release();
        AssertThat(paused).ContainsExactly(true, true);
    }

    /// <summary>
    /// A tap on a row reaches the dialog as GameUi hands it on (pointed and clicked): a greyed row
    /// does nothing, Leave answers with no selection, as B does.
    /// </summary>
    [TestCase]
    public void TheMenuBoxTakesATappedRow()
    {
        foreach (bool tap in new[] { true, false })
        {
            var dialog = new Engine_NewGameDialog();
            int answers = 0;
            NewGameSelection? answer = null;
            dialog.Open(selection => { answers++; answer = selection; },
                () => new NewGameSaveScan(Array.Empty<NewGameSave>(), Array.Empty<string>()));
            AssertThat(SpinWait.SpinUntil(() =>
            {
                dialog.Drive(0, 0, false, false);
                return dialog.Menu?.Text == "The Golden Cock";
            }, 2000)).IsTrue();
            AssertThat(dialog.Menu!.Options.Select(option => option.Enabled).ToArray()).ContainsExactly(false, false, true);

            dialog.Drive(1.0, 0, false, false, pointed: 0, clicked: true);
            AssertThat(answers).IsEqual(0);
            if (tap) dialog.Drive(1.0, 0, false, false, pointed: 2, clicked: true);
            else dialog.Drive(1.0, 0, false, true);
            AssertThat(answers).IsEqual(1);
            AssertThat(answer).IsNull();
        }
    }
}
