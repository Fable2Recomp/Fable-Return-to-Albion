using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Service handshakes and worker completion independent of paused Godot UI frames.</summary>
[TestSuite]
public sealed class AndroidInstallJobTests
{
    /// <summary>A ready service protects work without frame polling and is released after success and failure.</summary>
    [TestCase]
    public async Task WorkerProgressCompletionAndFailureReleaseService()
    {
        string user = Path.Combine(Refii.Tests.TestTemp.Root, "refii-job-" + Guid.NewGuid().ToString("N"));
        try
        {
            var job = new AndroidInstallJob(user);
            File.WriteAllText(Path.Combine(job.DirectoryPath, "ready"), "ready");
            int result = await job.RunAsync(token =>
            {
                AssertThat(token.IsCancellationRequested).IsFalse();
                job.Report(new string('x', 500));
                return Task.FromResult(17);
            }, CancellationToken.None);
            AssertThat(result).IsEqual(17);
            AssertThat(File.ReadAllText(Path.Combine(job.DirectoryPath, "progress")).Length).IsEqual(240);
            AssertThat(File.Exists(Path.Combine(job.DirectoryPath, "finished"))).IsTrue();
            var failed = new AndroidInstallJob(user);
            File.WriteAllText(Path.Combine(failed.DirectoryPath, "ready"), "ready");
            bool rejected = false;
            try { await failed.RunAsync<int>(_ => throw new IOException("fixture failure"), CancellationToken.None); }
            catch (IOException) { rejected = true; }
            AssertThat(rejected).IsTrue();
            AssertThat(File.Exists(Path.Combine(failed.DirectoryPath, "finished"))).IsTrue();
        }
        finally { Directory.Delete(user, true); }
    }

    /// <summary>Android notification cancellation and explicit launcher cancellation prevent an unprotected build.</summary>
    [TestCase]
    public async Task ServiceAndLauncherCancellationFinishBeforeWorkerStarts()
    {
        string user = Path.Combine(Refii.Tests.TestTemp.Root, "refii-job-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (bool notification in new[] { true, false })
            {
                var job = new AndroidInstallJob(user);
                using var cancel = new CancellationTokenSource();
                if (notification) File.WriteAllText(Path.Combine(job.DirectoryPath, "cancel"), "timeout or user cancel");
                else cancel.Cancel();
                bool ran = false, stopped = false;
                try { await job.RunAsync(_ => { ran = true; return Task.FromResult(0); }, cancel.Token); }
                catch (OperationCanceledException) { stopped = true; }
                AssertThat(ran).IsFalse();
                AssertThat(stopped).IsTrue();
                AssertThat(File.Exists(Path.Combine(job.DirectoryPath, "finished"))).IsTrue();
            }
        }
        finally { Directory.Delete(user, true); }
    }
}
