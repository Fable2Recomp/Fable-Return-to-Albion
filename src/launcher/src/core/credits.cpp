#include "credits.h"
#include <imgui.h>
#include <string>

Credits::Credits() 
    : contributorsLoaded(false), contributorsLoading(false) {
}

Credits::~Credits() {
    // eh, maybe eventually ill care to do cleanup
}

void Credits::Initialize(const std::string& repoName) {
    if (!contributorsLoading && !contributorsLoaded) {
        contributorsLoading = true;
        
        GitHubAPI::FetchContributorsAsync(
            repoName,
            [this](std::vector<GitContributor> contributors) {
                OnContributorsLoaded(std::move(contributors));
            });
    }
}

void Credits::OnContributorsLoaded(std::vector<GitContributor> contributors) {
    githubContributors = std::move(contributors);
    contributorsLoaded = true;
    contributorsLoading = false;
}

void Credits::Render() {
    ImGui::Begin("Credits");
    RenderContents();
    ImGui::End();
}

void Credits::RenderContents() {
    ImGui::BeginChild("CreditsChild", ImVec2(0, 0), true);

    if (contributorsLoading) {
        ImGui::Text("Loading contributors from GitHub...");
    }
    else if (!contributorsLoaded) {
        ImGui::Text("Failed to load contributors from GitHub");
        ImGui::Text("Please check your internet connection and try restarting the application.");
    }
    else if (githubContributors.empty()) {
        ImGui::Text("No contributors found for this repository");
    }
    else {

      ImGui::Text("Total Contributors: %zu", githubContributors.size());
      ImGui::Separator();

        for (size_t i = 0; i < githubContributors.size(); i++) {
            const auto& contributor = githubContributors[i];

            std::string childId = "contributor_" + std::to_string(i);

            ImGui::BeginChild(childId.c_str(), ImVec2(0, 60), true);

            ImGui::Text("@%s", contributor.login.c_str());
            ImGui::SameLine();

            ImGui::PushStyleColor(ImGuiCol_Text, ImVec4(0.7f, 0.7f, 0.7f, 1.0f));
            if (contributor.contributions > 1) {
                ImGui::Text("- %d contributions", contributor.contributions);
            } else {
                ImGui::Text("- %d contribution", contributor.contributions);
            }
            ImGui::PopStyleColor();

            ImGui::EndChild();
        }
    }

    ImGui::EndChild();
}
