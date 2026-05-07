#include "background.h"
#include "utils/launcherpaths.h"

#include <algorithm>
#include <cctype>
#include <filesystem>

namespace {

bool HasImageExtension(const std::filesystem::path& path) {
    const std::string extension = path.extension().string();
    std::string lower;
    lower.resize(extension.size());
    std::transform(extension.begin(), extension.end(), lower.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });

    return lower == ".png" || lower == ".jpg" || lower == ".jpeg" || lower == ".bmp" ||
           lower == ".gif" || lower == ".webp";
}

bool UploadTextureFromImage(
    const std::filesystem::path& imagePath,
    GLuint textureId,
    int& width,
    int& height,
    int& channels) {
    unsigned char* imageData =
        stbi_load(imagePath.string().c_str(), &width, &height, &channels, 4);
    if (imageData == nullptr) {
        return false;
    }

    glBindTexture(GL_TEXTURE_2D, textureId);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glTexImage2D(
        GL_TEXTURE_2D,
        0,
        GL_RGBA,
        width,
        height,
        0,
        GL_RGBA,
        GL_UNSIGNED_BYTE,
        imageData);

    stbi_image_free(imageData);
    return true;
}

}  // namespace

BackgroundManager::BackgroundManager() {
    currentBgIndex = -1;
    nextBgIndex = -1;

    titleTexture = 0;
    titleLoaded = false;
    titleWidth = 0;
    titleHeight = 0;
}

BackgroundManager::~BackgroundManager() {
    Cleanup();
}

void BackgroundManager::Initialize() {
    if (isInitialized) return;

    DiscoverBackgroundFiles();
    LoadTitleTexture();
    LoadRandomBackgroundOnMainThread();
    StartBackgroundLoadingThread();

    isInitialized = true;
}

void BackgroundManager::DiscoverBackgroundFiles() {
    const std::filesystem::path backgroundRoot = LauncherPaths::GetLauncherAssetPath("backgrounds");
    if (!std::filesystem::exists(backgroundRoot) || !std::filesystem::is_directory(backgroundRoot)) {
        return;
    }

    backgroundPaths.clear();
    backgroundTextures.clear();
    backgroundsLoaded.clear();
    allBackgroundsLoaded = false;
    currentBgIndex = -1;
    nextBgIndex = -1;

    for (const auto& entry : std::filesystem::directory_iterator(backgroundRoot)) {
        if (entry.is_regular_file() && HasImageExtension(entry.path())) {
            backgroundPaths.push_back(entry.path());
        }
    }

    std::sort(backgroundPaths.begin(), backgroundPaths.end());
    backgroundTextures.resize(backgroundPaths.size(), 0);
    backgroundsLoaded.resize(backgroundPaths.size());
    for (auto& loaded : backgroundsLoaded) {
        loaded = false;
    }
}

void BackgroundManager::LoadRandomBackgroundOnMainThread() {
    if (backgroundPaths.empty()) {
        return;
    }

    std::random_device rd;
    std::mt19937 gen(rd());
    std::uniform_int_distribution<> dis(
        0,
        static_cast<int>(backgroundPaths.size()) - 1);

    const int startIndex = dis(gen);
    int selectedIndex = startIndex;
    bool loaded = false;
    int width = 0;
    int height = 0;
    int channels = 0;

    while (!loaded) {
        GLuint texture = 0;
        glGenTextures(1, &texture);
        if (texture == 0) {
            break;
        }

        if (UploadTextureFromImage(backgroundPaths[selectedIndex], texture, width, height, channels)) {
            backgroundTextures[selectedIndex] = texture;
            bgWidth = width;
            bgHeight = height;
            bgChannels = channels;
            backgroundsLoaded[selectedIndex] = 1u;
            loaded = true;
            currentBgIndex = selectedIndex;
            nextBgIndex = (selectedIndex + 1) % static_cast<int>(backgroundPaths.size());
        } else {
            glDeleteTextures(1, &texture);
            selectedIndex = (selectedIndex + 1) % static_cast<int>(backgroundPaths.size());
            if (selectedIndex == startIndex) {
                break;
            }
        }
    }
}

void BackgroundManager::StartBackgroundLoadingThread() {
    if (backgroundPaths.size() <= 1) {
        if (currentBgIndex >= 0) {
            allBackgroundsLoaded = true;
        }
        return;
    }

    backgroundLoader = std::thread([this]() {
        for (int i = 0; i < static_cast<int>(backgroundPaths.size()); i++) {
            if (i == currentBgIndex) continue;

            int localWidth = 0;
            int localHeight = 0;
            int localChannels = 0;
            unsigned char* imageData =
                stbi_load(backgroundPaths[i].string().c_str(), &localWidth, &localHeight, &localChannels, 4);

            if (imageData != nullptr) {
                std::lock_guard<std::mutex> lock(textureMutex);
                pendingTextures.push({ i, imageData, localWidth, localHeight, localChannels });
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
    });
    backgroundLoader.detach();
}

void BackgroundManager::LoadTitleTexture() {
    std::string titlePath = LauncherPaths::GetLauncherAssetPath("title.png").string();
    int channels = 0;
    unsigned char* imageData =
        stbi_load(titlePath.c_str(), &titleWidth, &titleHeight, &channels, 4);

    if (imageData != nullptr) {
        glGenTextures(1, &titleTexture);
        glBindTexture(GL_TEXTURE_2D, titleTexture);

        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        glTexImage2D(
            GL_TEXTURE_2D,
            0,
            GL_RGBA,
            titleWidth,
            titleHeight,
            0,
            GL_RGBA,
            GL_UNSIGNED_BYTE,
            imageData);
        stbi_image_free(imageData);
        titleLoaded = true;
    } else {
        titleLoaded = false;
    }
}

void BackgroundManager::ProcessPendingTextures() {
    std::lock_guard<std::mutex> lock(textureMutex);
    while (!pendingTextures.empty()) {
        PendingTexture pending = pendingTextures.front();
        pendingTextures.pop();

        if (pending.index < 0 || pending.index >= static_cast<int>(backgroundTextures.size())) {
            stbi_image_free(pending.imageData);
            continue;
        }

        GLuint texture = 0;
        glGenTextures(1, &texture);
        if (texture == 0) {
            stbi_image_free(pending.imageData);
            continue;
        }

        glBindTexture(GL_TEXTURE_2D, texture);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        glTexImage2D(
            GL_TEXTURE_2D,
            0,
            GL_RGBA,
            pending.width,
            pending.height,
            0,
            GL_RGBA,
            GL_UNSIGNED_BYTE,
            pending.imageData);
        stbi_image_free(pending.imageData);

        backgroundTextures[pending.index] = texture;
        backgroundsLoaded[pending.index] = 1u;
    }
}

void BackgroundManager::UpdateBackgroundTransition(float deltaTime) {
    if (backgroundPaths.size() <= 1) {
        if (currentBgIndex < 0) {
            return;
        }
        allBackgroundsLoaded = true;
        return;
    }

    if (!allBackgroundsLoaded) {
        allBackgroundsLoaded = (currentBgIndex >= 0);
    }

    if (!allBackgroundsLoaded) {
        return;
    }

    backgroundTimer += deltaTime;
    if (backgroundTimer < transitionDuration) {
        return;
    }

    backgroundTimer -= transitionDuration;
    currentBgIndex = nextBgIndex;

    int originalNext = nextBgIndex;
    do {
        nextBgIndex = (nextBgIndex + 1) % static_cast<int>(backgroundTextures.size());
    } while (!backgroundsLoaded[nextBgIndex] && nextBgIndex != originalNext);

    if (!backgroundsLoaded[nextBgIndex] && nextBgIndex == originalNext) {
        nextBgIndex = currentBgIndex;
    }
}

void BackgroundManager::Update(float deltaTime) {
    if (!isInitialized) return;

    ProcessPendingTextures();
    UpdateBackgroundTransition(deltaTime);
}

void BackgroundManager::Render(ImGuiViewport* viewport) {
    if (!isInitialized || backgroundPaths.empty()) return;

    ImGui::SetNextWindowPos(viewport->Pos);
    ImGui::SetNextWindowSize(viewport->Size);
    ImGui::SetNextWindowViewport(viewport->ID);
	ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0, 0, 0, 0));
	ImGui::PushStyleColor(ImGuiCol_ChildBg, ImVec4(0, 0, 0, 0));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowRounding, 0.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 0.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0.0f, 0.0f));

    ImGuiWindowFlags window_flags = ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoCollapse |
        ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove |
        ImGuiWindowFlags_NoBringToFrontOnFocus | ImGuiWindowFlags_NoNavFocus |
        ImGuiWindowFlags_NoBackground | ImGuiWindowFlags_NoDocking;

    ImGui::Begin("BackgroundImage", nullptr, window_flags);

    float fadeTime = backgroundTimer - (transitionDuration - fadeDuration);
    float alpha = 1.0f;
    bool inFade = false;

    if (fadeTime > 0.0f && fadeDuration > 0.0f) {
        alpha = 1.0f - (fadeTime / fadeDuration);
        if (alpha < 0.0f) alpha = 0.0f;
        if (alpha > 1.0f) alpha = 1.0f;
        inFade = true;
    }

    if (currentBgIndex >= 0 && currentBgIndex < static_cast<int>(backgroundsLoaded.size()) &&
        backgroundsLoaded[currentBgIndex] && backgroundTextures[currentBgIndex] != 0) {
        ImGui::Image((ImTextureID)(intptr_t)backgroundTextures[currentBgIndex], viewport->Size);

        if (backgroundsLoaded.size() > 1 && inFade &&
            nextBgIndex >= 0 &&
            nextBgIndex < static_cast<int>(backgroundsLoaded.size()) &&
            backgroundsLoaded[nextBgIndex] &&
            backgroundTextures[nextBgIndex] != 0) {
            ImGui::PushStyleVar(ImGuiStyleVar_Alpha, alpha);
            ImGui::SetCursorPos(ImVec2(0, 0));
            ImGui::Image((ImTextureID)(intptr_t)backgroundTextures[currentBgIndex], viewport->Size);
            ImGui::PopStyleVar();

            ImGui::PushStyleVar(ImGuiStyleVar_Alpha, 1.0f - alpha);
            ImGui::SetCursorPos(ImVec2(0, 0));
            ImGui::Image((ImTextureID)(intptr_t)backgroundTextures[nextBgIndex], viewport->Size);
            ImGui::PopStyleVar();
        }
    }

    if (titleLoaded && titleTexture != 0) {
        float titlePosPixelX = titlePosX * viewport->Size.x;
        float titlePosPixelY = titlePosY * viewport->Size.y;

        float scaledWidth = titleWidth * titleScale;
        float scaledHeight = titleHeight * titleScale;

        float titleX = titlePosPixelX - (scaledWidth * 0.5f);
        float titleY = titlePosPixelY - (scaledHeight * 0.5f);

        if (titleRotation != 0.0f) {
            ImDrawList* drawList = ImGui::GetWindowDrawList();
            ImVec2 center = ImVec2(titlePosPixelX + viewport->Pos.x, titlePosPixelY + viewport->Pos.y);

            float cos_a = cosf(titleRotation * 3.14159f / 180.0f);
            float sin_a = sinf(titleRotation * 3.14159f / 180.0f);

            ImVec2 corners[4] = {
                ImVec2(-scaledWidth * 0.5f, -scaledHeight * 0.5f),
                ImVec2(scaledWidth * 0.5f, -scaledHeight * 0.5f),
                ImVec2(scaledWidth * 0.5f, scaledHeight * 0.5f),
                ImVec2(-scaledWidth * 0.5f, scaledHeight * 0.5f)};

            for (int i = 0; i < 4; i++) {
                float x = corners[i].x * cos_a - corners[i].y * sin_a;
                float y = corners[i].x * sin_a + corners[i].y * cos_a;
                corners[i] = ImVec2(center.x + x, center.y + y);
            }

            ImVec2 uv0 = ImVec2(0.0f, 0.0f);
            ImVec2 uv1 = ImVec2(1.0f, 0.0f);
            ImVec2 uv2 = ImVec2(1.0f, 1.0f);
            ImVec2 uv3 = ImVec2(0.0f, 1.0f);

            drawList->AddImageQuad((ImTextureID)(intptr_t)titleTexture,
                                   corners[0], corners[1], corners[2], corners[3],
                                   uv0, uv1, uv2, uv3);
        } else {
            ImGui::SetCursorPos(ImVec2(titleX, titleY));
            ImGui::Image((ImTextureID)(intptr_t)titleTexture, ImVec2(scaledWidth, scaledHeight));
        }
    }

    ImGui::End();
    ImGui::PopStyleVar(3);
	ImGui::PopStyleColor(2);
}

void BackgroundManager::Cleanup() {
    for (int i = 0; i < static_cast<int>(backgroundTextures.size()); i++) {
        if (backgroundsLoaded[i] && backgroundTextures[i] != 0) {
            glDeleteTextures(1, &backgroundTextures[i]);
            backgroundTextures[i] = 0;
            backgroundsLoaded[i] = 0u;
        }
    }

    if (titleLoaded && titleTexture != 0) {
        glDeleteTextures(1, &titleTexture);
        titleTexture = 0;
        titleLoaded = false;
    }
}
