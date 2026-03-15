// refii - ReXGlue Recompiled Project
//
// Shared host entry points for standalone and embedded launches.

#include "refii_host.h"

#include "generated/refii_config.h"
#include "generated/refii_init.h"
#include "refii_app.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/ui/windowed_app.h>

#if defined(_WIN32)
#include <rex/ui/windowed_app_context_win.h>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <objbase.h>
#else
#include <X11/Xlib.h>
#include <gtk/gtk.h>
#include <rex/ui/windowed_app_context_gtk.h>
#endif

namespace {

std::vector<char*> BuildArgvPointers(std::vector<std::string>& ownedArguments) {
    std::vector<char*> argvPointers;
    argvPointers.reserve(ownedArguments.size());
    for (std::string& argument : ownedArguments) {
        argvPointers.push_back(argument.data());
    }
    return argvPointers;
}

void ApplyPositionalArguments(
    rex::ui::WindowedApp& app,
    const std::vector<std::string>& remainingArguments) {
    const auto& optionNames = app.GetPositionalOptions();
    std::map<std::string, std::string> parsedArguments;
    const std::size_t count = std::min(remainingArguments.size(), optionNames.size());
    for (std::size_t i = 0; i < count; ++i) {
        parsedArguments[optionNames[i]] = remainingArguments[i];
    }
    app.SetParsedArguments(std::move(parsedArguments));
}

#if !defined(_WIN32)
void InitializeX11Threading() {
    static std::once_flag x11ThreadInitFlag;
    std::call_once(x11ThreadInitFlag, []() {
        XInitThreads();
    });
}
#endif

#if defined(_WIN32)
int RunEmbeddedOnWindows(std::vector<std::string> ownedArguments) {
    std::vector<char*> argvPointers = BuildArgvPointers(ownedArguments);
    auto remaining = rex::cvar::Init(static_cast<int>(argvPointers.size()), argvPointers.data());
    rex::cvar::ApplyEnvironment();

    rex::ui::Win32WindowedAppContext appContext(GetModuleHandleW(nullptr), SW_SHOWDEFAULT);
    if (!appContext.Initialize()) {
        return EXIT_FAILURE;
    }

    std::unique_ptr<rex::ui::WindowedApp> app = CreateRefiiApp(appContext);
    ApplyPositionalArguments(*app, remaining);

    const HRESULT coInitializeResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(coInitializeResult)) {
        return EXIT_FAILURE;
    }

    const int result = app->OnInitialize() ? appContext.RunMainMessageLoop() : EXIT_FAILURE;

    app->InvokeOnDestroy();
    rex::ShutdownLogging();
    CoUninitialize();
    return result;
}
#else
int RunEmbeddedOnPosix(std::vector<std::string> ownedArguments) {
    setenv("GDK_BACKEND", "x11", 1);
    InitializeX11Threading();

    std::vector<char*> argvPointers = BuildArgvPointers(ownedArguments);
    int argcAfterGtk = static_cast<int>(argvPointers.size());
    char** argvAfterGtk = argvPointers.data();
    if (!gtk_init_check(&argcAfterGtk, &argvAfterGtk)) {
        std::fputs("Failed to initialize GTK+\n", stderr);
        return EXIT_FAILURE;
    }

    auto remaining = rex::cvar::Init(argcAfterGtk, argvAfterGtk);
    rex::cvar::ApplyEnvironment();

    rex::ui::GTKWindowedAppContext appContext;
    std::unique_ptr<rex::ui::WindowedApp> app = CreateRefiiApp(appContext);
    ApplyPositionalArguments(*app, remaining);

    const int result = app->OnInitialize() ? (appContext.RunMainGTKLoop(), EXIT_SUCCESS) : EXIT_FAILURE;

    app->InvokeOnDestroy();
    rex::ShutdownLogging();
    return result;
}
#endif

}  // namespace

std::unique_ptr<rex::ui::WindowedApp> CreateRefiiApp(rex::ui::WindowedAppContext& app_context) {
    return RefiiApp::Create(app_context);
}

int RunRefiiEmbedded(
    const std::filesystem::path& game_directory,
    const std::vector<std::string>& launch_arguments) {
    std::vector<std::string> ownedArguments;
    ownedArguments.reserve(launch_arguments.size() + 2);
    ownedArguments.emplace_back("refii");
    ownedArguments.insert(ownedArguments.end(), launch_arguments.begin(), launch_arguments.end());
    ownedArguments.push_back(game_directory.string());

#if defined(_WIN32)
    return RunEmbeddedOnWindows(std::move(ownedArguments));
#else
    return RunEmbeddedOnPosix(std::move(ownedArguments));
#endif
}
