# Sonic Unleashed Renderer Reference

This folder is a direct reference snapshot from:

`/home/loreaxe/SonicUnleshed/UnleashedRecomp/UnleashedRecomp/gpu`

It is intentionally not compiled by Refii yet. Sonic's renderer is wired into
their app, config, UI, shader cache, generated resources, and hook system, so
keeping it as reference code gives us the reusable Plume/RHI implementation
without breaking the Refii build while we port it piece by piece.

Useful starting points:

- `gpu/video.h`: guest resource layouts, D3D format enums, and Plume-facing
  resource wrappers.
- `gpu/video.cpp`: Plume device setup, resource creation, texture/buffer locks,
  draw dispatch, render target state, shader setup, and the Sonic hook list.
- `gpu/cache/*.h`: pipeline state, vertex declaration, and vertex element cache
  helpers.
- `gpu/shader/*`: HLSL sources for copy, resolve, ImGui, movie, blur, and gamma
  passes.
- `gpu/imgui/*`: Sonic's Plume-backed ImGui rendering helpers.

Sonic CMake pieces worth porting when we start compiling adapted renderer code:

```cmake
set(UNLEASHED_RECOMP_GPU_CXX_SOURCES
    "gpu/video.cpp"
    "gpu/imgui/imgui_common.cpp"
    "gpu/imgui/imgui_font_builder.cpp"
    "gpu/imgui/imgui_snapshot.cpp"
)
```

```cmake
target_link_libraries(UnleashedRecomp PRIVATE
    plume
)
```

```cmake
function(compile_shader FILE_PATH TARGET_NAME)
    set(FILE_PATH ${CMAKE_CURRENT_SOURCE_DIR}/gpu/shader/${FILE_PATH}.hlsl)
    cmake_path(GET FILE_PATH STEM VARIABLE_NAME)
    add_custom_command(
        OUTPUT ${FILE_PATH}.spirv.h
        COMMAND ${DIRECTX_DXC_TOOL} -T ${TARGET_NAME} -HV 2021
            -all-resources-bound -spirv -fvk-use-dx-layout ${ARGN}
            -Fh ${FILE_PATH}.spirv.h ${FILE_PATH}
            -Vn g_${VARIABLE_NAME}_spirv
        DEPENDS ${FILE_PATH}
    )
    target_sources(UnleashedRecomp PRIVATE ${FILE_PATH}.spirv.h)
endfunction()
```

The copied source is kept with Sonic's upstream `COPYING` file in this directory.
