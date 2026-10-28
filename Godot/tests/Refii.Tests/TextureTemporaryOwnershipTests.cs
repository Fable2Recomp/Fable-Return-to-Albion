using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GdUnit4;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Exercises the production temporary-resource owner without uninitialized Godot bindings.</summary>
[TestSuite]
public sealed class TextureTemporaryOwnershipTests
{
    /// <summary>Publication preserves the cached winner and promptly releases a racing upload's loser.</summary>
    [TestCase]
    public void PublishedWinnerAndRacingLoserHaveDifferentLifetimes()
    {
        var winner = new Resource();
        var firstImage = new Resource();
        Resource? published = null;
        using (var first = new Engine_TextureTemporaries())
        {
            first.Own(firstImage);
            first.Own(winner);
            first.Own(winner);
            published = winner;
            AssertThat(ReferenceEquals(first.Transfer(winner), winner)).IsTrue();
        }
        AssertThat(firstImage.Releases).IsEqual(1);
        AssertThat(winner.Releases).IsEqual(0);
        var loser = new Resource();
        var secondImage = new Resource();
        using (var racing = new Engine_TextureTemporaries())
        {
            racing.Own(secondImage);
            racing.Own(loser);
            AssertThat(ReferenceEquals(published, winner)).IsTrue();
            // The existing entry wins; no ownership transfer occurs for this upload.
        }
        AssertThat(secondImage.Releases).IsEqual(1);
        AssertThat(loser.Releases).IsEqual(1);
        AssertThat(winner.Releases).IsEqual(0);
        winner.Dispose();
        AssertThat(winner.Releases).IsEqual(1);
    }

    /// <summary>Only an exclusive tracked resource may be transferred, and disposed operations stay closed.</summary>
    [TestCase]
    public void OwnershipTransferRejectsBorrowedAndClosedResources()
    {
        var borrowed = new Resource();
        var temporary = new Engine_TextureTemporaries();
        bool rejected = false;
        try { temporary.Transfer(borrowed); } catch (InvalidOperationException) { rejected = true; }
        AssertThat(rejected).IsTrue();
        temporary.Dispose();
        rejected = false;
        try { temporary.Transfer(borrowed); } catch (ObjectDisposedException) { rejected = true; }
        AssertThat(rejected).IsTrue();
        AssertThat(borrowed.Releases).IsEqual(0);
    }

    /// <summary>A decode/enhancement alias is released once, while an unregistered shared texture is untouched.</summary>
    [TestCase]
    public void AliasesReleaseOnceAndBorrowedResourcesRemainAlive()
    {
        var image = new Resource();
        var sharedTexture = new Resource();
        var temporary = new Engine_TextureTemporaries();
        AssertThat(ReferenceEquals(temporary.Own(image), image)).IsTrue();
        temporary.Own(image);
        AssertThat(temporary.Own<Resource>(null)).IsNull();
        AssertThat(image.Releases).IsEqual(0);
        temporary.Dispose();
        temporary.Dispose();
        AssertThat(image.Releases).IsEqual(1);
        AssertThat(sharedTexture.Releases).IsEqual(0);
    }

    /// <summary>A failed upload still releases both decoded and independently enhanced CPU images.</summary>
    [TestCase]
    public void UploadExceptionReleasesAllDistinctInputs()
    {
        var decoded = new Resource();
        var enhanced = new Resource();
        var expected = new InvalidOperationException("Synthetic upload failure");
        Exception? caught = null;
        try
        {
            using var temporary = new Engine_TextureTemporaries();
            temporary.Own(decoded);
            temporary.Own(enhanced);
            throw expected;
        }
        catch (Exception error) { caught = error; }
        AssertThat(ReferenceEquals(caught, expected)).IsTrue();
        AssertThat(decoded.Releases).IsEqual(1);
        AssertThat(enhanced.Releases).IsEqual(1);
    }

    /// <summary>Parallel terrain construction owns every partial slice once, including overwritten fallback slices.</summary>
    [TestCase]
    public void ParallelPartialAndReplacementSlicesAllRelease()
    {
        var slices = new Resource[32];
        var replacements = new Resource[32];
        using (var temporary = new Engine_TextureTemporaries())
        {
            Parallel.For(0, slices.Length, i =>
            {
                slices[i] = temporary.Own(new Resource())!;
                temporary.Own(slices[i]);
                replacements[i] = temporary.Own(new Resource())!;
            });
            foreach (Resource slice in slices) AssertThat(slice.Releases).IsEqual(0);
        }
        foreach (Resource slice in slices) AssertThat(slice.Releases).IsEqual(1);
        foreach (Resource replacement in replacements) AssertThat(replacement.Releases).IsEqual(1);
    }

    /// <summary>A native image array registered after its images releases references before their wrappers.</summary>
    [TestCase]
    public void NativeContainerCanReleaseBeforeItsImages()
    {
        var order = new List<int>();
        using (var temporary = new Engine_TextureTemporaries())
        {
            temporary.Own(new Resource(() => order.Add(1)));
            temporary.Own(new Resource(() => order.Add(2)));
            temporary.Own(new Resource(() => order.Add(3)));
        }
        AssertThat(string.Join(",", order)).IsEqual("3,2,1");
    }

    /// <summary>A cleanup failure cannot strand other owned images or trigger duplicate cleanup on retry.</summary>
    [TestCase]
    public void CleanupFailureStillReleasesTheRemainingImages()
    {
        var survivor = new Resource();
        var failed = new Resource(() => throw new InvalidOperationException("Synthetic cleanup failure"));
        var temporary = new Engine_TextureTemporaries();
        temporary.Own(survivor);
        temporary.Own(failed);
        AggregateException? caught = null;
        try { temporary.Dispose(); }
        catch (AggregateException error) { caught = error; }
        temporary.Dispose();
        AssertThat(caught).IsNotNull();
        AssertThat(caught!.InnerExceptions.Count).IsEqual(1);
        AssertThat(survivor.Releases).IsEqual(1);
        AssertThat(failed.Releases).IsEqual(1);
    }

    /// <summary>The real queued-completion helper keeps an image alive until apply and runs once.</summary>
    [TestCase]
    public void DeferredImageSurvivesUntilItsCompletion()
    {
        var image = new Resource();
        int applies = 0;
        Action completion = Engine_TextureTemporaries.ReleaseAfter(image, owned =>
        {
            AssertThat(ReferenceEquals(owned, image)).IsTrue();
            AssertThat(owned.Releases).IsEqual(0);
            ++applies;
        });
        AssertThat(image.Releases).IsEqual(0);
        completion();
        completion();
        AssertThat(applies).IsEqual(1);
        AssertThat(image.Releases).IsEqual(1);
    }

    /// <summary>Both a dead-target early return and a failing main-thread upload release the queued image.</summary>
    [TestCase]
    public void DeferredSkipAndFailureBothRelease()
    {
        var skipped = new Resource();
        Engine_TextureTemporaries.ReleaseAfter(skipped, _ => { return; })();
        AssertThat(skipped.Releases).IsEqual(1);
        var failed = new Resource();
        var expected = new InvalidOperationException("Synthetic replacement failure");
        Action completion = Engine_TextureTemporaries.ReleaseAfter(failed, _ => throw expected);
        Exception? caught = null;
        try { completion(); }
        catch (Exception error) { caught = error; }
        completion();
        AssertThat(ReferenceEquals(caught, expected)).IsTrue();
        AssertThat(failed.Releases).IsEqual(1);
    }

    /// <summary>Represents resource ownership only; native pixel correctness is tested in a real Godot fixture.</summary>
    private sealed class Resource : IDisposable
    {
        private readonly Action? _release;

        /// <summary>Creates a synthetic resource with optional disposal ordering/failure behavior.</summary>
        internal Resource(Action? release = null) => _release = release;

        /// <summary>Number of actual release attempts, including failed attempts.</summary>
        internal int Releases { get; private set; }

        /// <summary>Records one release and invokes the supplied behavior.</summary>
        public void Dispose() { ++Releases; _release?.Invoke(); }
    }
}
