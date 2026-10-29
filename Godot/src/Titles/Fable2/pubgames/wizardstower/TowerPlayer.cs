namespace Refii.Titles.Fable2.PubGames.WizardsTower;

public sealed class TowerPlayer
{
    public TowerPlayer(int id, string playername, int chips, int stakes, int position)
    {
        Personality = PersonalitySmart;
        SetName(id, playername);
        Chips = chips;
        ChipsWon = 0;
        NetChips = 0;
        Position = position;
        Stakes = stakes;
        SafetyStakes = stakes;
        In = false;
        Out = false;
        StatusText = string.Empty;
        EmptySeat = false;
        HandsPlayed = 0;
    }

    public const string PersonalitySmart = "Smart";
    public const string PersonalityCautious = "Cautious";
    public const string PersonalityTimid = "Timid";
    public const string PersonalityAggressive = "Aggressive";

    public int ID { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Personality { get; private set; }

    public int Chips { get; set; }

    public int ChipsWon { get; set; }

    public int NetChips { get; set; }

    public int Position { get; set; }

    public int Stakes { get; set; }

    public int SafetyStakes { get; set; }

    public bool In { get; set; }

    public bool Out { get; set; }

    public string StatusText { get; set; }

    public bool SeatEmpty { get; set; }

    public bool EmptySeat { get; set; }

    public int HandsPlayed { get; set; }

    public void SetName(int id, string playername)
    {
        ID = id;
        Name = playername;
        Personality = (id % 4) switch
        {
            0 => PersonalitySmart,
            1 => PersonalityCautious,
            2 => PersonalityTimid,
            _ => PersonalityAggressive,
        };

        HandsPlayed = 0;
    }
}
