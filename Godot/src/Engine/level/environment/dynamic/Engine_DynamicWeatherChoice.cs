using System;
using Godot;

namespace Refii.Level;

/// <summary>
/// WHAT THE DYNAMIC WEATHER IS, apart from whether this machine draws it. The dynamic sky's switch shows or
/// hides its sky, clouds and precipitation here; the weather they show is decided here every frame either
/// way: the player's override, else the level's rule — fixed, interior, the game's own theme, or a weighted
/// roll that drifts to a neighbour about every <see cref="Engine_DynamicWeatherRules.ChangeMinutes"/>.
/// </summary>
/// <remarks>
/// ONE WEATHER IN A CO-OP SESSION: the host's. The host states its decision with the level it was made for
/// (<see cref="Statement"/>); a joiner (<see cref="Remote"/>) decides nothing and shows the host's statement for
/// the level it is in (<see cref="State"/>), at once on arrival in a level and blended after that.
/// </remarks>
public static class Engine_DynamicWeatherChoice
{
    public const string ThemeToken = "theme";
    public const string InteriorToken = "interior";

    private static readonly Random Dice = new();
    private static float _untilDrift = -1f;
    private static string _token = ThemeToken;
    private static string _statedLevel = string.Empty;
    private static string? _stated;
    private static bool _statedHere;

    public static DynamicWeatherRule Rule { get; private set; } = DynamicWeatherRule.Random;

    /// <summary>The roll a Random level shows; kept across Random levels, so weather outlasts a region change.</summary>
    public static DynamicWeatherPreset? Pick { get; private set; }

    /// <summary>The level the decision is for: its name and scenario.</summary>
    public static string Level { get; private set; } = string.Empty;

    /// <summary>The decided weather: a preset's name, <see cref="ThemeToken"/> or <see cref="InteriorToken"/>.</summary>
    public static string Token => _token;

    /// <summary>Bumped whenever <see cref="Token"/> changes; a driver following the level retargets on it.</summary>
    public static int Generation { get; private set; }

    /// <summary>How long the latest change blends over.</summary>
    public static float BlendSeconds { get; private set; }

    /// <summary>This machine is an online joiner: the host decides its weather.</summary>
    public static bool Remote { get; set; }

    /// <summary>The host's decision as it travels: "level|token".</summary>
    public static string Statement => Level + "|" + _token;

    public static void SetLevel(LevelEntry level)
    {
        DynamicWeatherRule rule = Engine_DynamicWeatherRules.Resolve(level);
        if (rule != Rule || rule.Mode != DynamicWeatherMode.Random)
        {
            Pick = null;
        }

        Rule = rule;
        Level = (level.Name + "/" + level.Scenario).ToLowerInvariant();
        _statedHere = false;
        GD.Print($"[dynamic-sky] {level}: scenario='{level.Scenario}' weather rule={rule.Mode} " +
            $"preset={rule.Preset} ({rule.Reason})");

        // Decided now: the level's sky is primed from it before its first frame.
        Tick(0f);
    }

    /// <summary>A joiner's copy of the host's statement ("level|token"); held until this machine is in that level.</summary>
    public static void State(string statement)
    {
        int bar = statement.IndexOf('|');
        if (bar <= 0)
        {
            return;
        }

        _statedLevel = statement[..bar];
        _stated = statement[(bar + 1)..];
    }

    /// <summary>Once a frame, drawn or not.</summary>
    public static void Tick(float delta)
    {
        delta = float.IsFinite(delta) ? MathF.Max(delta, 0f) : 0f;
        string token;
        float blend = Engine_DynamicSky.BlendSeconds;
        if (Remote)
        {
            if (_stated is null || _statedLevel != Level)
            {
                return;
            }

            // The first statement for a level lands at once: the joiner has only just arrived.
            blend = _statedHere ? blend : 0f;
            _statedHere = true;
            token = _stated;
        }
        else
        {
            token = Decide(delta, ref blend);
        }

        if (token == _token)
        {
            return;
        }

        GD.Print($"[dynamic-sky] weather {_token} -> {token}{(Remote ? " (the host's)" : string.Empty)}");
        _token = token;
        BlendSeconds = blend;
        ++Generation;
    }

    /// <summary>The weather state a token shows; the theme token follows the game's own theme weather.</summary>
    public static DynamicWeatherState StateOf(string token, WeatherTheme theme)
    {
        if (token == InteriorToken)
        {
            return DynamicWeatherState.Of(DynamicWeatherPreset.Clear) with { Coverage = 0.1f, Cirrus = 0.1f };
        }

        return Engine_DynamicSky.TryParsePreset(token, out DynamicWeatherPreset preset) &&
            preset != DynamicWeatherPreset.Theme
            ? DynamicWeatherState.Of(preset)
            : DynamicWeatherState.FromTheme(theme);
    }

    private static string Decide(float delta, ref float blend)
    {
        if (Engine_DynamicSky.TryParsePreset(Engine_DynamicSky.WeatherSwitch, out DynamicWeatherPreset forced) &&
            forced != DynamicWeatherPreset.Theme)
        {
            return forced.ToString();
        }

        switch (Rule.Mode)
        {
            case DynamicWeatherMode.Fixed:
                return Rule.Preset.ToString();
            case DynamicWeatherMode.None:
                return InteriorToken;
            case DynamicWeatherMode.Random:
                if (Pick is not { } pick)
                {
                    pick = Engine_DynamicWeatherRules.Roll(Dice);
                    Pick = pick;
                    _untilDrift = NextDriftSeconds();
                    GD.Print($"[dynamic-sky] rolled {pick}");
                    return pick.ToString();
                }

                if (_untilDrift < 0f)
                {
                    _untilDrift = NextDriftSeconds();
                }

                _untilDrift -= delta;
                if (_untilDrift <= 0f)
                {
                    _untilDrift = NextDriftSeconds();
                    DynamicWeatherPreset next = Engine_DynamicWeatherRules.Drift(pick, Dice);
                    if (next != pick)
                    {
                        Pick = next;
                        blend = Engine_DynamicSky.BlendSeconds * 3f;
                        return next.ToString();
                    }
                }

                return pick.ToString();
            default:
                return ThemeToken;
        }
    }

    private static float NextDriftSeconds()
    {
        float minutes = MathF.Max(Engine_DynamicWeatherRules.ChangeMinutes, 0.1f);
        return minutes * 60f * (0.6f + ((float)Dice.NextDouble() * 0.8f));
    }
}
