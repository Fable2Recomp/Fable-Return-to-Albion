namespace Refii.FlashGames.MageDice;

/// <summary>
/// One card of the scripted tutorial.
/// </summary>
/// <remarks>
/// EVERY FIELD IS A LOCALISATION ID, not text. The tutorial ships in thirteen languages and the
/// card is filled from <c>magedice/localization.&lt;lang&gt;.xml</c> at display time.
///
/// <c>Button1</c> and <c>Button2</c> are BUTTON ARTWORK FRAMES — "a", "b", "ls", "lt" — which the
/// card's button clips seek to, so the prompt shows the right glyph for the pad.
/// </remarks>
public readonly record struct MageDiceTutorialStep(
    string? Title,
    string Body,
    string? Button1,
    string? Label1,
    string? Button2,
    string? Label2);

public sealed partial class MageDice
{
    /// <summary>The group every tutorial action runs under, so the script can be waited on.</summary>
    public const string TutorialScript = "tScript";

    /// <summary>
    /// The twenty-two cards, in order.
    /// </summary>
    /// <remarks>
    /// A 1:1 transcription of <c>tutorialLabels</c> in <c>MageDice.as</c>. The shape of a step says
    /// what it wants: a step with only <c>Button1 = "a"</c> is a "press A to continue" card, while
    /// one carrying a second button is a step that wants the player to DO something — set a bet
    /// with the stick and then spin — and <see cref="StateTutorial"/> is what decides whether they
    /// did it.
    ///
    /// THE TITLES ARE SPARSE ON PURPOSE. Only six steps carry one; they are the chapter openings,
    /// and the cards between them continue the same topic without repeating a heading.
    /// </remarks>
    private static readonly MageDiceTutorialStep[] TutorialLabels =
    {
        new("txtTutorialHeader0", "txtTutorialInfo0", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialInfo1", "ls", "txtTutorialChangeBet", "a", "txtTutorialSpin"),
        new(null, "txtTutorialInfo2", "a", "txtTutorialContinue", null, null),
        new("txtTutorialHeader3", "txtTutorialInfo3", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialInfo4", "ls", "txtTutorialChangeBet", "a", "txtTutorialSpin"),
        new(null, "txtTutorialInfo5", "a", "txtTutorialContinue", null, null),
        new("txtTutorialHeader6", "txtTutorialInfo6", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialInfo7", "ls", "txtTutorialChangeBet", "a", "txtTutorialSpin"),
        new(null, "txtTutorialInfo8", "a", "txtTutorialContinue", null, null),
        new("txtTutorialHeader9", "txtTutorialInfo9", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialInfo10", "a", "txtTutorialSpin", null, null),
        new(null, "txtTutorialInfo11", "a", "txtTutorialContinue", null, null),
        new("txtTutorialHeader12", "txtTutorialInfo12", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialInfo13", "a", "txtTutorialSpin", null, null),
        new(null, "txtTutorialInfo14", "a", "txtTutorialContinue", null, null),
        new("txtTutorialHeader15", "txtTutorialInfo15", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialInfo16", "a", "txtTutorialSpin", null, null),
        new(null, "txtTutorialInfo17", "a", "txtTutorialContinue", null, null),
        new("txtTutorialHeader18", "txtTutorialInfo18", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialInfo19", "lt", "txtTutorialViewPays", "ls", "txtTutorialChangeBet"),
        new(null, "txtTutorialInfo20", "a", "txtTutorialContinue", null, null),
        new("txtTutorialHeader21", "txtTutorialInfo21", "b", "txtTutorialEndTutorial", null, null),
    };
}
