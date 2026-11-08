using Godot;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>Self-contained Android disc/update installer and compiler for the user's own game.</summary>
/// <remarks>Compilation is isolated from the active runtime. Selecting documents makes
/// private copies through SAF; playing the newly compiled game is the only action that activates
/// new output. Fresh disc assets use a separate private installation; original documents and saves
/// are preserved. The installer itself sends no guest input.</remarks>
public partial class AndroidPhoneLauncher : Control
{
    /// <summary>The app's data directory, and the active title's own folder in it (AndroidRuntimeOptions.TitleDirectory).</summary>
    private string _app = "", _user = "";
    private string _selected = "", _bundle = "", _native = "", _message = "";
    private Label? _status;
    private Button? _compile, _pick, _update, _play, _playCompiled, _cancel, _exportBoth, _exportSaves, _importSave;
    private Task<AndroidPhoneCompiler.Result>? _task;
    private CancellationTokenSource? _cancellation;
    private AndroidPhoneCompiler.Result? _result;
    private AndroidRuntimeOptions? _runtime;
    private bool _keepScreenOn;
    private bool _verificationOnly;
    private string _isoUri = "", _updateUri = "";
    private AndroidGameInstaller.Prepared? _prepared;
    private AndroidInstallJob? _job;

    /// <summary>Uses this control as the original launcher's installation tab.</summary>
    public bool Hosted { get; set; }

    /// <summary>The button that plays the compiled game: the launcher's own when hosted in it.</summary>
    /// <summary>Whether the active title has a title update to install (Fable 3 has none).</summary>
    private static bool TakesUpdate => Engine_Title.Active.SupportsTitleUpdate;

    private string PlayLabel => Hosted ? "Play " + Engine_Title.Active.Name : "Play newly installed game";
    /// <summary>Current background worker status for the shared launcher's progress display.</summary>
    public string Status => _message;
    /// <summary>Whether an owned installation worker is still running.</summary>
    public bool IsInstalling => _task != null;
    /// <summary>Whether a completed installation or previous runtime is available to play.</summary>
    public bool HasPlayableInstall => _result != null || _runtime != null;
    /// <summary>Whether the playable module was built from another APK's compiler bundle and must be built again
    /// before it is played (<see cref="IsStale"/>); the launcher's button offers Build Game Code, as on desktop.</summary>
    public bool NeedsBuild { get; private set; }

    /// <summary>Starts the game-code build from the shared launcher's Build Game Code button.</summary>
    public void BuildFromLauncher() => StartCompile();
    /// <summary>Physical prepared or active game path shared with the original launcher's automatic runtime settings.</summary>
    public string? InstalledGameRoot => _prepared?.GameRoot ?? _runtime?.GameRoot;

    /// <summary>Runs the platform's verified activation path from the shared launcher's Play button.</summary>
    public void PlayFromLauncher()
    {
        if (_result != null) PlayCompiled();
        else PlayInstalled();
    }

    /// <summary>Explicitly cancels the owned worker; activity backgrounding is not a cancellation.</summary>
    public void CancelInstallation() => _cancellation?.Cancel();

    /// <summary>Whether an export or an import is under way; the transfer buttons wait for it.</summary>
    private bool _transferring;

    /// <summary>Saves the logs, the saves or both as a zip in the shared Download folder (AndroidLogBundle) and states where, or why not.</summary>
    /// <param name="contents">What the zip holds.</param>
    public async void Export(AndroidLogBundle.Contents contents)
    {
        if (_transferring) return;
        SetTransferring(true);
        _message = "Exporting " + AndroidLogBundle.Noun(contents).ToLowerInvariant() + "...";
        _message = await AndroidLogBundle.ExportAsync(contents);
        SetTransferring(false);
    }

    /// <summary>
    /// Picks an export in Android's document picker and restores its save (AndroidSaveTransfer), after
    /// writing the current save to Download as a backup; states the outcome. Never while an
    /// installation runs, and refused once the game has started.
    /// </summary>
    public void ImportSave()
    {
        if (_transferring || _task != null) return;
        var error = DisplayServer.FileDialogShow("Import save", "", "", false, DisplayServer.FileDialogMode.OpenFile,
            // An export is a zip; some file managers report no MIME type for it, so show every document and check its contents.
            new[] { "*.zip;Fable 2 export;*/*" },
            Callable.From<bool, string[], int>((selected, paths, _) =>
            {
                if (!selected || paths.Length != 1) return;
                string uri = paths[0];
                Callable.From(() => RunImport(uri)).CallDeferred();
            }));
        if (error != Error.Ok) _message = "The Android document picker could not open: " + error;
    }

    /// <summary>Imports the picked export on the main thread's schedule, the work itself on a worker.</summary>
    private async void RunImport(string uri)
    {
        if (_transferring || _task != null) return;
        SetTransferring(true);
        _message = "Importing the save...";
        _message = await AndroidSaveTransfer.ImportAsync(uri);
        SetTransferring(false);
    }

    /// <summary>Disables the export and import buttons while one runs.</summary>
    private void SetTransferring(bool running)
    {
        _transferring = running;
        foreach (Button? button in new[] { _exportBoth, _exportSaves, _importSave })
            if (button != null && IsInstanceValid(button)) button.Disabled = running;
        if (!running) UpdateButtons();
    }

    /// <summary>Explicit private debug request for an already audited phone-produced module.</summary>
    /// <param name="Module">Prior module beneath the launcher's private build root.</param>
    /// <param name="Sha256">Approved exact module bytes.</param>
    /// <param name="Version">Original executable layout.</param>
    internal sealed record DiagnosticRequest(string Module, string Sha256, string Version);

    /// <summary>Resolves the APK-extracted native directory from a small shipped helper library.</summary>
    [DllImport("librefii_phone_bridge.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr refii_phone_native_directory();

    /// <summary>Creates controller-focusable installer actions before any guest initialization.</summary>
    public override void _Ready()
    {
        _app = OS.GetUserDataDir();
        _user = AndroidRuntimeOptions.TitleDirectory(_app);
        Directory.CreateDirectory(_user);
        _keepScreenOn = DisplayServer.ScreenIsKeptOn();
        var margin = new MarginContainer { AnchorRight = 1, AnchorBottom = 1 };
        AddChild(margin);
        foreach (string side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, Hosted ? 4 : 32);
        var box = new VBoxContainer();
        margin.AddChild(box);
        box.AddChild(new Label { Text = Engine_Title.Active.Name + " — install on this phone", ThemeTypeVariation = "HeaderLarge" });
        _status = new Label { Text = "Choose your legally ripped Xbox 360 ISO" + (TakesUpdate ? " and its title update" : "") +
            ". Installation can continue while you use other apps.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddChild(_status);
        _pick = Button(box, "Choose Xbox 360 ISO", () => Pick(false));
        _update = Button(box, "Choose title update (TU file), optional", () => Pick(true));
        if (!TakesUpdate) _update.Hide();
        _compile = Button(box, "Extract and compile on this phone", StartCompile);
        _cancel = Button(box, "Cancel compilation", () => _cancellation?.Cancel());
        _play = Button(box, "Play installed game", PlayInstalled);
        _playCompiled = Button(box, "Play newly installed game", PlayCompiled);
        _exportBoth = Button(box, "Export logs and saves to Download (for bug reports)", () => Export(AndroidLogBundle.Contents.LogsAndSaves));
        _exportSaves = Button(box, "Export saves to Download (a backup)", () => Export(AndroidLogBundle.Contents.Saves));
        _importSave = Button(box, "Import save from an export", ImportSave);
        if (Hosted) { _play.Hide(); _playCompiled.Hide(); CustomMinimumSize = new Vector2(0, 320); }
        try
        {
            string config = Path.Combine(_user, AndroidRuntimeOptions.FileName);
            if (File.Exists(config))
            {
                _runtime = AndroidRuntimeOptions.Parse(File.ReadAllText(config), _user);
                _selected = _runtime.GameRoot;
            }
            AndroidInstallState.RecoverInterrupted(_user);
            PruneUnused();
            var saved = AndroidInstallState.Load(_user);
            _prepared = saved.Game;
            _result = saved.Compiled;
            if (_prepared != null) _selected = _prepared.GameRoot;
            Engine_NativeLibraries.Install();
            _native = Marshal.PtrToStringUTF8(refii_phone_native_directory()) ?? "";
            if (_native.Contains('!') || !Directory.Exists(_native))
                throw new IOException("The compiler APK must extract its native libraries");
            _message = _result != null ? Engine_Title.Active.Name + " is installed and ready to play." : _selected.Length > 0
                ? "The game files are ready. Choose Extract and compile on this phone to finish installing."
                : (TakesUpdate ? "Choose your ISO, then its TU file." : "Choose your ISO.") + " Original files stay untouched.";
            // AN UPDATE THAT CHANGES THE GAME CODE ASKS FOR A BUILD. The module is compiled on the phone
            // from the APK's bundle (hooks, version configs, headers), so a new APK can leave the
            // installed module behind; played anyway, it ran without the update's native fixes
            // (a co-op crash fix in a game hook did nothing until the module was compiled again).
            // The build starts when the player chooses it, as desktop's Build Game Code does.
            if (HasPlayableInstall && _selected.Length > 0 && _native.Length > 0 && IsStale(_result, ApkBundleIdentity()) && !AdoptIfCurrent())
            {
                NeedsBuild = true;
                _message = "This update changes the game's code. Saves are kept.";
            }
        }
        catch (Exception error) { _message = error.Message; }
        UpdateButtons();
        _pick.GrabFocus();
    }

    /// <summary>Whether the playable module was built from another compiler bundle than this APK's.</summary>
    /// <param name="compiled">The completed build the launcher would play, or null when only an older
    /// runtime configuration names one; such a module's bundle is unknown, so it is stale.</param>
    /// <param name="apkBundle">This APK's bundle identity; empty when it ships none, which never asks for a rebuild.</param>
    public static bool IsStale(AndroidPhoneCompiler.Result? compiled, string apkBundle) =>
        apkBundle.Length > 0 && (compiled?.Bundle ?? "") != apkBundle;

    /// <summary>This APK's compiler bundle identity (<see cref="AndroidPhoneCompiler.BundleIdentity"/>), as
    /// <see cref="ExtractBundle"/> names its extraction. Empty when the APK ships no bundle or it cannot be read.</summary>
    internal static string ApkBundleIdentity()
    {
        byte[] bytes = Godot.FileAccess.GetFileAsBytes("res://phonecompiler/data.zip");
        if (bytes.Length == 0) return "";
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return AndroidPhoneCompiler.BundleIdentity(archive);
    }

    /// <summary>Marks the completed build as this APK's when nothing it was built from has changed
    /// (<see cref="AndroidPhoneCompiler.StillCurrent"/>), so that the update asks for no build.</summary>
    /// <returns>Whether the module stands.</returns>
    private bool AdoptIfCurrent()
    {
        if (_result == null) return false;
        try
        {
            byte[] bytes = Godot.FileAccess.GetFileAsBytes("res://phonecompiler/data.zip");
            if (bytes.Length == 0) return false;
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (archive.GetEntry("bundle.json") is not { } entry) return false;
            using var reader = new StreamReader(entry.Open());
            var manifest = JsonSerializer.Deserialize(reader.ReadToEnd(), AndroidJsonContext.Default.BundleManifest);
            if (manifest == null || !AndroidPhoneCompiler.StillCurrent(_result, manifest, _native, Engine_Title.Active)) return false;
            _result = _result with { Bundle = AndroidPhoneCompiler.BundleIdentity(archive) };
            AndroidInstallState.Save(_user, new(_prepared, _result));
            GD.Print("[install] this APK's bundle builds the same module; no build needed");
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            GD.PushWarning("[install] could not compare this APK's bundle: " + error.Message);
            return false;
        }
    }

    /// <summary>Shows worker progress on the main thread and restores normal idle screen behavior after completion.</summary>
    public override void _Process(double delta)
    {
        if (_task?.IsCompleted == true)
        {
            var task = _task;
            _task = null;
            DisplayServer.ScreenSetKeepOn(_keepScreenOn);
            try { _result = task.GetAwaiter().GetResult(); NeedsBuild = false; _message = _verificationOnly
                ? $"The game code already built on this phone checks out. Select {PlayLabel}."
                : $"Installation compiled and verified. Temporary copies and build files removed. Select {PlayLabel}."; }
            catch (OperationCanceledException) { _message = "Compilation cancelled. Your installed game is preserved."; }
            catch (Exception error) { _message = "Compilation stopped: " + error.Message; }
            _cancellation?.Dispose();
            _cancellation = null;
            UpdateButtons();
        }
        if (_status != null) _status.Text = _message;
    }

    /// <summary>Releases the owned worker and restores the screen policy when leaving the launcher.</summary>
    public override void _ExitTree()
    {
        _cancellation?.Cancel();
        DisplayServer.ScreenSetKeepOn(_keepScreenOn);
    }

    /// <summary>Checks a completed output again before activation; tests call this without native Godot controls.</summary>
    /// <param name="result">Completed attempt and recorded module hash.</param>
    /// <returns>Whether the exact structurally valid module and completion marker still exist.</returns>
    public static bool CanActivate(AndroidPhoneCompiler.Result? result)
    {
        if (result == null || !File.Exists(result.Module) || !File.Exists(Path.Combine(result.Directory, "completed.json"))) return false;
        try
        {
            byte[] data = File.ReadAllBytes(result.Module);
            AndroidPhoneCompiler.ValidateModule(data);
            return Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(data)).ToLowerInvariant() == result.Sha256;
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>Selects original ISO/TU documents without copying on the UI thread or requesting broad permissions.</summary>
    private void Pick(bool update)
    {
        var error = DisplayServer.FileDialogShow("", "", "", false, DisplayServer.FileDialogMode.OpenFile,
            // TU packages often have no recognized extension or MIME type. Let SAF show
            // all documents and validate their contents instead of hiding legitimate inputs.
            new[] { update ? "*;Xbox 360 title update;*/*" : "*.iso;Xbox 360 disc image;*/*" },
            Callable.From<bool, string[], int>((selected, paths, _) =>
            {
                if (!selected || paths.Length != 1) return;
                try
                {
                    if (update) _updateUri = paths[0];
                    else { _isoUri = paths[0]; _updateUri = ""; }
                    _prepared = null;
                    _result = null;
                    _message = update ? "ISO and title update selected. Ready to extract and compile."
                        : TakesUpdate ? "ISO selected. Choose its title update, then extract and compile." : "ISO selected. Ready to extract and compile.";
                    UpdateButtons();
                }
                catch (Exception exception) { _message = "Import stopped: " + exception.Message; }
            }));
        if (error != Error.Ok) _message = "The Android document picker could not open: " + error;
    }

    /// <summary>Streams an original document on the owned worker with bounded memory, cancellation and copy progress.</summary>
    /// <param name="uri">Read-only document URI returned by Android SAF.</param>
    /// <param name="target">Unique temporary destination owned by the installer.</param>
    /// <param name="progress">Receives human-readable progress without touching Godot UI controls.</param>
    /// <param name="cancellation">Stops the owned copy between chunks.</param>
    private static void CopyDocument(string uri, string target, Action<string> progress, CancellationToken cancellation)
    {
        using var source = Godot.FileAccess.Open(uri, Godot.FileAccess.ModeFlags.Read)
            ?? throw new IOException("Cannot read the selected document");
        using var output = new FileStream(target, FileMode.CreateNew, System.IO.FileAccess.Write);
        ulong length = source.GetLength();
        ulong limit = target.EndsWith("disc.iso", StringComparison.Ordinal) ? 16UL * 1024 * 1024 * 1024 : 64UL * 1024 * 1024;
        if (length == 0 || length > limit) throw new InvalidDataException("Selected document has an unsupported size");
        long lastReport = System.Environment.TickCount64;
        while (source.GetPosition() < source.GetLength())
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] data = source.GetBuffer((long)Math.Min(1024UL * 1024, source.GetLength() - source.GetPosition()));
            if (data.Length == 0) throw new EndOfStreamException("Selected document ended early");
            output.Write(data);
            if (System.Environment.TickCount64 - lastReport >= 250)
            {
                lastReport = System.Environment.TickCount64;
                progress($"Copying selected file: {source.GetPosition() * 100 / length}%");
            }
        }
    }

    /// <summary>Owns a complete prepare/compile/cleanup pipeline; progress writes contain no Godot UI calls.</summary>
    private void StartCompile()
    {
        if (_task != null) return;
        try
        {
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            // Debug-only checks diagnose a prior real compiler output without redoing
            // an hour of compilation. Preserve the consumed request; never alter game config.
            string check = Path.Combine(_user, "phone-compiler-verify-once.json");
            DiagnosticRequest? diagnosis = OS.HasFeature("debug") && File.Exists(check)
                ? JsonSerializer.Deserialize(File.ReadAllText(check), AndroidJsonContext.Default.DiagnosticRequest)
                    ?? throw new InvalidDataException("Invalid private module verification request") : null;
            _verificationOnly = diagnosis != null;
            if (diagnosis != null) File.Move(check, Path.Combine(_user,
                "phone-compiler-verify-consumed-" + Guid.NewGuid().ToString("N") + ".json"), false);
            _result = null;
            _bundle = "";
            if (!Engine.HasSingleton("RefiiInstall")) throw new IOException("This APK lacks the Android installation service");
            _job = new AndroidInstallJob(_app);
            string serviceError = Engine.GetSingleton("RefiiInstall").Call("begin", _job.Id).AsString();
            if (serviceError.Length > 0) throw new IOException(serviceError);
            var job = _job;
            DisplayServer.ScreenSetKeepOn(true);
            _task = Task.Run(() => job.RunAsync(async protectedToken =>
            {
                token = protectedToken;
                void Progress(string status) { _message = status; job.Report(status); }
                if (_isoUri.Length > 0 && _prepared == null)
                {
                    _prepared = AndroidGameInstaller.Prepare(
                        (target, report, cancel) => CopyDocument(_isoUri, target, report, cancel),
                        _updateUri.Length == 0 ? null : (target, report, cancel) => CopyDocument(_updateUri, target, report, cancel),
                        Path.Combine(_user, "phone-installs"), Progress, token);
                    _selected = _prepared.GameRoot;
                    AndroidInstallState.Save(_user, new(_prepared, null));
                }
                string builds = Path.Combine(_user, "phone-builds");
                Directory.CreateDirectory(builds);
                var before = Directory.EnumerateDirectories(builds).ToHashSet(StringComparer.Ordinal);
                try
                {
                    Progress("Preparing the bundled compiler");
                    _bundle = ExtractBundle(token);
                    var request = new AndroidPhoneCompiler.Request(_bundle, _native, _selected, builds, Engine_Title.Active,
                        Path.Combine(_user, "phone-object-cache"));
                    var result = diagnosis == null
                        ? await AndroidPhoneCompiler.CompileAsync(request, Progress, token)
                        : await AndroidPhoneCompiler.VerifyExistingAsync(request, diagnosis.Module, diagnosis.Sha256,
                            diagnosis.Version, Progress, token);
                    AndroidInstallState.Save(_user, new(_prepared, result));
                    Progress("Removing temporary build files");
                    if (File.Exists(Path.Combine(result.Directory, ".refii-build-owner")))
                        AndroidInstallState.TrimBuild(result.Directory, true);
                    AndroidInstallState.TrimBundle(_user, _bundle);
                    return result;
                }
                catch
                {
                    foreach (string attempt in Directory.EnumerateDirectories(builds).Where(path => !before.Contains(path)))
                        if (File.Exists(Path.Combine(attempt, ".refii-build-owner")))
                            AndroidInstallState.TrimBuild(attempt, File.Exists(Path.Combine(attempt, "completed.json")));
                    if (_bundle.Length > 0 && Directory.Exists(_bundle)) AndroidInstallState.TrimBundle(_user, _bundle);
                    throw;
                }
            }, token));
            UpdateButtons();
        }
        catch (Exception error)
        {
            _job?.Finish();
            _cancellation?.Dispose();
            _cancellation = null;
            _message = "Cannot start compilation: " + error.Message;
        }
    }

    /// <summary>Extracts the immutable APK data bundle into a uniquely identified private tools directory.</summary>
    /// <param name="cancellation">Stops between public resource entries without retaining a large partial cache.</param>
    /// <returns>The owned public tool cache, removed after compilation or failure.</returns>
    private string ExtractBundle(CancellationToken cancellation)
    {
        byte[] bytes = Godot.FileAccess.GetFileAsBytes("res://phonecompiler/data.zip");
        if (bytes.Length == 0) throw new IOException("This APK does not include the compiler data bundle");
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        string identity = AndroidPhoneCompiler.BundleIdentity(archive);
        string root = Path.Combine(_user, "phone-toolchain-" + identity);
        if (File.Exists(Path.Combine(root, "extracted.complete"))) return root;
        // A failed extraction is never reused or deleted: use a distinct directory.
        if (Directory.Exists(root)) root += "-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".refii-toolchain-owner"), Path.GetFileName(root));
        _bundle = root;
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                entry.FullName.Contains('\\') || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("Unsafe compiler bundle entry");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = entry.Open();
            using var target = new FileStream(path, FileMode.CreateNew, System.IO.FileAccess.Write);
            source.CopyTo(target);
        }
        File.WriteAllText(Path.Combine(root, "extracted.complete"), identity);
        return root;
    }

    /// <summary>Runs the existing configured Android host without initializing a second guest.</summary>
    private void PlayInstalled()
    {
        if (_task == null && _runtime != null) GetTree().ChangeSceneToFile("res://src/android/AndroidGame.tscn");
    }

    /// <summary>Activates a verified fresh installation or replacement while preserving previous configuration and saves.</summary>
    private void PlayCompiled()
    {
        if (_task != null || !CanActivate(_result)) return;
        try
        {
            _runtime = AndroidInstallState.Activate(_user, _selected, _result!);
            PruneUnused();
            AndroidLauncherSettings.Prepare(Path.Combine(_app, "runtime"), _runtime.GameRoot);
            GetTree().ChangeSceneToFile("res://src/android/AndroidGame.tscn");
        }
        catch (Exception error) { _message = "Cannot play this installation: " + error.Message; }
    }

    /// <summary>Removes the installs and builds the game no longer uses (<see cref="AndroidInstallState.PruneUnused"/>); a failure only logs.</summary>
    private void PruneUnused()
    {
        try
        {
            var removed = AndroidInstallState.PruneUnused(_user);
            if (removed.Count > 0) GD.Print("[install] removed unused " + string.Join(", ", removed));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            GD.PushWarning("[install] cleanup stopped: " + error.Message);
        }
    }

    /// <summary>Disables conflicting actions during compilation and offers activation only for completed output.</summary>
    private void UpdateButtons()
    {
        bool running = _task != null;
        _pick!.Disabled = running;
        _update!.Disabled = running || _isoUri.Length == 0;
        _compile!.Disabled = running || (_selected.Length == 0 && _isoUri.Length == 0) || _native.Length == 0;
        _cancel!.Disabled = !running;
        _play!.Disabled = running || _runtime == null;
        _playCompiled!.Disabled = running || _result == null;
        if (_importSave != null) _importSave.Disabled = running || _transferring;
    }

    /// <summary>Adds a controller-focusable action to the installer layout.</summary>
    private static Button Button(VBoxContainer parent, string text, Action action)
    {
        var button = new Button { Text = text };
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }
}
