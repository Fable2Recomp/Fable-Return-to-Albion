namespace Refii.FlashGames.Framework;

/// <summary>A 1:1 port of <c>Util.as</c>, which holds exactly one thing.</summary>
public static class Util
{
    public static double Clamp(double x, double min, double max)
    {
        if (x <= min)
        {
            return min;
        }

        return x >= max ? max : x;
    }

    public static int Clamp(int x, int min, int max)
    {
        if (x <= min)
        {
            return min;
        }

        return x >= max ? max : x;
    }
}
