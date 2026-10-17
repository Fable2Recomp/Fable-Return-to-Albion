#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;
using Refii.Bnk;
using Refii.Lua;
using Refii.Mods;

namespace Refii.Assets;

/// <summary>
/// The Lua view of the bank mount: a script-bank entry decompiled on demand, edited, checked
/// with the game's compiler and saved into a mod at the same path, where the mount overlays
/// it and the guest compiles the text itself. Nothing is written anywhere but the mod folder.
/// </summary>
[Tool]
public sealed partial class LuaScriptEditor : Window
{
	private const string DebugBankName = "gamescripts.bnk";

	private readonly string _virtualPath;
	private readonly BnkFile _file;
	private readonly Action _remount;
	private readonly byte[]? _original;   // the chunk the text was decompiled from, for Verify

	private CodeEdit _code = null!;
	private OptionButton _mods = null!;
	private LineEdit _newMod = null!;
	private Label _header = null!;
	private Label _status = null!;
	private List<ModInstall> _installed = new();

	public LuaScriptEditor(BnkFile file, string virtualPath, Action remount)
	{
		_file = file;
		_virtualPath = virtualPath;
		_remount = remount;
		_original = PickOriginal(file);
		Title = virtualPath;
		Size = new Vector2I(1100, 820);
		CloseRequested += QueueFree;
	}

	public override void _Ready()
	{
		var root = new VBoxContainer();
		root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		root.AddThemeConstantOverride("separation", 6);
		AddChild(root);

		_header = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		root.AddChild(_header);

		_code = new CodeEdit
		{
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			GuttersDrawLineNumbers = true,
			AutoBraceCompletionEnabled = false,
			IndentUseSpaces = false,
			SyntaxHighlighter = MakeHighlighter(),
		};
		root.AddChild(_code);

		var bar = new HBoxContainer();
		bar.AddThemeConstantOverride("separation", 8);
		root.AddChild(bar);

		bar.AddChild(new Label { Text = "Mod:" });
		_mods = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
		bar.AddChild(_mods);
		_newMod = new LineEdit
		{
			PlaceholderText = "or a new mod name",
			CustomMinimumSize = new Vector2(200, 0),
		};
		bar.AddChild(_newMod);

		var check = new Button { Text = "Check", TooltipText = "Compile the text with the game's Lua compiler" };
		check.Pressed += Check;
		bar.AddChild(check);
		var verify = new Button { Text = "Verify", TooltipText = "Recompile and compare with the original chunk byte-for-byte" };
		verify.Pressed += Verify;
		bar.AddChild(verify);
		var save = new Button { Text = "Save to mod", TooltipText = "Write this text into the mod at the script's own path" };
		save.Pressed += Save;
		bar.AddChild(save);
		var remove = new Button { Text = "Remove from mod", TooltipText = "Delete the mod's copy so the game's own script loads again" };
		remove.Pressed += Remove;
		bar.AddChild(remove);
		var reload = new Button { Text = "Reload original", TooltipText = "Decompile the shipped chunk again, discarding edits here" };
		reload.Pressed += () => Load(preferMod: false);
		bar.AddChild(reload);

		_status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		root.AddChild(_status);

		RefreshMods();
		Load(preferMod: true);
	}

	// ---- loading -------------------------------------------------------------

	/// <summary>
	/// The chunk to decompile: the debug bank's entry when the name is served by more than one
	/// bank, since that one carries local names and line layout; otherwise whatever won.
	/// </summary>
	private static byte[]? PickOriginal(BnkFile file)
	{
		BnkFile chosen = file;
		if (file.SourceMod.Length == 0 &&
			!file.SourceArchiveName.Equals(DebugBankName, StringComparison.OrdinalIgnoreCase))
		{
			foreach (BnkFile alternate in file.Shadowed)
			{
				if (alternate.SourceMod.Length == 0 &&
					alternate.SourceArchiveName.Equals(DebugBankName, StringComparison.OrdinalIgnoreCase))
				{
					chosen = alternate;
					break;
				}
			}
		}

		if (chosen.SourceMod.Length != 0)
		{
			// The mod's copy is the text being edited; the shipped chunk is what it shadows.
			foreach (BnkFile alternate in chosen.Shadowed)
			{
				if (alternate.SourceMod.Length == 0)
				{
					chosen = alternate;
					break;
				}
			}
		}

		try
		{
			byte[] bytes = chosen.ReadAllBytes();
			return Engine_LuaDecompiler.IsChunk(bytes) ? bytes : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void Load(bool preferMod)
	{
		// A mod that already ships this script: edit its copy, not the decompiled original.
		if (preferMod && _file.SourceMod.Length != 0)
		{
			try
			{
				byte[] modBytes = _file.ReadAllBytes();
				if (!Engine_LuaDecompiler.IsChunk(modBytes))
				{
					_code.Text = Encoding.UTF8.GetString(modBytes);
					SelectMod(_file.SourceMod);
					SetHeader($"{_virtualPath}  ·  editing the copy shipped by mod '{_file.SourceMod}'");
					SetStatus("Loaded the mod's text.");
					return;
				}
			}
			catch (Exception exception)
			{
				SetStatus($"Could not read the mod's copy: {exception.Message}");
			}
		}

		byte[] bytes;
		try
		{
			bytes = _original ?? _file.ReadAllBytes();
		}
		catch (Exception exception)
		{
			SetStatus($"Could not read {_virtualPath}: {exception.Message}");
			return;
		}

		if (!Engine_LuaDecompiler.IsChunk(bytes))
		{
			// The game ships a handful of scripts as plain text already.
			_code.Text = Encoding.UTF8.GetString(bytes);
			SetHeader($"{_virtualPath}  ·  plain text  ·  {_file.SourceArchiveName}");
			SetStatus("Shipped as source; edit and save into a mod.");
			return;
		}

		if (!Engine_LuaDecompiler.Available)
		{
			_code.Text = string.Empty;
			SetHeader(_virtualPath);
			SetStatus("The refii_luadec host library is not available (build the natives and " +
					  "stage them beside the editor).");
			return;
		}

		if (Engine_LuaDecompiler.TryDecompile(
				bytes, out string text, out LuaVerifyResult verify, out string message))
		{
			_code.Text = text;
			SetHeader($"{_virtualPath}  ·  decompiled from {SourceLabel()}  ·  {Describe(verify)}");
			SetStatus(verify == LuaVerifyResult.Exact
				? "Recompiles to the shipped bytes exactly."
				: $"{Describe(verify)}: {message}");
			return;
		}

		if (Engine_LuaDecompiler.TryDisassemble(bytes, out string listing, out _))
		{
			_code.Text = "-- This chunk did not decompile: " + message + "\n-- Listing follows.\n" + listing;
		}
		else
		{
			_code.Text = string.Empty;
		}
		SetHeader($"{_virtualPath}  ·  could not be decompiled");
		SetStatus(message);
	}

	private string SourceLabel()
	{
		if (_file.SourceArchiveName.Equals(DebugBankName, StringComparison.OrdinalIgnoreCase))
		{
			return _file.SourceArchiveName;
		}
		foreach (BnkFile alternate in _file.Shadowed)
		{
			if (alternate.SourceArchiveName.Equals(DebugBankName, StringComparison.OrdinalIgnoreCase))
			{
				return alternate.SourceArchiveName + " (names kept)";
			}
		}
		return _file.SourceArchiveName;
	}

	private static string Describe(LuaVerifyResult verify) => verify switch
	{
		LuaVerifyResult.Exact => "byte-exact",
		LuaVerifyResult.CodeExact => "code-exact (debug info differs)",
		LuaVerifyResult.Mismatch => "does NOT recompile to the same code",
		LuaVerifyResult.CompileError => "does not compile",
		_ => "not verifiable",
	};

	// ---- checking ------------------------------------------------------------

	private void Check()
	{
		if (Engine_LuaDecompiler.TryCheckSource(_code.Text, "=" + _virtualPath, out string error))
		{
			SetStatus("Compiles.");
			return;
		}
		SetStatus("Compile error: " + error);
		JumpToError(error);
	}

	private void Verify()
	{
		if (_original is null)
		{
			SetStatus("No shipped chunk to compare against.");
			return;
		}
		LuaVerifyResult verify = Engine_LuaDecompiler.Verify(_original, _code.Text, out string message);
		SetStatus(verify == LuaVerifyResult.Exact
			? "Identical to the shipped chunk: this text changes nothing."
			: $"{Describe(verify)}. {message}");
		if (verify == LuaVerifyResult.CompileError)
		{
			JumpToError(message);
		}
	}

	/// <summary>Lua errors read "name:LINE: message"; put the caret there.</summary>
	private void JumpToError(string error)
	{
		int colon = error.LastIndexOf(':');
		while (colon > 0)
		{
			int start = error.LastIndexOf(':', colon - 1);
			if (int.TryParse(error.AsSpan(start + 1, colon - start - 1), out int line) && line > 0)
			{
				_code.SetCaretLine(line - 1);
				_code.CenterViewportToCaret();
				return;
			}
			colon = start;
		}
	}

	// ---- mods ----------------------------------------------------------------

	private void RefreshMods()
	{
		_installed = ModWorkspace.DiscoverModInstalls(Engine_GameAssets.ModsRoot);
		_mods.Clear();
		_mods.AddItem("(choose a mod)");
		foreach (ModInstall mod in _installed)
		{
			_mods.AddItem(mod.Name);
		}
		_mods.Selected = 0;
		SelectMod(Refii.EditorTools.EditorModExporter.ActiveModName);
	}

	private void SelectMod(string name)
	{
		for (int index = 0; index < _installed.Count; index++)
		{
			if (string.Equals(_installed[index].Name, name, StringComparison.OrdinalIgnoreCase))
			{
				_mods.Selected = index + 1;
				return;
			}
		}
	}

	private ModInstall? ResolveTargetMod(bool create)
	{
		string newName = _newMod.Text.Trim();
		if (newName.Length != 0)
		{
			if (!ModWorkspace.IsSafeModFolderName(newName) || newName.Contains('/') || newName.Contains('\\'))
			{
				SetStatus($"'{newName}' is not a usable mod folder name.");
				return null;
			}
			ModInstall? existing = _installed.Find(
				mod => string.Equals(mod.Name, newName, StringComparison.OrdinalIgnoreCase));
			if (existing is not null)
			{
				return existing;
			}
			if (!create)
			{
				return null;
			}
			// A MOD IS A PACKAGE. A loose folder with a mod.toml is not discovered by anything.
			if (!ModWorkspace.CreateModPackage(Engine_GameAssets.ModsRoot, newName, out _, out string createError))
			{
				SetStatus($"Could not create the mod: {createError}");
				return null;
			}
			RefreshMods();
			SelectMod(newName);
			_newMod.Text = string.Empty;
			return _installed.Find(mod => string.Equals(mod.Name, newName, StringComparison.OrdinalIgnoreCase));
		}

		int selected = _mods.Selected - 1;
		if (selected < 0 || selected >= _installed.Count)
		{
			SetStatus("Choose a mod, or type a name for a new one.");
			return null;
		}
		return _installed[selected];
	}

	private bool PackageHasScript(ModInstall mod)
	{
		if (!F2ModPackage.TryOpen(mod.PackagePath, out F2ModPackage? package, out _) || package is null)
		{
			return false;
		}

		using (package)
		{
			return package.TryGetEntry(_virtualPath, out _);
		}
	}

	private void Save()
	{
		ModInstall? mod = ResolveTargetMod(create: true);
		if (mod is null)
		{
			return;
		}

		// INTO THE PACKAGE, at the script's own virtual path, the way the mount serves it.
		try
		{
			byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(_code.Text);
			F2ModPackage.IncrementalSync(mod.PackagePath, new[] { (_virtualPath, bytes) });
			ModManifest.EnsureCurrent(mod);
		}
		catch (Exception exception)
		{
			SetStatus($"Could not write {_virtualPath} into '{mod.Name}': {exception.Message}");
			return;
		}

		Refii.EditorTools.EditorModExporter.NotifyModContentChanged(mod.Name);
		bool compiles = Engine_LuaDecompiler.TryCheckSource(_code.Text, "=" + _virtualPath, out string error);
		SetStatus($"Saved {_virtualPath} into '{mod.Name}'. " + (compiles
			? "Enable the mod in the launcher and the game loads this text in place of the bank's script."
			: "WARNING, it does not compile: " + error));
		_remount();
	}

	private void Remove()
	{
		ModInstall? mod = ResolveTargetMod(create: false);
		if (mod is null)
		{
			return;
		}

		if (!PackageHasScript(mod))
		{
			SetStatus($"'{mod.Name}' has no copy of {_virtualPath}.");
			return;
		}
		try
		{
			F2ModPackage.IncrementalSync(mod.PackagePath, Array.Empty<(string, byte[])>(), new[] { _virtualPath });
			ModManifest.EnsureCurrent(mod);
		}
		catch (Exception exception)
		{
			SetStatus($"Could not remove {_virtualPath} from '{mod.Name}': {exception.Message}");
			return;
		}

		Refii.EditorTools.EditorModExporter.NotifyModContentChanged(mod.Name);
		SetStatus($"Removed {_virtualPath} from '{mod.Name}'; the game's own script loads again.");
		_remount();
	}

	// ---- presentation --------------------------------------------------------

	private void SetHeader(string text) => _header.Text = text;

	private void SetStatus(string text)
	{
		_status.Text = text;
		GD.Print($"[bnk_mount] lua: {text}");
	}

	private static CodeHighlighter MakeHighlighter()
	{
		var highlighter = new CodeHighlighter
		{
			NumberColor = new Color(0.72f, 0.86f, 1.0f),
			SymbolColor = new Color(0.82f, 0.82f, 0.82f),
			FunctionColor = new Color(0.56f, 0.82f, 1.0f),
			MemberVariableColor = new Color(0.86f, 0.86f, 0.86f),
		};
		var keyword = new Color(1.0f, 0.55f, 0.55f);
		foreach (string word in new[]
		{
			"and", "break", "continue", "do", "else", "elseif", "end", "false", "for", "function",
			"if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
		})
		{
			highlighter.AddKeywordColor(word, keyword);
		}
		var comment = new Color(0.5f, 0.6f, 0.5f);
		var text = new Color(1.0f, 0.85f, 0.55f);
		highlighter.AddColorRegion("--[[", "]]", comment);
		highlighter.AddColorRegion("--", "", comment, lineOnly: true);
		highlighter.AddColorRegion("[[", "]]", text);
		highlighter.AddColorRegion("\"", "\"", text, lineOnly: true);
		highlighter.AddColorRegion("'", "'", text, lineOnly: true);
		return highlighter;
	}
}
#endif
