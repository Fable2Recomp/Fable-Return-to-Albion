using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Refii.Save;

namespace Refii.Ui;

/// <summary>The Golden Cock's flow, a host menu box (<see cref="Engine_UiMenuBoxDialog"/>).</summary>
public sealed class Engine_NewGameDialog : Engine_UiMenuBoxDialog
{
    private Task<NewGameSaveScan>? _scanTask;
    private NewGameSaveScan? _scan;
    private Action<NewGameSelection?>? _answer;
    private int _sourcePage;

    public void Open(Action<NewGameSelection?> answer, Func<NewGameSaveScan>? scan = null)
    {
        _answer = answer;
        _scan = null;
        _sourcePage = 0;
        IsOpen = true;
        ShowPage("The Golden Cock: checking existing saves...", () => Finish(null));
        AddChoice("Leave", () => Finish(null));
        if (scan is null)
        {
            var roots = Engine_NewGameRuntime.SaveRoots();
            scan = () => Engine_NewGameCatalog.Scan(roots);
        }
        _scanTask = Task.Run(scan);
    }

    public void ShowError(string error)
    {
        ShowPage(error, ShowOptions);
        AddChoice("Back", ShowOptions);
    }

    public void ShowWorking()
    {
        ShowPage("Starting adulthood...", null);
        AddChoice("Please wait", () => { }, false);
    }

    public void ShowTransitionFailure(string message)
    {
        ShowPage(message, () => Finish(null));
        AddChoice("Close", () => Finish(null));
    }

    public void Dismiss()
    {
        ClearPages();
        _answer = null;
        _scanTask = null;
    }

    /// <summary>The save scan's page, once it has finished.</summary>
    protected override bool Poll()
    {
        if (_scanTask is not { IsCompleted: true } completed)
        {
            return false;
        }

        _scanTask = null;
        _scan = completed.IsCompletedSuccessfully ? completed.Result :
            new NewGameSaveScan(Array.Empty<NewGameSave>(), new[] { "Save scan failed." });
        ShowOptions();
        return true;
    }

    private void ShowOptions()
    {
        if (_scan is null) return;
        string title = _scan.Errors.Count > 0 ? "The Golden Cock: some saves could not be read" : "The Golden Cock";
        ShowPage(title, () => Finish(null));
        AddChoice(_scan.CanSkipChildhood ? "Skip childhood" : "Skip childhood (requires an adult save)",
            () => ShowPosters(NewGameStart.Adulthood, null), _scan.CanSkipChildhood);
        bool completed = _scan.CanSkipChildhood && _scan.Completed.Any();
        AddChoice("New Game+", ShowSources, completed);
        AddChoice("Leave", () => Finish(null));
    }

    private void ShowSources()
    {
        var sources = _scan!.Completed.ToArray();
        int pages = Math.Max(1, (sources.Length + 1) / 2);
        _sourcePage = Math.Clamp(_sourcePage, 0, pages - 1);
        ShowPage($"Choose a completed hero ({_sourcePage + 1}/{pages})", ShowOptions);
        foreach (NewGameSave source in sources.Skip(_sourcePage * 2).Take(2))
            AddChoice(source.Label, () => ShowPosters(NewGameStart.NewGamePlus, source.Directory));
        if (_sourcePage > 0) AddChoice("Previous page", () => { _sourcePage--; ShowSources(); });
        if (_sourcePage + 1 < pages) AddChoice("Next page", () => { _sourcePage++; ShowSources(); });
        AddChoice("Back", ShowOptions);
    }

    private void ShowPosters(NewGameStart start, string? source)
    {
        Action back = start == NewGameStart.NewGamePlus ? ShowSources : ShowOptions;
        ShowPage("Who did you give the wanted posters to?", back);
        AddChoice("Derek - prosperous Old Town", () => Confirm(new(start, WantedPosterRecipient.Derek, source)));
        AddChoice("Arfur - the slums", () => Confirm(new(start, WantedPosterRecipient.Arfur, source)));
        AddChoice("Back", back);
    }

    private void Confirm(NewGameSelection selection)
    {
        string title = selection.Start == NewGameStart.NewGamePlus
            ? "Replace this hero's progress? Source save stays intact."
            : "Skip this hero's childhood?";
        ShowPage(title, () => ShowPosters(selection.Start, selection.SourceDirectory));
        AddChoice("Go back", () => ShowPosters(selection.Start, selection.SourceDirectory));
        AddChoice($"Begin adulthood - posters to {selection.Posters}", () => Finish(selection));
    }

    private void Finish(NewGameSelection? selection) => _answer?.Invoke(selection);
}
