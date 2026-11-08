using System;
using System.Linq;
using GdUnit4;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>A slide entry with nothing to state costs no arrays, and writes back the same.</summary>
[TestSuite]
public sealed class BgfEmptySlideEntryTests
{
    [TestCase]
    public void EmptyEntriesShareTheEmptyArraysAndRoundTrip()
    {
        Engine_BgfDocument doc = Engine_BgfEdit.NewDocument("Scene", 1280, 720, true);
        int label = Engine_BgfEdit.AddChild(doc, 0, "Label");
        doc.Slides = new[]
        {
            new BgfSlide
            {
                Name = "Master Slide",
                OwnerElement = 0,
                Entries = new[]
                {
                    new BgfSlideEntry { ElementOrdinal = 0, Active = true, Commands = Array.Empty<BgfAttribute>(), AnimationEvents = Array.Empty<BgfAnimationEvent>() },
                    new BgfSlideEntry { ElementOrdinal = label, Active = true, Commands = Array.Empty<BgfAttribute>(), AnimationEvents = new[] { new BgfAnimationEvent(1, 0) } },
                },
            },
        };

        byte[] written = Engine_BgfWriter.Write(doc);
        Engine_BgfDocument back = Engine_BgfDocument.Parse(written);

        BgfSlideEntry empty = back.Slides[0].Entries[0];
        AssertThat(ReferenceEquals(empty.Commands, Array.Empty<BgfAttribute>())).IsTrue();
        AssertThat(ReferenceEquals(empty.ActivatedAnimations, Array.Empty<int>())).IsTrue();
        AssertThat(ReferenceEquals(empty.AnimationEvents, Array.Empty<BgfAnimationEvent>())).IsTrue();
        AssertThat(empty.EventCount).IsEqual(0);

        BgfSlideEntry stated = back.Slides[0].Entries[1];
        AssertThat(stated.AnimationEvents.Length).IsEqual(1);
        AssertThat(stated.ActivatedAnimations!.Length).IsEqual(1);

        AssertThat(Engine_BgfWriter.Write(back).SequenceEqual(written)).IsTrue();
    }
}
