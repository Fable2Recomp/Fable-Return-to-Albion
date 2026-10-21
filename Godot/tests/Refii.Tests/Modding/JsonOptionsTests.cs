namespace Refii.Tests.Modding;

using System.Text.Json.Nodes;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The exported game runs without System.Text.Json's implicit reflection resolver, which the test
/// host has: options that state no resolver write fine here and throw in the game. The writer's
/// options must state one — their absence once threw from the HUD build and left the game with no UI.
/// </summary>
[TestSuite]
public class JsonOptionsTests
{
	[TestCase]
	public void TheTemplateWriterStatesItsResolver()
	{
		AssertThat(Engine_UiTemplateJson.WriteOptions.TypeInfoResolver is not null).IsTrue();
		var node = new JsonObject { ["name"] = "row", ["eventType"] = 3, ["drop"] = true, ["list"] = new JsonArray("a", 1) };
		AssertThat(Engine_UiTemplateJson.Write(node).Contains("\"eventType\": 3")).IsTrue();
	}
}
