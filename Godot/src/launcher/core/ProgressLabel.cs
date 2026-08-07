using System.Text;

namespace Refii.Launcher.Core;

public static class ProgressLabel
{
	public static string Normalize(string value, int maxLength = 64)
	{
		var replaced = new StringBuilder(value.Length);
		foreach (char character in value)
		{
			replaced.Append(character is '\n' or '\r' or '\t' ? ' ' : character);
		}

		string flattened = replaced.ToString();
		int detail = flattened.IndexOf(" (", System.StringComparison.Ordinal);
		if (detail >= 0)
		{
			flattened = flattened[..detail];
		}

		var compact = new StringBuilder(flattened.Length);
		bool previousWasSpace = false;
		foreach (char character in flattened)
		{
			if (character == ' ')
			{
				if (!previousWasSpace)
				{
					compact.Append(character);
				}

				previousWasSpace = true;
				continue;
			}

			compact.Append(character);
			previousWasSpace = false;
		}

		string result = compact.ToString().Trim(' ');

		if (result.Length > maxLength && maxLength > 3)
		{
			result = result[..(maxLength - 3)] + "...";
		}

		return result;
	}

	public static string BuildButtonLabel(string operationLabel, string statusMessage)
	{
		string operation = Normalize(operationLabel, 40);
		if (operation.Length == 0)
		{
			operation = "Working";
		}

		string status = Normalize(statusMessage);
		return status.Length == 0 ? operation + "..." : operation + "...\n" + status;
	}
}
