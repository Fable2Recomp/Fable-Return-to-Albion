namespace Refii.AndroidRuntime;

/// <summary>What the game says when it cannot go on: plain words and how to report it, not a stack trace.</summary>
public static class AndroidFailureText
{
    /// <summary>The message for <paramref name="error"/>, an exception's full text, from the game <paramref name="title"/> names.</summary>
    /// <remarks>Only its first line is shown, the type and message; the whole error is in the log
    /// the export carries.</remarks>
    public static string For(string error, string title)
    {
        string first = error.Split('\n', 2)[0].Trim();
        return title + " ran into a problem and can't continue.\n\n" +
            "To report it, close the game and open it again, then in the launcher choose " +
            "File > Export logs and saves, and attach the file it puts in your Download folder.\n\n" +
            "Error: " + first;
    }
}
