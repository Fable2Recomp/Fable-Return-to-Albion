namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The text distance field (<see cref="Engine_SdfField"/>): 0.5 on the coverage's half contour,
/// above it inside, below it outside, falling off linearly with distance. No Godot type is built.
/// </summary>
[TestSuite]
public class SdfFieldTests
{
	private const int Size = 32;

	/// <summary>A filled square, columns 8..23 and rows 8..23, hard-edged.</summary>
	private static float[] Square()
	{
		var coverage = new float[Size * Size];
		for (int y = 8; y < 24; ++y)
		{
			for (int x = 8; x < 24; ++x)
			{
				coverage[(y * Size) + x] = 1f;
			}
		}

		return coverage;
	}

	private static float At(byte[] field, int x, int y) => field[(y * Size) + x] / 255f;

	[TestCase]
	public void InsideReadsAboveHalfAndOutsideBelow()
	{
		byte[] field = Engine_SdfField.FromCoverage(Square(), Size, Size, spread: 8f, edgeWidth: 1f);

		AssertThat(At(field, 16, 16)).IsGreater(0.5f);
		AssertThat(At(field, 2, 2)).IsLess(0.5f);
	}

	[TestCase]
	public void TheHardEdgeSitsOnTheHalfContour()
	{
		byte[] field = Engine_SdfField.FromCoverage(Square(), Size, Size, spread: 8f, edgeWidth: 1f);

		// The pixels either side of the left edge are half a pixel from it, one each way.
		float inside = At(field, 8, 16);
		float outside = At(field, 7, 16);
		AssertThat(inside).IsGreater(0.5f);
		AssertThat(outside).IsLess(0.5f);
		AssertThat(System.MathF.Abs((inside - 0.5f) - (0.5f - outside))).IsLess(0.01f);
	}

	[TestCase]
	public void DistanceFallsOffLinearlyAwayFromTheEdge()
	{
		byte[] field = Engine_SdfField.FromCoverage(Square(), Size, Size, spread: 8f, edgeWidth: 1f);

		// Outside, left of the square: 0.5, 1.5, 2.5 px from the edge — equal steps of 1/16.
		float near = At(field, 7, 16);
		float mid = At(field, 6, 16);
		float far = At(field, 5, 16);
		AssertThat(System.MathF.Abs((near - mid) - (1f / 16f))).IsLess(0.01f);
		AssertThat(System.MathF.Abs((mid - far) - (1f / 16f))).IsLess(0.01f);
	}

	[TestCase]
	public void AnAntiAliasedEdgeReadsTheCoverage()
	{
		float[] coverage = Square();
		coverage[(16 * Size) + 7] = 0.25f;   // a quarter-covered pixel just outside the left edge

		byte[] field = Engine_SdfField.FromCoverage(coverage, Size, Size, spread: 8f, edgeWidth: 4f);

		// (0.5 − 0.25) × 4 = 1 px outside: 0.5 − 1/16.
		AssertThat(System.MathF.Abs(At(field, 7, 16) - (0.5f - (1f / 16f)))).IsLess(0.01f);
	}
}
