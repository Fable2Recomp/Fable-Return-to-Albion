namespace Refii.Titles.Fable2.PubGames.Framework;

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
