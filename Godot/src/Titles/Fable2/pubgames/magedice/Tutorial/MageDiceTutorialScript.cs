namespace Refii.Titles.Fable2.PubGames.MageDice;

public readonly record struct MageDiceTutorialStep(
    string? Title,
    string Body,
    string? Button1,
    string? Label1,
    string? Button2,
    string? Label2);

public sealed partial class MageDice
{
    public const string TutorialScript = "tScript";

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
