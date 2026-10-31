// android_dotnet_gc: .NET GC settings for the Android NativeAOT library, from refii.toml, set as
// DOTNET_ environment variables before the runtime reads them.
//
// WHY HERE. The GC reads its configuration once, when the runtime starts, and on Android nothing
// can give an app's process an environment (no shell, no wrap script on a release build); a
// runtimeconfig is compiled into the library at publish, so trying a gen0 budget meant a rebuild.
// A NativeAOT shared library starts its runtime lazily, at the first call into it, while a C
// constructor in the same library runs when Godot dlopens it, before that call. So the
// constructor reads the app's own refii.toml and setenv()s what it names, and the runtime finds it
// exactly as it would have found a variable set in a desktop shell (RhConfig reads DOTNET_<name>
// before the compiled-in settings). The managed side cannot do this: by the time any C# runs,
// the GC has been configured.
//
// THE KEY. `android_dotnet_gc = "GCgen0size=2000000 gcConcurrent=0"`: space-separated NAME=VALUE
// pairs, each set as DOTNET_NAME. Values are the runtime's own spelling: hexadecimal digits, no
// 0x (2000000 is 32 MB). Only names that begin with "GC" or "gc" are taken, so the key reaches
// the GC and nothing else; an empty or absent key sets nothing. A variable already set wins.
// What was set is left in REFII_DOTNET_GC_APPLIED for the debug channel's metrics.gc.config.
//
// SAFE BY CONSTRUCTION. No allocation, fixed buffers, every failure returns having done
// nothing, and the file is read once. The path is the one the launcher writes:
// /data/data/<package>/files/runtime/refii.toml, the package from /proc/self/cmdline.

#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define REFII_KEY "android_dotnet_gc"

static char refii_text[65536];

static int refii_read_file(const char *path, char *into, size_t capacity)
{
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0)
    {
        return -1;
    }

    size_t used = 0;
    while (used + 1 < capacity)
    {
        ssize_t got = read(fd, into + used, capacity - 1 - used);
        if (got <= 0)
        {
            break;
        }

        used += (size_t)got;
    }

    close(fd);
    into[used] = '\0';
    return (int)used;
}

static int refii_is_name_char(char c)
{
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
}

static int refii_is_hex(char c)
{
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

// One NAME=VALUE token: set DOTNET_NAME=VALUE if both are well formed. Appends to `applied`.
static void refii_apply(const char *token, size_t length, char *applied, size_t appliedCapacity)
{
    char name[64];
    char value[20];
    size_t eq = 0;
    while (eq < length && token[eq] != '=')
    {
        ++eq;
    }

    if (eq < 3 || eq >= length || eq >= sizeof(name) || length - eq - 1 == 0 ||
        length - eq - 1 >= 16 || !(token[0] == 'G' || token[0] == 'g') || !(token[1] == 'C' || token[1] == 'c'))
    {
        return;
    }

    for (size_t i = 0; i < eq; ++i)
    {
        if (!refii_is_name_char(token[i]))
        {
            return;
        }

        name[i] = token[i];
    }

    name[eq] = '\0';
    size_t valueLength = length - eq - 1;
    for (size_t i = 0; i < valueLength; ++i)
    {
        if (!refii_is_hex(token[eq + 1 + i]))
        {
            return;
        }

        value[i] = token[eq + 1 + i];
    }

    value[valueLength] = '\0';

    char variable[80];
    snprintf(variable, sizeof(variable), "DOTNET_%s", name);
    if (getenv(variable) != NULL)
    {
        return;
    }

    if (setenv(variable, value, 0) == 0)
    {
        size_t used = strlen(applied);
        snprintf(applied + used, appliedCapacity - used, "%s%s=%s", used == 0 ? "" : " ", name, value);
    }
}

// Finds the key's line in the TOML text (edited in place) and applies its pairs.
static void refii_scan(char *text)
{
    const size_t keyLength = strlen(REFII_KEY);
    char *line = text;
    while (line != NULL && *line != '\0')
    {
        char *next = strchr(line, '\n');
        if (next != NULL)
        {
            *next = '\0';
            ++next;
        }

        char *p = line;
        while (*p == ' ' || *p == '\t')
        {
            ++p;
        }

        if (strncmp(p, REFII_KEY, keyLength) == 0 && !refii_is_name_char(p[keyLength]))
        {
            p += keyLength;
            while (*p == ' ' || *p == '\t')
            {
                ++p;
            }

            if (*p != '=')
            {
                line = next;
                continue;
            }

            ++p;
            while (*p == ' ' || *p == '\t' || *p == '"' || *p == '\'')
            {
                ++p;
            }

            char applied[256];
            applied[0] = '\0';
            while (*p != '\0' && *p != '"' && *p != '\'' && *p != '#' && *p != '\r')
            {
                while (*p == ' ' || *p == '\t' || *p == ',' || *p == ';')
                {
                    ++p;
                }

                const char *start = p;
                while (*p != '\0' && *p != ' ' && *p != '\t' && *p != ',' && *p != ';' &&
                    *p != '"' && *p != '\'' && *p != '#' && *p != '\r')
                {
                    ++p;
                }

                if (p > start)
                {
                    refii_apply(start, (size_t)(p - start), applied, sizeof(applied));
                }
            }

            if (applied[0] != '\0')
            {
                setenv("REFII_DOTNET_GC_APPLIED", applied, 1);
            }

            return;
        }

        line = next;
    }
}

// Priority 101: before any other constructor in the library, though the runtime itself only
// starts later, at the first call in.
__attribute__((constructor(101))) static void refii_gc_preinit(void)
{
    char package[256];
    int got = refii_read_file("/proc/self/cmdline", package, sizeof(package));
    if (got <= 0)
    {
        return;
    }

    // The first NUL-terminated argument; a service process carries ":name" after the package.
    char *colon = strchr(package, ':');
    if (colon != NULL)
    {
        *colon = '\0';
    }

    if (package[0] == '\0' || strchr(package, '/') != NULL)
    {
        return;
    }

    char path[512];
    snprintf(path, sizeof(path), "/data/data/%s/files/runtime/refii.toml", package);
    if (refii_read_file(path, refii_text, sizeof(refii_text)) <= 0)
    {
        return;
    }

    refii_scan(refii_text);
}
