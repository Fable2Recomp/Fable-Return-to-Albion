#include "patches.h"

#include "platform.h"
#include "utils/inimanager.h"
#include "utils/ini.h"

#include <imgui.h>

#include <algorithm>
#include <cctype>
#include <iostream>
#include <regex>
#include <sstream>

namespace {

void LoadPatchesFromResponse(const std::string& responseData, bool& onlineSuccess) {
    std::regex boolPattern("REXCVAR_DEFINE_BOOL\\(([^,]+),\\s*[^,]+,\\s*[^,]+,\\s*\"([^\"]+)\"\\)");
    std::regex intPattern("REXCVAR_DEFINE_INT32\\(([^,]+),\\s*([^,]+),\\s*[^,]+,\\s*\"([^\"]+)\"\\)");
    std::regex intPatternAlt("REXCVAR_DEFINE_INT32\\(([^,]+),\\s*([^,]+),\\s*\"([^\"]+)\"[^)]*\\)");

    std::sregex_iterator end;

    for (std::sregex_iterator iter(responseData.begin(), responseData.end(), boolPattern); iter != end; ++iter) {
        const std::smatch match = *iter;
        patchList.push_back(match[1].str());
        patchDescriptions.push_back(match[2].str());
        patchTypes.push_back("bool");
        patchDefaultValues.push_back(0);
    }

    for (std::sregex_iterator iter(responseData.begin(), responseData.end(), intPattern); iter != end; ++iter) {
        const std::smatch match = *iter;
        int defaultValue = 0;
        try {
            defaultValue = std::stoi(match[2].str());
        } catch (...) {
        }

        patchList.push_back(match[1].str());
        patchDescriptions.push_back(match[3].str());
        patchTypes.push_back("int");
        patchDefaultValues.push_back(defaultValue);
    }

    for (std::sregex_iterator iter(responseData.begin(), responseData.end(), intPatternAlt); iter != end; ++iter) {
        const std::smatch match = *iter;
        const std::string patchName = match[1].str();

        bool alreadyFound = false;
        for (std::size_t i = 0; i < patchList.size(); ++i) {
            if (patchList[i] == patchName && patchTypes[i] == "int") {
                alreadyFound = true;
                break;
            }
        }

        if (alreadyFound) {
            continue;
        }

        int defaultValue = 0;
        try {
            defaultValue = std::stoi(match[2].str());
        } catch (...) {
        }

        patchList.push_back(patchName);
        patchDescriptions.push_back(match[3].str());
        patchTypes.push_back("int");
        patchDefaultValues.push_back(defaultValue);
    }

    onlineSuccess = !patchList.empty();
}

std::vector<std::string> TokenizeArguments(const std::string& rawArguments) {
    std::vector<std::string> arguments;
    std::string current;
    char quote = '\0';

    for (char character : rawArguments) {
        if (quote != '\0') {
            if (character == quote) {
                quote = '\0';
            } else {
                current += character;
            }
            continue;
        }

        if (character == '"' || character == '\'') {
            quote = character;
            continue;
        }

        if (std::isspace(static_cast<unsigned char>(character))) {
            if (!current.empty()) {
                arguments.push_back(current);
                current.clear();
            }
            continue;
        }

        current += character;
    }

    if (!current.empty()) {
        arguments.push_back(current);
    }

    return arguments;
}

}  // namespace

void Patches::Init() {
    patchList.clear();
    patchDescriptions.clear();
    patchTypes.clear();
    patchDefaultValues.clear();
    patches.clear();

    bool onlineSuccess = false;
    std::string error;
    const std::string responseData = Platform::HttpGet(
        INI::GetString(
            "PatchesLocation",
            "https://raw.githubusercontent.com/Fable2Recomp/refii/master/src/hooks.cpp",
            "Github"),
        &error);
    if (!responseData.empty()) {
        LoadPatchesFromResponse(responseData, onlineSuccess);
    } else if (!error.empty()) {
        std::cout << "Patch fetch failed: " << error << std::endl;
    }

    if (!onlineSuccess) {
        LoadPatchesFromINI();
    }

    EnsurePatchStateLoaded();
}

void Patches::LoadPatchesFromINI() {
    mINI::INIFile iniFile = mINI::INIFile(INI::GetIniFilePath());
    mINI::INIStructure iniData;

    if (!iniFile.read(iniData) || !iniData.has("Patches")) {
        return;
    }

    auto& patchesSection = iniData["Patches"];
    for (auto it = patchesSection.begin(); it != patchesSection.end(); ++it) {
        const std::string patchName = it->first;

        std::string description = "Patch loaded from config";
        std::string type = "bool";

        if (iniData.has("PatchDescriptions")) {
            auto& descSection = iniData["PatchDescriptions"];
            const std::string descKey = patchName + "_desc";
            if (descSection.has(descKey)) {
                description = descSection[descKey];
            }
        }

        if (iniData.has("PatchTypes")) {
            auto& typeSection = iniData["PatchTypes"];
            const std::string typeKey = patchName + "_type";
            if (typeSection.has(typeKey)) {
                type = typeSection[typeKey];
            }
        }

        patchList.push_back(patchName);
        patchDescriptions.push_back(description);
        patchTypes.push_back(type);
        patchDefaultValues.push_back(0);
    }
}

void Patches::Render() {
    if (patchList.empty()) {
        ImGui::Text("No patches available");
        return;
    }

    EnsurePatchStateLoaded();

    for (std::size_t i = 0; i < patches.size(); ++i) {
        Patch& patch = patches[i];
        if (patch.type == "bool") {
            ImGui::BeginChild((patch.name + "_child").c_str(), ImVec2(0, 50), true);
            ImGui::Checkbox(patch.name.c_str(), &patch.enabled);
            if (ImGui::IsItemHovered()) {
                ImGui::SetTooltip("%s", patch.description.c_str());
            }
            ImGui::EndChild();
        } else if (patch.type == "int") {
            ImGui::BeginChild((patch.name + "_child").c_str(), ImVec2(0, 70), true);
            ImGui::Text("%s", patch.name.c_str());
            if (ImGui::IsItemHovered()) {
                ImGui::SetTooltip("%s", patch.description.c_str());
            }
            ImGui::InputInt(("##" + patch.name).c_str(), &patch.intValue, 1, 10, ImGuiInputTextFlags_None);
            if (patch.intValue < 0) {
                patch.intValue = 0;
            }
            if (ImGui::IsItemHovered()) {
                ImGui::SetTooltip("%s", patch.description.c_str());
            }
            ImGui::EndChild();
        }
    }
}

void Patches::EnsurePatchStateLoaded() {
    for (std::size_t i = 0; i < patchList.size(); ++i) {
        const std::string& patchName = patchList[i];
        const std::string& description = patchDescriptions[i];
        const std::string& type = patchTypes[i];
        const int defaultValue = patchDefaultValues[i];

        auto it = std::find_if(patches.begin(), patches.end(), [&](const Patch& patch) {
            return patch.name == patchName;
        });

        if (it == patches.end()) {
            bool enabledValue = false;
            int intValue = 0;

            if (type == "bool") {
                enabledValue = INI::GetBool(patchName, "Patches", false);
            } else if (type == "int") {
                enabledValue = true;
                intValue = INI::GetInt(patchName, defaultValue, "Patches");
            }

            patches.push_back({ patchName, description, type, enabledValue, intValue });
            continue;
        }

        it->description = description;
        it->type = type;
        if (type == "int") {
            it->enabled = true;
        }
    }
}

std::vector<Patch>& Patches::GetItems() {
    EnsurePatchStateLoaded();
    return patches;
}

const std::vector<Patch>& Patches::GetItems() const {
    const_cast<Patches*>(this)->EnsurePatchStateLoaded();
    return patches;
}

std::vector<std::string> Patches::GetPatchLaunchArguments() {
    EnsurePatchStateLoaded();

    std::vector<std::string> arguments;

    for (Patch& patch : patches) {
        if (patch.type == "bool") {
            arguments.push_back("--" + patch.name + "=" + std::string(patch.enabled ? "true" : "false"));
            INI::SetBool(patch.name, patch.enabled, "Patches");
        } else if (patch.type == "int") {
            arguments.push_back("--" + patch.name + "=" + std::to_string(patch.intValue));
            INI::SetInt(patch.name, patch.intValue, "Patches");
        }

        INI::SetString(patch.name + "_desc", patch.description, "PatchDescriptions");
        INI::SetString(patch.name + "_type", patch.type, "PatchTypes");
    }

    const std::vector<std::string> extraArguments = TokenizeArguments(
        INI::GetString(
            "launch-args",
            "",
            "Launch"));
    arguments.insert(arguments.end(), extraArguments.begin(), extraArguments.end());

    return arguments;
}

std::string Patches::GetPatchLaunchConfig() {
    std::string config;
    for (const std::string& argument : GetPatchLaunchArguments()) {
        config += " " + argument;
    }
    return config;
}
