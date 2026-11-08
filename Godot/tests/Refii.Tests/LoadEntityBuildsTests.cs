using System.Diagnostics;
using System.Linq;
using GdUnit4;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>A load's entity builds: the release's report.</summary>
[TestSuite]
public sealed class LoadEntityBuildsTests
{
	[TestCase]
	public void TheReportNamesTheSlowestAndStartsAfresh()
	{
		bool before = Engine_LoadEntityBuilds.Collecting;
		try
		{
			Engine_LoadEntityBuilds.Collecting = true;
			Engine_LoadEntityBuilds.Take();
			long second = Stopwatch.Frequency;
			Engine_LoadEntityBuilds.NoteMesh("door", second / 100);
			Engine_LoadEntityBuilds.NoteComposite("hero+4", second / 10, second / 5);
			for (int i = 0; i < Engine_LoadEntityBuilds.SlowestKept + 2; i++)
			{
				Engine_LoadEntityBuilds.NoteMesh("chicken" + i, 1);
			}

			Engine_LoadEntityBuilds.NoteLockWait(second / 20);

			string line = Engine_LoadEntityBuilds.Take();
			AssertThat(line).StartsWith("8 model(s) 10 ms, 1 composite(s) 100 ms joining and 200 ms on textures, 50 ms waiting for model locks (1 ask(s)); slowest: hero+4 300 door 10");
			AssertThat(line.Split(' ').Count(word => word.StartsWith("chicken"))).IsEqual(Engine_LoadEntityBuilds.SlowestKept - 2);

			AssertThat(Engine_LoadEntityBuilds.Take()).StartsWith("0 model(s) 0 ms, 0 composite(s)");

			Engine_LoadEntityBuilds.Collecting = false;
			Engine_LoadEntityBuilds.NoteMesh("play", second);
			AssertThat(Engine_LoadEntityBuilds.Take()).StartsWith("0 model(s)");
		}
		finally
		{
			Engine_LoadEntityBuilds.Take();
			Engine_LoadEntityBuilds.Collecting = before;
		}
	}
}
