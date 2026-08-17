namespace Refii.FlashGames.Framework;

public delegate bool PubCallbackMethod(double ticks, object? callbackData, bool blocked);

public delegate bool PubPredicateMethod(object? callbackData);

public delegate void PubEndCallback(object? userData);

public class Callback
{
    private PubCallbackMethod? _callbackMethod;
    private object? _callbackData;
    private PubPredicateMethod? _blockingMethod;
    private PubPredicateMethod? _abortMethod;
    private object? _group;
    private bool _blocking;

    public Callback()
    {
        _callbackMethod = Run;
        _callbackData = this;
        _blockingMethod = Block;
        _abortMethod = Abort;
        _group = null;
        _blocking = false;
    }

    public virtual bool Run(double ticks, object? callbackData, bool blocked) => true;

    public virtual bool Block(object? callbackData) => _blocking;

    public virtual bool Abort(object? callbackData) => false;

    public PubCallbackMethod? CallbackMethod
    {
        get => _callbackMethod;
        set => _callbackMethod = value;
    }

    public object? CallbackData
    {
        get => _callbackData;
        set => _callbackData = value;
    }

    public PubPredicateMethod? BlockingMethod
    {
        get => _blockingMethod;
        set => _blockingMethod = value;
    }

    public PubPredicateMethod? AbortMethod
    {
        get => _abortMethod;
        set => _abortMethod = value;
    }

    public object? Group
    {
        get => _group;
        set => _group = value;
    }

    public bool Blocking
    {
        get => _blocking;
        set => _blocking = value;
    }

    public virtual void Dispose()
    {
        _callbackMethod = null;
        _callbackData = null;
        _blockingMethod = null;
        _abortMethod = null;
        _group = null;
    }
}
