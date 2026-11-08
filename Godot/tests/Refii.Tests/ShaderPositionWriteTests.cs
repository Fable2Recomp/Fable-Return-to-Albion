namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// A spatial shader that writes <c>POSITION</c> must write it on every path through <c>vertex()</c>.
/// Godot 4.7.1 defines OVERRIDE_POSITION for any write (shader_compiler.cpp, usage_defines) and the
/// scene shader starts <c>position</c> at <c>vec4(1.0)</c> and outputs it as gl_Position
/// (scene_forward_clustered.glsl), so a path without the write puts every vertex on one point and
/// draws nothing, with no error. <c>ui_text.gdshader</c> wrote it only when snapping glyphs, and every
/// Fable 3 label (snap off) drew nothing. Source text only; no Godot runtime type.
/// </summary>
[TestSuite]
public sealed class ShaderPositionWriteTests
{
	private static string GodotRoot([CallerFilePath] string source = "") =>
		Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));

	private static readonly Regex VertexEntry = new(@"\bvoid\s+vertex\s*\(\s*\)\s*\{", RegexOptions.Compiled);

	private static readonly Regex PositionWrite = new(@"\bPOSITION\s*=(?!=)", RegexOptions.Compiled);

	/// <summary>The source without comments and preprocessor lines.</summary>
	internal static string Strip(string code)
	{
		code = Regex.Replace(code.Replace("\r\n", "\n"), @"/\*.*?\*/", " ", RegexOptions.Singleline);
		code = Regex.Replace(code, @"//[^\n]*", string.Empty);
		return Regex.Replace(code, @"^[ \t]*#[^\n]*", string.Empty, RegexOptions.Multiline);
	}

	/// <summary>The body of <c>vertex()</c>, between its braces; null when the source has none.</summary>
	internal static string? VertexBody(string stripped)
	{
		Match entry = VertexEntry.Match(stripped);
		if (!entry.Success)
		{
			return null;
		}

		int open = entry.Index + entry.Length - 1;
		int close = Matching(stripped, open, '{', '}');
		return stripped.Substring(open + 1, close - open - 1);
	}

	private static int Matching(string code, int open, char opener, char closer)
	{
		int depth = 0;
		for (int i = open; i < code.Length; i++)
		{
			if (code[i] == opener)
			{
				depth++;
			}
			else if (code[i] == closer && --depth == 0)
			{
				return i;
			}
		}

		throw new FormatException($"unbalanced '{opener}' at {open}");
	}

	private static int SkipSpace(string code, int at)
	{
		while (at < code.Length && char.IsWhiteSpace(code[at]))
		{
			at++;
		}

		return at;
	}

	private static bool KeywordAt(string code, int at, string keyword) =>
		string.CompareOrdinal(code, at, keyword, 0, keyword.Length) == 0 &&
		(at + keyword.Length >= code.Length || !(char.IsLetterOrDigit(code[at + keyword.Length]) || code[at + keyword.Length] == '_'));

	/// <summary>One statement from <paramref name="at"/>: where it ends, whether it writes POSITION on every path, and whether a path leaves the function.</summary>
	private static (int End, bool Writes, bool Returns) Statement(string code, int at)
	{
		at = SkipSpace(code, at);
		if (code[at] == '{')
		{
			int close = Matching(code, at, '{', '}');
			(bool writes, bool returns) = Block(code.Substring(at + 1, close - at - 1));
			return (close + 1, writes, returns);
		}

		if (KeywordAt(code, at, "if"))
		{
			int open = code.IndexOf('(', at);
			int afterCondition = Matching(code, open, '(', ')') + 1;
			(int thenEnd, bool thenWrites, bool thenReturns) = Statement(code, afterCondition);
			int next = SkipSpace(code, thenEnd);
			if (next < code.Length && KeywordAt(code, next, "else"))
			{
				(int elseEnd, bool elseWrites, bool elseReturns) = Statement(code, next + "else".Length);
				return (elseEnd, thenWrites && elseWrites, thenReturns || elseReturns);
			}

			return (thenEnd, false, thenReturns);
		}

		foreach (string loop in new[] { "for", "while", "switch" })
		{
			if (KeywordAt(code, at, loop))
			{
				int open = code.IndexOf('(', at);
				(int bodyEnd, bool _, bool bodyReturns) = Statement(code, Matching(code, open, '(', ')') + 1);
				return (bodyEnd, false, bodyReturns);
			}
		}

		int end = at;
		int parens = 0;
		while (end < code.Length && !(code[end] == ';' && parens == 0))
		{
			parens += code[end] == '(' ? 1 : code[end] == ')' ? -1 : 0;
			end++;
		}

		string text = code.Substring(at, end - at);
		return (end + 1, PositionWrite.IsMatch(text), KeywordAt(text, 0, "return"));
	}

	/// <summary>Whether a block writes POSITION on every path before it can leave, and whether a path leaves it.</summary>
	private static (bool Writes, bool Returns) Block(string code)
	{
		bool returns = false;
		int at = SkipSpace(code, 0);
		while (at < code.Length)
		{
			(int end, bool writes, bool leaves) = Statement(code, at);
			if (writes && !returns)
			{
				return (true, false);
			}

			returns |= leaves;
			at = SkipSpace(code, end);
		}

		return (false, returns);
	}

	/// <summary>Whether the body writes POSITION anywhere, and whether every path through it does.</summary>
	internal static (bool Any, bool Every) PositionWrites(string body) =>
		(PositionWrite.IsMatch(body), Block(body).Writes);

	private static IEnumerable<string> ShaderFiles()
	{
		string src = Path.Combine(GodotRoot(), "src");
		foreach (string file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
		{
			if (file.EndsWith(".gdshader", StringComparison.Ordinal) || file.EndsWith(".gdshaderinc", StringComparison.Ordinal))
			{
				yield return file;
			}
		}
	}

	[TestCase]
	public void EveryPositionWriterWritesOnEveryPath()
	{
		var partial = new List<string>();
		int writers = 0;
		foreach (string file in ShaderFiles())
		{
			if (VertexBody(Strip(File.ReadAllText(file))) is not { } body)
			{
				continue;
			}

			(bool any, bool every) = PositionWrites(body);
			if (!any)
			{
				continue;
			}

			writers++;
			if (!every)
			{
				partial.Add(Path.GetRelativePath(GodotRoot(), file));
			}
		}

		AssertThat(writers).IsGreater(1);
		AssertThat(string.Join(", ", partial)).IsEqual(string.Empty);
	}

	/// <summary>The UI text shader writes POSITION, and on every path: snapped or not, under any camera.</summary>
	[TestCase]
	public void TheTextShaderPlacesEveryVertex()
	{
		string path = Path.Combine(GodotRoot(), "src/Engine/ui/ui_text.gdshader");
		string? body = VertexBody(Strip(File.ReadAllText(path)));
		AssertThat(body).IsNotNull();
		(bool any, bool every) = PositionWrites(body!);
		AssertThat(any).IsTrue();
		AssertThat(every).IsTrue();
	}

	/// <summary>The analysis itself: a write behind an <c>if</c> alone is caught; if/else both writing, or a write after the branch, is not.</summary>
	[TestCase]
	public void TheAnalysisCatchesAConditionalWrite()
	{
		AssertThat(PositionWrites("if (snap) { POSITION = clip; }").Every).IsFalse();
		AssertThat(PositionWrites("if (a) { POSITION = x; } else { POSITION = y; }").Every).IsTrue();
		AssertThat(PositionWrites("if (a) { POSITION = x; } else if (b) { POSITION = y; }").Every).IsFalse();
		AssertThat(PositionWrites("if (a) { POSITION = x; } else if (b) { POSITION = y; } else { POSITION = z; }").Every).IsTrue();
		AssertThat(PositionWrites("vec4 c = v; if (snap) { c.xy = round(c.xy); } POSITION = c;").Every).IsTrue();
		AssertThat(PositionWrites("if (a) { return; } POSITION = c;").Every).IsFalse();
		AssertThat(PositionWrites("for (int i = 0; i < 2; i++) { POSITION = c; }").Every).IsFalse();
		AssertThat(PositionWrites("if (a == POSITION.x) { }").Any).IsFalse();
	}
}
