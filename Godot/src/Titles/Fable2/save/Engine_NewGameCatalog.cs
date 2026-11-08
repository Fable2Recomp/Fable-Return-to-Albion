using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Refii.Titles.Fable2;

using Refii.Save;

public enum NewGameStart { Childhood, Adulthood, NewGamePlus }
public enum WantedPosterRecipient { Derek, Arfur }

public sealed record NewGameSave(string Directory, DateTime Modified, Engine_SaveHero.Hero Hero)
{
    public bool ChildhoodFinished => Hero.CompletedQuests.Contains("QC010_Childhood") ||
        float.IsFinite(Hero.ChapterProgress) && Hero.ChapterProgress >= 2f;
    public bool StoryFinished => Hero.CompletedQuests.Contains("QC240_Endgame");
    public string Label => $"{Path.GetFileName(Directory)} · {Modified.ToLocalTime():yyyy-MM-dd HH:mm} · " +
        $"{(Hero.Female ? "Female" : "Male")} · {Hero.Money:N0} gold";
}

public sealed record NewGameSaveScan(IReadOnlyList<NewGameSave> Saves, IReadOnlyList<string> Errors)
{
    public bool CanSkipChildhood => Saves.Any(s => s.ChildhoodFinished);
    public IEnumerable<NewGameSave> Completed => Saves.Where(s => s.StoryFinished);
}

public sealed record NewGameSelection(NewGameStart Start, WantedPosterRecipient? Posters, string? SourceDirectory);

public static class Engine_NewGameCatalog
{
    /// <summary>
    /// Fable 2's saved games in a save profile, <c>&lt;profile&gt;/Fable 2/00000001</c>: only that title's,
    /// never Fable 3's (which have the same file names) and never the Older saves beside it.
    /// </summary>
    public static string SaveGamesOf(string profileDirectory) =>
        Path.GetFullPath(Engine_SaveLayout.SaveGames(profileDirectory, Refii.Core.Engine_Title.Fable2.TitleId));

    /// <summary>The saves directly in each root: a root is a savegame folder, each save one folder in it.</summary>
    public static NewGameSaveScan Scan(IEnumerable<string> roots)
    {
        var saves = new List<NewGameSave>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1,
                    AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                foreach (string main in Directory.EnumerateFiles(root, "mainsave.bin", options))
                {
                    string directory = Path.GetFullPath(Path.GetDirectoryName(main)!);
                    if (!seen.Add(directory)) continue;
                    if (!File.Exists(Path.Combine(directory, "herosave.bin")))
                    {
                        errors.Add($"{directory}: herosave.bin is missing");
                        continue;
                    }
                    var hero = Engine_SaveHero.Read(directory);
                    if (hero is null) errors.Add($"{directory}: {Engine_SaveHero.LastError}");
                    else saves.Add(new NewGameSave(directory, File.GetLastWriteTimeUtc(main), hero));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{root}: {e.Message}");
            }
        }
        return new NewGameSaveScan(saves.OrderByDescending(s => s.Modified).ToArray(), errors);
    }

    public static NewGameSave? Validate(NewGameSaveScan scan, NewGameSelection selection)
    {
        if (selection.Start == NewGameStart.Childhood) return null;
        if (!Enum.IsDefined(selection.Start) || !scan.CanSkipChildhood)
            throw new InvalidOperationException("An existing save must have finished childhood.");
        if (selection.Posters is null || !Enum.IsDefined(selection.Posters.Value))
            throw new InvalidOperationException("Choose Derek or Arfur before starting.");
        if (selection.Start == NewGameStart.Adulthood) return null;
        var source = scan.Saves.FirstOrDefault(s => string.Equals(s.Directory, selection.SourceDirectory,
            StringComparison.OrdinalIgnoreCase));
        if (source is null || !source.StoryFinished)
            throw new InvalidOperationException("Choose a save that has completed the main story.");
        return source;
    }
}
