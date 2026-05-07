#pragma once

#include <vector>
#include "utils/githubapi.h"

class ChangeLog {
public:
    ChangeLog();
    ~ChangeLog();

    void Initialize(const std::string& repoName, const std::string& branchName);
    
    void Render();
    void RenderContents();
    
    bool IsLoading() const { return commitsLoading; }
    
    bool IsLoaded() const { return commitsLoaded; }
    
private:
    std::vector<GitCommit> githubCommits;
    bool commitsLoaded;
    bool commitsLoading;
    
    void OnCommitsLoaded(std::vector<GitCommit> commits);
};
