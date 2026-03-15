#pragma once

#ifndef __gl_h_
#include "glad/glad.h"
#endif

#include "imgui.h"
#include "imgui_impl_glfw.h"
#include "imgui_impl_opengl3.h"

#include <GLFW/glfw3.h>

#include <memory>
#include <string>

class VinceWindow {
public:
    VinceWindow(int width, int height, const char* title);

    void SetupImGuiIO();
    void NewFrame();
    void EndFrame();

    GLFWwindow* getWindow() const {
        return window.get();
    }

    int width = 0;
    int height = 0;

private:
    void init();

    const char* glsl_version = "#version 330";
    std::string title;
    std::unique_ptr<GLFWwindow, void(*)(GLFWwindow*)> window;
};
