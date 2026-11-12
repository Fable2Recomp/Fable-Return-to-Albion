using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Refii.Recompile;

/// <summary>
/// Shared ReXGlue code generation transforms, manifest source-list validation, and <c>codegen.partition.json</c>
/// shard placement persistence used by both Desktop and Android game-code builds.
/// </summary>
public static class CodegenTransforms
{
    /// <summary>The generator's file recording which recompiled shard each guest function belongs to.</summary>
    public const string PartitionFile = "codegen.partition.json";

    /// <summary>The legacy process-wide interrupt-lock block emitted into <c>refii_pch.h</c> by older generators.</summary>
    public const string ProcessWideGuestLock = """
        inline std::atomic<i32>& ppc_global_lock_count_() {
          static std::atomic<i32> count{0};
          return count;
        }

        #define REX_CHECK_GLOBAL_LOCK()                                        \
          ([&]() -> u64 {                                                      \
            auto lock_ = rex::thread::global_critical_region::AcquireDirect(); \
            return ppc_global_lock_count_().load() ? 0 : 0x8000;               \
          }())

        #define REX_ENTER_GLOBAL_LOCK()                          \
          do {                                                   \
            rex::thread::global_critical_region::mutex().lock(); \
            ppc_global_lock_count_().fetch_add(1);               \
          } while (0)

        #define REX_LEAVE_GLOBAL_LOCK()                                                           \
          do {                                                                                    \
            auto old_count_ = ppc_global_lock_count_().fetch_sub(1);                              \
            assert(old_count_ >= 1 && "LeaveGlobalLock called without matching EnterGlobalLock"); \
            rex::thread::global_critical_region::mutex().unlock();                                \
          } while (0)

        """;

    /// <summary>The zero-mutex thread-local interrupt-disable depth block emitted by the current SDK template.</summary>
    public const string ThreadLocalGuestLock = """
        // The guest's own interrupt-disable depth on THIS thread. mfmsr only asks whether the
        // calling thread is inside such a section, and only the thread holding the global critical
        // region can be, so its own depth answers without taking the lock. A process-wide count read
        // under the lock gave the same answer for a mutex round trip per guest interlocked operation.
        inline i32& ppc_global_lock_depth_() {
          static thread_local i32 depth = 0;
          return depth;
        }

        #define REX_CHECK_GLOBAL_LOCK() (ppc_global_lock_depth_() ? u64(0) : u64(0x8000))

        #define REX_ENTER_GLOBAL_LOCK()                          \
          do {                                                   \
            rex::thread::global_critical_region::mutex().lock(); \
            ++ppc_global_lock_depth_();                          \
          } while (0)

        #define REX_LEAVE_GLOBAL_LOCK()                                                    \
          do {                                                                             \
            assert(ppc_global_lock_depth_() >= 1 &&                                        \
                   "LeaveGlobalLock called without matching EnterGlobalLock");             \
            --ppc_global_lock_depth_();                                                    \
            rex::thread::global_critical_region::mutex().unlock();                         \
          } while (0)

        """;

    /// <summary>
    /// Upgrades an older generator's <c>refii_pch.h</c> process-wide interrupt lock to thread-local depth.
    /// </summary>
    /// <param name="header">Fresh generator output for <c>refii_pch.h</c>.</param>
    /// <returns>The header with the process-wide block replaced once, or unchanged when absent.</returns>
    public static string ThreadLocalLockDepth(string header)
    {
        string old = ProcessWideGuestLock.ReplaceLineEndings("\n");
        int at = header.IndexOf(old, StringComparison.Ordinal);
        if (at < 0 || header.IndexOf(old, at + 1, StringComparison.Ordinal) >= 0)
        {
            return header;
        }

        return header.Remove(at, old.Length).Insert(at, ThreadLocalGuestLock.ReplaceLineEndings("\n"));
    }

    private static readonly Regex SetFunctionLinePattern = new(
        @"\Aregistrar->SetFunction\((?<addr>0x[0-9a-fA-F]{1,8}),\s*(?<sym>[A-Za-z_][A-Za-z0-9_]*)\);\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FuncMappingEntryPattern = new(
        @"\{\s*(?<addr>0x[0-9a-fA-F]{1,8})\s*,\s*(?<sym>[A-Za-z_][A-Za-z0-9_]*)\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsTableDrivenRegistrationBody(string body)
    {
        string normalized = Regex.Replace(body.Trim(), @"\s+", " ");
        return normalized ==
               "for (const PPCFuncMapping* m = PPCFuncMappings; m->guest != 0 || m->host != nullptr; ++m) { if (m->host != nullptr) { registrar->SetFunction(static_cast<uint32_t>(m->guest), m->host); } }" ||
               normalized ==
               "for (const PPCFuncMapping* m = PPCFuncMappings; m->guest != 0; ++m) { if (m->host != nullptr) { registrar->SetFunction(static_cast<uint32_t>(m->guest), m->host); } }";
    }

    private static bool TryMatchFuncMappings(IReadOnlyList<(ulong Address, string Symbol)> calls, string initSource)
    {
        int tableIndex = initSource.IndexOf("PPCFuncMapping PPCFuncMappings[]", StringComparison.Ordinal);
        if (tableIndex < 0)
        {
            return false;
        }

        int openBrace = initSource.IndexOf('{', tableIndex);
        int endTable = initSource.IndexOf("};", openBrace >= 0 ? openBrace : tableIndex, StringComparison.Ordinal);
        if (openBrace < 0 || endTable <= openBrace)
        {
            return false;
        }

        string tableBody = initSource[(openBrace + 1)..endTable];
        MatchCollection matches = FuncMappingEntryPattern.Matches(tableBody);
        int count = matches.Count;
        if (count > 0)
        {
            Match last = matches[count - 1];
            if (last.Groups["sym"].Value == "nullptr")
            {
                count--;
            }
        }

        if (count != calls.Count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            Match m = matches[i];
            if (m.Groups["sym"].Value != calls[i].Symbol)
            {
                return false;
            }

            if (!ulong.TryParse(
                    m.Groups["addr"].Value.AsSpan(2),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out ulong mappedAddr) ||
                mappedAddr != calls[i].Address)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rewrites generated function registration into a compact loop over <c>PPCFuncMappings[]</c> (skipping
    /// the 130k-declaration <c>_funcs.h</c> header) when <paramref name="initSource"/> proves an exact
    /// 1-to-1 <c>(address, symbol)</c> match, or falls back to <see cref="BoundRegistration"/> otherwise.
    /// </summary>
    /// <param name="registerSource">Fresh generator output for <c>{prefix}_register.cpp</c>.</param>
    /// <param name="initSource">Optional sibling <c>{prefix}_init.cpp</c> defining <c>PPCFuncMappings[]</c>.</param>
    /// <param name="functionName">Generated registration function name (defaults to <c>refii_RegisterFunctions</c>).</param>
    /// <returns>Table-driven registration TU when verified against <c>PPCFuncMappings</c>, or bounded helper chunks.</returns>
    public static string OptimizeRegistration(
        string registerSource,
        string? initSource,
        string functionName = "refii_RegisterFunctions")
    {
        var match = Regex.Match(
            registerSource,
            @"\A(?<head>.*?)void " + Regex.Escape(functionName) + @"\(rex::runtime::IModuleRegistrar\* registrar\)\s*\{(?<body>.*?)\}\s*\z",
            RegexOptions.Singleline);
        if (!match.Success)
        {
            throw new InvalidDataException("Unexpected generated registration function");
        }

        string body = match.Groups["body"].Value;
        if (IsTableDrivenRegistrationBody(body))
        {
            return registerSource;
        }

        string[] lines = body.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length != 0)
            .ToArray();

        var parsedCalls = new List<(ulong Address, string Symbol)>(lines.Length);
        foreach (string line in lines)
        {
            Match callMatch = SetFunctionLinePattern.Match(line);
            if (!callMatch.Success ||
                !ulong.TryParse(
                    callMatch.Groups["addr"].Value.AsSpan(2),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out ulong addr))
            {
                throw new InvalidDataException("Unexpected generated registration statement");
            }

            parsedCalls.Add((addr, callMatch.Groups["sym"].Value));
        }

        if (parsedCalls.Count > 0 &&
            !string.IsNullOrEmpty(initSource) &&
            TryMatchFuncMappings(parsedCalls, initSource))
        {
            string prefix = functionName.EndsWith("_RegisterFunctions", StringComparison.Ordinal) &&
                            functionName.Length > "_RegisterFunctions".Length
                ? functionName[..^"_RegisterFunctions".Length]
                : "refii";

            return
                "//=============================================================================\n" +
                "// ReXGlue Generated - " + prefix + " Function Registration\n" +
                "//=============================================================================\n\n" +
                "#include \"" + prefix + "_pch.h\"\n" +
                "#include <rex/system/function_dispatcher.h>\n\n" +
                "void " + functionName + "(rex::runtime::IModuleRegistrar* registrar) {\n" +
                "  for (const PPCFuncMapping* m = PPCFuncMappings; m->guest != 0 || m->host != nullptr; ++m) {\n" +
                "    if (m->host != nullptr) {\n" +
                "      registrar->SetFunction(static_cast<uint32_t>(m->guest), m->host);\n" +
                "    }\n" +
                "  }\n" +
                "}\n";
        }

        return BoundRegistration(registerSource, functionName);
    }

    /// <summary>
    /// Bounds LLVM <c>-O2</c> optimizer work on generated function registration by splitting tens of thousands
    /// of <c>SetFunction</c> virtual calls into ordered <c>noinline</c> 512-call helpers without altering call order.
    /// </summary>
    /// <param name="source">Fresh generator output containing only the registration function.</param>
    /// <param name="functionName">Generated registration function name (defaults to <c>refii_RegisterFunctions</c>).</param>
    /// <returns>Equivalent ordered <c>noinline</c> helper chunks, or the unchanged small/table-driven function.</returns>
    public static string BoundRegistration(string source, string functionName = "refii_RegisterFunctions")
    {
        var match = Regex.Match(
            source,
            @"\A(?<head>.*?)void " + Regex.Escape(functionName) + @"\(rex::runtime::IModuleRegistrar\* registrar\)\s*\{(?<body>.*?)\}\s*\z",
            RegexOptions.Singleline);
        if (!match.Success)
        {
            throw new InvalidDataException("Unexpected generated registration function");
        }

        if (IsTableDrivenRegistrationBody(match.Groups["body"].Value))
        {
            return source;
        }

        string[] calls = match.Groups["body"].Value.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length != 0)
            .ToArray();
        foreach (string call in calls)
        {
            if (!Regex.IsMatch(call, @"\Aregistrar->SetFunction\(0x[0-9a-fA-F]{8},\s*[A-Za-z_][A-Za-z0-9_]*\);\z"))
            {
                throw new InvalidDataException("Unexpected generated registration statement");
            }
        }

        const int perPart = 512;
        if (calls.Length <= perPart)
        {
            return source;
        }

        var text = new StringBuilder(match.Groups["head"].Value);
        int parts = (calls.Length + perPart - 1) / perPart;
        for (int part = 0; part < parts; part++)
        {
            text.Append("static __attribute__((noinline)) void ")
                .Append(functionName)
                .Append("_PhonePart_")
                .Append(part)
                .Append("(rex::runtime::IModuleRegistrar* registrar) {\n");
            for (int index = part * perPart; index < Math.Min(calls.Length, (part + 1) * perPart); index++)
            {
                text.Append("  ").Append(calls[index]).Append('\n');
            }

            text.Append("}\n\n");
        }

        text.Append("void ").Append(functionName).Append("(rex::runtime::IModuleRegistrar* registrar) {\n");
        for (int part = 0; part < parts; part++)
        {
            text.Append("  ").Append(functionName).Append("_PhonePart_").Append(part).Append("(registrar);\n");
        }

        return text.Append("}\n").ToString();
    }

    /// <summary>
    /// Reads and validates literal generated C++ translation unit paths from <c>sources.cmake</c>,
    /// ensuring every listed file exists inside <paramref name="root"/> without executing CMake.
    /// </summary>
    /// <param name="list">Path to the generator's <c>sources.cmake</c> file.</param>
    /// <param name="root">Expected generated source directory containing all listed units.</param>
    /// <returns>Validated canonical paths to all unique generated C++ translation units.</returns>
    public static string[] ReadGeneratedSources(string list, string root)
    {
        var block = Regex.Match(File.ReadAllText(list), @"set\(GENERATED_SOURCES\s+([^)]*)\)", RegexOptions.Singleline);
        if (!block.Success)
        {
            throw new InvalidDataException("Generator produced no source list");
        }

        string[] sources = block.Groups[1].Value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                int comment = line.IndexOf('#');
                string clean = comment >= 0 ? line[..comment] : line;
                return clean.Trim().Trim('"').Replace("${CMAKE_CURRENT_LIST_DIR}", root, StringComparison.Ordinal);
            })
            .Where(line => line.Length > 0)
            .ToArray();
        if (sources.Length == 0 || sources.Distinct(StringComparer.Ordinal).Count() != sources.Length)
        {
            throw new InvalidDataException("Missing or duplicate generated sources");
        }

        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (string source in sources)
        {
            if (!source.EndsWith(".cpp", StringComparison.Ordinal) ||
                !File.Exists(source) ||
                !Path.GetFullPath(source).StartsWith(fullRoot, comparison))
            {
                throw new InvalidDataException("Generated source escapes its private root: " + source);
            }
        }

        return sources.Select(Path.GetFullPath).ToArray();
    }

    /// <summary>
    /// Runs code generation seeded with the previous build's <c>codegen.partition.json</c> placement when available,
    /// retrying once cleanly without the seed if the generator rejects it, and atomically saving the new placement.
    /// </summary>
    /// <param name="cacheDirectory">Persistent cache directory storing <see cref="PartitionFile"/>, or null to disable seeding.</param>
    /// <param name="generatedDirectory">Output directory where the generator reads and writes <see cref="PartitionFile"/>.</param>
    /// <param name="generate">Callback that invokes the generator.</param>
    /// <param name="prepareBeforeRetry">Optional callback run after clearing a rejected seed before the clean retry.</param>
    public static async Task RunWithPartitionSeedAsync(
        string? cacheDirectory,
        string generatedDirectory,
        Func<Task> generate,
        Action? prepareBeforeRetry = null)
    {
        string? kept = string.IsNullOrEmpty(cacheDirectory) ? null : Path.Combine(cacheDirectory, PartitionFile);
        if (kept != null && File.Exists(kept))
        {
            Directory.CreateDirectory(generatedDirectory);
            File.Copy(kept, Path.Combine(generatedDirectory, PartitionFile), overwrite: true);
            try
            {
                await generate().ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                File.Delete(kept);
                if (Directory.Exists(generatedDirectory))
                {
                    Directory.Delete(generatedDirectory, recursive: true);
                }

                prepareBeforeRetry?.Invoke();
                await generate().ConfigureAwait(false);
            }
        }
        else
        {
            await generate().ConfigureAwait(false);
        }

        string made = Path.Combine(generatedDirectory, PartitionFile);
        if (kept == null || !File.Exists(made))
        {
            return;
        }

        Directory.CreateDirectory(cacheDirectory!);
        string temp = kept + ".tmp";
        File.Copy(made, temp, overwrite: true);
        File.Move(temp, kept, overwrite: true);
    }
}
