using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Refii.AndroidRuntime;

/// <summary>Private file handshake between the managed installer and Android foreground service.</summary>
/// <remarks>The worker owns completion even while Godot's frame loop is paused. Android
/// cancellation and timeout signals stop the same linked token used by extraction and tools.</remarks>
public sealed class AndroidInstallJob
{
    /// <summary>Unique job name supplied to the non-exported Android service.</summary>
    public string Id { get; } = "job-" + Guid.NewGuid().ToString("N");
    /// <summary>Owned private progress and lifecycle receipts; never a game or source directory.</summary>
    public string DirectoryPath { get; }

    /// <summary>Creates an isolated service handshake before any expensive work starts.</summary>
    /// <param name="user">Application-private user root.</param>
    public AndroidInstallJob(string user)
    {
        DirectoryPath = Path.Combine(user, "phone-jobs", Id);
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, "owner"), Id);
        Report("Preparing installation");
    }

    /// <summary>Atomically publishes bounded progress for the Android notification.</summary>
    /// <param name="message">Human-readable current installation stage.</param>
    public void Report(string message)
    {
        string path = Path.Combine(DirectoryPath, "progress");
        File.WriteAllText(path + ".tmp", message.Length <= 240 ? message : message[..240]);
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>Ends a service request that could not be started, including late Android delivery.</summary>
    public void Finish() => File.WriteAllText(Path.Combine(DirectoryPath, "finished"), "finished");

    /// <summary>Waits for Android protection, runs the full lifecycle and always releases it.</summary>
    /// <typeparam name="T">Verified installation result type.</typeparam>
    /// <param name="operation">Extraction, compilation and cleanup using the linked cancellation token.</param>
    /// <param name="cancellation">Explicit launcher cancellation.</param>
    /// <returns>The completed worker result.</returns>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var stopMonitor = new CancellationTokenSource();
        Task monitor = MonitorAsync(linked, stopMonitor.Token);
        try
        {
            long until = System.Environment.TickCount64 + 10000;
            while (!File.Exists(Path.Combine(DirectoryPath, "ready")))
            {
                linked.Token.ThrowIfCancellationRequested();
                if (System.Environment.TickCount64 >= until) throw new IOException("Android could not start the installation service");
                await Task.Delay(50, linked.Token);
            }
            linked.Token.ThrowIfCancellationRequested();
            return await operation(linked.Token);
        }
        finally
        {
            stopMonitor.Cancel();
            await monitor;
            Finish();
        }
    }

    /// <summary>Observes notification cancellation without depending on foreground Godot frames.</summary>
    /// <param name="linked">Installer token source to cancel.</param>
    /// <param name="stop">Ends observation after the worker has finished.</param>
    private async Task MonitorAsync(CancellationTokenSource linked, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (File.Exists(Path.Combine(DirectoryPath, "cancel"))) { linked.Cancel(); return; }
                await Task.Delay(250, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
