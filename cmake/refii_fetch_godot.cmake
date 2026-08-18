# Bundles the Godot editor that takes the release export, and the export template it fuses
# with the PCK. Pinned and hash-verified, exactly as refii_fetch_zig.cmake does for Zig.
#
# THE EXPORT NEEDS AN EDITOR, not a runtime. Godot's export logic lives in the editor build,
# and 4.7.1's SConstruct offers exactly three targets -- editor, template_release,
# template_debug -- so there is no headless or server-only editor to prefer. `--headless`,
# which refii_godot_export.cmake already passes, is a RUNTIME FLAG on the ordinary editor
# binary. That is why this fetches the editor rather than something smaller: nothing smaller
# exists.
#
# FETCHED RATHER THAN COMMITTED, and the pin is the part that lives in git. The editor
# executable is 179 MB and windows_release_x86_64.exe is 104 MB; GitHub rejects any file over
# 100 MB, so committing the official builds is not a thing the remote will accept, and Git LFS
# would put ~900 MB against a 1 GB free quota. The version and the SHA-256 below are the
# dependency as far as this repository is concerned -- they are tracked, they are reviewed
# with everything else, and a build without the artefact fails loudly naming what it wanted.
# That is the opposite of Godot/export_presets.cfg, which was .gitignored and so went missing
# on a second machine with nothing recorded to say it was ever needed.
#
# NOT BUILT FROM SOURCE, deliberately. It is buildable -- but the .NET editor is a three-stage
# build (compile with module_mono_enabled=yes, run it to --generate-mono-glue, then
# build_assemblies.py) before templates are even started, and it would add SCons and a second
# Python build path to a tree that already carries CMake, Ninja, vcpkg and Zig.
#
# ONLY THE TEMPLATE THIS TREE EXPORTS is staged. The .tpz carries every platform Godot ships,
# 1.2 GB unpacked, and a build tree exports one platform and one architecture -- the natives
# are per-arch and cannot be cross-staged (see refii_godot_export.cmake). Taking one template
# out of the archive keeps tools/godot at roughly a tenth of it.
#
# Cached, so repeat packaging is offline.

cmake_minimum_required(VERSION 3.24)

set(REFII_GODOT_VERSION "4.7.1-stable")

# The .NET build, because Godot/refii-launcher.csproj is C# against Godot.NET.Sdk/4.7.1 and
# project.godot lists "C#" in its features. The non-.NET editor cannot export this project.
set(REFII_GODOT_FLAVOUR "mono")

# The directory name Godot looks for templates under, from the .tpz's own version.txt rather
# than assembled from the version string. It is NOT derivable by concatenation -- the flavour
# suffix and the release status are both in it -- and getting it wrong produces "no export
# template found" at the end of a long build instead of at configure time.
set(REFII_GODOT_TEMPLATE_DIR "4.7.1.stable.mono")

set(_REFII_GODOT_BASE_URL
    "https://github.com/godotengine/godot/releases/download/${REFII_GODOT_VERSION}")

# The editor archive for the HOST that runs the export, and its SHA-256.
#
# Only the hosts we actually package from are pinned. An unpinned host is a hard error naming
# REFII_GODOT_EXECUTABLE, because silently falling back to whatever Godot is on PATH is how a
# release gets exported by an editor nobody chose.
function(_refii_godot_editor_spec platform arch out_file out_sha)
    if(platform STREQUAL "windows" AND arch STREQUAL "x86_64")
        set(_f "Godot_v${REFII_GODOT_VERSION}_${REFII_GODOT_FLAVOUR}_win64.zip")
        set(_s "764a089809fb1a6f745686ce9f6d3ca83adce8fb60fb9a4e2324b63baaebaa45")
    elseif(platform STREQUAL "linux" AND arch STREQUAL "x86_64")
        set(_f "Godot_v${REFII_GODOT_VERSION}_${REFII_GODOT_FLAVOUR}_linux_x86_64.zip")
        set(_s "6ca7ff0459f1b806900be683c1b0837c607a9c16834c530dc68c81b9fc3ae1f6")
    else()
        message(FATAL_ERROR
            "No pinned Godot editor for host ${platform}-${arch}. Add one to "
            "cmake/refii_fetch_godot.cmake, or set REFII_GODOT_EXECUTABLE to a "
            "Godot ${REFII_GODOT_VERSION} .NET binary you have already.")
    endif()

    set(${out_file} "${_f}" PARENT_SCOPE)
    set(${out_sha} "${_s}" PARENT_SCOPE)
endfunction()

# One archive carries every platform's templates, so it is pinned once.
function(_refii_godot_templates_spec out_file out_sha)
    set(${out_file}
        "Godot_v${REFII_GODOT_VERSION}_${REFII_GODOT_FLAVOUR}_export_templates.tpz"
        PARENT_SCOPE)
    set(${out_sha}
        "ef9a708be51ecd974cd7dccdcafd7a1870da3d3e1c24c072bdbb9818c7a7db63"
        PARENT_SCOPE)
endfunction()

# The name the export TARGET platform's release template has inside the .tpz. Windows and
# Linux disagree about the separator before the architecture, which is why this is a table
# and not a format string.
function(_refii_godot_template_name platform arch out_name)
    if(platform STREQUAL "windows")
        set(_n "windows_release_${arch}.exe")
    elseif(platform STREQUAL "linux")
        set(_n "linux_release.${arch}")
    else()
        message(FATAL_ERROR "No export template mapping for ${platform}-${arch}")
    endif()
    set(${out_name} "${_n}" PARENT_SCOPE)
endfunction()

# Where the executable lands inside <dest>. Separate because the already-staged check needs it
# before anything has been extracted.
function(_refii_godot_binary_path platform dest out_binary)
    if(platform STREQUAL "windows")
        # THE CONSOLE BUILD. The plain .exe is a GUI-subsystem binary: run headless from a
        # build it still detaches from the console, so --export-release prints nothing and a
        # failed export is indistinguishable from a slow one. The console wrapper writes to
        # the terminal it was started from, which is what makes a packaging failure readable
        # in the build log. It is a wrapper -- it needs the GUI executable beside it, which
        # staging the whole tree already guarantees.
        file(GLOB _candidates "${dest}/Godot_v*_win64_console.exe")
    else()
        file(GLOB _candidates "${dest}/Godot_v*_linux.x86_64")
    endif()

    list(GET _candidates 0 _binary)
    if(NOT _binary)
        set(_binary "${dest}/godot-not-staged")
    endif()
    set(${out_binary} "${_binary}" PARENT_SCOPE)
endfunction()

# Downloads <file> into <cache_dir> unless a copy with the right hash is already there.
function(_refii_godot_download file sha cache_dir out_path)
    file(MAKE_DIRECTORY "${cache_dir}")
    set(_archive "${cache_dir}/${file}")

    if(EXISTS "${_archive}")
        file(SHA256 "${_archive}" _have)
        if(NOT _have STREQUAL sha)
            message(STATUS "Cached ${file} has the wrong hash; refetching")
            file(REMOVE "${_archive}")
        endif()
    endif()

    if(NOT EXISTS "${_archive}")
        message(STATUS "Downloading ${file}")
        file(DOWNLOAD
             "${_REFII_GODOT_BASE_URL}/${file}"
             "${_archive}"
             EXPECTED_HASH SHA256=${sha}
             SHOW_PROGRESS
             STATUS _status)
        list(GET _status 0 _code)
        if(NOT _code EQUAL 0)
            list(GET _status 1 _reason)
            file(REMOVE "${_archive}")
            message(FATAL_ERROR "Could not download ${file}: ${_reason}")
        endif()
    endif()

    set(${out_path} "${_archive}" PARENT_SCOPE)
endfunction()

# Stages the editor for <host_platform>-<host_arch> into <dest>, together with the release
# template for <target_platform>-<target_arch>, and reports the binary through <out_binary>.
function(refii_fetch_godot host_platform host_arch
                           target_platform target_arch
                           dest cache_dir out_binary)
    _refii_godot_editor_spec("${host_platform}" "${host_arch}" _editor_file _editor_sha)
    _refii_godot_templates_spec(_templates_file _templates_sha)
    _refii_godot_template_name("${target_platform}" "${target_arch}" _template_name)

    # The editor is staged whole and the template is added beside it, so the two steps are
    # not independent: re-extracting the editor would delete a template already placed. The
    # stamp records both pins, so a change to either re-does the whole thing.
    set(_stamp "${dest}/.refii-godot-stamp")
    set(_want "${REFII_GODOT_VERSION} ${_editor_sha} ${_templates_sha} ${_template_name}")
    if(EXISTS "${_stamp}")
        file(READ "${_stamp}" _have)
        string(STRIP "${_have}" _have)
        if(_have STREQUAL _want)
            _refii_godot_binary_path("${host_platform}" "${dest}" _binary)
            if(EXISTS "${_binary}")
                set(${out_binary} "${_binary}" PARENT_SCOPE)
                return()
            endif()
        endif()
    endif()

    _refii_godot_download("${_editor_file}" "${_editor_sha}" "${cache_dir}" _editor_archive)
    _refii_godot_download("${_templates_file}" "${_templates_sha}" "${cache_dir}"
                          _templates_archive)

    set(_work "${cache_dir}/extract-godot-${host_platform}-${host_arch}")
    file(REMOVE_RECURSE "${_work}")
    file(MAKE_DIRECTORY "${_work}")
    file(ARCHIVE_EXTRACT INPUT "${_editor_archive}" DESTINATION "${_work}")

    file(GLOB _root "${_work}/Godot_v*")
    if(NOT _root)
        message(FATAL_ERROR "Unexpected layout inside ${_editor_file}")
    endif()

    # THE WHOLE TREE, not just the executable. GodotSharp/ beside it carries the .NET API
    # assemblies the editor loads to build the C# project; an editor without it cannot open
    # a C# project at all, let alone export one.
    file(REMOVE_RECURSE "${dest}")
    file(MAKE_DIRECTORY "${dest}")
    file(COPY "${_root}/" DESTINATION "${dest}")

    _refii_godot_binary_path("${host_platform}" "${dest}" _binary)
    if(NOT EXISTS "${_binary}")
        message(FATAL_ERROR "Godot staged but ${_binary} is missing")
    endif()
    file(CHMOD "${_binary}"
         PERMISSIONS OWNER_READ OWNER_WRITE OWNER_EXECUTE
                     GROUP_READ GROUP_EXECUTE WORLD_READ WORLD_EXECUTE)

    # SELF-CONTAINED MODE. `._sc_` beside the executable makes Godot put its data directory
    # at <exe dir>/editor_data instead of %APPDATA%\Godot or ~/.local/share/godot
    # (editor/file_system/editor_paths.cpp:137). Without it the templates would have to be
    # installed into the machine's Godot profile, where they are invisible to this repository,
    # collide with any other Godot the developer has, and make the packaged output depend on
    # which machine ran it.
    file(WRITE "${dest}/._sc_" "")

    set(_template_dir "${dest}/editor_data/export_templates/${REFII_GODOT_TEMPLATE_DIR}")
    file(MAKE_DIRECTORY "${_template_dir}")

    set(_template_work "${cache_dir}/extract-godot-templates")
    file(REMOVE_RECURSE "${_template_work}")
    file(MAKE_DIRECTORY "${_template_work}")

    # The .tpz is a zip whatever the extension says, and CMake unpacks it by content. Its
    # entries all sit under templates/.
    file(ARCHIVE_EXTRACT INPUT "${_templates_archive}" DESTINATION "${_template_work}"
         PATTERNS "templates/${_template_name}" "templates/version.txt")

    if(NOT EXISTS "${_template_work}/templates/${_template_name}")
        message(FATAL_ERROR
            "${_templates_file} has no ${_template_name}; the export template naming for "
            "${target_platform}-${target_arch} has changed.")
    endif()

    file(COPY "${_template_work}/templates/${_template_name}"
         DESTINATION "${_template_dir}")
    file(COPY "${_template_work}/templates/version.txt" DESTINATION "${_template_dir}")
    file(CHMOD "${_template_dir}/${_template_name}"
         PERMISSIONS OWNER_READ OWNER_WRITE OWNER_EXECUTE
                     GROUP_READ GROUP_EXECUTE WORLD_READ WORLD_EXECUTE)

    file(REMOVE_RECURSE "${_work}" "${_template_work}")
    file(WRITE "${_stamp}" "${_want}\n")

    message(STATUS
        "Bundled Godot ${REFII_GODOT_VERSION} (${REFII_GODOT_FLAVOUR}) for "
        "${host_platform}-${host_arch}, with the ${target_platform}-${target_arch} "
        "release template")
    set(${out_binary} "${_binary}" PARENT_SCOPE)
endfunction()
