using System.Collections.Generic;

using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.WizardsTower;

public sealed partial class WizardsTower
{
    public const int TutorialSteps = 50;

    public void InitTutorialData()
    {
        TutorialTextList = new List<string>();
        TutorialHeaderList = new List<string>();
        TutorialButtonA = new List<string>();
        TutorialButtonB = new List<string>();
        TutorialButtonLS = new List<string>();
        TutorialButtonLT = new List<string>();

        for (var i = 0; i < TutorialSteps; i++)
        {
            TutorialButtonA.Add("txtContinue");
            TutorialButtonB.Add(string.Empty);
            TutorialButtonLS.Add(string.Empty);
            TutorialButtonLT.Add(string.Empty);
            TutorialHeaderList.Add(string.Empty);
        }

        var step = 0;
        Header(step, "txtTutorialHeader1");
        Text(step++, "txtTutorialSection1A");
        Text(step++, "txtTutorialSection1B");

        Header(step, "txtTutorialHeader2");
        Text(step++, "txtTutorialSection2A");
        TutorialButtonA[step] = string.Empty;
        TutorialButtonLS[step] = "txtChangeBet";
        Text(step++, "txtTutorialSection2B");
        Text(step++, "txtTutorialSection2C");

        Header(step, "txtTutorialHeader3");
        Text(step++, "txtTutorialSection3A");
        TutorialButtonA[step] = "txtDealHand";
        Text(step++, "txtTutorialSection3B");
        Text(step++, "txtTutorialSection3C");

        Header(step, "txtTutorialHeader4");
        Text(step++, "txtTutorialSection4A");
        TutorialButtonA[step] = "txtDealRow";
        Text(step++, "txtTutorialSection4B");
        Text(step++, "txtTutorialSection4C");

        Header(step, "txtTutorialHeader5");
        Text(step++, "txtTutorialSection5A");
        Text(step++, "txtTutorialSection5B");

        Header(step, "txtTutorialHeader6");
        TutorialButtonA[step] = "txtDealRow";
        Text(step++, "txtTutorialSection6A");
        Text(step++, "txtTutorialSection6B");

        Header(step, "txtTutorialHeader7");
        Text(step++, "txtTutorialSection7A");
        TutorialButtonA[step] = string.Empty;
        TutorialButtonB[step] = "txtTakeWin";
        Text(step++, "txtTutorialSection7B");
        Text(step++, "txtTutorialSection7C");

        Header(step, "txtTutorialHeader8");
        Text(step++, "txtTutorialSection8A");
        Text(step++, "txtTutorialSection8B");

        Header(step, "txtTutorialHeader9");
        Text(step++, "txtTutorialSection9A");
        Text(step++, "txtTutorialSection9B");

        Header(step, "txtTutorialHeader10");
        Text(step++, "txtTutorialSection10A");
        Text(step++, "txtTutorialSection10B");

        Header(step, "txtTutorialHeader11");
        TutorialButtonA[step] = string.Empty;
        TutorialButtonB[step] = "txtEndTutorial";
        TutorialButtonLT[step] = "txtTowerOdds";
        Text(step, "txtTutorialSection11A");

        TutorialLastStep = step;
    }

    private void Header(int step, string id) => TutorialHeaderList[step] = id;

    private void Text(int step, string id)
    {
        while (TutorialTextList.Count <= step)
        {
            TutorialTextList.Add(string.Empty);
        }

        TutorialTextList[step] = id;
    }

    private static string Prompt(List<string> list, int step) =>
        step >= 0 && step < list.Count ? list[step] : string.Empty;

    public bool DisplayTutorialDialog(double ticks, object? callbackData, bool groupActive)
    {
        if (!TutorialPauseUntilA && !TutorialPauseUntilB && !TutorialPauseUntilLS)
        {
            TutorialStep++;

            if (Prompt(TutorialButtonA, TutorialStep).Length != 0)
            {
                TutorialPauseUntilA = true;
            }

            if (Prompt(TutorialButtonB, TutorialStep).Length != 0)
            {
                TutorialPauseUntilB = true;
            }

            if (Prompt(TutorialButtonLS, TutorialStep).Length != 0)
            {
                TutorialPauseUntilLS = true;
            }

            TutorialIgnoreButtonPress = Prompt(TutorialButtonA, TutorialStep) == "txtContinue";
            TutorialDialogActive = false;
            return true;
        }

        if (!TutorialDialogActive)
        {
            DrawTutorialCard();
        }

        TutorialDialogActive = true;
        return false;
    }

    private void DrawTutorialCard()
    {
        IPubClip? card = Clip("foregroundclip")?.GetChild("Tutorial");
        if (card is null)
        {
            return;
        }

        string header = Prompt(TutorialHeaderList, TutorialStep);
        card.GotoAndStop(header.Length == 0 ? 1 : 2);
        card.X = 700;
        card.Y = header.Length == 0 ? 700 : 425;

        IPubClip? body = card.GetChild("TClip");
        if (body is null)
        {
            return;
        }

        body.GetChild("ExampleImages")?.GotoAndStop(TutorialStep + 1);

        if (header.Length != 0)
        {
            LocText(body.GetChild("TitleText"), header);
        }

        LocText(body.GetChild("TutorialText"), Prompt(TutorialTextList, TutorialStep));

        PromptButton(body, "A", Prompt(TutorialButtonA, TutorialStep));
        PromptButton(body, "B", Prompt(TutorialButtonB, TutorialStep));
        PromptButton(body, "LS", Prompt(TutorialButtonLS, TutorialStep));

        string odds = Prompt(TutorialButtonLT, TutorialStep);
        LocText(body.GetChild("LTButtonText")?.GetChild("StatusText"), odds);
        IPubClip? trigger = body.GetChild("LTbutton");
        if (trigger is not null)
        {
            trigger.Alpha = odds.Length == 0 ? 0 : 100;
        }

        if (odds.Length == 0)
        {
            return;
        }

        IPubClip? bButton = body.GetChild("Bbutton");
        IPubClip? bText = body.GetChild("BButtonText");
        if (bButton is not null)
        {
            bButton.X += 150;
        }

        if (bText is not null)
        {
            bText.X += 150;
        }
    }

    private void PromptButton(IPubClip body, string button, string id)
    {
        IPubClip? status = body.GetChild(button + "ButtonText")?.GetChild("StatusText");
        LocText(status, id);

        IPubClip? icon = body.GetChild(button == "LS" ? "LSbutton" : button + "button");
        if (icon is not null)
        {
            icon.Alpha = status is not null && status.Text.Length != 0 ? 100 : 0;
        }
    }
}
