#pragma once

#include <string>
#include <vector>

inline std::vector<std::string> patchList;
inline std::vector<std::string> patchDescriptions;
inline std::vector<std::string> patchTypes;
inline std::vector<int> patchDefaultValues;

struct Patch {
    std::string name;
    std::string description;
    std::string type;
    bool enabled;
    int intValue;
};

inline std::vector<Patch> patches;

class Patches {
public:
    void Init();
    void LoadPatchesFromINI();
    void EnsurePatchStateLoaded();
    void Render();
    std::vector<Patch>& GetItems();
    const std::vector<Patch>& GetItems() const;
    std::vector<std::string> GetPatchLaunchArguments();
    std::string GetPatchLaunchConfig();
    bool HasPatches() const { return !patchList.empty(); }
};
