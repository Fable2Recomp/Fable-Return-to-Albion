include_guard(GLOBAL)

function(refii_ensure_windows_vcpkg_packages)
    if(NOT WIN32)
        return()
    endif()

    if(NOT REFII_RENDERER_PLUME_D3D12_AGILITY_SDK_ENABLED)
        return()
    endif()

    set(_refii_vcpkg_local_root "${CMAKE_SOURCE_DIR}/tools/vcpkg")
    if(EXISTS "${_refii_vcpkg_local_root}/vcpkg.exe" OR
       NOT (DEFINED ENV{VCPKG_ROOT} AND EXISTS "$ENV{VCPKG_ROOT}/vcpkg.exe"
            AND IS_DIRECTORY "$ENV{VCPKG_ROOT}/ports"))
        set(_refii_vcpkg_default_root "${_refii_vcpkg_local_root}")
    else()
        set(_refii_vcpkg_default_root "$ENV{VCPKG_ROOT}")
    endif()

    set(REFII_VCPKG_ROOT
        "${_refii_vcpkg_default_root}"
        CACHE PATH
        "Path to vcpkg for Windows-only third-party packages"
    )
    if(NOT EXISTS "${REFII_VCPKG_ROOT}/vcpkg.exe" AND
       EXISTS "${_refii_vcpkg_local_root}/vcpkg.exe")
        set(REFII_VCPKG_ROOT
            "${_refii_vcpkg_local_root}"
            CACHE PATH
            "Path to vcpkg for Windows-only third-party packages"
            FORCE
        )
    endif()
    if(EXISTS "${REFII_VCPKG_ROOT}/vcpkg.exe" AND
       NOT IS_DIRECTORY "${REFII_VCPKG_ROOT}/ports")
        set(REFII_VCPKG_ROOT
            "${_refii_vcpkg_local_root}"
            CACHE PATH
            "Path to vcpkg for Windows-only third-party packages"
            FORCE
        )
    endif()
    set(REFII_VCPKG_TRIPLET
        "x64-windows"
        CACHE STRING
        "vcpkg triplet for Windows-only third-party packages"
    )

    set(_refii_vcpkg_exe "${REFII_VCPKG_ROOT}/vcpkg.exe")
    if(NOT EXISTS "${_refii_vcpkg_exe}")
        if(NOT EXISTS "${REFII_VCPKG_ROOT}/.git")
            find_package(Git REQUIRED)
            get_filename_component(_refii_vcpkg_parent "${REFII_VCPKG_ROOT}" DIRECTORY)
            file(MAKE_DIRECTORY "${_refii_vcpkg_parent}")
            message(STATUS "vcpkg not found; cloning into ${REFII_VCPKG_ROOT}")
            execute_process(
                COMMAND "${GIT_EXECUTABLE}" clone https://github.com/microsoft/vcpkg.git "${REFII_VCPKG_ROOT}"
                RESULT_VARIABLE _refii_vcpkg_clone_result
            )
            if(NOT _refii_vcpkg_clone_result EQUAL 0)
                message(FATAL_ERROR "Failed to clone vcpkg into ${REFII_VCPKG_ROOT}")
            endif()
        endif()

        if(EXISTS "${REFII_VCPKG_ROOT}/bootstrap-vcpkg.bat")
            message(STATUS "Bootstrapping vcpkg at ${REFII_VCPKG_ROOT}")
            execute_process(
                COMMAND "${REFII_VCPKG_ROOT}/bootstrap-vcpkg.bat" -disableMetrics
                WORKING_DIRECTORY "${REFII_VCPKG_ROOT}"
                RESULT_VARIABLE _refii_vcpkg_bootstrap_result
            )
            if(NOT _refii_vcpkg_bootstrap_result EQUAL 0)
                message(FATAL_ERROR "Failed to bootstrap vcpkg at ${REFII_VCPKG_ROOT}")
            endif()
        endif()
    endif()

    if(NOT EXISTS "${_refii_vcpkg_exe}")
        message(FATAL_ERROR "vcpkg executable was not found at ${_refii_vcpkg_exe}")
    endif()

    set(_refii_vcpkg_installed_prefix
        "${REFII_VCPKG_ROOT}/installed/${REFII_VCPKG_TRIPLET}"
    )
    set(REFII_D3D12_AGILITY_CORE_DLL
        "${_refii_vcpkg_installed_prefix}/bin/D3D12Core.dll"
        CACHE FILEPATH
        "Release D3D12 Agility SDK core DLL"
        FORCE
    )
    set(REFII_D3D12_AGILITY_CORE_DEBUG_DLL
        "${_refii_vcpkg_installed_prefix}/debug/bin/D3D12Core.dll"
        CACHE FILEPATH
        "Debug D3D12 Agility SDK core DLL"
        FORCE
    )
    set(REFII_D3D12_AGILITY_LAYERS_DLL
        "${_refii_vcpkg_installed_prefix}/debug/bin/d3d12SDKLayers.dll"
        CACHE FILEPATH
        "D3D12 Agility SDK debug layers DLL"
        FORCE
    )
    set(_refii_vcpkg_directx_headers_config
        "${_refii_vcpkg_installed_prefix}/share/directx-headers/directx-headers-config.cmake"
    )
    set(_refii_vcpkg_directx12_agility_config
        "${_refii_vcpkg_installed_prefix}/share/directx12-agility/directx12-agility-config.cmake"
    )

    if(NOT EXISTS "${_refii_vcpkg_directx_headers_config}" OR
       NOT EXISTS "${_refii_vcpkg_directx12_agility_config}")
        message(STATUS "Installing Windows DirectX packages with vcpkg")
        execute_process(
            COMMAND "${CMAKE_COMMAND}" -E env
                    "VCPKG_ROOT=${REFII_VCPKG_ROOT}"
                    "${_refii_vcpkg_exe}"
                    install
                    "directx-headers:${REFII_VCPKG_TRIPLET}"
                    "directx12-agility:${REFII_VCPKG_TRIPLET}"
            WORKING_DIRECTORY "${REFII_VCPKG_ROOT}"
            RESULT_VARIABLE _refii_vcpkg_install_result
        )
        if(NOT _refii_vcpkg_install_result EQUAL 0)
            message(FATAL_ERROR "Failed to install DirectX packages with vcpkg")
        endif()
    endif()

    list(PREPEND CMAKE_PREFIX_PATH "${_refii_vcpkg_installed_prefix}")
    set(CMAKE_PREFIX_PATH "${CMAKE_PREFIX_PATH}" PARENT_SCOPE)

    unset(_refii_vcpkg_default_root)
    unset(_refii_vcpkg_local_root)
    unset(_refii_vcpkg_exe)
    unset(_refii_vcpkg_parent)
    unset(_refii_vcpkg_installed_prefix)
    unset(_refii_vcpkg_directx_headers_config)
    unset(_refii_vcpkg_directx12_agility_config)
endfunction()
