using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2.PubGames.Framework;

public class Player
{
    public Player(
        int id, bool isAI, string name, int gold, int debt,
        int starRating, int points, int gamePoints)
    {
        ID = id;
        IsAI = isAI;
        IsSeated = !isAI;
        Table = -1;
        Name = name;
        Gold = gold;
        Debt = debt;
        Chips = 0;
        StarRating = starRating;
        Points = points;
        GamePoints = gamePoints;
        SessionPointsEarned = 0;
        Busted = false;
        BustedRank = 0;
        Rank = 0;
        AdditionalData = null;
    }

    public int ID { get; }

    public bool IsAI { get; set; }

    public bool IsSeated { get; set; }

    public int Table { get; set; }

    public string Name { get; set; }

    public int Gold { get; set; }

    public int Debt { get; set; }

    public int Chips { get; set; }

    public int StarRating { get; set; }

    public int Points { get; set; }

    public int GamePoints { get; set; }

    public int SessionPointsEarned { get; set; }

    public bool Busted { get; set; }

    public int BustedRank { get; set; }

    public int Rank { get; set; }

    public object? AdditionalData { get; set; }

    public virtual void ClearBets()
    {
    }
}

public sealed class PlayerInfo
{
    private readonly List<Player?> _players = new();

    public int NumPlayers => _players.Count;

    public void AddPlayer(int id, Player player)
    {
        while (_players.Count <= id)
        {
            _players.Add(null);
        }

        _players[id] = player;
    }

    public void RemovePlayer(int id)
    {
        if (id >= 0 && id < _players.Count)
        {
            _players[id] = null;
        }
    }

    public Player? GetPlayer(int id) =>
        id >= 0 && id < _players.Count ? _players[id] : null;

    public void UnseatPlayer(int id)
    {
        Player? player = GetPlayer(id);
        if (player is not null)
        {
            player.IsSeated = false;
        }
    }

    public Player? GetNewUnseatedPlayer(IPubRandom random)
    {
        if (_players.Count <= 1)
        {
            return null;
        }

        int start = (int)Math.Floor(random.NextDouble() * (_players.Count - 1)) + 1;
        int index = start;
        do
        {
            Player? player = GetPlayer(index);
            if (player is not null && !player.IsSeated)
            {
                return player;
            }

            index++;
            if (index >= _players.Count)
            {
                index = 1;
            }
        }
        while (index != start);

        return null;
    }
}
