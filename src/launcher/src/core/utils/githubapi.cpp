#include "githubapi.h"

#include "platform.h"

#include <cctype>
#include <iomanip>
#include <sstream>
#include <iostream>
#include <thread>

namespace {

std::string UrlEncode(const std::string& input) {
    std::ostringstream encoded;
    encoded.fill('0');
    encoded << std::hex << std::uppercase;

    for (unsigned char character : input) {
        if (std::isalnum(character) || character == '-' || character == '_' || character == '.' || character == '~') {
            encoded << character;
        } else {
            encoded << '%' << std::setw(2) << static_cast<int>(character);
        }
    }

    return encoded.str();
}

}  // namespace

void GitHubAPI::FetchCommitsAsync(
    const std::string& repo,
    const std::string& branch,
    std::function<void(std::vector<GitCommit>)> callback) {
    std::thread([repo, branch, callback]() {
        auto commits = FetchCommits(repo, branch);
        callback(commits);
    }).detach();
}

void GitHubAPI::FetchContributorsAsync(
    const std::string& repo,
    std::function<void(std::vector<GitContributor>)> callback) {
    std::thread([repo, callback]() {
        auto contributors = FetchContributors(repo);
        callback(contributors);
    }).detach();
}

void GitHubAPI::FetchLatestCommitShaAsync(
    const std::string& repo,
    const std::string& branch,
    std::function<void(std::string)> callback) {
    std::thread([repo, branch, callback]() {
        auto sha = FetchLatestCommitSha(repo, branch);
        callback(sha);
    }).detach();
}

void GitHubAPI::FetchLatestCommitShaForFileAsync(
    const std::string& repo,
    const std::string& branch,
    const std::string& filePath,
    std::function<void(std::string)> callback) {
    std::thread([repo, branch, filePath, callback]() {
        auto sha = FetchLatestCommitShaForFile(repo, branch, filePath);
        callback(sha);
    }).detach();
}

std::vector<GitCommit> GitHubAPI::FetchCommits(const std::string& repo, const std::string& branch) {
    std::vector<GitCommit> commits;

    try {
        const std::string url =
            "https://api.github.com/repos/" + repo +
            "/commits?sha=" + UrlEncode(branch) +
            "&per_page=10";

        std::cout << "Fetching commits from: " << url << std::endl;

        std::string error;
        std::string jsonResponse = Platform::HttpGet(url, &error);

        if (!jsonResponse.empty()) {
            commits = ParseCommitsJson(jsonResponse);
        } else {
            std::cout << "Commit fetch failed: " << error << std::endl;
        }
    }
    catch (...) {
        // Handle errors eventually
    }

    return commits;
}

std::vector<GitContributor> GitHubAPI::FetchContributors(const std::string& repo) {
    std::vector<GitContributor> contributors;

    try {
        const std::string url = "https://api.github.com/repos/" + repo + "/contributors";

        std::string error;
        std::string jsonResponse = Platform::HttpGet(url, &error);

        if (!jsonResponse.empty()) {
            std::cout << "GitHub API Response (first 200 chars): " << jsonResponse.substr(0, 200) << std::endl;
            contributors = ParseContributorsJson(jsonResponse);
            std::cout << "Parsed " << contributors.size() << " contributors" << std::endl;
        } else {
            std::cout << "Contributor fetch failed: " << error << std::endl;
        }
    }
    catch (...) {
      // Handle errors eventually
    }

    return contributors;
}

std::string GitHubAPI::FetchLatestCommitSha(const std::string& repo, const std::string& branch) {
    std::string latestSha;

    try {
        const std::string url =
            "https://api.github.com/repos/" + repo +
            "/commits?sha=" + UrlEncode(branch) +
            "&per_page=1";

        std::string jsonResponse = Platform::HttpGet(url);

        if (!jsonResponse.empty()) {
            size_t shaPos = jsonResponse.find("\"sha\":");
            if (shaPos != std::string::npos) {
                latestSha = ExtractJsonString(jsonResponse, "sha", shaPos);
                std::cout << "Latest commit SHA: " << latestSha << std::endl;
            }
        }
    }
    catch (...) {
      // Handle errors eventually
    }

    return latestSha;
}

std::string GitHubAPI::FetchLatestCommitShaForFile(const std::string& repo, const std::string& branch, const std::string& filePath) {
    std::string latestSha;

    try {
        const std::string url =
            "https://api.github.com/repos/" + repo +
            "/commits?sha=" + UrlEncode(branch) +
            "&path=" + UrlEncode(filePath) +
            "&per_page=1";

        std::cout << "Fetching latest commit for file: " << filePath << std::endl;

        std::string jsonResponse = Platform::HttpGet(url);

        if (!jsonResponse.empty()) {
            size_t shaPos = jsonResponse.find("\"sha\":");
            if (shaPos != std::string::npos) {
                latestSha = ExtractJsonString(jsonResponse, "sha", shaPos);
                std::cout << "Latest commit SHA for " << filePath << ": " << latestSha << std::endl;
            } else {
                std::cout << "No commits found for file: " << filePath << std::endl;
            }
        }
    }
    catch (...) {
      // Handle errors eventually
    }

    return latestSha;
}

std::vector<GitCommit> GitHubAPI::ParseCommitsJson(const std::string& json) {
    std::vector<GitCommit> commits;
    
    size_t pos = 0;
    while ((pos = json.find("\"sha\":", pos)) != std::string::npos) {
        GitCommit commit;
        
        commit.sha = ExtractJsonString(json, "sha", pos);
        if (commit.sha.length() > 8) {
            commit.sha = commit.sha.substr(0, 8);
        }
        
        size_t commitPos = json.find("\"commit\":", pos);
        if (commitPos != std::string::npos) {
            size_t messagePos = json.find("\"message\":", commitPos);
            if (messagePos != std::string::npos) {
                commit.message = ExtractJsonString(json, "message", messagePos);
                size_t newlinePos = commit.message.find("\\n");
                if (newlinePos != std::string::npos) {
                    commit.message = commit.message.substr(0, newlinePos);
                }
            }
            
            size_t authorPos = json.find("\"author\":", commitPos);
            if (authorPos != std::string::npos) {
                size_t namePos = json.find("\"name\":", authorPos);
                if (namePos != std::string::npos) {
                    commit.author = ExtractJsonString(json, "name", namePos);
                }
                
                size_t datePos = json.find("\"date\":", authorPos);
                if (datePos != std::string::npos) {
                    commit.date = ExtractJsonString(json, "date", datePos);
                    if (commit.date.length() >= 10) {
                        commit.date = commit.date.substr(0, 10);
                    }
                }
            }
        }
        
        if (!commit.sha.empty() && !commit.message.empty()) {
            commits.push_back(commit);
        }
        
        pos = commitPos != std::string::npos ? commitPos + 1 : pos + 1;
        
        // Limit to reasonable number of commits to avoid UI clutter, Im going to say 15 for now but yeah
        if (commits.size() >= 15) break;
    }

    return commits;
}

std::vector<GitContributor> GitHubAPI::ParseContributorsJson(const std::string& json) {
    std::vector<GitContributor> contributors;

    size_t pos = 0;
    while ((pos = json.find("\"login\":", pos)) != std::string::npos) {
        GitContributor contributor;

        contributor.login = ExtractJsonString(json, "login", pos);

        contributor.contributions = ExtractJsonInt(json, "contributions", pos);

        contributor.avatar_url = ExtractJsonString(json, "avatar_url", pos);

        if (!contributor.login.empty()) {
            contributors.push_back(contributor);
        }

        pos++;

        if (contributors.size() >= 30) break;
    }

    return contributors;
}

std::string GitHubAPI::ExtractJsonString(const std::string& json, const std::string& key, size_t startPos) {
    size_t keyPos = json.find("\"" + key + "\":", startPos);
    if (keyPos == std::string::npos) return "";
    
    size_t valueStart = json.find("\"", keyPos + key.length() + 2);
    if (valueStart == std::string::npos) return "";
    valueStart++;
    
    size_t valueEnd = valueStart;
    while (valueEnd < json.length() && json[valueEnd] != '"') {
        if (json[valueEnd] == '\\') {
            valueEnd += 2;
        } else {
            valueEnd++;
        }
    }
    
    if (valueEnd == std::string::npos) return "";

    return json.substr(valueStart, valueEnd - valueStart);
}

int GitHubAPI::ExtractJsonInt(const std::string& json, const std::string& key, size_t startPos) {
    size_t keyPos = json.find("\"" + key + "\":", startPos);
    if (keyPos == std::string::npos) return 0;

    size_t valueStart = json.find(":", keyPos + key.length() + 2);
    if (valueStart == std::string::npos) return 0;
    valueStart++;

    while (valueStart < json.length() && (json[valueStart] == ' ' || json[valueStart] == '\t' || json[valueStart] == '\n' || json[valueStart] == '\r')) {
        valueStart++;
    }

    size_t valueEnd = valueStart;
    while (valueEnd < json.length() && json[valueEnd] >= '0' && json[valueEnd] <= '9') {
        valueEnd++;
    }

    if (valueEnd == valueStart) return 0;

    try {
        return std::stoi(json.substr(valueStart, valueEnd - valueStart));
    } catch (...) {
        return 0;
    }
}
