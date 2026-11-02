using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2.PubGames.Framework;

/// <summary>
/// The pub games' button prompts, as the SWFs state them. Every prompt in the three games is one
/// of the shapes the shared `button` symbol files under a frame label — <c>a</c> <c>b</c> <c>x</c>
/// <c>y</c> <c>ls</c> <c>rs</c> <c>lt</c> <c>rt</c> <c>lb</c> <c>rb</c>: the `button` clips the
/// overlays, legends and tutorial cards turn with <c>gotoAndStop</c>, and the stand-alone prompts
/// (Wizard's Tower's <c>Abutton</c>, <c>buttonAG</c>, <c>LTbutton</c>) that place the same shapes.
/// So a prompt is found by its shape, and the button it names is the label it is filed under.
/// </summary>
/// <remarks>
/// A label's shape is what its frame places that no other button's frame places: the face
/// buttons and the sticks sit in one shared bezel, which names no button and stays as shipped.
/// Pure: the scene reads the symbol's timeline and draws (<c>SwfScenePrompts</c>).
/// </remarks>
public static class PubGamePrompts
{
    /// <summary>The labels the `button` symbol names buttons by, in its frame order.</summary>
    public static readonly string[] Labels = { "a", "b", "x", "y", "ls", "rs", "lt", "rt", "lb", "rb" };

    /// <summary>
    /// The pad button (the glyph table's name: <c>A</c>, <c>LS</c>, <c>RT</c>) a frame label names,
    /// or null for a label that names none.
    /// </summary>
    public static string? ButtonOfLabel(string label) => label switch
    {
        "a" => "A",
        "b" => "B",
        "x" => "X",
        "y" => "Y",
        "ls" => "LS",
        "rs" => "RS",
        "lt" => "LT",
        "rt" => "RT",
        "lb" => "LB",
        "rb" => "RB",
        _ => null,
    };

    /// <summary>
    /// Whether a sprite's labels make it the prompt symbol: it names all four face buttons.
    /// </summary>
    public static bool IsPromptSymbol(Func<string, bool> hasLabel) =>
        hasLabel("a") && hasLabel("b") && hasLabel("x") && hasLabel("y");

    /// <summary>
    /// The prompt shapes of the symbol, by character, each with the pad button it names.
    /// <paramref name="labels"/> are the symbol's frame labels (1-based frames);
    /// <paramref name="charactersAt"/> the characters a frame places. Empty unless the labels name
    /// all four face buttons.
    /// </summary>
    public static Dictionary<int, string> Faces(
        IReadOnlyDictionary<string, int> labels, Func<int, IReadOnlyCollection<int>> charactersAt)
    {
        var faces = new Dictionary<int, string>();
        if (!IsPromptSymbol(labels.ContainsKey))
        {
            return faces;
        }

        var placed = new List<(string Button, IReadOnlyCollection<int> Characters)>();
        foreach (string label in Labels)
        {
            if (labels.TryGetValue(label, out int frame) && ButtonOfLabel(label) is { } button)
            {
                placed.Add((button, charactersAt(frame)));
            }
        }

        var uses = new Dictionary<int, int>();
        foreach ((_, IReadOnlyCollection<int> characters) in placed)
        {
            foreach (int character in new HashSet<int>(characters))
            {
                uses[character] = uses.GetValueOrDefault(character) + 1;
            }
        }

        foreach ((string button, IReadOnlyCollection<int> characters) in placed)
        {
            foreach (int character in characters)
            {
                if (uses[character] == 1)
                {
                    faces[character] = button;
                }
            }
        }

        return faces;
    }
}
