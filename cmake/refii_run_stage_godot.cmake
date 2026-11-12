# Stages the native runtime closure into Godot/build/<arch>/bin in a single CMake script pass.
#
# Invoked from refii_stage_godot's stamped custom command via:
#   cmake -DSTAGE_MANIFEST=<path> -DSOURCE_ROOT=<repo> -P cmake/refii_run_stage_godot.cmake

cmake_minimum_required(VERSION 3.24)

if(NOT STAGE_MANIFEST OR NOT EXISTS "${STAGE_MANIFEST}")
    message(FATAL_ERROR "refii_run_stage_godot: STAGE_MANIFEST does not exist: '${STAGE_MANIFEST}'")
endif()

include("${STAGE_MANIFEST}")

if(NOT REFII_STAGE_BIN_DIR)
    message(FATAL_ERROR "refii_run_stage_godot: REFII_STAGE_BIN_DIR not defined in manifest")
endif()

file(MAKE_DIRECTORY "${REFII_STAGE_BIN_DIR}")

# Prune other configurations' copies of configuration-postfixed SDK libraries (rexruntime, TracyClient).
foreach(_pair IN LISTS REFII_STAGE_PRUNE_PAIRS)
    string(FIND "${_pair}" "=" _eq)
    if(_eq GREATER 0)
        string(SUBSTRING "${_pair}" 0 ${_eq} _stem)
        math(EXPR _val_start "${_eq} + 1")
        string(SUBSTRING "${_pair}" ${_val_start} -1 _keep)
        set(BIN "${REFII_STAGE_BIN_DIR}")
        set(STEM "${_stem}")
        set(KEEP "${_keep}")
        include("${SOURCE_ROOT}/cmake/refii_prune_stale_configs.cmake")
    endif()
endforeach()

# Stage per-title instance libraries into bin/<Folder>/ and remove any pre-split flat copy in bin/.
foreach(_entry IN LISTS REFII_STAGE_TITLE_FILES)
    string(FIND "${_entry}" "=" _eq)
    if(_eq GREATER 0)
        string(SUBSTRING "${_entry}" 0 ${_eq} _folder)
        math(EXPR _src_start "${_eq} + 1")
        string(SUBSTRING "${_entry}" ${_src_start} -1 _src_file)
        if(EXISTS "${_src_file}")
            get_filename_component(_fname "${_src_file}" NAME)
            set(_flat_legacy "${REFII_STAGE_BIN_DIR}/${_fname}")
            if(EXISTS "${_flat_legacy}")
                file(REMOVE "${_flat_legacy}")
            endif()
            set(_title_dir "${REFII_STAGE_BIN_DIR}/${_folder}")
            file(MAKE_DIRECTORY "${_title_dir}")
            file(COPY_FILE "${_src_file}" "${_title_dir}/${_fname}" ONLY_IF_DIFFERENT)
        endif()
    endif()
endforeach()

# Stage shared native libraries and DXC libraries into bin/.
foreach(_src_file IN LISTS REFII_STAGE_SHARED_FILES REFII_STAGE_SHADER_LIB_FILES)
    if(EXISTS "${_src_file}")
        get_filename_component(_fname "${_src_file}" NAME)
        file(COPY_FILE "${_src_file}" "${REFII_STAGE_BIN_DIR}/${_fname}" ONLY_IF_DIFFERENT)
    endif()
endforeach()

# Stage DXC compiler executable and shader_common.h into bin/dxc/.
if(REFII_STAGE_SHADER_TOOL_FILES)
    file(MAKE_DIRECTORY "${REFII_STAGE_BIN_DIR}/dxc")
    foreach(_src_file IN LISTS REFII_STAGE_SHADER_TOOL_FILES)
        if(EXISTS "${_src_file}")
            get_filename_component(_fname "${_src_file}" NAME)
            file(COPY_FILE "${_src_file}" "${REFII_STAGE_BIN_DIR}/dxc/${_fname}" ONLY_IF_DIFFERENT)
        endif()
    endforeach()
endif()
