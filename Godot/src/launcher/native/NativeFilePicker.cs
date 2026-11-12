using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher.Native;

/// <summary>
/// Opens native OS file and folder dialogs without instantiating <see cref="FileDialog"/>
/// scene nodes (which synchronously enumerate system drives and scan the working directory on
/// Godot's main UI thread) and without blocking Godot's render loop on Windows Shell startup.
/// </summary>
public static class NativeFilePicker
{
	public const string PathsIniSection = "Paths";

	public readonly record struct NativeFileFilter(string Label, string Spec);

	public readonly record struct WindowsDialogDiagnostic(
		bool Succeeded,
		uint ConfiguredOptions,
		uint ConfiguredFileTypeIndex,
		string? ConfiguredFolderPath);

	public const uint WindowsOptionNoChangeDir = FosNoChangeDir;
	public const uint WindowsOptionPickFolders = FosPickFolders;
	public const uint WindowsOptionForceFilesystem = FosForceFilesystem;
	public const uint WindowsOptionPathMustExist = FosPathMustExist;
	public const uint WindowsOptionFileMustExist = FosFileMustExist;

	private enum WindowsDialogOutcome
	{
		Selected,
		Canceled,
		ComUnavailable,
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ComDlgFilterSpec
	{
		public IntPtr PszName;
		public IntPtr PszSpec;
	}

	private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
	private static readonly Guid IidIFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");
	private static readonly Guid IidIShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

	private const uint ClsctxInprocServer = 0x1;
	private const uint FosNoChangeDir = 0x00000008;
	private const uint FosPickFolders = 0x00000020;
	private const uint FosForceFilesystem = 0x00000040;
	private const uint FosPathMustExist = 0x00000800;
	private const uint FosFileMustExist = 0x00001000;
	private const uint SigdnFilesysPath = 0x80058000;
	private const int HResultCancelled = unchecked((int)0x800704C7);

	private static int _activePickerCount;

	public static bool IsPickerActive => Volatile.Read(ref _activePickerCount) != 0;

	[DllImport("ole32.dll", ExactSpelling = true)]
	private static extern unsafe int CoCreateInstance(
		Guid* rclsid, IntPtr pUnkOuter, uint dwClsContext, Guid* riid, void** ppv);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	private static extern unsafe int SHCreateItemFromParsingName(
		string pszPath, IntPtr pbc, Guid* riid, void** ppv);

	[DllImport("ole32.dll", ExactSpelling = true)]
	private static extern void CoTaskMemFree(IntPtr pv);

	/// <summary>
	/// Opens an OS native file-open picker asynchronously without freezing the launcher main thread.
	/// </summary>
	public static void OpenFile(
		string title,
		string[] godotFilters,
		string? currentPathHint,
		string iniKey,
		Action<string> onSelected,
		Action? onCanceled = null)
	{
		OpenInternal(
			title,
			godotFilters,
			currentPathHint,
			iniKey,
			directoryMode: false,
			onSelected,
			onCanceled);
	}

	/// <summary>
	/// Opens an OS native directory picker asynchronously without freezing the launcher main thread.
	/// </summary>
	public static void OpenDirectory(
		string title,
		string? currentDirHint,
		string iniKey,
		Action<string> onSelected,
		Action? onCanceled = null)
	{
		OpenInternal(
			title,
			Array.Empty<string>(),
			currentDirHint,
			iniKey,
			directoryMode: true,
			onSelected,
			onCanceled);
	}

	private static void OpenInternal(
		string title,
		string[] godotFilters,
		string? currentPathHint,
		string iniKey,
		bool directoryMode,
		Action<string> onSelected,
		Action? onCanceled)
	{
		ArgumentNullException.ThrowIfNull(onSelected);

		if (!TryAcquireSingleFlight())
		{
			return;
		}

		// Read the in-memory INI value on the main thread (dictionary lookup, zero disk I/O);
		// filesystem existence checks happen off-thread on Windows.
		string persistedDir = !string.IsNullOrWhiteSpace(iniKey)
			? Ini.GetString(iniKey, string.Empty, PathsIniSection)
			: string.Empty;

		if (OperatingSystem.IsWindows())
		{
			NativeFileFilter[] parsedFilters = directoryMode
				? Array.Empty<NativeFileFilter>()
				: ParseGodotFilters(godotFilters);

			var thread = new Thread(() =>
			{
				WindowsDialogOutcome outcome = WindowsDialogOutcome.Canceled;
				string? selectedPath = null;
				string initialDir = string.Empty;

				try
				{
					initialDir = ResolveInitialDirectory(currentPathHint, persistedDir, directoryMode);
					outcome = RunWindowsFileOpenDialog(
						title,
						parsedFilters,
						initialDir,
						directoryMode,
						out selectedPath);
				}
				catch (Exception)
				{
					outcome = WindowsDialogOutcome.ComUnavailable;
				}
				finally
				{
					if (outcome != WindowsDialogOutcome.ComUnavailable)
					{
						ReleaseSingleFlight();
					}
				}

				string capturedSelected = selectedPath ?? string.Empty;
				string capturedInitialDir = initialDir;
				WindowsDialogOutcome capturedOutcome = outcome;

				Callable.From(() =>
				{
					if (capturedOutcome == WindowsDialogOutcome.ComUnavailable)
					{
						// Release single-flight inside DisplayServer fallback once it finishes.
						ShowViaDisplayServer(
							title,
							godotFilters,
							capturedInitialDir,
							iniKey,
							directoryMode,
							onSelected,
							onCanceled);
						return;
					}

					if (capturedOutcome == WindowsDialogOutcome.Selected &&
						!string.IsNullOrWhiteSpace(capturedSelected))
					{
						PersistRememberedDirectory(iniKey, capturedSelected, directoryMode);
						onSelected(capturedSelected);
					}
					else
					{
						onCanceled?.Invoke();
					}
				}).CallDeferred();
			})
			{
				Name = "RefiiNativeFilePicker",
				IsBackground = true,
			};

			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
			return;
		}

		string resolvedDir = ResolveInitialDirectory(currentPathHint, persistedDir, directoryMode);
		ShowViaDisplayServer(
			title,
			godotFilters,
			resolvedDir,
			iniKey,
			directoryMode,
			onSelected,
			onCanceled);
	}

	private static void ShowViaDisplayServer(
		string title,
		string[] godotFilters,
		string initialDir,
		string iniKey,
		bool directoryMode,
		Action<string> onSelected,
		Action? onCanceled)
	{
		DisplayServer.FileDialogMode mode = directoryMode
			? DisplayServer.FileDialogMode.OpenDir
			: DisplayServer.FileDialogMode.OpenFile;

		Error err;
		try
		{
			err = DisplayServer.FileDialogShow(
				title,
				initialDir,
				string.Empty,
				false,
				mode,
				directoryMode ? Array.Empty<string>() : godotFilters,
				Callable.From<bool, string[], int>((status, selectedPaths, _) =>
				{
					ReleaseSingleFlight();
					if (status && selectedPaths.Length > 0 && !string.IsNullOrWhiteSpace(selectedPaths[0]))
					{
						string chosen = selectedPaths[0];
						PersistRememberedDirectory(iniKey, chosen, directoryMode);
						onSelected(chosen);
					}
					else
					{
						onCanceled?.Invoke();
					}
				}));
		}
		catch (Exception)
		{
			ReleaseSingleFlight();
			onCanceled?.Invoke();
			return;
		}

		if (err != Error.Ok)
		{
			ReleaseSingleFlight();
			onCanceled?.Invoke();
		}
	}

	private static void PersistRememberedDirectory(string iniKey, string selectedPath, bool directoryMode)
	{
		if (string.IsNullOrWhiteSpace(iniKey) || string.IsNullOrWhiteSpace(selectedPath))
		{
			return;
		}

		string? dir = directoryMode ? selectedPath : SafeGetDirectoryName(selectedPath);
		if (string.IsNullOrWhiteSpace(dir) || IsUncPath(dir))
		{
			return;
		}

		Ini.SetString(iniKey, NormalizeDirectoryPath(dir), PathsIniSection);
		Ini.Save();
	}

	public static bool TryAcquireSingleFlight() =>
		Interlocked.CompareExchange(ref _activePickerCount, 1, 0) == 0;

	public static void ReleaseSingleFlight() =>
		Interlocked.Exchange(ref _activePickerCount, 0);

	/// <summary>
	/// Converts Godot filter strings (e.g. <c>"*.iso ; Disc image"</c>, <c>"* ; Title update package"</c>)
	/// into Win32 <c>COMDLG_FILTERSPEC</c> label/pattern pairs.
	/// </summary>
	public static NativeFileFilter[] ParseGodotFilters(IReadOnlyList<string>? godotFilters)
	{
		if (godotFilters == null || godotFilters.Count == 0)
		{
			return new[] { new NativeFileFilter("All files (*.*)", "*.*") };
		}

		var results = new List<NativeFileFilter>(godotFilters.Count);
		var specParts = new List<string>(4);

		for (int i = 0; i < godotFilters.Count; i++)
		{
			string raw = godotFilters[i];
			if (string.IsNullOrWhiteSpace(raw))
			{
				continue;
			}

			int semi = raw.IndexOf(';');
			string rawPatterns = semi >= 0 ? raw[..semi] : raw;
			string description = semi >= 0 ? raw[(semi + 1)..].Trim() : string.Empty;

			specParts.Clear();
			string[] tokens = rawPatterns.Split(',', StringSplitOptions.RemoveEmptyEntries);
			for (int t = 0; t < tokens.Length; t++)
			{
				string pattern = tokens[t].Trim();
				if (pattern.Length == 0)
				{
					continue;
				}

				if (pattern == "*")
				{
					pattern = "*.*";
				}
				else if (!pattern.Contains('*') && !pattern.Contains('?'))
				{
					pattern = pattern.StartsWith('.') ? "*" + pattern : "*." + pattern;
				}

				specParts.Add(pattern);
			}

			if (specParts.Count == 0)
			{
				continue;
			}

			string winSpec = string.Join(";", specParts);
			string displayPatterns = string.Join(", ", specParts);
			string label = description.Length == 0
				? displayPatterns
				: description.Contains('*')
					? description
					: $"{description} ({displayPatterns})";

			results.Add(new NativeFileFilter(label, winSpec));
		}

		if (results.Count == 0)
		{
			results.Add(new NativeFileFilter("All files (*.*)", "*.*"));
		}

		return results.ToArray();
	}

	/// <summary>
	/// Returns true when <paramref name="path"/> is a Windows UNC network path (<c>\\server\share</c>).
	/// </summary>
	public static bool IsUncPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}

		ReadOnlySpan<char> trimmed = path.AsSpan().Trim();
		return trimmed.Length >= 2 &&
			((trimmed[0] == '\\' && trimmed[1] == '\\') ||
			 (trimmed[0] == '/' && trimmed[1] == '/'));
	}

	/// <summary>
	/// Resolves a fast local starting folder for the native picker:
	/// 1. Explicit <paramref name="currentPathHint"/> (or its parent directory) if it exists.
	/// 2. Remembered <paramref name="persistedDir"/> from <c>Ini</c> if local (non-UNC) and existing.
	/// 3. User's local <c>Downloads</c> folder, else <c>UserProfile</c>.
	/// </summary>
	public static string ResolveInitialDirectory(
		string? currentPathHint,
		string? persistedDir,
		bool directoryMode,
		Func<string, bool>? directoryExists = null,
		string? fallbackUserProfile = null)
	{
		Func<string, bool> exists = directoryExists ?? Directory.Exists;

		if (!string.IsNullOrWhiteSpace(currentPathHint))
		{
			string trimmedHint = currentPathHint.Trim().Trim('"');
			if (trimmedHint.Length > 0)
			{
				if (exists(trimmedHint))
				{
					return NormalizeDirectoryPath(trimmedHint);
				}

				if (!directoryMode)
				{
					string? hintParent = SafeGetDirectoryName(trimmedHint);
					if (!string.IsNullOrWhiteSpace(hintParent) && exists(hintParent))
					{
						return NormalizeDirectoryPath(hintParent);
					}
				}
				else
				{
					string? dirParent = SafeGetDirectoryName(trimmedHint.TrimEnd('/', '\\'));
					if (!string.IsNullOrWhiteSpace(dirParent) && exists(dirParent))
					{
						return NormalizeDirectoryPath(dirParent);
					}
				}
			}
		}

		if (!string.IsNullOrWhiteSpace(persistedDir))
		{
			string trimmedPersisted = persistedDir.Trim().Trim('"');
			if (trimmedPersisted.Length > 0 && !IsUncPath(trimmedPersisted) && exists(trimmedPersisted))
			{
				return NormalizeDirectoryPath(trimmedPersisted);
			}
		}

		string userRoot = fallbackUserProfile ?? SafeGetUserProfile();
		if (!string.IsNullOrWhiteSpace(userRoot) && !IsUncPath(userRoot))
		{
			string downloads = Path.Combine(userRoot, "Downloads");
			if (exists(downloads))
			{
				return NormalizeDirectoryPath(downloads);
			}

			if (exists(userRoot))
			{
				return NormalizeDirectoryPath(userRoot);
			}
		}

		return string.Empty;
	}

	public static string NormalizeWindowsParsingPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return string.Empty;
		}

		string normalized = path.Trim().Trim('"').Replace('/', '\\');
		while (normalized.Length > 3 && normalized.EndsWith('\\'))
		{
			normalized = normalized[..^1];
		}

		if (normalized.Length == 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
		{
			normalized += '\\';
		}

		return normalized;
	}

	private static string NormalizeDirectoryPath(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			return NormalizeWindowsParsingPath(path);
		}

		string trimmed = path.Trim().Trim('"');
		while (trimmed.Length > 1 && (trimmed.EndsWith('/') || trimmed.EndsWith('\\')))
		{
			trimmed = trimmed[..^1];
		}

		return trimmed;
	}

	private static string? SafeGetDirectoryName(string path)
	{
		try
		{
			return Path.GetDirectoryName(path);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static string SafeGetUserProfile()
	{
		try
		{
			return System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	/// <summary>
	/// Instantiates, configures, inspects, and immediately releases <c>CLSID_FileOpenDialog</c>
	/// on the calling Windows STA thread without opening a window, for headless verification.
	/// </summary>
	public static unsafe WindowsDialogDiagnostic ProbeWindowsDialogConfiguration(
		string title,
		NativeFileFilter[] filters,
		string initialDir,
		bool directoryMode)
	{
		if (!TryCreateAndConfigureWindowsDialog(
				title,
				filters,
				initialDir,
				directoryMode,
				readBackDiagnostics: true,
				out void* dialog,
				out uint options,
				out uint fileTypeIndex,
				out string? folderPath))
		{
			return default;
		}

		ComRelease(dialog);
		return new WindowsDialogDiagnostic(true, options, fileTypeIndex, folderPath);
	}

	private static unsafe WindowsDialogOutcome RunWindowsFileOpenDialog(
		string title,
		NativeFileFilter[] filters,
		string initialDir,
		bool directoryMode,
		out string? selectedPath)
	{
		selectedPath = null;
		if (!TryCreateAndConfigureWindowsDialog(
				title,
				filters,
				initialDir,
				directoryMode,
				readBackDiagnostics: false,
				out void* dialog,
				out _,
				out _,
				out _))
		{
			return WindowsDialogOutcome.ComUnavailable;
		}

		try
		{
			void** vtbl = *(void***)dialog;
			// IModalWindow::Show (slot 3)
			var show = (delegate* unmanaged[Stdcall]<void*, IntPtr, int>)vtbl[3];
			int hr = show(dialog, IntPtr.Zero);
			if (hr == HResultCancelled || hr < 0)
			{
				return WindowsDialogOutcome.Canceled;
			}

			// IFileDialog::GetResult (slot 20)
			var getResult = (delegate* unmanaged[Stdcall]<void*, void**, int>)vtbl[20];
			void* resultItem = null;
			if (getResult(dialog, &resultItem) < 0 || resultItem == null)
			{
				return WindowsDialogOutcome.Canceled;
			}

			try
			{
				selectedPath = ReadShellItemFileSystemPath(resultItem);
				return string.IsNullOrWhiteSpace(selectedPath)
					? WindowsDialogOutcome.Canceled
					: WindowsDialogOutcome.Selected;
			}
			finally
			{
				ComRelease(resultItem);
			}
		}
		finally
		{
			ComRelease(dialog);
		}
	}

	private static unsafe bool TryCreateAndConfigureWindowsDialog(
		string title,
		NativeFileFilter[] filters,
		string initialDir,
		bool directoryMode,
		bool readBackDiagnostics,
		out void* dialogOut,
		out uint configuredOptions,
		out uint configuredFileTypeIndex,
		out string? configuredFolderPath)
	{
		dialogOut = null;
		configuredOptions = 0;
		configuredFileTypeIndex = 0;
		configuredFolderPath = null;

		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		Guid clsid = ClsidFileOpenDialog;
		Guid iid = IidIFileOpenDialog;
		void* dialog = null;
		int hr = CoCreateInstance(&clsid, IntPtr.Zero, ClsctxInprocServer, &iid, &dialog);
		if (hr < 0 || dialog == null)
		{
			return false;
		}

		try
		{
			void** vtbl = *(void***)dialog;

			// IFileDialog::GetOptions (slot 10) & SetOptions (slot 9)
			var getOptions = (delegate* unmanaged[Stdcall]<void*, uint*, int>)vtbl[10];
			var setOptions = (delegate* unmanaged[Stdcall]<void*, uint, int>)vtbl[9];

			uint options = 0;
			getOptions(dialog, &options);
			options |= FosForceFilesystem | FosPathMustExist | FosNoChangeDir;
			options |= directoryMode ? FosPickFolders : FosFileMustExist;
			if (setOptions(dialog, options) < 0)
			{
				ComRelease(dialog);
				return false;
			}

			if (readBackDiagnostics)
			{
				uint readBackOptions = 0;
				if (getOptions(dialog, &readBackOptions) >= 0)
				{
					configuredOptions = readBackOptions;
				}
			}

			// IFileDialog::SetTitle (slot 17)
			if (!string.IsNullOrEmpty(title))
			{
				var setTitle = (delegate* unmanaged[Stdcall]<void*, char*, int>)vtbl[17];
				fixed (char* pTitle = title)
				{
					setTitle(dialog, pTitle);
				}
			}

			// IFileDialog::SetFileTypes (slot 4), SetFileTypeIndex (slot 5), GetFileTypeIndex (slot 6)
			if (!directoryMode && filters.Length > 0)
			{
				var specs = new ComDlgFilterSpec[filters.Length];
				try
				{
					for (int i = 0; i < filters.Length; i++)
					{
						specs[i].PszName = Marshal.StringToCoTaskMemUni(filters[i].Label);
						specs[i].PszSpec = Marshal.StringToCoTaskMemUni(filters[i].Spec);
					}

					var setFileTypes = (delegate* unmanaged[Stdcall]<void*, uint, ComDlgFilterSpec*, int>)vtbl[4];
					var setFileTypeIndex = (delegate* unmanaged[Stdcall]<void*, uint, int>)vtbl[5];
					var getFileTypeIndex = (delegate* unmanaged[Stdcall]<void*, uint*, int>)vtbl[6];

					fixed (ComDlgFilterSpec* pSpecs = specs)
					{
						if (setFileTypes(dialog, (uint)specs.Length, pSpecs) >= 0)
						{
							setFileTypeIndex(dialog, 1u);
							if (readBackDiagnostics)
							{
								uint idx = 0;
								if (getFileTypeIndex(dialog, &idx) >= 0)
								{
									configuredFileTypeIndex = idx;
								}
							}
						}
					}
				}
				finally
				{
					for (int i = 0; i < specs.Length; i++)
					{
						if (specs[i].PszName != IntPtr.Zero)
						{
							Marshal.FreeCoTaskMem(specs[i].PszName);
						}

						if (specs[i].PszSpec != IntPtr.Zero)
						{
							Marshal.FreeCoTaskMem(specs[i].PszSpec);
						}
					}
				}
			}

			// IFileDialog::SetFolder (slot 12) & GetFolder (slot 13)
			string winDir = NormalizeWindowsParsingPath(initialDir);
			if (winDir.Length > 0)
			{
				Guid shellItemIid = IidIShellItem;
				void* folderItem = null;
				if (SHCreateItemFromParsingName(winDir, IntPtr.Zero, &shellItemIid, &folderItem) >= 0 &&
					folderItem != null)
				{
					try
					{
						var setFolder = (delegate* unmanaged[Stdcall]<void*, void*, int>)vtbl[12];
						if (setFolder(dialog, folderItem) >= 0 && readBackDiagnostics)
						{
							var getFolder = (delegate* unmanaged[Stdcall]<void*, void**, int>)vtbl[13];
							void* currentFolder = null;
							if (getFolder(dialog, &currentFolder) >= 0 && currentFolder != null)
							{
								try
								{
									configuredFolderPath = ReadShellItemFileSystemPath(currentFolder);
								}
								finally
								{
									ComRelease(currentFolder);
								}
							}
						}
					}
					finally
					{
						ComRelease(folderItem);
					}
				}
			}

			dialogOut = dialog;
			return true;
		}
		catch
		{
			ComRelease(dialog);
			dialogOut = null;
			throw;
		}
	}

	private static unsafe string? ReadShellItemFileSystemPath(void* shellItem)
	{
		if (shellItem == null)
		{
			return null;
		}

		void** itemVtbl = *(void***)shellItem;
		// IShellItem::GetDisplayName (slot 5)
		var getDisplayName = (delegate* unmanaged[Stdcall]<void*, uint, IntPtr*, int>)itemVtbl[5];
		IntPtr pszPath = IntPtr.Zero;
		if (getDisplayName(shellItem, SigdnFilesysPath, &pszPath) < 0 || pszPath == IntPtr.Zero)
		{
			return null;
		}

		try
		{
			return Marshal.PtrToStringUni(pszPath);
		}
		finally
		{
			CoTaskMemFree(pszPath);
		}
	}

	private static unsafe void ComRelease(void* comObj)
	{
		if (comObj == null)
		{
			return;
		}

		void** vtbl = *(void***)comObj;
		// IUnknown::Release (slot 2)
		var release = (delegate* unmanaged[Stdcall]<void*, uint>)vtbl[2];
		release(comObj);
	}
}
