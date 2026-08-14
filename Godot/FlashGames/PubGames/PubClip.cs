namespace Refii.FlashGames.PubGames;

public interface IPubClip
{
    float X { get; set; }

    float Y { get; set; }

    float Alpha { get; set; }

    float XScale { get; set; }

    float YScale { get; set; }

    float Rotation { get; set; }

    bool Visible { get; set; }

    int CurrentFrame { get; }

    int TotalFrames { get; }

    string Text { get; set; }

    float Height { get; }

    float TextHeight { get; }

    string Name { get; }

    void GotoAndStop(int frame);

    void GotoAndStop(string label);

    void GotoAndPlay(int frame);

    void Play();

    void Stop();

    IPubClip? GetChild(string name);

    IPubClip? AttachMovie(string exportName, string instanceName, int depth);

    IPubClip CreateEmptyMovieClip(string name, int depth);

    int GetDepth();

    void RemoveMovieClip();

    void SwapDepths(int depth);

    int GetNextHighestDepth();
}

public interface IAnimPos
{
    void SetX(float value);

    void SetY(float value);
}

public interface IAnimScale
{
    void SetXScale(float value);

    void SetYScale(float value);
}

public interface IAnimAlpha
{
    void SetAlpha(float value);
}

public interface IAnimFrame
{
    void SetFrame(int frame);
}

public interface IAnimValue
{
    double Value { get; set; }
}
