#include "tools/workspace/lua_workspace_camera_recovery.h"

#include "tools/workspace/lua_workspace_text_repair.h"

namespace refii::lua::workspace {
namespace {

using text_repair::ReplaceAllLiteral;
using text_repair::ApplyLiteralRules;
using text_repair::ReplaceFunctionByHeader;

bool FoldFastStringAndCageCameraRawReadableRecovery(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("StringAndCageCamera") == std::string::npos ||
        source->find("SACCamera.CalculateCentreAndCage") == std::string::npos) {
        return false;
    }

    bool changed = false;

    changed |= ApplyLiteralRules(
        source,
        {
            {
                "  local r0 = CameraBase:Create(\"StringAndCageCamera\")\n"
                "  SACCamera = r0\n",
                "  SACCamera = CameraBase:Create(\"StringAndCageCamera\")\n",
            },
            {
                "  local r1 = (math.pi / (7))\n"
                "  SACCamera.IgnorePitch = r1\n"
                "  r1 = (math.pi / (10))\n"
                "  SACCamera.LimitPitch = r1\n",
                "  SACCamera.IgnorePitch = math.pi / 7\n"
                "  SACCamera.LimitPitch = math.pi / 10\n",
            },
            {
                "  r1 = {}\n"
                "  local r2 = {}\n"
                "  r2.SlotWidth = 0\n"
                "  r2.SlotDepth = 0\n"
                "  r2.StringLength = 4\n"
                "  r2.CageRadius = 4\n"
                "  r2.HeightOffset = 0.5\n"
                "  r2.FocalOffset = 0.94999998807907104\n"
                "  r2.FOV = 45\n"
                "  r2.DisregardsGroundSlope = true\n"
                "  r1.Name = \"Vanity\"\n"
                "  r1[1] = r2\n"
                "  SACCamera.VanityZoomLevels = r1\n",
                "  SACCamera.VanityZoomLevels = {{SlotWidth = 0, SlotDepth = 0, StringLength = 4, CageRadius = 4, HeightOffset = 0.5, FocalOffset = 0.94999998807907104, FOV = 45, DisregardsGroundSlope = true}, Name = \"Vanity\"}\n",
            },
            {
                "  r1 = {}\n"
                "  r2 = {}\n"
                "  r2.SlotWidth = 0.30000001192092896\n"
                "  r2.SlotDepth = 0\n"
                "  r2.StringLength = 4\n"
                "  r2.CageRadius = 3.4000000953674316\n"
                "  r2.MaxCageRadius = 10\n"
                "  r2.HeightOffset = 0.80000001192092896\n"
                "  r2.FocalOffset = 1.6000000238418579\n"
                "  r2.FOV = 70\n"
                "  r2.TrackAmount = 0.10000000149011612\n"
                "  r2.HorizontalFocalOffset = 0\n"
                "  r2.MaxCoopDistance = 8\n"
                "  r1.Name = \"Interior\"\n"
                "  r1[1] = r2\n"
                "  SACCamera.InteriorZoomLevels = r1\n",
                "  SACCamera.InteriorZoomLevels = {{SlotWidth = 0.30000001192092896, SlotDepth = 0, StringLength = 4, CageRadius = 3.4000000953674316, MaxCageRadius = 10, HeightOffset = 0.80000001192092896, FocalOffset = 1.6000000238418579, FOV = 70, TrackAmount = 0.10000000149011612, HorizontalFocalOffset = 0, MaxCoopDistance = 8}, Name = \"Interior\"}\n",
            },
            {
                "  r1 = {}\n"
                "  r2 = {}\n"
                "  r2.SlotWidth = 0.40000000596046448\n"
                "  r2.SlotDepth = 0.15000000596046448\n"
                "  r2.StringLength = 5\n"
                "  r2.CageRadius = 3.4000000953674316\n"
                "  r2.MaxCageRadius = 15\n"
                "  r2.HeightOffset = 0.89999997615814209\n"
                "  r2.FocalOffset = 1.7000000476837158\n"
                "  r2.FOV = 75\n"
                "  r2.TrackAmount = 0\n"
                "  r2.HorizontalFocalOffset = 0\n"
                "  r2.MaxCoopDistance = 13\n"
                "  r1.Name = \"Exterior\"\n"
                "  r1[1] = r2\n"
                "  SACCamera.ExteriorZoomLevels = r1\n",
                "  SACCamera.ExteriorZoomLevels = {{SlotWidth = 0.40000000596046448, SlotDepth = 0.15000000596046448, StringLength = 5, CageRadius = 3.4000000953674316, MaxCageRadius = 15, HeightOffset = 0.89999997615814209, FocalOffset = 1.7000000476837158, FOV = 75, TrackAmount = 0, HorizontalFocalOffset = 0, MaxCoopDistance = 13}, Name = \"Exterior\"}\n",
            },
            {
                "  local r3 = 1\n"
                "  CameraFunctions.Interpolate(track_amount, combat_weight, r3)\n",
                "  CameraFunctions.Interpolate(track_amount, combat_weight, 1)\n",
            },
            {
                "  SACCamera.GetXYZFromDirection = function(A0_0)\n"
                "    A0_0:Normalise()\n"
                "    L1_1:GetCross(CVector3((0), (0), (1))):Normalise()\n"
                "    L2_2:GetCross(L1_1):Normalise()\n"
                "    return L2_2, L3_3, L1_1\n"
                "  end\n",
                "  SACCamera.GetXYZFromDirection = function(A0_0)\n"
                "    local L1_1 = A0_0\n"
                "    L1_1:Normalise()\n"
                "    local L2_2 = L1_1:GetCross(CVector3((0), (0), (1)))\n"
                "    L2_2:Normalise()\n"
                "    local L3_3 = L2_2:GetCross(L1_1)\n"
                "    L3_3:Normalise()\n"
                "    return L2_2, L3_3, L1_1\n"
                "  end\n",
            },
            {
                "  SACCamera.GetRightFromDirection = function(A0_0)\n"
                "    A0_0:Normalise()\n"
                "    L1_1:GetCross(CVector3((0), (0), (1))):Normalise()\n"
                "    return r2\n"
                "  end\n",
                "  SACCamera.GetRightFromDirection = function(A0_0)\n"
                "    local L1_1 = A0_0\n"
                "    L1_1:Normalise()\n"
                "    local L2_2 = L1_1:GetCross(CVector3((0), (0), (1)))\n"
                "    L2_2:Normalise()\n"
                "    return L2_2\n"
                "  end\n",
            },
            {
                "  SACCamera.CalculateLevelTriggerZoomLevel = function(A0_0)\n"
                "    if CameraManager.GetLevelOverrideID:GetLevelOverrideID(CameraManager):GetLevelOverrideID() ~= A0_0.LevelOverrideID then\n"
                "    A0_0.LevelTriggerZoomLevel = A0_0:MergeZoomLevel(CameraManager.GetLevelOverrideValues:GetLevelOverrideValues(CameraManager))\n"
                "    A0_0.LevelOverrideID = CameraManager.GetLevelOverrideID:GetLevelOverrideID(CameraManager)\n"
                "    end\n"
                "  end\n",
                "  SACCamera.CalculateLevelTriggerZoomLevel = function(A0_0)\n"
                "    if CameraManager:GetLevelOverrideID() ~= A0_0.LevelOverrideID then\n"
                "    A0_0.LevelTriggerZoomLevel = A0_0:MergeZoomLevel(CameraManager:GetLevelOverrideValues())\n"
                "    A0_0.LevelOverrideID = CameraManager:GetLevelOverrideID()\n"
                "    end\n"
                "  end\n",
            },
            {
                "  SACCamera.CalculateLevelTriggerZoomLevel = function(A0_0)\n"
                "    if CameraManager:GetLevelOverrideID():GetLevelOverrideID() ~= A0_0.LevelOverrideID then\n"
                "    A0_0.LevelTriggerZoomLevel = A0_0:MergeZoomLevel(CameraManager.GetLevelOverrideValues:GetLevelOverrideValues(CameraManager))\n"
                "    A0_0.LevelOverrideID = CameraManager:GetLevelOverrideID()\n"
                "    end\n"
                "  end\n",
                "  SACCamera.CalculateLevelTriggerZoomLevel = function(A0_0)\n"
                "    if CameraManager:GetLevelOverrideID() ~= A0_0.LevelOverrideID then\n"
                "    A0_0.LevelTriggerZoomLevel = A0_0:MergeZoomLevel(CameraManager:GetLevelOverrideValues())\n"
                "    A0_0.LevelOverrideID = CameraManager:GetLevelOverrideID()\n"
                "    end\n"
                "  end\n",
            },
        });
    changed |= ApplyLiteralRules(
        source,
        {
            {
                "  SACCamera.AddInterestingEntityListToWeightedList = function(A0_0, A1_1, A2_2)\n"
                "    local L3_3 = A2_2\n"
                "    for r7, r8 in pairs(A1_1) do\n"
                "    if CameraInterest.IsAvailable(r8) then\n"
                "    else\n"
                "    L3_3 = A2_2\n"
                "    end\n"
                "    SACCamera.AddInterestingEntityToWeightedList(A0_0, r8, L3_3)\n"
                "    end\n"
                "  end\n",
                "  SACCamera.AddInterestingEntityListToWeightedList = function(A0_0, A1_1, A2_2)\n"
                "    local L3_3 = A2_2\n"
                "    for r7, r8 in pairs(A1_1) do\n"
                "    if CameraInterest.IsAvailable(r8) then\n"
                "    L3_3 = CameraInterest.GetRange(r8)\n"
                "    else\n"
                "    L3_3 = A2_2\n"
                "    end\n"
                "    SACCamera.AddInterestingEntityToWeightedList(A0_0, r8, L3_3)\n"
                "    end\n"
                "  end\n",
            },
            {
                "  SACCamera.GetRadiusFromEntity = function(A0_0)\n"
                "    if A0_0:IsAlive() then\n"
                "    if CameraInterest.IsAvailable(A0_0) then\n"
                "    else\n"
                "    if Physics.IsAvailable(A0_0) then\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return L1_1\n"
                "  end\n",
                "  SACCamera.GetRadiusFromEntity = function(A0_0)\n"
                "    local L1_1 = SACCamera.RadiusPerPerson\n"
                "    if A0_0:IsAlive() then\n"
                "    if CameraInterest.IsAvailable(A0_0) then\n"
                "    L1_1 = CameraInterest.GetRadius(A0_0)\n"
                "    else\n"
                "    if Physics.IsAvailable(A0_0) then\n"
                "    L1_1 = Physics.GetRadius(A0_0)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return L1_1\n"
                "  end\n",
            },
            {
                "  SACCamera.AddInterestingEntityToWeightedList = function(A0_0, A1_1, A2_2, A3_3)\n"
                "    if A0_0[GetIDFromEntity(A1_1)] == nil then\n"
                "    A0_0[r4] = {Entity = A1_1, InterestingRange = (A2_2 or (10))}\n"
                "    if not r7 then\n"
                "    end\n"
                "    A0_0[r4].Radius = r7\n"
                "    A0_0[r4].Position = A1_1:GetPosition()\n"
                "    else\n"
                "    A0_0[r4] = {InterestingRange = (A2_2 or (10))}\n"
                "    end\n"
                "    A0_0[r4] = {Valid = true}\n"
                "  end\n",
                "  SACCamera.AddInterestingEntityToWeightedList = function(A0_0, A1_1, A2_2, A3_3)\n"
                "    local L4_4 = GetIDFromEntity(A1_1)\n"
                "    local L5_5 = A2_2 or 10\n"
                "    if A0_0[L4_4] == nil then\n"
                "    A0_0[L4_4] = {Entity = A1_1, InterestingRange = L5_5, Radius = A3_3 or SACCamera.GetRadiusFromEntity(A1_1), Position = A1_1:GetPosition()}\n"
                "    else\n"
                "    A0_0[L4_4].InterestingRange = L5_5\n"
                "    end\n"
                "    A0_0[L4_4].Valid = true\n"
                "  end\n",
            },
            {
                "  SACCamera.GetImportance = function(A0_0, A1_1)\n"
                "    if not A0_0.Importance then\n"
                "    end\n"
                "    A0_0.Importance = A0_0.Importance\n"
                "    if not A0_0.Valid or not A1_1 then\n"
                "    local A1_1 = 0\n"
                "    end\n"
                "    A0_0.Importance = CameraFunctions.GetRollingAverage(A0_0.Importance, A1_1, SACCamera.InterestingEnemyDamping)\n"
                "    return A0_0.Importance\n"
                "  end\n",
                "  SACCamera.GetImportance = function(A0_0, A1_1)\n"
                "    A0_0.Importance = A0_0.Importance or 0\n"
                "    if not A0_0.Valid or not A1_1 then\n"
                "    A1_1 = 0\n"
                "    end\n"
                "    A0_0.Importance = CameraFunctions.GetRollingAverage(A0_0.Importance, A1_1, SACCamera.InterestingEnemyDamping)\n"
                "    return A0_0.Importance\n"
                "  end\n",
            },
            {
                "  SACCamera.GetMaxSearchDistanceForEnemies = function(A0_0)\n"
                "    local L1_1 = math.max((SACCamera.InterestingEnemyDistance * SACCamera.InterestingRangeFadeFactor), SACCamera.CombatValues.FullHeroCamDistance)\n"
                "    if A0_0.Player then\n"
                "    return math.max(math.min(math.max((CameraManager.GetMaxCameraCreatureInterestRange() * SACCamera.InterestingRangeFadeFactor), L1_1), (50)), (0))\n"
                "    end\n"
                "    return math.max(math.min(L1_1, (50)), (0))\n"
                "  end\n",
                "  SACCamera.GetMaxSearchDistanceForEnemies = function(A0_0)\n"
                "    local L1_1 = math.max((SACCamera.InterestingEnemyDistance * SACCamera.InterestingRangeFadeFactor), SACCamera.CombatValues.FullHeroCamDistance)\n"
                "    if A0_0.Player then\n"
                "    local L2_2 = math.max((CameraManager.GetMaxCameraCreatureInterestRange() * SACCamera.InterestingRangeFadeFactor), L1_1)\n"
                "    L2_2 = math.min(L2_2, 50)\n"
                "    L2_2 = math.max(L2_2, 0)\n"
                "    return L2_2\n"
                "    end\n"
                "    L1_1 = math.min(L1_1, 50)\n"
                "    L1_1 = math.max(L1_1, 0)\n"
                "    return L1_1\n"
                "  end\n",
            },
        });
    changed |= ApplyLiteralRules(
        source,
        {
            {
                "  SACCamera.CalculateNearbyFoes = function(A0_0)\n"
                "    if SACCamera.UseNewNearbyFoeSearch then\n"
                "    return CameraManager.GetFoes(A0_0:GetMaxSearchDistanceForEnemies())\n"
                "    end\n"
                "    local L1_1 = {}\n"
                "    local L2_2 = A0_0.Player\n"
                "    if L2_2:IsAlive() then\n"
                "    if Faction.IsAvailable(L2_2) then\n"
                "    end\n"
                "    end\n"
                "    if CameraFunctions.IsMultiplayer(L2_2) then\n"
                "    if Faction.IsAvailable(GetPlayerHenchman()) then\n"
                "    local L3_3 = L3_3(SearchTools.StartNewSearch(\"creature\"), GetPlayerHenchman(), A0_0:GetMaxSearchDistanceForEnemies())\n"
                "    local L5_5 = L3_3\n"
                "    end\n"
                "    L3_3 = {}\n"
                "    for r7, r8 in ipairs(L1_1) do\n"
                "    L3_3[GetIDFromEntity(r8)] = true\n"
                "    end\n"
                "    for L8_8, r8 in ipairs({}) do\n"
                "    if not L3_3[GetIDFromEntity(r8)] then\n"
                "    table.insert(L1_1, r8)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return L1_1\n"
                "  end\n",
                "  SACCamera.CalculateNearbyFoes = function(A0_0)\n"
                "    if SACCamera.UseNewNearbyFoeSearch then\n"
                "    return CameraManager.GetFoes(A0_0:GetMaxSearchDistanceForEnemies())\n"
                "    else\n"
                "    local L1_1 = {}\n"
                "    if A0_0.Player:IsAlive() and Faction.IsAvailable(A0_0.Player) then\n"
                "    local L2_2 = SearchTools.FilterFoesForCombatCamera(SearchTools.StartNewSearch(\"creature\"), A0_0.Player, A0_0:GetMaxSearchDistanceForEnemies())\n"
                "    L1_1 = SearchTools.GetSearchResults(L2_2)\n"
                "    end\n"
                "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
                "    local L2_2 = {}\n"
                "    if Faction.IsAvailable(GetPlayerHenchman()) then\n"
                "    local L3_3 = SearchTools.FilterFoesForCombatCamera(SearchTools.StartNewSearch(\"creature\"), GetPlayerHenchman(), A0_0:GetMaxSearchDistanceForEnemies())\n"
                "    L2_2 = SearchTools.GetSearchResults(L3_3)\n"
                "    end\n"
                "    local L3_3 = {}\n"
                "    for r7, r8 in ipairs(L1_1) do\n"
                "    L3_3[GetIDFromEntity(r8)] = true\n"
                "    end\n"
                "    for L8_8, r8 in ipairs(L2_2) do\n"
                "    local L9_9 = GetIDFromEntity(r8)\n"
                "    if not L3_3[L9_9] then\n"
                "    table.insert(L1_1, r8)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return L1_1\n"
                "    end\n"
                "  end\n",
            },
            {
                "  SACCamera.CalculateNearbyFoes = function(A0_0)\n"
                "    if SACCamera.UseNewNearbyFoeSearch then\n"
                "    return CameraManager.GetFoes(A0_0:GetMaxSearchDistanceForEnemies())\n"
                "    end\n"
                "    local L1_1 = {}\n"
                "    local L2_2 = A0_0.Player\n"
                "    if L2_2.IsAlive:IsAlive(L2_2) then\n"
                "    if Faction.IsAvailable(L2_2) then\n"
                "    end\n"
                "    end\n"
                "    if CameraFunctions.IsMultiplayer(L2_2) then\n"
                "    if Faction.IsAvailable(GetPlayerHenchman()) then\n"
                "    local L3_3 = L3_3(SearchTools.StartNewSearch(\"creature\"), GetPlayerHenchman(), A0_0:GetMaxSearchDistanceForEnemies())\n"
                "    local L5_5 = L3_3\n"
                "    end\n"
                "    L3_3 = {}\n"
                "    for r7, r8 in ipairs(L1_1) do\n"
                "    L3_3[GetIDFromEntity(r8)] = true\n"
                "    end\n"
                "    for L8_8, r8 in ipairs({}) do\n"
                "    if not L3_3[GetIDFromEntity(r8)] then\n"
                "    table.insert(L1_1, r8)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return L1_1\n"
                "  end\n",
                "  SACCamera.CalculateNearbyFoes = function(A0_0)\n"
                "    if SACCamera.UseNewNearbyFoeSearch then\n"
                "    return CameraManager.GetFoes(A0_0:GetMaxSearchDistanceForEnemies())\n"
                "    else\n"
                "    local L1_1 = {}\n"
                "    if A0_0.Player:IsAlive() and Faction.IsAvailable(A0_0.Player) then\n"
                "    local L2_2 = SearchTools.FilterFoesForCombatCamera(SearchTools.StartNewSearch(\"creature\"), A0_0.Player, A0_0:GetMaxSearchDistanceForEnemies())\n"
                "    L1_1 = SearchTools.GetSearchResults(L2_2)\n"
                "    end\n"
                "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
                "    local L2_2 = {}\n"
                "    if Faction.IsAvailable(GetPlayerHenchman()) then\n"
                "    local L3_3 = SearchTools.FilterFoesForCombatCamera(SearchTools.StartNewSearch(\"creature\"), GetPlayerHenchman(), A0_0:GetMaxSearchDistanceForEnemies())\n"
                "    L2_2 = SearchTools.GetSearchResults(L3_3)\n"
                "    end\n"
                "    local L3_3 = {}\n"
                "    for r7, r8 in ipairs(L1_1) do\n"
                "    L3_3[GetIDFromEntity(r8)] = true\n"
                "    end\n"
                "    for L8_8, r8 in ipairs(L2_2) do\n"
                "    local L9_9 = GetIDFromEntity(r8)\n"
                "    if not L3_3[L9_9] then\n"
                "    table.insert(L1_1, r8)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return L1_1\n"
                "    end\n"
                "  end\n",
            },
            {
                "  SACCamera.CalculateNearbyInterestingEntities = function(A0_0, A1_1)\n"
                "    local L2_2 = {}\n"
                "    if A0_0.Player:IsAlive() then\n"
                "    if Player.IsAvailable(A0_0.Player) then\n"
                "    end\n"
                "    end\n"
                "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
                "    if Player.IsAvailable(GetPlayerHenchman()) then\n"
                "    end\n"
                "    local L4_4 = {}\n"
                "    for r8, r9 in ipairs(L2_2) do\n"
                "    L4_4[GetIDFromEntity(r9)] = true\n"
                "    end\n"
                "    for L9_9, r9 in ipairs({}) do\n"
                "    if not L4_4[GetIDFromEntity(r9)] then\n"
                "    table.insert(L2_2, r9)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return L2_2\n"
                "  end\n",
                "  SACCamera.CalculateNearbyInterestingEntities = function(A0_0, A1_1)\n"
                "    local L2_2 = {}\n"
                "    if A0_0.Player:IsAlive() and Player.IsAvailable(A0_0.Player) then\n"
                "    L2_2 = CameraManager.GetInterestingCameraObjects(A0_0.Player:GetPosition(), SACCamera.InterestingEntityDistance, A1_1)\n"
                "    end\n"
                "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
                "    local L3_3 = {}\n"
                "    if Player.IsAvailable(GetPlayerHenchman()) then\n"
                "    L3_3 = CameraManager.GetInterestingCameraObjects(GetPlayerHenchman():GetPosition(), SACCamera.InterestingEntityDistance, A1_1)\n"
                "    end\n"
                "    local L4_4 = {}\n"
                "    for r8, r9 in ipairs(L2_2) do\n"
                "    L4_4[GetIDFromEntity(r9)] = true\n"
                "    end\n"
                "    for L9_9, r9 in ipairs(L3_3) do\n"
                "    local L10_10 = GetIDFromEntity(r9)\n"
                "    if not L4_4[L10_10] then\n"
                "    table.insert(L2_2, r9)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return L2_2\n"
                "  end\n",
            },
            {
                "  SACCamera.BoolInterpolate = function(A0_0, A1_1, A2_2)\n"
                "    if A0_0 ~= true or not 1 then\n"
                "    if A0_0 or not 0 then\n"
                "    local L3_3 = A0_0\n"
                "    end\n"
                "    end\n"
                "    if A2_2 ~= true or not 1 then\n"
                "    if A2_2 or not 0 then\n"
                "    local L4_4 = A2_2\n"
                "    end\n"
                "    end\n"
                "    return SACCamera.SafeInterpolate(L3_3, A1_1, L4_4)\n"
                "  end\n",
                "  SACCamera.BoolInterpolate = function(A0_0, A1_1, A2_2)\n"
                "    local L3_3 = A0_0 == true and 1 or not A0_0 and 0 or A0_0\n"
                "    local L4_4 = A2_2 == true and 1 or not A2_2 and 0 or A2_2\n"
                "    return SACCamera.SafeInterpolate(L3_3, A1_1, L4_4)\n"
                "  end\n",
            },
        });
    changed |= ApplyLiteralRules(
        source,
        {
            {
                "  SACCamera.InterpolateZoomLevels = function(A0_0, A1_1, A2_2)\n"
                "    local L3_3 = {}\n"
                "    L3_3.SlotWidth = SACCamera.SafeInterpolate(A0_0.SlotWidth, A1_1, A2_2.SlotWidth)\n"
                "    L3_3.SlotDepth = SACCamera.SafeInterpolate(A0_0.SlotDepth, A1_1, A2_2.SlotDepth)\n"
                "    L3_3.StringLength = SACCamera.SafeInterpolate(A0_0.StringLength, A1_1, A2_2.StringLength)\n"
                "    L3_3.CageRadius = SACCamera.SafeInterpolate(A0_0.CageRadius, A1_1, A2_2.CageRadius)\n"
                "    L3_3.MaxCageRadius = SACCamera.SafeInterpolate(A0_0.MaxCageRadius, A1_1, A2_2.MaxCageRadius)\n"
                "    L3_3.HeightOffset = SACCamera.SafeInterpolate(A0_0.HeightOffset, A1_1, A2_2.HeightOffset)\n"
                "    L3_3.FocalOffset = SACCamera.SafeInterpolate(A0_0.FocalOffset, A1_1, A2_2.FocalOffset)\n"
                "    L3_3.FOV = SACCamera.SafeInterpolate(A0_0.FOV, A1_1, A2_2.FOV)\n"
                "    L3_3.OverridesCombat = A0_0.OverridesCombat\n"
                "    L3_3.HorizontalFocalOffset = SACCamera.SafeInterpolate(A0_0.HorizontalFocalOffset, A1_1, A2_2.HorizontalFocalOffset)\n"
                "    L3_3.TrackAmount = SACCamera.SafeInterpolate(A0_0.TrackAmount, A1_1, A2_2.TrackAmount)\n"
                "    L3_3.SpecificBone = A0_0.SpecificBone\n"
                "    L3_3.MovementFactor = SACCamera.SafeInterpolate(A0_0.MovementFactor, A1_1, A2_2.MovementFactor)\n"
                "    L3_3.BlendTime = SACCamera.SafeInterpolate(A0_0.BlendTime, A1_1, A2_2.BlendTime)\n"
                "    L3_3.MaxCoopDistance = SACCamera.SafeInterpolate(A0_0.MaxCoopDistance, A1_1, A2_2.MaxCoopDistance)\n"
                "    L3_3.DisregardsGroundSlope = SACCamera.BoolInterpolate(A0_0.DisregardsGroundSlope, A1_1, A2_2.DisregardsGroundSlope)\n"
                "    return L3_3\n"
                "  end\n",
                "  SACCamera.InterpolateZoomLevels = function(A0_0, A1_1, A2_2)\n"
                "    local L3_3 = {\n"
                "      SlotWidth = SACCamera.SafeInterpolate(A0_0.SlotWidth, A1_1, A2_2.SlotWidth),\n"
                "      SlotDepth = SACCamera.SafeInterpolate(A0_0.SlotDepth, A1_1, A2_2.SlotDepth),\n"
                "      StringLength = SACCamera.SafeInterpolate(A0_0.StringLength, A1_1, A2_2.StringLength),\n"
                "      CageRadius = SACCamera.SafeInterpolate(A0_0.CageRadius, A1_1, A2_2.CageRadius),\n"
                "      MaxCageRadius = SACCamera.SafeInterpolate(A0_0.MaxCageRadius, A1_1, A2_2.MaxCageRadius),\n"
                "      HeightOffset = SACCamera.SafeInterpolate(A0_0.HeightOffset, A1_1, A2_2.HeightOffset),\n"
                "      FocalOffset = SACCamera.SafeInterpolate(A0_0.FocalOffset, A1_1, A2_2.FocalOffset),\n"
                "      FOV = SACCamera.SafeInterpolate(A0_0.FOV, A1_1, A2_2.FOV),\n"
                "      OverridesCombat = A0_0.OverridesCombat,\n"
                "      HorizontalFocalOffset = SACCamera.SafeInterpolate(A0_0.HorizontalFocalOffset, A1_1, A2_2.HorizontalFocalOffset),\n"
                "      TrackAmount = SACCamera.SafeInterpolate(A0_0.TrackAmount, A1_1, A2_2.TrackAmount),\n"
                "      SpecificBone = A0_0.SpecificBone,\n"
                "      MovementFactor = SACCamera.SafeInterpolate(A0_0.MovementFactor, A1_1, A2_2.MovementFactor),\n"
                "      BlendTime = SACCamera.SafeInterpolate(A0_0.BlendTime, A1_1, A2_2.BlendTime),\n"
                "      MaxCoopDistance = SACCamera.SafeInterpolate(A0_0.MaxCoopDistance, A1_1, A2_2.MaxCoopDistance),\n"
                "      DisregardsGroundSlope = SACCamera.BoolInterpolate(A0_0.DisregardsGroundSlope, A1_1, A2_2.DisregardsGroundSlope)\n"
                "    }\n"
                "    return L3_3\n"
                "  end\n",
            },
            {
                "  SACCamera.ScaleZoomLevel = function(A0_0, A1_1)\n"
                "    local L2_2 = {}\n"
                "    L2_2.SlotWidth = ((A0_0.SlotWidth and (A0_0.SlotWidth * A1_1)) or A0_0.SlotWidth)\n"
                "    local L3_3 = A0_0.SlotDepth\n"
                "    L2_2.SlotDepth = ((L3_3 and (L3_3 * A1_1)) or L3_3)\n"
                "    L3_3 = A0_0.StringLength\n"
                "    L2_2.StringLength = ((L3_3 and (A0_0.StringLength * A1_1)) or L3_3)\n"
                "    L3_3 = A0_0.CageRadius\n"
                "    L2_2.CageRadius = ((L3_3 and (A0_0.CageRadius * A1_1)) or L3_3)\n"
                "    L3_3 = A0_0.MaxCageRadius\n"
                "    L2_2.MaxCageRadius = ((L3_3 and (A0_0.MaxCageRadius * A1_1)) or L3_3)\n"
                "    L3_3 = A0_0.HeightOffset\n"
                "    L2_2.HeightOffset = ((L3_3 and (A0_0.HeightOffset * A1_1)) or L3_3)\n"
                "    L3_3 = A0_0.FocalOffset\n"
                "    L2_2.FocalOffset = ((L3_3 and (A0_0.FocalOffset * A1_1)) or L3_3)\n"
                "    L2_2.FOV = A0_0.FOV\n"
                "    L2_2.OverridesCombat = A0_0.OverridesCombat\n"
                "    L3_3 = A0_0.HorizontalFocalOffset\n"
                "    L2_2.HorizontalFocalOffset = ((L3_3 and (A0_0.HorizontalFocalOffset * A1_1)) or L3_3)\n"
                "    L2_2.TrackAmount = A0_0.TrackAmount\n"
                "    L2_2.SpecificBone = A0_0.SpecificBone\n"
                "    L2_2.MovementFactor = A0_0.MovementFactor\n"
                "    L2_2.BlendTime = A0_0.BlendTime\n"
                "    L3_3 = A0_0.MaxCoopDistance\n"
                "    L2_2.MaxCoopDistance = ((L3_3 and (A0_0.MaxCoopDistance * A1_1)) or L3_3)\n"
                "    L2_2.DisregardsGroundSlope = A0_0.DisregardsGroundSlope\n"
                "    return L2_2\n"
                "  end\n",
                "  SACCamera.ScaleZoomLevel = function(A0_0, A1_1)\n"
                "    local L2_2 = {\n"
                "      SlotWidth = A0_0.SlotWidth and A0_0.SlotWidth * A1_1,\n"
                "      SlotDepth = A0_0.SlotDepth and A0_0.SlotDepth * A1_1,\n"
                "      StringLength = A0_0.StringLength and A0_0.StringLength * A1_1,\n"
                "      CageRadius = A0_0.CageRadius and A0_0.CageRadius * A1_1,\n"
                "      MaxCageRadius = A0_0.MaxCageRadius and A0_0.MaxCageRadius * A1_1,\n"
                "      HeightOffset = A0_0.HeightOffset and A0_0.HeightOffset * A1_1,\n"
                "      FocalOffset = A0_0.FocalOffset and A0_0.FocalOffset * A1_1,\n"
                "      FOV = A0_0.FOV,\n"
                "      OverridesCombat = A0_0.OverridesCombat,\n"
                "      HorizontalFocalOffset = A0_0.HorizontalFocalOffset and A0_0.HorizontalFocalOffset * A1_1,\n"
                "      TrackAmount = A0_0.TrackAmount,\n"
                "      SpecificBone = A0_0.SpecificBone,\n"
                "      MovementFactor = A0_0.MovementFactor,\n"
                "      BlendTime = A0_0.BlendTime,\n"
                "      MaxCoopDistance = A0_0.MaxCoopDistance and A0_0.MaxCoopDistance * A1_1,\n"
                "      DisregardsGroundSlope = A0_0.DisregardsGroundSlope\n"
                "    }\n"
                "    return L2_2\n"
                "  end\n",
            },
            {
                "  SACCamera.CalculateNearbyInterestingEntities = function(A0_0, A1_1)\n"
                "    local L2_2 = {}\n"
                "    if A0_0.Player.IsAlive:IsAlive(A0_0.Player) then\n"
                "    if Player.IsAvailable(A0_0.Player) then\n"
                "    end\n"
                "    end\n"
                "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
                "    if Player.IsAvailable(GetPlayerHenchman()) then\n"
                "    end\n"
                "    local L4_4 = {}\n"
                "    for r8, r9 in ipairs(L2_2) do\n"
                "    L4_4[GetIDFromEntity(r9)] = true\n"
                "    end\n"
                "    for L9_9, r9 in ipairs({}) do\n"
                "    if not L4_4[GetIDFromEntity(r9)] then\n"
                "    table.insert(L2_2, r9)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return L2_2\n"
                "  end\n",
                "  SACCamera.CalculateNearbyInterestingEntities = function(A0_0, A1_1)\n"
                "    local L2_2 = {}\n"
                "    if A0_0.Player:IsAlive() and Player.IsAvailable(A0_0.Player) then\n"
                "    L2_2 = CameraManager.GetInterestingCameraObjects(A0_0.Player:GetPosition(), SACCamera.InterestingEntityDistance, A1_1)\n"
                "    end\n"
                "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
                "    local L3_3 = {}\n"
                "    if Player.IsAvailable(GetPlayerHenchman()) then\n"
                "    L3_3 = CameraManager.GetInterestingCameraObjects(GetPlayerHenchman():GetPosition(), SACCamera.InterestingEntityDistance, A1_1)\n"
                "    end\n"
                "    local L4_4 = {}\n"
                "    for r8, r9 in ipairs(L2_2) do\n"
                "    L4_4[GetIDFromEntity(r9)] = true\n"
                "    end\n"
                "    for L9_9, r9 in ipairs(L3_3) do\n"
                "    local L10_10 = GetIDFromEntity(r9)\n"
                "    if not L4_4[L10_10] then\n"
                "    table.insert(L2_2, r9)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return L2_2\n"
                "  end\n",
            },
            {
                "  SACCamera.CalculateScale = function(A0_0, A1_1)\n"
                "    if A0_0 == nil then\n"
                "    return (1)\n"
                "    end\n"
                "    if Age.IsAvailable(A0_0) then\n"
                "    if Age.GetAgeGroup(A0_0) == EAgeGroup.EAGE_GROUP_CHILD then\n"
                "    if not A1_1 then\n"
                "    return (0.69999998807907104)\n"
                "    end\n"
                "    end\n"
                "    else\n"
                "    local L2_2 = 0\n"
                "    local L3_3 = 0\n"
                "    if Stats.IsAvailable(A0_0) then\n"
                "    end\n"
                "    return (((1) + (L2_2 * (0))) + (L3_3 * (0.125)))\n"
                "    end\n"
                "  end\n",
                "  SACCamera.CalculateScale = function(A0_0, A1_1)\n"
                "    if A0_0 == nil then\n"
                "    return (1)\n"
                "    end\n"
                "    if Age.IsAvailable(A0_0) and Age.GetAgeGroup(A0_0) == EAgeGroup.EAGE_GROUP_CHILD and not A1_1 then\n"
                "    return (0.69999998807907104)\n"
                "    else\n"
                "    local L2_2 = 0\n"
                "    local L3_3 = 0\n"
                "    if Stats.IsAvailable(A0_0) then\n"
                "    L2_2 = Stats.GetStrength(A0_0)\n"
                "    L3_3 = math.min(Stats.GetSkill(A0_0) + Stats.GetHeight(A0_0), 1)\n"
                "    end\n"
                "    return (((1) + (L2_2 * (0))) + (L3_3 * (0.125)))\n"
                "    end\n"
                "  end\n",
            },
        });
    changed |= ApplyLiteralRules(
        source,
        {
            {
                "    A0_0.NearbyFoes = A0_0\n"
                "    A0_0.IsNearToDrop = false\n",
                "    A0_0.NearbyFoes = {}\n"
                "    A0_0.IsNearToDrop = false\n",
            },
            {
                "  SACCamera.MoveToFaceDirection = function(A0_0, A1_1, A2_2, A3_3, A4_4)\n"
                "    local L7_6 = A2_2\n"
                "    if A0_0.PreBoundFocalPos then\n"
                "    if A0_0.PreBoundCamPos then\n"
                "    if not (A0_0.PreBoundFocalPos - A0_0.PreBoundCamPos).GetLengthXY:GetLengthXY((A0_0.PreBoundFocalPos - A0_0.PreBoundCamPos)) then\n"
                "    end\n"
                "    end\n"
                "    else\n"
                "    end\n"
                "    local L5_5 = (L5_5 + (A3_3 * (-A0_0.PreBoundFocalPos)))\n"
                "    L5_5:SetZ((L5_5:GetZ() + A0_0:GetActiveZoomLevel().HeightOffset))\n"
                "    if A4_4 then\n"
                "    Camera.MoveToCut(A1_1, L5_5)\n"
                "    end\n"
                "    A0_0.PreBoundCamPos = L5_5\n"
                "    A0_0.PreBoundFocalPos = L7_6\n"
                "    return L5_5\n"
                "  end\n",
                "  SACCamera.MoveToFaceDirection = function(A0_0, A1_1, A2_2, A3_3, A4_4)\n"
                "    local L5_5 = A2_2\n"
                "    local L6_6 = L5_5\n"
                "    local L7_7 = A3_3\n"
                "    local L8_8 = (A0_0.PreBoundFocalPos and A0_0.PreBoundCamPos and (A0_0.PreBoundFocalPos - A0_0.PreBoundCamPos):GetLengthXY()) or A0_0:CalculateStringLength(A0_0:GetActiveZoomLevel().CageRadius)\n"
                "    L7_7 = L7_7 * -L8_8\n"
                "    L5_5 = L5_5 + L7_7\n"
                "    L5_5:SetZ((L5_5:GetZ() + A0_0:GetActiveZoomLevel().HeightOffset))\n"
                "    if A4_4 then\n"
                "    Camera.MoveToCut(A1_1, L5_5)\n"
                "    end\n"
                "    A0_0.PreBoundCamPos = L5_5\n"
                "    A0_0.PreBoundFocalPos = L6_6\n"
                "    return L5_5\n"
                "  end\n",
            },
            {
                "  SACCamera.MoveToBehindTarget = function(A0_0, A1_1, A2_2, A3_3)\n"
                "    return A0_0:MoveToFaceDirection(A1_1, A0_0:CalculateSimpleCentre(A2_2), Physics.GetFacingVector(A2_2), A3_3)\n"
                "  end\n"
                "  SACCamera.MoveToFaceTarget = function(A0_0, A1_1, A2_2)\n"
                "    return A0_0:MoveToFaceDirection(A1_1, A0_0:CalculateSimpleCentre(A2_2), (Physics.GetFacingVector(A2_2) * (-1)), force)\n"
                "  end\n",
                "  SACCamera.MoveToBehindTarget = function(A0_0, A1_1, A2_2, A3_3)\n"
                "    local L4_4 = A0_0:CalculateSimpleCentre(A2_2)\n"
                "    local L5_5 = Physics.GetFacingVector(A2_2)\n"
                "    return A0_0:MoveToFaceDirection(A1_1, L4_4, L5_5, A3_3)\n"
                "  end\n"
                "  SACCamera.MoveToFaceTarget = function(A0_0, A1_1, A2_2)\n"
                "    local L3_3 = A0_0:CalculateSimpleCentre(A2_2)\n"
                "    local L4_4 = Physics.GetFacingVector(A2_2)\n"
                "    L4_4 = L4_4 * -1\n"
                "    return A0_0:MoveToFaceDirection(A1_1, L3_3, L4_4, force)\n"
                "  end\n",
            },
            {
                "    A0_0.WeightedNearbyFoes = A0_0\n"
                "    A0_0.WeightedNearbyInterestingEntities = A0_0\n"
                "    A0_0.WeightedNearbyInterestingEntitiesHigh = A0_0\n"
                "    L5_5 = L5_5.SetInTargetingCamera\n"
                "    L5_5(A0_0.Player, true)\n"
                "    L5_5 = A0_0.Camera\n"
                "    if not L5_5 or not L5_5:IsAlive() then\n"
                "    L5_5 = L5_5.Error\n"
                "    L5_5(\"AdamL:SACCamera's self.Camera is invalid at the end of SACCamera:Activate, please remember what has just happened and grab me.\")\n"
                "    end\n"
                "    L5_5 = L5_5.SetShouldDrawPlayerIcons\n"
                "    L5_5(true)\n",
                "    A0_0.WeightedNearbyFoes = {}\n"
                "    A0_0.WeightedNearbyInterestingEntities = {}\n"
                "    A0_0.WeightedNearbyInterestingEntitiesHigh = {}\n"
                "    Targeting.SetInTargetingCamera(A0_0.Player, true)\n"
                "    if not A0_0.Camera or not A0_0.Camera:IsAlive() then\n"
                "    Debug.Error(\"AdamL:SACCamera's self.Camera is invalid at the end of SACCamera:Activate, please remember what has just happened and grab me.\")\n"
                "    end\n"
                "    GUI.SetShouldDrawPlayerIcons(true)\n",
            },
            {
                "    A0_0.WeightedNearbyFoes = A0_0\n"
                "    A0_0.WeightedNearbyInterestingEntities = A0_0\n"
                "    A0_0.WeightedNearbyInterestingEntitiesHigh = A0_0\n"
                "    L5_5 = L5_5.SetInTargetingCamera\n"
                "    L5_5(A0_0.Player, true)\n"
                "    L5_5 = A0_0.Camera\n"
                "    if not L5_5 or not L5_5.IsAlive:IsAlive(L5_5) then\n"
                "    L5_5 = L5_5.Error\n"
                "    L5_5(\"AdamL:SACCamera's self.Camera is invalid at the end of SACCamera:Activate, please remember what has just happened and grab me.\")\n"
                "    end\n"
                "    L5_5 = L5_5.SetShouldDrawPlayerIcons\n"
                "    L5_5(true)\n",
                "    A0_0.WeightedNearbyFoes = {}\n"
                "    A0_0.WeightedNearbyInterestingEntities = {}\n"
                "    A0_0.WeightedNearbyInterestingEntitiesHigh = {}\n"
                "    Targeting.SetInTargetingCamera(A0_0.Player, true)\n"
                "    if not A0_0.Camera or not A0_0.Camera:IsAlive() then\n"
                "    Debug.Error(\"AdamL:SACCamera's self.Camera is invalid at the end of SACCamera:Activate, please remember what has just happened and grab me.\")\n"
                "    end\n"
                "    GUI.SetShouldDrawPlayerIcons(true)\n",
            },
        });
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.Activate = function(A0_0, A1_1, A2_2, A3_3)\n"
        "    if not A2_2 or not A2_2:IsAlive() then\n"
        "    Debug.Error(\"AdamL:SACCamera:Activate's camera parameter is invalid, please remember what has just happened and grab me.\")\n"
        "    end\n"
        "    A0_0.Player = A1_1\n"
        "    A0_0.Primary = A0_0.Player\n"
        "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
        "    end\n"
        "    A0_0.Secondary = L4_4\n"
        "    A0_0:SetOverriddenData(CameraManager.GetActiveOverriddenData(A1_1).OverrideData)\n"
        "    if A0_0:GetOverriddenData().ZoomData then\n"
        "    A0_0:SetOverriddenZoomLevel(A0_0:GetOverriddenData().ZoomData)\n"
        "    end\n"
        "    A0_0:EstablishMyZoomLevels()\n"
        "    A0_0:CalculateDesiredCombatScale()\n"
        "    A0_0.CombatScale = A0_0.DesiredCombatScale\n"
        "    A0_0:CalculateActiveZoomLevel()\n"
        "    if not A0_0.PreviouslyActivated or SACCamera.SetBehindHero then\n"
        "    if A0_0.MoveToFaceTargetOnActivation then\n"
        "    A0_0:MoveToFaceTarget(A2_2, A1_1)\n"
        "    else\n"
        "    if A3_3 then\n"
        "    if A3_3:IsAlive() then\n"
        "    if SACCamera.SetBehindHero then\n"
        "    A0_0:MoveToBehindTarget(A2_2, A1_1, (not SACCamera.SetBehindHero))\n"
        "    SACCamera.SetBehindHero = false\n"
        "    end\n"
        "    end\n"
        "    else\n"
        "    A0_0:MoveToBehindTarget(A2_2, A1_1, (not SACCamera.SetBehindHero))\n"
        "    SACCamera.SetBehindHero = false\n"
        "    end\n"
        "    end\n"
        "    A0_0.PreviouslyActivated = true\n"
        "    end\n"
        "    if not A0_0.PreBoundCamPos then\n"
        "    end\n"
        "    A0_0.PreBoundCamPos = L4_4\n"
        "    if not A0_0.PreBoundFocalPos then\n"
        "    local L4_4 = (A2_2:GetPosition() + CVector3((0), (0), A0_0:GetActiveZoomLevel().FocalOffset))\n"
        "    end\n"
        "    A0_0.PreBoundFocalPos = L4_4\n"
        "    A0_0.LastTargetPosition = A0_0.PreBoundCamPos\n"
        "    if SACCamera.SmoothBlend then\n"
        "    A0_0.BlendFrameDuration = 100\n"
        "    else\n"
        "    L4_4 = (Timing.GetProgramFrame() - FirstPersonCamera.FrameActivated)\n"
        "    if Timing.GetProgramFrame() - FirstPersonCamera.FrameActivated < 5 then\n"
        "    A0_0.BlendFrameDuration = 0\n"
        "    CameraBase.BlendOut = nil\n"
        "    A0_0:SetUpToCutBehindHero()\n"
        "    else\n"
        "    if not A0_0.BlendFrameDuration then\n"
        "    A0_0.BlendFrameDuration = 50\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    CameraBase.Activate(A0_0, A1_1, A2_2, A3_3)\n"
        "    if true then\n"
        "    if A3_3 then\n"
        "    if A3_3:IsAlive() then\n"
        "    if Camera.DoesCameraRayIntersect(A0_0.Camera, A3_3:GetPosition(), A0_0.PreBoundCamPos, 0) then\n"
        "    A0_0:CutFromPreviousCamera()\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if A0_0.BlendFrameDuration == 0 then\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    end\n"
        "    L5_5 = L5_5(A0_0.Player)\n"
        "    A0_0.PrevLookAtDown = L5_5\n"
        "    A0_0:SetFOV(A0_0:GetActiveZoomLevel().FOV)\n"
        "    L5_5 = L5_5.SetZoomLevel\n"
        "    L5_5(A0_0.Camera, A0_0.ZoomLevel)\n"
        "    L5_5 = L5_5()\n"
        "    A0_0.InitialFrame = L5_5\n"
        "    A0_0.StartInterpolate = false\n"
        "    A0_0.SafeToSwitchDistance = true\n"
        "    A0_0.SlopeAngle = 0\n"
        "    A0_0.ManualHeightOffset = 0\n"
        "    A0_0.WeightedNearbyFoes = {}\n"
        "    A0_0.WeightedNearbyInterestingEntities = {}\n"
        "    A0_0.WeightedNearbyInterestingEntitiesHigh = {}\n"
        "    Targeting.SetInTargetingCamera(A0_0.Player, true)\n"
        "    if not A0_0.Camera or not A0_0.Camera:IsAlive() then\n"
        "    Debug.Error(\"AdamL:SACCamera's self.Camera is invalid at the end of SACCamera:Activate, please remember what has just happened and grab me.\")\n"
        "    end\n"
        "    GUI.SetShouldDrawPlayerIcons(true)\n"
        "  end\n",
        "  SACCamera.Activate = function(A0_0, A1_1, A2_2, A3_3)\n"
        "    if not A2_2 or not A2_2:IsAlive() then\n"
        "    Debug.Error(\"AdamL:SACCamera:Activate's camera parameter is invalid, please remember what has just happened and grab me.\")\n"
        "    end\n"
        "    A0_0.Player = A1_1\n"
        "    A0_0.Primary = A0_0.Player\n"
        "    A0_0.Secondary = CameraFunctions.IsMultiplayer(A0_0.Player) and GetPlayerHenchman()\n"
        "    A0_0:SetOverriddenData(CameraManager.GetActiveOverriddenData(A1_1).OverrideData)\n"
        "    if A0_0:GetOverriddenData().ZoomData then\n"
        "    A0_0:SetOverriddenZoomLevel(A0_0:GetOverriddenData().ZoomData)\n"
        "    end\n"
        "    A0_0:EstablishMyZoomLevels()\n"
        "    A0_0:CalculateDesiredCombatScale()\n"
        "    A0_0.CombatScale = A0_0.DesiredCombatScale\n"
        "    A0_0:CalculateActiveZoomLevel()\n"
        "    if not A0_0.PreviouslyActivated or SACCamera.SetBehindHero then\n"
        "    if A0_0.MoveToFaceTargetOnActivation then\n"
        "    A0_0:MoveToFaceTarget(A2_2, A1_1)\n"
        "    else\n"
        "    if not A3_3 or not A3_3:IsAlive() or SACCamera.SetBehindHero then\n"
        "    A0_0:MoveToBehindTarget(A2_2, A1_1, (not SACCamera.SetBehindHero))\n"
        "    SACCamera.SetBehindHero = false\n"
        "    end\n"
        "    end\n"
        "    A0_0.PreviouslyActivated = true\n"
        "    end\n"
        "    A0_0.PreBoundCamPos = A0_0.PreBoundCamPos or A2_2:GetPosition()\n"
        "    A0_0.PreBoundFocalPos = (A0_0.PreBoundFocalPos or (A2_2:GetPosition() + CVector3((0), (0), A0_0:GetActiveZoomLevel().FocalOffset)))\n"
        "    A0_0.LastTargetPosition = A0_0.PreBoundCamPos\n"
        "    if SACCamera.SmoothBlend then\n"
        "    A0_0.BlendFrameDuration = 100\n"
        "    else\n"
        "    if Timing.GetProgramFrame() - FirstPersonCamera.FrameActivated < 5 then\n"
        "    A0_0.BlendFrameDuration = 0\n"
        "    CameraBase.BlendOut = nil\n"
        "    A0_0:SetUpToCutBehindHero()\n"
        "    else\n"
        "    if not A0_0.BlendFrameDuration then\n"
        "    A0_0.BlendFrameDuration = 50\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    CameraBase.Activate(A0_0, A1_1, A2_2, A3_3)\n"
        "    local L4_4 = true\n"
        "    if L4_4 and A3_3 and A3_3:IsAlive() then\n"
        "    local L5_5 = A3_3:GetPosition()\n"
        "    if Camera.DoesCameraRayIntersect(A0_0.Camera, L5_5, A0_0.PreBoundCamPos, 0) then\n"
        "    A0_0:CutFromPreviousCamera()\n"
        "    end\n"
        "    end\n"
        "    if A0_0.BlendFrameDuration == 0 then\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    end\n"
        "    A0_0.PrevLookAtDown = CameraFunctions.GetSafeLookAtButtonDown(A0_0.Player)\n"
        "    A0_0:SetFOV(A0_0:GetActiveZoomLevel().FOV)\n"
        "    Camera.SetZoomLevel(A0_0.Camera, A0_0.ZoomLevel)\n"
        "    A0_0.InitialFrame = Timing.GetProgramFrame()\n"
        "    A0_0.StartInterpolate = false\n"
        "    A0_0.SafeToSwitchDistance = true\n"
        "    A0_0.SlopeAngle = 0\n"
        "    A0_0.ManualHeightOffset = 0\n"
        "    A0_0.WeightedNearbyFoes = {}\n"
        "    A0_0.WeightedNearbyInterestingEntities = {}\n"
        "    A0_0.WeightedNearbyInterestingEntitiesHigh = {}\n"
        "    Targeting.SetInTargetingCamera(A0_0.Player, true)\n"
        "    if not A0_0.Camera or not A0_0.Camera:IsAlive() then\n"
        "    Debug.Error(\"AdamL:SACCamera's self.Camera is invalid at the end of SACCamera:Activate, please remember what has just happened and grab me.\")\n"
        "    end\n"
        "    GUI.SetShouldDrawPlayerIcons(true)\n"
        "  end\n");

    changed |= ApplyLiteralRules(
        source,
        {
            {
                "    if not L4_4 then\n"
                "    end\n",
                "    if not L4_4 then\n"
                "    L4_4 = A0_0.StoredDesiredInsideBuilding\n"
                "    end\n",
            },
            {
                "  SACCamera.GetActiveZoomBone = function(A0_0)\n"
                "    if not A0_0:GetActiveZoomLevel().SpecificBone then\n"
                "    return nil\n"
                "    end\n"
                "    return r1.SpecificBone\n"
                "  end\n",
                "  SACCamera.GetActiveZoomBone = function(A0_0)\n"
                "    return A0_0:GetActiveZoomLevel().SpecificBone or nil\n"
                "  end\n",
            },
            {
                "  SACCamera.GetCentre = function(A0_0, A1_1, A2_2)\n"
                "    if Creature.IsAvailable(A1_1) then\n"
                "    local r3 = Creature.GetCollisionHeadPosition(A1_1)\n"
                "    r3:SetZ((r3:GetZ() + A2_2))\n"
                "    return r3\n"
                "    end\n"
                "    local r6 = CameraFunctions.GetObjectPosition(A1_1)\n"
                "    A1_1:GetPosition():SetZ((r6.GetZ:GetZ(r6) + A2_2))\n"
                "    return r3\n"
                "  end\n",
                "  SACCamera.GetCentre = function(A0_0, A1_1, A2_2)\n"
                "    if Creature.IsAvailable(A1_1) then\n"
                "    local L3_3 = Creature.GetCollisionHeadPosition(A1_1)\n"
                "    L3_3:SetZ((L3_3:GetZ() + A2_2))\n"
                "    return L3_3\n"
                "    end\n"
                "    local L3_3 = A1_1:GetPosition()\n"
                "    L3_3:SetZ((CameraFunctions.GetObjectPosition(A1_1):GetZ() + A2_2))\n"
                "    return L3_3\n"
                "  end\n",
            },
            {
                "  SACCamera.TendFocusBasedOnDistanceToCollisionCentre = function(A0_0, A1_1, A2_2, A3_3)\n"
                "    local L4_4 = (A2_2 - A3_3)\n"
                "    L4_4 = L4_4:GetLength()\n"
                "    if SACCamera.ClosenessToTiltUpStart < (A2_2 - A3_3).GetLength:GetLength((A2_2 - A3_3)):GetLength() then\n"
                "    return A1_1, A2_2\n"
                "    end\n"
                "    local L5_5 = A1_1 - A2_2\n"
                "    L5_5:SetZ((0))\n"
                "    L5_5:Normalise()\n"
                "    A1_1:SetZ(CameraFunctions.Interpolate(A1_1:GetZ(), math.max(math.min(((SACCamera.ClosenessToTiltUpStart - L4_4) / (SACCamera.ClosenessToTiltUpStart - SACCamera.ClosenessToTiltUpMax)), (1)), (0)), A3_3:GetZ()))\n"
                "    local L7_7 = (L7_7 + (L5_5 * L6_6))\n"
                "    local L10_10 = A2_2\n"
                "    L10_10:SetZ(CameraFunctions.Interpolate(L10_10:GetZ(), L6_6, A3_3:GetZ()))\n"
                "    return L7_7, L10_10\n"
                "  end\n",
                "  SACCamera.TendFocusBasedOnDistanceToCollisionCentre = function(A0_0, A1_1, A2_2, A3_3)\n"
                "    local L4_4 = (A2_2 - A3_3):GetLength()\n"
                "    if SACCamera.ClosenessToTiltUpStart < L4_4 then\n"
                "    return A1_1, A2_2\n"
                "    end\n"
                "    local L5_5 = A1_1 - A2_2\n"
                "    L5_5:SetZ((0))\n"
                "    L5_5:Normalise()\n"
                "    local L6_6 = (SACCamera.ClosenessToTiltUpStart - L4_4) / (SACCamera.ClosenessToTiltUpStart - SACCamera.ClosenessToTiltUpMax)\n"
                "    L6_6 = math.min(L6_6, 1)\n"
                "    L6_6 = math.max(L6_6, 0)\n"
                "    local L7_7 = A1_1\n"
                "    local L8_8 = CameraFunctions.Interpolate(A1_1:GetZ(), L6_6, A3_3:GetZ())\n"
                "    L7_7:SetZ(L8_8)\n"
                "    L7_7 = L7_7 + L5_5 * L6_6\n"
                "    local L9_9 = A2_2\n"
                "    local L10_10 = CameraFunctions.Interpolate(A2_2:GetZ(), L6_6, A3_3:GetZ())\n"
                "    L9_9:SetZ(L10_10)\n"
                "    return L7_7, L9_9\n"
                "  end\n",
            },
            {
                "  SACCamera.CalculateSlotShrinkage = function(A0_0, A1_1)\n"
                "    if not A1_1 then\n"
                "    return (1)\n"
                "    end\n"
                "    if (A1_1 / Timing.GetTickRate()) < SACCamera.TimeToStartShrinking then\n"
                "    return (1)\n"
                "    end\n"
                "    if SACCamera.TimeToEndShrinking < r2 then\n"
                "    return (1)\n"
                "    end\n"
                "    return ((1) - (((1) - SACCamera.MaxShrinkageFactor) * ((r2 - SACCamera.TimeToStartShrinking) / (SACCamera.TimeToEndShrinking - SACCamera.TimeToStartShrinking))))\n"
                "  end\n",
                "  SACCamera.CalculateSlotShrinkage = function(A0_0, A1_1)\n"
                "    if not A1_1 then\n"
                "    return (1)\n"
                "    end\n"
                "    local L2_2 = A1_1 / Timing.GetTickRate()\n"
                "    if L2_2 < SACCamera.TimeToStartShrinking then\n"
                "    return (1)\n"
                "    end\n"
                "    if SACCamera.TimeToEndShrinking < L2_2 then\n"
                "    return (1)\n"
                "    end\n"
                "    local L3_3 = (L2_2 - SACCamera.TimeToStartShrinking) / (SACCamera.TimeToEndShrinking - SACCamera.TimeToStartShrinking)\n"
                "    return 1 - ((1 - SACCamera.MaxShrinkageFactor) * L3_3)\n"
                "  end\n",
            },
            {
                "  SACCamera.UpdateEdgeFrames = function(A0_0, A1_1, A2_2, A3_3)\n"
                "    if not A1_1 then\n"
                "    if not A2_2 then\n"
                "    if A3_3 then\n"
                "    A0_0.FramesOutsideEdge = (A0_0.FramesOutsideEdge + (1))\n"
                "    A0_0.FramesInsideEdge = 0\n"
                "    end\n"
                "    end\n"
                "    else\n"
                "    A0_0.FramesOutsideEdge = (A0_0.FramesOutsideEdge + (1))\n"
                "    A0_0.FramesInsideEdge = 0\n"
                "    end\n"
                "    A0_0.FramesOutsideEdge = 0\n"
                "    A0_0.FramesInsideEdge = (A0_0.FramesInsideEdge + (1))\n"
                "    if A1_1 then\n"
                "    A0_0.FramesOutsideSide = (A0_0.FramesOutsideSide + (1))\n"
                "    else\n"
                "    A0_0.FramesOutsideSide = 0\n"
                "    end\n"
                "    if A2_2 then\n"
                "    A0_0.FramesOutsideFront = (A0_0.FramesOutsideFront + (1))\n"
                "    else\n"
                "    A0_0.FramesOutsideFront = 0\n"
                "    end\n"
                "    if A3_3 then\n"
                "    A0_0.FramesOutsideBack = (A0_0.FramesOutsideBack + (1))\n"
                "    else\n"
                "    A0_0.FramesOutsideBack = 0\n"
                "    end\n"
                "  end\n",
                "  SACCamera.UpdateEdgeFrames = function(A0_0, A1_1, A2_2, A3_3)\n"
                "    if A1_1 or A2_2 or A3_3 then\n"
                "    A0_0.FramesOutsideEdge = (A0_0.FramesOutsideEdge + (1))\n"
                "    A0_0.FramesInsideEdge = 0\n"
                "    else\n"
                "    A0_0.FramesOutsideEdge = 0\n"
                "    A0_0.FramesInsideEdge = (A0_0.FramesInsideEdge + (1))\n"
                "    end\n"
                "    if A1_1 then\n"
                "    A0_0.FramesOutsideSide = (A0_0.FramesOutsideSide + (1))\n"
                "    else\n"
                "    A0_0.FramesOutsideSide = 0\n"
                "    end\n"
                "    if A2_2 then\n"
                "    A0_0.FramesOutsideFront = (A0_0.FramesOutsideFront + (1))\n"
                "    else\n"
                "    A0_0.FramesOutsideFront = 0\n"
                "    end\n"
                "    if A3_3 then\n"
                "    A0_0.FramesOutsideBack = (A0_0.FramesOutsideBack + (1))\n"
                "    else\n"
                "    A0_0.FramesOutsideBack = 0\n"
                "    end\n"
                "  end\n",
            },
            {
                "  SACCamera.LazyEvaluateFramesOutside = function(A0_0)\n"
                "    A0_0.FramesOutsideEdge = (((not A0_0.FramesOutsideEdge) and (0)) or A0_0.FramesOutsideEdge)\n"
                "    local L1_1 = A0_0.FramesInsideEdge\n"
                "    A0_0.FramesInsideEdge = (((not L1_1) and (0)) or L1_1)\n"
                "    L1_1 = A0_0.FramesOutsideSide\n"
                "    A0_0.FramesOutsideSide = (((not L1_1) and (0)) or L1_1)\n"
                "    L1_1 = A0_0.FramesOutsideFront\n"
                "    A0_0.FramesOutsideFront = (((not L1_1) and (0)) or L1_1)\n"
                "    L1_1 = A0_0.FramesOutsideBack\n"
                "    A0_0.FramesOutsideBack = (((not L1_1) and (0)) or L1_1)\n"
                "  end\n",
                "  SACCamera.LazyEvaluateFramesOutside = function(A0_0)\n"
                "    A0_0.FramesOutsideEdge = A0_0.FramesOutsideEdge or 0\n"
                "    A0_0.FramesInsideEdge = A0_0.FramesInsideEdge or 0\n"
                "    A0_0.FramesOutsideSide = A0_0.FramesOutsideSide or 0\n"
                "    A0_0.FramesOutsideFront = A0_0.FramesOutsideFront or 0\n"
                "    A0_0.FramesOutsideBack = A0_0.FramesOutsideBack or 0\n"
                "  end\n",
            },
        });
    changed |= ApplyLiteralRules(
        source,
        {
            {
                "    if not A2_2 then\n"
                "    local A2_2 = 0\n"
                "    end\n"
                "    if not A3_3 then\n"
                "    local A3_3 = 0\n"
                "    end\n"
                "    if not A0_0:GetActiveZoomLevel().SlotWidth or not math.min((A0_0:GetActiveZoomLevel().SlotWidth - A3_3), A2_2) then\n"
                "    local L4_4 = 0\n"
                "    end\n"
                "    local L5_5 = ((A0_0:GetActiveZoomLevel().SlotDepth and math.min((A0_0:GetActiveZoomLevel().SlotDepth - A3_3), A2_2)) or (0))\n",
                "    if not A2_2 then\n"
                "    A2_2 = 0\n"
                "    end\n"
                "    if not A3_3 then\n"
                "    A3_3 = 0\n"
                "    end\n"
                "    local L4_4 = ((A0_0:GetActiveZoomLevel().SlotWidth and math.min((A0_0:GetActiveZoomLevel().SlotWidth - A3_3), A2_2)) or (0))\n"
                "    local L5_5 = ((A0_0:GetActiveZoomLevel().SlotDepth and math.min((A0_0:GetActiveZoomLevel().SlotDepth - A3_3), A2_2)) or (0))\n",
            },
            {
                "    if SACCamera.ShrinkSlot then\n"
                "    if not SACCamera.OnlyShrinkOnSide or not A0_0.FramesOutsideSide then\n"
                "    end\n"
                "    L4_4 = (L4_4 * A0_0:CalculateSlotShrinkage(L8_8))\n"
                "    if not SACCamera.OnlyShrinkOnSide or not A0_0.FramesOutsideSide then\n"
                "    end\n"
                "    L5_5 = (L5_5 * A0_0:CalculateSlotShrinkage(L8_8))\n"
                "    end\n",
                "    if SACCamera.ShrinkSlot then\n"
                "    L4_4 = L4_4 * A0_0:CalculateSlotShrinkage(SACCamera.OnlyShrinkOnSide and A0_0.FramesOutsideSide or A0_0.FramesOutsideEdge)\n"
                "    L5_5 = L5_5 * A0_0:CalculateSlotShrinkage(SACCamera.OnlyShrinkOnSide and A0_0.FramesOutsideSide or A0_0.FramesOutsideEdge)\n"
                "    end\n",
            },
            {
                "    if not A0_0.PreviousInputCentre then\n"
                "    A0_0.PreviousInputCentre = A1_1\n"
                "    else\n"
                "    A0_0.PreviousInputCentre = L6_6\n"
                "    end\n"
                "    if SACCamera.VelocityThresholdToPush / Timing.GetTickRate() < (A1_1 - A0_0.PreviousInputCentre):GetLength() then\n",
                "    A0_0.PreviousInputCentre = A0_0.PreviousInputCentre or A1_1\n"
                "    local L6_6 = A1_1 - A0_0.PreviousInputCentre\n"
                "    local L7_7 = L6_6:GetLength()\n"
                "    if SACCamera.VelocityThresholdToPush / Timing.GetTickRate() < L7_7 then\n",
            },
            {
                "    if SACCamera.VelocityThresholdToPush / Timing.GetTickRate() < (A1_1 - A0_0.PreviousInputCentre):GetLength() then\n"
                "    local L7_7 = true\n",
                "    if SACCamera.VelocityThresholdToPush / Timing.GetTickRate() < L7_7 then\n"
                "    L7_7 = true\n",
            },
            {
                "    if SACCamera.VelocityThresholdToPush / Timing.GetTickRate() < L7_7 then\n"
                "    local L7_7 = true\n",
                "    if SACCamera.VelocityThresholdToPush / Timing.GetTickRate() < L7_7 then\n"
                "    L7_7 = true\n",
            },
            {
                "    if SACCamera.VelocityThresholdToPush / Timing.GetTickRate() < L7_7 then\n"
                "    L7_7 = true\n"
                "    else\n"
                "    L7_7 = false\n"
                "    end\n",
                "    L7_7 = SACCamera.VelocityThresholdToPush / Timing.GetTickRate() < L7_7 and true\n",
            },
            {
                "    A0_0.DesiredSlotOffset = A0_0.DesiredSlotOffset - L13_13 * SACCamera.TurnDriftRate\n"
                "    local L17_17 = A0_0.DesiredSlotOffset\n"
                "    local L18_18 = 0 < A0_0.DesiredSlotOffset and SACCamera.DriftRate or -SACCamera.DriftRate\n"
                "    A0_0.DesiredSlotOffset = L17_17 + L18_18\n",
                "    A0_0.DesiredSlotOffset = A0_0.DesiredSlotOffset - L13_13 * SACCamera.TurnDriftRate\n"
                "    A0_0.DesiredSlotOffset = A0_0.DesiredSlotOffset + (0 < A0_0.DesiredSlotOffset and SACCamera.DriftRate or -SACCamera.DriftRate)\n",
            },
            {
                "    if not L15_15 and not L16_16 and not L14_14 then\n"
                "    A0_0.DesiredSlotOffset = A0_0.DesiredSlotOffset or L12_12\n"
                "    end\n",
                "    if L15_15 or L16_16 or L14_14 then\n"
                "    A0_0.DesiredSlotOffset = A0_0.DesiredSlotOffset or L12_12\n"
                "    end\n",
            },
            {
                "    if not L15_15 and not L16_16 and L14_14 then\n"
                "    A0_0.DesiredSlotOffset = A0_0.DesiredSlotOffset or L12_12\n"
                "    end\n",
                "    if L15_15 or L16_16 or L14_14 then\n"
                "    A0_0.DesiredSlotOffset = A0_0.DesiredSlotOffset or L12_12\n"
                "    end\n",
            },
        });
    changed |= ApplyLiteralRules(
        source,
        {
            {
                "  SACCamera.GetLimitedPitchBetweenPositions = function(A0_0, A1_1, A2_2)\n"
                "    local r5 = (A1_1 - A2_2)\n"
                "    pitch = math.atan(((Camera.GetGroundPositionAtPosition(A0_0.Camera, A1_1):GetZ() - Camera.GetGroundPositionAtPosition(A0_0.Camera, A2_2):GetZ()) / r5:GetLengthXY()))\n"
                "    if SACCamera.IgnorePitch < math.abs(pitch) then\n"
                "    pitch = (0)\n"
                "    end\n"
                "    pitch = math.min(SACCamera.LimitPitch, pitch)\n"
                "    pitch = math.max((-SACCamera.LimitPitch), pitch)\n"
                "    return pitch\n"
                "  end\n",
                "  SACCamera.GetLimitedPitchBetweenPositions = function(A0_0, A1_1, A2_2)\n"
                "    pitch = math.atan(((Camera.GetGroundPositionAtPosition(A0_0.Camera, A1_1):GetZ() - Camera.GetGroundPositionAtPosition(A0_0.Camera, A2_2):GetZ()) / (A1_1 - A2_2):GetLengthXY()))\n"
                "    if SACCamera.IgnorePitch < math.abs(pitch) then\n"
                "    pitch = (0)\n"
                "    end\n"
                "    pitch = math.min(SACCamera.LimitPitch, pitch)\n"
                "    pitch = math.max((-SACCamera.LimitPitch), pitch)\n"
                "    return pitch\n"
                "  end\n",
            },
            {
                "  SACCamera.GetLimitedPitchBetweenPositions = function(A0_0, A1_1, A2_2)\n"
                "    pitch = math.atan(((Camera.GetGroundPositionAtPosition(A0_0.Camera, A1_1):GetZ() - Camera.GetGroundPositionAtPosition(A0_0.Camera, A2_2):GetZ()) / (A1_1 - A2_2):GetLengthXY()))\n"
                "    if SACCamera.IgnorePitch < math.abs(pitch) then\n"
                "    pitch = (0)\n"
                "    end\n"
                "    pitch = math.min(SACCamera.LimitPitch, pitch)\n"
                "    pitch = math.max((-SACCamera.LimitPitch), pitch)\n"
                "    return pitch\n"
                "  end\n",
                "  SACCamera.GetLimitedPitchBetweenPositions = function(A0_0, A1_1, A2_2)\n"
                "    local L3_3 = Camera.GetGroundPositionAtPosition(A0_0.Camera, A2_2)\n"
                "    local L4_4 = Camera.GetGroundPositionAtPosition(A0_0.Camera, A1_1)\n"
                "    local L5_5 = (A1_1 - A2_2):GetLengthXY()\n"
                "    pitch = math.atan((L4_4:GetZ() - L3_3:GetZ()) / L5_5)\n"
                "    if math.abs(pitch) > SACCamera.IgnorePitch then\n"
                "    pitch = (0)\n"
                "    end\n"
                "    pitch = math.min(SACCamera.LimitPitch, pitch)\n"
                "    pitch = math.max((-SACCamera.LimitPitch), pitch)\n"
                "    return pitch\n"
                "  end\n",
            },
        });
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.GetDesiredCombatCamWeight = function(A0_0)\n"
        "    if Timing.GetProgramFrame() ~= A0_0.CombatCamWeightCalculateFrame then\n"
        "    A0_0.CombatCamWeightCalculateFrame = Timing.GetProgramFrame()\n"
        "    A0_0.CombatWeightThisFrame = 0\n"
        "    if A0_0.NearbyFoes == nil or A0_0.NearbyFoes[1] == nil then\n"
        "    return A0_0.CombatWeightThisFrame\n"
        "    end\n"
        "    if A0_0.Player == nil then\n"
        "    return A0_0.CombatWeightThisFrame\n"
        "    end\n"
        "    for r5, r6 in ipairs(A0_0.NearbyFoes) do\n"
        "    if r6:IsAlive() then\n"
        "    if A0_0.Primary then\n"
        "    local r9 = (r6:GetPosition() - A0_0.Primary:GetPosition())\n"
        "    if A0_0.Secondary then\n"
        "    r9 = (r6:GetPosition() - A0_0.Secondary:GetPosition())\n"
        "    end\n"
        "    else\n"
        "    r9 = (r6:GetPosition() - A0_0.Player:GetPosition())\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    A0_0.CombatWeightThisFrame = ((SACCamera.CombatValues.FullHeroCamDistance - L1_1) / (SACCamera.CombatValues.FullHeroCamDistance - SACCamera.CombatValues.FullCombatCamDistance))\n"
        "    A0_0.CombatWeightThisFrame = math.min(A0_0.CombatWeightThisFrame, (1))\n"
        "    A0_0.CombatWeightThisFrame = math.max(A0_0.CombatWeightThisFrame, (0))\n"
        "    end\n"
        "    return A0_0.CombatWeightThisFrame\n"
        "  end\n",
        "  SACCamera.GetDesiredCombatCamWeight = function(A0_0)\n"
        "    if Timing.GetProgramFrame() ~= A0_0.CombatCamWeightCalculateFrame then\n"
        "    A0_0.CombatCamWeightCalculateFrame = Timing.GetProgramFrame()\n"
        "    A0_0.CombatWeightThisFrame = 0\n"
        "    if A0_0.NearbyFoes == nil or A0_0.NearbyFoes[1] == nil then\n"
        "    return A0_0.CombatWeightThisFrame\n"
        "    end\n"
        "    if A0_0.Player == nil then\n"
        "    return A0_0.CombatWeightThisFrame\n"
        "    end\n"
        "    local L1_1 = SACCamera.CombatValues.FullHeroCamDistance\n"
        "    for r5, r6 in ipairs(A0_0.NearbyFoes) do\n"
        "    if r6:IsAlive() then\n"
        "    if A0_0.Primary then\n"
        "    L1_1 = math.min(L1_1, (r6:GetPosition() - A0_0.Primary:GetPosition()):GetLengthXY())\n"
        "    if A0_0.Secondary then\n"
        "    L1_1 = math.min(L1_1, (r6:GetPosition() - A0_0.Secondary:GetPosition()):GetLengthXY())\n"
        "    end\n"
        "    else\n"
        "    L1_1 = math.min(L1_1, (r6:GetPosition() - A0_0.Player:GetPosition()):GetLengthXY())\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    A0_0.CombatWeightThisFrame = ((SACCamera.CombatValues.FullHeroCamDistance - L1_1) / (SACCamera.CombatValues.FullHeroCamDistance - SACCamera.CombatValues.FullCombatCamDistance))\n"
        "    A0_0.CombatWeightThisFrame = math.min(A0_0.CombatWeightThisFrame, (1))\n"
        "    A0_0.CombatWeightThisFrame = math.max(A0_0.CombatWeightThisFrame, (0))\n"
        "    end\n"
        "    return A0_0.CombatWeightThisFrame\n"
        "  end\n");
    changed |= ApplyLiteralRules(
        source,
        {
            {
                "  SACCamera.GetCombatScaleFromEntity = function(A0_0, A1_1)\n"
                "    if A1_1:IsAlive() then\n"
                "    if creature_type == ECreatureType.CREATURE_SHARD then\n"
                "    if (A1_1:GetPosition() - A0_0.Player:GetPosition()).GetLengthXY:GetLengthXY((A1_1:GetPosition() - A0_0.Player:GetPosition())):GetLengthXY() < 25 then\n"
                "    A0_0.DesiredCombatScale = 1.2999999523162842\n"
                "    return true\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return false\n"
                "  end\n",
                "  SACCamera.GetCombatScaleFromEntity = function(A0_0, A1_1)\n"
                "    if A1_1:IsAlive() then\n"
                "    if creature_type == ECreatureType.CREATURE_SHARD then\n"
                "    if (A1_1:GetPosition() - A0_0.Player:GetPosition()):GetLengthXY() < 25 then\n"
                "    A0_0.DesiredCombatScale = 1.2999999523162842\n"
                "    return true\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    return false\n"
                "  end\n",
            },
            {
                "  SACCamera.CalculateDesiredCombatScale = function(A0_0)\n"
                "    A0_0.DesiredCombatScale = 1\n"
                "    local L5_5 = A0_0\n"
                "    L5_5[1] = L5_5.Player\n"
                "    if CameraFunctions.IsMultiplayer(L5_5.Player) then\n"
                "    table.insert(L5_5, GetPlayerHenchman())\n"
                "    end\n"
                "    for r5, r6 in ipairs(L5_5) do\n"
                "    for r11, r12 in ipairs(CombatRegister:GetEntitiesAttackingEntity(r6)) do\n"
                "    if A0_0:GetCombatScaleFromEntity(r12) then\n"
                "    return\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "  end\n",
                "  SACCamera.CalculateDesiredCombatScale = function(A0_0)\n"
                "    A0_0.DesiredCombatScale = 1\n"
                "    local L1_1 = {A0_0.Player}\n"
                "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
                "    table.insert(L1_1, GetPlayerHenchman())\n"
                "    end\n"
                "    for r5, r6 in ipairs(L1_1) do\n"
                "    local L7_7 = CombatRegister:GetEntitiesAttackingEntity(r6)\n"
                "    for r11, r12 in ipairs(L7_7) do\n"
                "    if A0_0:GetCombatScaleFromEntity(r12) then\n"
                "    return\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "  end\n",
            },
        });
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.GetInterestingPoint = function(A0_0)\n"
        "    if A0_0:GetOverriddenData().InterestingObject then\n"
        "    if A0_0:GetOverriddenData().InterestingObject.IsAlive:IsAlive(A0_0:GetOverriddenData().InterestingObject) then\n"
        "    A0_0:GetOverriddenData().InterestingPoint = A0_0:GetOverriddenData().InterestingObject.GetPosition:GetPosition(A0_0:GetOverriddenData().InterestingObject)\n"
        "    end\n"
        "    end\n"
        "    return A0_0:GetOverriddenData().InterestingPoint\n"
        "  end\n",
        "  SACCamera.GetInterestingPoint = function(A0_0)\n"
        "    if A0_0:GetOverriddenData().InterestingObject then\n"
        "    if A0_0:GetOverriddenData().InterestingObject:IsAlive() then\n"
        "    local __refii_GetOverriddenData_4 = A0_0:GetOverriddenData()\n"
        "    __refii_GetOverriddenData_4.InterestingPoint = A0_0:GetOverriddenData().InterestingObject:GetPosition()\n"
        "    end\n"
        "    end\n"
        "    return A0_0:GetOverriddenData().InterestingPoint\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.SupportHysteresis = function(A0_0, A1_1, A2_2, A3_3, A4_4, A5_5)\n"
        "    if A1_1 == nil then\n"
        "    if A2_2 then\n"
        "    else\n"
        "    end\n"
        "    return true\n"
        "    end\n"
        "    if A0_0[A3_3] == nil then\n"
        "    A0_0[A3_3] = 0\n"
        "    end\n"
        "    if A1_1 == A2_2 then\n"
        "    A0_0[A3_3] = 0\n"
        "    return A1_1\n"
        "    end\n"
        "    A0_0[A3_3] = (A0_0[A3_3] + (1))\n"
        "    if A2_2 == true then\n"
        "    if A4_4 < A0_0[A3_3] then\n"
        "    return true\n"
        "    end\n"
        "    return false\n"
        "    end\n"
        "    if A5_5 < A0_0[A3_3] then\n"
        "    return false\n"
        "    end\n"
        "    return true\n"
        "  end\n",
        "  SACCamera.SupportHysteresis = function(A0_0, A1_1, A2_2, A3_3, A4_4, A5_5)\n"
        "    if A1_1 == nil then\n"
        "    local L6_6 = A2_2 and true or false\n"
        "    return L6_6\n"
        "    end\n"
        "    if A0_0[A3_3] == nil then\n"
        "    A0_0[A3_3] = 0\n"
        "    end\n"
        "    if A1_1 == A2_2 then\n"
        "    A0_0[A3_3] = 0\n"
        "    return A1_1\n"
        "    end\n"
        "    A0_0[A3_3] = (A0_0[A3_3] + (1))\n"
        "    if A2_2 == true then\n"
        "    if A4_4 < A0_0[A3_3] then\n"
        "    return true\n"
        "    end\n"
        "    return false\n"
        "    end\n"
        "    if A5_5 < A0_0[A3_3] then\n"
        "    return false\n"
        "    end\n"
        "    return true\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "    if 0.05000000074505806 < A0_0.MovementThisFrame:GetLengthXY() then\n"
        "    return false\n"
        "    end\n"
        "    return true\n"
        "  end\n"
        "  SACCamera.SupportHysteresis = function(A0_0, A1_1, A2_2, A3_3, A4_4, A5_5)\n",
        "    if A0_0.MovementThisFrame:GetLengthXY() > 0.05000000074505806 then\n"
        "    return false\n"
        "    end\n"
        "    return true\n"
        "  end\n"
        "  SACCamera.SupportHysteresis = function(A0_0, A1_1, A2_2, A3_3, A4_4, A5_5)\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.CalculateActiveZoomLevel = function(A0_0)",
        "  SACCamera.CalculateActiveZoomLevel = function(A0_0)\n"
        "    A0_0.CurrentZoomSet = A0_0:CalculateGameZoomSet()\n"
        "    A0_0.ZoomLevel = math.min(A0_0.ZoomLevel, #A0_0.CurrentZoomSet)\n"
        "    A0_0.ZoomLevel = math.max(A0_0.ZoomLevel, 1)\n"
        "    A0_0.ActiveZoomLevel = A0_0.CurrentZoomSet[A0_0.ZoomLevel]\n"
        "    if A0_0.ActiveZoomLevel ~= A0_0.PreviousZoomLevel then\n"
        "      local L1_1 = A0_0.PreviousZoomLevel and A0_0.PreviousZoomLevel.SpecificBone ~= \"\" and A0_0.PreviousZoomLevel.SpecificBone or nil\n"
        "      local L2_2 = A0_0.ActiveZoomLevel.SpecificBone ~= \"\" and A0_0.ActiveZoomLevel.SpecificBone or nil\n"
        "      if not A0_0.PreviousZoomLevel or L1_1 ~= L2_2 then\n"
        "        A0_0.StartInterpolate = true\n"
        "        A0_0.SnapThisFrame = true\n"
        "        A0_0.BlendingZoomLevel = nil\n"
        "        A0_0.BlendFrameStart = nil\n"
        "        A0_0.BlendFrames = nil\n"
        "      else\n"
        "        A0_0.BlendFrameStart = Timing.GetProgramFrame()\n"
        "        A0_0.BlendFrames = previous_zoom_level and previous_zoom_level.BlendOutTime or A0_0.ActiveZoomLevel.BlendTime or SACCamera.CameraTransitionTime\n"
        "        A0_0:SetPulseBlur(SACCamera.TransitionBlur, A0_0.BlendFrames)\n"
        "        A0_0.BlendFrames = A0_0.BlendFrames * Timing.GetTickRate()\n"
        "        if not A0_0.BlendingZoomLevel then\n"
        "          A0_0.BlendingZoomLevel = A0_0.PreviousZoomLevel\n"
        "        end\n"
        "      end\n"
        "      A0_0.PreviousZoomLevel = A0_0.ActiveZoomLevel\n"
        "    end\n"
        "    local L1_1 = A0_0:IsBlending()\n"
        "    if L1_1 then\n"
        "      if 1 <= L1_1 then\n"
        "        A0_0.BlendingZoomLevel = nil\n"
        "        A0_0.BlendFrameStart = nil\n"
        "        A0_0.BlendFrames = nil\n"
        "      else\n"
        "        A0_0.BlendingZoomLevel = SACCamera.InterpolateZoomLevels(A0_0.BlendingZoomLevel, L1_1, A0_0.ActiveZoomLevel)\n"
        "        A0_0.ActiveZoomLevel = A0_0.BlendingZoomLevel\n"
        "      end\n"
        "    end\n"
        "    local L2_2 = A0_0:GetDesiredCombatCamWeight()\n"
        "    local L3_3 = A0_0.IsInsideBuilding and A0_0.MyCombatInteriorZoomLevel or A0_0.MyCombatExteriorZoomLevel\n"
        "    if A0_0.IsAssassin then\n"
        "      L2_2 = 0\n"
        "    end\n"
        "    if SACCamera.IsFightingTroll then\n"
        "      L2_2 = 0\n"
        "    end\n"
        "    if SACCamera.IgnoreCombatCamera then\n"
        "      L2_2 = 0\n"
        "    end\n"
        "    if A0_0:GetOverriddenData().SupercedesCombat then\n"
        "      L2_2 = 0\n"
        "    end\n"
        "    if A0_0.ActiveZoomLevel.OverridesCombat then\n"
        "      L2_2 = 0\n"
        "    end\n"
        "    A0_0.ZoomLevelCombatWeight = L2_2\n"
        "    local L4_4 = SACCamera.InterpolateZoomLevels(A0_0.ActiveZoomLevel, L2_2, L3_3)\n"
        "    local L5_5 = A0_0.Secondary and SACCamera.MultiplayerCombatMaxCageMultiplier or 1\n"
        "    local L6_6 = CameraFunctions.Interpolate(L4_4.MaxCageRadius, L2_2, L4_4.MaxCageRadius * L5_5)\n"
        "    L4_4.MaxCageRadius = L6_6\n"
        "    if Camera.DisplayZoomLevel then\n"
        "      if A0_0.CurrentZoomSet.Name then\n"
        "        print(A0_0.CurrentZoomSet.Name .. \" blended with \" .. L2_2 * 100 .. \"% Combat\")\n"
        "      end\n"
        "      Camera.DisplayZoomLevel = nil\n"
        "    end\n"
        "    if A0_0.Secondary then\n"
        "      L6_6 = 1\n"
        "    else\n"
        "      L6_6 = SACCamera.CalculateScale(A0_0.Player, A0_0:GetLevelTriggerZoomLevel())\n"
        "    end\n"
        "    L6_6 = L6_6 * A0_0.CurrentCombatScale\n"
        "    A0_0.ScaledActiveZoomLevel = SACCamera.ScaleZoomLevel(L4_4, L6_6)\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.RampMovementFactor = function(A0_0)\n"
        "    A0_0.CurrentMovementFactor = CameraFunctions.GetRollingAverage(A0_0.CurrentMovementFactor, (((not A0_0:GetActiveZoomLevel().MovementFactor) and (1)) or A0_0:GetActiveZoomLevel().MovementFactor), (10))\n"
        "  end\n",
        "  SACCamera.RampMovementFactor = function(A0_0)\n"
        "    local L1_1 = A0_0:GetActiveZoomLevel().MovementFactor or 1\n"
        "    A0_0.CurrentMovementFactor = CameraFunctions.GetRollingAverage(A0_0.CurrentMovementFactor, L1_1, 10)\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.GetPositionBasedOnImportance = function(A0_0, A1_1, A2_2, A3_3, A4_4)\n"
        "    if type(A1_1) == \"table\" then\n"
        "    local L5_5 = A1_1\n"
        "    else\n"
        "    L5_5 = false\n"
        "    end\n"
        "    local L6_6 = ((L5_5 and A1_1.Position) or A1_1)\n"
        "    local L7_7, L8_8 = nil\n"
        "    if SACCamera.UseNewPositionImportance then\n"
        "    if A4_4 then\n"
        "    else\n"
        "    end\n"
        "    CameraManager.GetNearestPosAndImportance, r10 = CameraManager.GetNearestPosAndImportance(L6_6, A2_2, SACCamera.HeightImportanceFactor, SACCamera.InterestingRangeFadeFactor, true)\n"
        "    L7_7 = r10\n"
        "    L7_7 = A3_3\n"
        "    end\n"
        "    local L9_9 = nil\n"
        "    for r13, r14 in ipairs(A0_0) do\n"
        "    local r15 = (L6_6 - r14.Position)\n"
        "    r15:SetZ((r15:GetZ() * SACCamera.HeightImportanceFactor))\n"
        "    local r16 = r15:GetLength()\n"
        "    if not L9_9 then\n"
        "    L9_9 = r16\n"
        "    end\n"
        "    if not L8_8 then\n"
        "    end\n"
        "    if r16 < L9_9 then\n"
        "    L9_9 = r16\n"
        "    end\n"
        "    end\n"
        "    local r10 = (A2_2 * SACCamera.InterestingRangeFadeFactor)\n"
        "    L7_7 = A3_3\n"
        "    if L5_5 then\n"
        "    end\n"
        "    L9_9 = nil\n"
        "    if SACCamera.UseNewFinalPosition then\n"
        "    else\n"
        "    L9_9 = (L8_8 + ((L6_6 - L8_8) * L7_7))\n"
        "    end\n"
        "    if 0 < L7_7 then\n"
        "    return L9_9\n"
        "    end\n"
        "    return nil\n"
        "  end\n",
        "  SACCamera.GetPositionBasedOnImportance = function(A0_0, A1_1, A2_2, A3_3, A4_4)\n"
        "    local L5_5\n"
        "    if type(A1_1) == \"table\" then\n"
        "    L5_5 = A1_1\n"
        "    else\n"
        "    L5_5 = false\n"
        "    end\n"
        "    local L6_6 = ((L5_5 and A1_1.Position) or A1_1)\n"
        "    local L7_7, L8_8 = nil\n"
        "    if SACCamera.UseNewPositionImportance then\n"
        "    local L14_14\n"
        "    if A4_4 then\n"
        "    L14_14 = false\n"
        "    else\n"
        "    L14_14 = true\n"
        "    end\n"
        "    L8_8, L7_7 = CameraManager.GetNearestPosAndImportance(L6_6, A2_2, SACCamera.HeightImportanceFactor, SACCamera.InterestingRangeFadeFactor, L14_14)\n"
        "    L7_7 = A3_3 or L7_7\n"
        "    else\n"
        "    local L9_9 = nil\n"
        "    for r13, r14 in ipairs(A0_0) do\n"
        "    local r15 = (L6_6 - r14.Position)\n"
        "    r15:SetZ((r15:GetZ() * SACCamera.HeightImportanceFactor))\n"
        "    local r16 = r15:GetLength()\n"
        "    if not L9_9 then\n"
        "    L9_9 = r16\n"
        "    end\n"
        "    if not L8_8 then\n"
        "    L8_8 = r14.Position\n"
        "    end\n"
        "    if r16 < L9_9 then\n"
        "    L9_9 = r16\n"
        "    L8_8 = r14.Position\n"
        "    end\n"
        "    end\n"
        "    local L10_10 = A2_2 * SACCamera.InterestingRangeFadeFactor\n"
        "    L7_7 = A3_3\n"
        "    if not L7_7 then\n"
        "    L7_7 = 1 - ((L9_9 - A2_2) / (L10_10 - A2_2))\n"
        "    end\n"
        "    L7_7 = math.max(L7_7, 0)\n"
        "    L7_7 = math.min(L7_7, 1)\n"
        "    end\n"
        "    if L5_5 then\n"
        "    L7_7 = SACCamera.GetImportance(L5_5, L7_7)\n"
        "    end\n"
        "    local L9_9 = nil\n"
        "    if SACCamera.UseNewFinalPosition then\n"
        "    L9_9 = CameraManager.BlendToPosFromImportance(L8_8, L6_6, L7_7)\n"
        "    else\n"
        "    L9_9 = (L8_8 + ((L6_6 - L8_8) * L7_7))\n"
        "    end\n"
        "    if 0 < L7_7 then\n"
        "    return L9_9\n"
        "    end\n"
        "    return nil\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.GetCircleForEntityGroup = function(A0_0, A1_1, A2_2, A3_3)\n"
        "    for r10, r11 in pairs(A0_0) do\n"
        "    if type(r11) == \"table\" then\n"
        "    else\n"
        "    end\n"
        "    if not r12 or not r11.Entity then\n"
        "    end\n"
        "    if r12 or r11:IsAlive() then\n"
        "    if not r12 or not r12.Position then\n"
        "    end\n"
        "    if not r12 or not r12.InterestingRange then\n"
        "    end\n"
        "    if nil then\n"
        "    if A3_3 ~= nil then\n"
        "    if r11 ~= A3_3 or 1 then\n"
        "    local r16 = 0\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if nil then\n"
        "    table.insert({}, nil)\n"
        "    if not L6_6 then\n"
        "    local L6_6 = {x = (nil).GetX(nil), y = (nil).GetY(nil), z = (nil).GetZ(nil)}\n"
        "    end\n"
        "    if not L7_7 then\n"
        "    local r16 = r16\n"
        "    r16.x = (nil).GetX(nil)\n"
        "    r16.y = (nil).GetY(nil)\n"
        "    r16.z = (nil).GetZ(nil)\n"
        "    local L7_7 = r16\n"
        "    end\n"
        "    L6_6.x = math.max(L6_6.x, (nil).GetX(nil))\n"
        "    L6_6.y = math.max(L6_6.y, (nil).GetY(nil))\n"
        "    L6_6.z = math.max(L6_6.z, (nil).GetZ(nil))\n"
        "    L7_7.x = math.min(L7_7.x, (nil).GetX(nil))\n"
        "    L7_7.y = math.min(L7_7.y, (nil).GetY(nil))\n"
        "    L7_7.z = math.min(L7_7.z, (nil).GetZ(nil))\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if not L6_6 then\n"
        "    return nil\n"
        "    end\n"
        "    local L12_12 = CVector3(L7_7.x, L7_7.y, L7_7.z)\n"
        "    local L13_13 = (L12_12 + ((CVector3(L6_6.x, L6_6.y, L6_6.z) - L12_12) * (0.5)))\n"
        "    for r14, r15 in ipairs(L8_8) do\n"
        "    if Camera.DrawDebug then\n"
        "    DrawApproxCircle((r15 - CVector3((0), (0), (r15:GetZ() - L13_13:GetZ()))), SACCamera.RadiusPerPerson, 128, 0, 0)\n"
        "    local r21 = 0\n"
        "    local r22 = (r22 - L13_13:GetZ())\n"
        "    local r19 = (r15 - r19)\n"
        "    Debug.DrawLine(L13_13, r19, (128), (0), 0)\n"
        "    end\n"
        "    end\n"
        "    return {Radius = (nil + SACCamera.RadiusPerPerson), Position = L13_13}\n"
        "  end\n",
        "  SACCamera.GetCircleForEntityGroup = function(A0_0, A1_1, A2_2, A3_3)\n"
        "    local L4_4\n"
        "    local L5_5\n"
        "    local L6_6 = {}\n"
        "    for r10, r11 in pairs(A0_0) do\n"
        "    local L12_12 = type(r11) == \"table\" and r11 or false\n"
        "    local L13_13 = (L12_12 and r11.Entity) or r11\n"
        "    if L12_12 or L13_13:IsAlive() then\n"
        "    local L14_14 = (L12_12 and L12_12.Position) or L13_13:GetPosition()\n"
        "    local L15_15 = (L12_12 and L12_12.InterestingRange) or A1_1\n"
        "    if L15_15 then\n"
        "    local L16_16 = nil\n"
        "    if A3_3 ~= nil then\n"
        "    L16_16 = (L13_13 ~= A3_3 and 1) or 0\n"
        "    end\n"
        "    L14_14 = SACCamera.GetPositionBasedOnImportance(A2_2, L12_12 or L14_14, L15_15, L16_16)\n"
        "    end\n"
        "    if L14_14 then\n"
        "    table.insert(L6_6, L14_14)\n"
        "    if not L4_4 then\n"
        "    L4_4 = {x = L14_14:GetX(), y = L14_14:GetY(), z = L14_14:GetZ()}\n"
        "    end\n"
        "    if not L5_5 then\n"
        "    L5_5 = {x = L14_14:GetX(), y = L14_14:GetY(), z = L14_14:GetZ()}\n"
        "    end\n"
        "    L4_4.x = math.max(L4_4.x, L14_14:GetX())\n"
        "    L4_4.y = math.max(L4_4.y, L14_14:GetY())\n"
        "    L4_4.z = math.max(L4_4.z, L14_14:GetZ())\n"
        "    L5_5.x = math.min(L5_5.x, L14_14:GetX())\n"
        "    L5_5.y = math.min(L5_5.y, L14_14:GetY())\n"
        "    L5_5.z = math.min(L5_5.z, L14_14:GetZ())\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if not L4_4 then\n"
        "    return nil\n"
        "    end\n"
        "    local L7_7 = CVector3(L4_4.x, L4_4.y, L4_4.z)\n"
        "    local L8_8 = CVector3(L5_5.x, L5_5.y, L5_5.z)\n"
        "    local L9_9 = (L8_8 + ((L7_7 - L8_8) * (0.5)))\n"
        "    local L10_10 = nil\n"
        "    for r14, r15 in ipairs(L6_6) do\n"
        "    local L16_16 = (r15 - L9_9):GetLengthXY()\n"
        "    if not L10_10 then\n"
        "    L10_10 = L16_16\n"
        "    end\n"
        "    L10_10 = math.max(L10_10, L16_16)\n"
        "    if Camera.DrawDebug then\n"
        "    DrawApproxCircle((r15 - CVector3((0), (0), (r15:GetZ() - L9_9:GetZ()))), SACCamera.RadiusPerPerson, 128, 0, 0)\n"
        "    Debug.DrawLine(L9_9, (r15 - CVector3((0), (0), (r15:GetZ() - L9_9:GetZ()))), (128), (0), 0)\n"
        "    end\n"
        "    end\n"
        "    return {Radius = (L10_10 + SACCamera.RadiusPerPerson), Position = L9_9}\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.CalculateCentreAndRadiusFromCircle = function(A0_0, A1_1, A2_2, A3_3)\n"
        "    local L4_4 = A2_2.Position\n"
        "    local L5_5 = A2_2.Radius\n"
        "    if Camera.DrawDebug then\n"
        "    DrawApproxCircle(L4_4, L5_5, (0), (0), (128))\n"
        "    end\n"
        "    if not A0_0 then\n"
        "    local A0_0 = L4_4\n"
        "    else\n"
        "    local L6_6 = (A0_0 - A0_0)\n"
        "    L6_6 = L6_6:GetLengthXY()\n"
        "    local L8_8 = A0_0 - A0_0\n"
        "    L8_8:Normalise()\n"
        "    local L8_8 = (L6_6 + A1_1)\n"
        "    if (A0_0 - A0_0).GetLengthXY:GetLengthXY((A0_0 - A0_0)) + A1_1 < L5_5 then\n"
        "    local r9 = math.min((math.min(L5_5, A3_3) - A1_1), L6_6)\n"
        "    if A1_1 < math.min(L5_5, A3_3) then\n"
        "    A0_0 = (A0_0 + (L7_7 * r9))\n"
        "    local A1_1 = L8_8\n"
        "    end\n"
        "    else\n"
        "    L8_8 = (L6_6 + L5_5)\n"
        "    if L6_6 + L5_5 < A1_1 then\n"
        "    else\n"
        "    if A1_1 < math.min((((L6_6 + A1_1) + L5_5) * (0.5)), A3_3) then\n"
        "    A0_0 = (A0_0 + (L7_7 * (L8_8 - A1_1)))\n"
        "    A1_1 = L8_8\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if Camera.DrawDebug then\n"
        "    if A3_3 <= A1_1 then\n"
        "    DrawApproxCircle(A0_0, A1_1, (255), (255), (255))\n"
        "    else\n"
        "    DrawApproxCircle(A0_0, A1_1, (0), (0), (128))\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    return A0_0, A1_1\n"
        "  end\n",
        "  SACCamera.CalculateCentreAndRadiusFromCircle = function(A0_0, A1_1, A2_2, A3_3)\n"
        "    local L4_4 = A2_2.Position\n"
        "    local L5_5 = A2_2.Radius\n"
        "    if Camera.DrawDebug then\n"
        "    DrawApproxCircle(L4_4, L5_5, (0), (0), (128))\n"
        "    end\n"
        "    if not A0_0 then\n"
        "    A0_0 = L4_4\n"
        "    A1_1 = math.min(L5_5, A3_3)\n"
        "    else\n"
        "    local L6_6 = (A0_0 - L4_4):GetLengthXY()\n"
        "    local L7_7 = (L4_4 - A0_0)\n"
        "    L7_7:Normalise()\n"
        "    local L8_8 = (L6_6 + A1_1)\n"
        "    if L8_8 < L5_5 then\n"
        "    L8_8 = L5_5\n"
        "    L8_8 = math.min(L8_8, A3_3)\n"
        "    local L9_9 = math.min((L8_8 - A1_1), L6_6)\n"
        "    if A1_1 < L8_8 then\n"
        "    A0_0 = (A0_0 + (L7_7 * L9_9))\n"
        "    A1_1 = L8_8\n"
        "    end\n"
        "    else\n"
        "    L8_8 = (L6_6 + L5_5)\n"
        "    if L8_8 < A1_1 then\n"
        "    else\n"
        "    L8_8 = (((L6_6 + A1_1) + L5_5) * (0.5))\n"
        "    L8_8 = math.min(L8_8, A3_3)\n"
        "    if A1_1 < L8_8 then\n"
        "    A0_0 = (A0_0 + (L7_7 * (L8_8 - A1_1)))\n"
        "    A1_1 = L8_8\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if Camera.DrawDebug then\n"
        "    if A3_3 <= A1_1 then\n"
        "    DrawApproxCircle(A0_0, A1_1, (255), (255), (255))\n"
        "    else\n"
        "    DrawApproxCircle(A0_0, A1_1, (0), (0), (128))\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    return A0_0, A1_1\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.CalculateCentreAndRadiusFromCircleCollection = function(A0_0, A1_1)\n"
        "    for r7, r8 in ipairs(A0_0) do\n"
        "    local r9, r10 = SACCamera.CalculateCentreAndRadiusFromCircle(L4_4, L7_7, r8, A1_1)\n"
        "    local L4_4 = r9\n"
        "    if A1_1 <= r10 then\n"
        "    end\n"
        "    end\n"
        "    return L4_4, L7_7\n"
        "  end\n",
        "  SACCamera.CalculateCentreAndRadiusFromCircleCollection = function(A0_0, A1_1)\n"
        "    local L2_2\n"
        "    local L3_3\n"
        "    for r7, r8 in ipairs(A0_0) do\n"
        "    L2_2, L3_3 = SACCamera.CalculateCentreAndRadiusFromCircle(L2_2, L3_3, r8, A1_1)\n"
        "    if A1_1 <= L3_3 then\n"
        "    break\n"
        "    end\n"
        "    end\n"
        "    return L2_2, L3_3\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.GetCageRadiusToViewRadiusFactor = function(A0_0)\n"
        "    return (math.sin((((A0_0:GetActiveZoomLevel().FOV - SACCamera.BorderFOV) * (math.pi / (180))) / (2))) / math.cos(math.atan((A0_0:GetActiveZoomLevel().HeightOffset / A0_0:GetActiveZoomLevel().CageRadius))))\n"
        "  end\n",
        "  SACCamera.GetCageRadiusToViewRadiusFactor = function(A0_0)\n"
        "    local L1_1 = A0_0:GetActiveZoomLevel().FOV - SACCamera.BorderFOV\n"
        "    local L2_2 = L1_1 * (math.pi / (180))\n"
        "    local L3_3 = math.atan(A0_0:GetActiveZoomLevel().HeightOffset / A0_0:GetActiveZoomLevel().CageRadius)\n"
        "    return (math.sin(L2_2 / (2)) / math.cos(L3_3))\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.CalculatePrimaryAndSecondary = function(A0_0, A1_1, A2_2)\n"
        "    if not SACCamera.MultiplayerSwitchPrimaryAndSecondary or A0_0.LevelJustLoaded then\n"
        "    A0_0.Primary = A0_0.Player\n"
        "    if not CameraFunctions.IsMultiplayer(A0_0.Player) or not GetPlayerHenchman() then\n"
        "    local L3_3 = nil\n"
        "    end\n"
        "    A0_0.Secondary = L3_3\n"
        "    return\n"
        "    end\n"
        "    A0_0.SafeToSwitchDistance = true\n"
        "    L3_3 = A0_0.Primary\n"
        "    if not A0_0.Primary then\n"
        "    A0_0.Primary = A0_0.Player\n"
        "    end\n"
        "    A0_0.Primary = L4_4\n"
        "    if not CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
        "    A0_0.Primary = A0_0.Player\n"
        "    A0_0.Secondary = nil\n"
        "    else\n"
        "    A0_0.SafeToSwitchDistance = false\n"
        "    if not A0_0.Secondary then\n"
        "    end\n"
        "    A0_0.Secondary = L4_4\n"
        "    local r7 = ((2) * ((A0_0:GetMaxCageRadius() * A0_0:GetCageRadiusToViewRadiusFactor()) - SACCamera.RadiusPerPerson))\n"
        "    local L11_11 = math.abs(CVector3((A0_0.Player.GetPosition:GetPosition(A0_0.Player) - GetPlayerHenchman():GetPosition())):GetZ())\n"
        "    CVector3((A0_0.Player.GetPosition:GetPosition(A0_0.Player) - GetPlayerHenchman():GetPosition())):SetZ(CVector3((A0_0.Player.GetPosition:GetPosition(A0_0.Player) - GetPlayerHenchman():GetPosition())):GetZ())\n"
        "    if CVector3((A0_0.Player.GetPosition:GetPosition(A0_0.Player) - GetPlayerHenchman():GetPosition())):GetLengthXY() < r7 then\n"
        "    local L12_12 = (SACCamera.MaxHeightChange * (2))\n"
        "    if L11_11 < SACCamera.MaxHeightChange * (2) then\n"
        "    A0_0.SafeToSwitchDistance = true\n"
        "    end\n"
        "    end\n"
        "    if A0_0.SafeToSwitchDistance then\n"
        "    if not A2_2 then\n"
        "    L12_12 = not Camera.DoesCameraRayIntersect(A0_0.Camera, Creature.GetCollisionHeadPosition(A0_0.Player), Creature.GetCollisionHeadPosition(GetPlayerHenchman()), (0))\n"
        "    end\n"
        "    end\n"
        "    if L12_12 then\n"
        "    if (A0_0.Player.GetPosition:GetPosition(A0_0.Player) - A1_1).GetSquaredLengthXY:GetSquaredLengthXY((A0_0.Player.GetPosition:GetPosition(A0_0.Player) - A1_1)) < (GetPlayerHenchman().GetPosition:GetPosition(GetPlayerHenchman()) - A1_1).GetSquaredLengthXY:GetSquaredLengthXY((GetPlayerHenchman().GetPosition:GetPosition(GetPlayerHenchman()) - A1_1)):GetSquaredLengthXY() then\n"
        "    else\n"
        "    end\n"
        "    if r14 ~= r17 then\n"
        "    if not A2_2 then\n"
        "    local r16 = not Camera.DoesCameraRayIntersect(A0_0.Camera, A0_0.Camera:GetPosition(), Creature.GetCollisionHeadPosition(A0_0.Primary), (0))\n"
        "    end\n"
        "    end\n"
        "    if r16 then\n"
        "    A0_0.Primary = A0_0.Primary\n"
        "    A0_0.Secondary = A0_0.Secondary\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if A0_0.Primary ~= L3_3 then\n"
        "    A0_0.PreviousCollisionCentre = nil\n"
        "    end\n"
        "  end\n",
        "  SACCamera.CalculatePrimaryAndSecondary = function(A0_0, A1_1, A2_2)\n"
        "    if not SACCamera.MultiplayerSwitchPrimaryAndSecondary or A0_0.LevelJustLoaded then\n"
        "    A0_0.Primary = A0_0.Player\n"
        "    A0_0.Secondary = ((CameraFunctions.IsMultiplayer(A0_0.Player) and GetPlayerHenchman()) or nil)\n"
        "    return\n"
        "    end\n"
        "    A0_0.SafeToSwitchDistance = true\n"
        "    local L3_3 = A0_0.Primary\n"
        "    A0_0.Primary = A0_0.Primary or A0_0.Player\n"
        "    if not CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
        "    A0_0.Primary = A0_0.Player\n"
        "    A0_0.Secondary = nil\n"
        "    else\n"
        "    A0_0.SafeToSwitchDistance = false\n"
        "    A0_0.Secondary = A0_0.Secondary or GetPlayerHenchman()\n"
        "    local L4_4 = A0_0:GetMaxCageRadius()\n"
        "    local L5_5 = A0_0:GetCageRadiusToViewRadiusFactor()\n"
        "    local L6_6 = L4_4 * L5_5\n"
        "    local L7_7 = (2) * (L6_6 - SACCamera.RadiusPerPerson)\n"
        "    local L8_8 = CVector3(A0_0.Player:GetPosition() - GetPlayerHenchman():GetPosition())\n"
        "    local L9_9 = math.abs(L8_8:GetZ())\n"
        "    L8_8:SetZ(L8_8:GetZ())\n"
        "    if L8_8:GetLengthXY() < L7_7 then\n"
        "    local L10_10 = SACCamera.MaxHeightChange * (2)\n"
        "    if L9_9 < L10_10 then\n"
        "    A0_0.SafeToSwitchDistance = true\n"
        "    end\n"
        "    end\n"
        "    local L10_10 = A0_0.SafeToSwitchDistance\n"
        "    if A0_0.SafeToSwitchDistance and not A2_2 then\n"
        "    local L11_11 = Creature.GetCollisionHeadPosition(A0_0.Player)\n"
        "    local L12_12 = Creature.GetCollisionHeadPosition(GetPlayerHenchman())\n"
        "    L10_10 = not Camera.DoesCameraRayIntersect(A0_0.Camera, L11_11, L12_12, (0))\n"
        "    end\n"
        "    if L10_10 then\n"
        "    local L11_11 = A1_1\n"
        "    local L12_12 = (A0_0.Player:GetPosition() - L11_11):GetSquaredLengthXY()\n"
        "    local L13_13 = (GetPlayerHenchman():GetPosition() - L11_11):GetSquaredLengthXY()\n"
        "    local L14_14 = A0_0.Primary\n"
        "    local L15_15 = A0_0.Secondary\n"
        "    if L12_12 < L13_13 then\n"
        "    L14_14 = A0_0.Player\n"
        "    L15_15 = GetPlayerHenchman()\n"
        "    else\n"
        "    L14_14 = GetPlayerHenchman()\n"
        "    L15_15 = A0_0.Player\n"
        "    end\n"
        "    local L16_16 = true\n"
        "    if L14_14 ~= A0_0.Primary and not A2_2 then\n"
        "    local L17_17 = Creature.GetCollisionHeadPosition(L14_14)\n"
        "    L16_16 = not Camera.DoesCameraRayIntersect(A0_0.Camera, A0_0.Camera:GetPosition(), L17_17, (0))\n"
        "    end\n"
        "    if L16_16 then\n"
        "    A0_0.Primary = L14_14\n"
        "    A0_0.Secondary = L15_15\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if A0_0.Primary ~= L3_3 then\n"
        "    A0_0.PreviousCollisionCentre = nil\n"
        "    end\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.CalculateCentreAndCage = function(A0_0)\n"
        "    local L1_1 = {}\n"
        "    if not A0_0.Secondary or not HeroRadiusMultiplierMultiPlayer then\n"
        "    end\n"
        "    if A0_0.InVanity then\n"
        "    local L2_2 = (0.5)\n"
        "    else\n"
        "    end\n"
        "    local L4_4 = L3_3.Radius\n"
        "    L3_3.Radius = (L4_4 * ((L4_4 and (1)) or L2_2))\n"
        "    SACCamera.SafeInsert(L1_1, L3_3)\n"
        "    L4_4 = L4_4:GetZ()\n"
        "    if not A0_0.InVanity then\n"
        "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
        "    if not A0_0.LevelJustLoaded then\n"
        "    if SACCamera.GetCircleForEntity(A0_0.Secondary, SACCamera.SecondaryInterestRange, L1_1, true) then\n"
        "    L5_5.Radius = (L5_5.Radius * ((L4_4 and (1)) or L2_2))\n"
        "    end\n"
        "    SACCamera.SafeInsert(L1_1, L5_5)\n"
        "    end\n"
        "    end\n"
        "    SACCamera.SafeInsert(L1_1, SACCamera.GetCircleForEntityGroup(A0_0.WeightedNearbyInterestingEntitiesHigh, SACCamera.InterestingEntityDistance, SACCamera.DuplicateCollection(L1_1)))\n"
        "    SACCamera.SafeInsert(L1_1, SACCamera.GetCircleForEntityGroup(A0_0.WeightedNearbyFoes, SACCamera.InterestingEnemyDistance, L5_5, A0_0.TargetLockTarget))\n"
        "    for L12_12, r11 in pairs(A0_0:GetInterestingEntities()) do\n"
        "    SACCamera.SafeInsert(L1_1, SACCamera.GetCircleForEntity(r11, r11.InterestingRange, L5_5))\n"
        "    end\n"
        "    L8_8 = L8_8.SafeInsert\n"
        "    L8_8(L1_1, SACCamera.GetCircleForEntityGroup(A0_0.WeightedNearbyInterestingEntities, SACCamera.InterestingEntityDistance, L5_5))\n"
        "    end\n"
        "    local L9_9 = L9_9.CalculateCentreAndRadiusFromCircleCollection\n"
        "    local L11_11 = L1_1\n"
        "    L9_9, L11_11 = L9_9(L11_11, (A0_0:GetMaxCageRadius() * A0_0:GetCageRadiusToViewRadiusFactor()))\n"
        "    A0_0.NonCageRadius = L11_11\n"
        "    local L12_12 = (L11_11 / L6_6)\n"
        "    local L13_13 = L13_13.CageRadius\n"
        "    L12_12 = math.max(L12_12, L13_13)\n"
        "    if GetDog() then\n"
        "    if GetDog():IsAlive() then\n"
        "    Dog.SetCameraCircle(GetDog(), L9_9, L11_11)\n"
        "    end\n"
        "    end\n"
        "    local r16 = ((A0_0.ExpressionVillager and (0)) or (L8_8 - L11_11))\n"
        "    local r13 = r13:GetZ()\n"
        "    r16 = (L4_4 - SACCamera.MaxHeightChange)\n"
        "    local r14 = math.min(r14, (L4_4 + SACCamera.MaxHeightChange))\n"
        "    r16 = (r14 + r16.FocalOffset)\n"
        "    r13:SetZ(r16)\n"
        "    if A0_0.InVanity then\n"
        "    r16 = (r16 + A0_0:GetActiveZoomLevel().FocalOffset)\n"
        "    r13:SetZ(r16)\n"
        "    end\n"
        "    return r13, L12_12\n"
        "  end\n",
        "  SACCamera.CalculateCentreAndCage = function(A0_0)\n"
        "    local L1_1 = {}\n"
        "    local L2_2 = (A0_0.Secondary and HeroRadiusMultiplierMultiPlayer) or SACCamera.HeroRadiusMultiplierSinglePlayer\n"
        "    L2_2 = (A0_0.InVanity and 0.5) or L2_2\n"
        "    local L3_3 = SACCamera.GetCircleForEntity(A0_0.Primary)\n"
        "    if A0_0.ExpressionVillager then\n"
        "    L2_2 = 1\n"
        "    end\n"
        "    L3_3.Radius = L3_3.Radius * L2_2\n"
        "    SACCamera.SafeInsert(L1_1, L3_3)\n"
        "    local L4_4 = L3_3.Position:GetZ()\n"
        "    if not A0_0.InVanity then\n"
        "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
        "    if not A0_0.LevelJustLoaded then\n"
        "    local L5_5 = SACCamera.GetCircleForEntity(A0_0.Secondary, SACCamera.SecondaryInterestRange, L1_1, true)\n"
        "    if L5_5 then\n"
        "    L5_5.Radius = L5_5.Radius * L2_2\n"
        "    end\n"
        "    SACCamera.SafeInsert(L1_1, L5_5)\n"
        "    end\n"
        "    end\n"
        "    local L5_5 = SACCamera.DuplicateCollection(L1_1)\n"
        "    SACCamera.SafeInsert(L1_1, SACCamera.GetCircleForEntityGroup(A0_0.WeightedNearbyInterestingEntitiesHigh, SACCamera.InterestingEntityDistance, L5_5))\n"
        "    SACCamera.SafeInsert(L1_1, SACCamera.GetCircleForEntityGroup(A0_0.WeightedNearbyFoes, SACCamera.InterestingEnemyDistance, L5_5, A0_0.TargetLockTarget))\n"
        "    local L6_6 = A0_0:GetInterestingEntities()\n"
        "    for L12_12, r11 in pairs(L6_6) do\n"
        "    SACCamera.SafeInsert(L1_1, SACCamera.GetCircleForEntity(r11, r11.InterestingRange, L5_5))\n"
        "    end\n"
        "    SACCamera.SafeInsert(L1_1, SACCamera.GetCircleForEntityGroup(A0_0.WeightedNearbyInterestingEntities, SACCamera.InterestingEntityDistance, L5_5))\n"
        "    end\n"
        "    local L5_5 = A0_0:GetMaxCageRadius()\n"
        "    local L6_6 = A0_0:GetCageRadiusToViewRadiusFactor()\n"
        "    local L7_7 = L5_5 * L6_6\n"
        "    local L8_8, L9_9 = SACCamera.CalculateCentreAndRadiusFromCircleCollection(L1_1, L7_7)\n"
        "    A0_0.NonCageRadius = L9_9\n"
        "    local L10_10 = L9_9 / L6_6\n"
        "    local L11_11 = A0_0:GetActiveZoomLevel().CageRadius\n"
        "    L10_10 = math.max(L10_10, L11_11)\n"
        "    if GetDog() then\n"
        "    if GetDog():IsAlive() then\n"
        "    Dog.SetCameraCircle(GetDog(), L8_8, L9_9)\n"
        "    end\n"
        "    end\n"
        "    local L12_12 = L7_7 - L9_9\n"
        "    if A0_0.ExpressionVillager then\n"
        "    L12_12 = 0\n"
        "    end\n"
        "    L8_8 = A0_0:ApplySlot(L8_8, L12_12)\n"
        "    local L13_13 = L8_8:GetZ()\n"
        "    L13_13 = math.max(L13_13, L4_4 - SACCamera.MaxHeightChange)\n"
        "    L13_13 = math.min(L13_13, L4_4 + SACCamera.MaxHeightChange)\n"
        "    L8_8:SetZ(L13_13 + A0_0:GetActiveZoomLevel().FocalOffset)\n"
        "    if A0_0.InVanity then\n"
        "    L8_8:SetZ(A0_0:GetObjectPosition(A0_0.Primary):GetZ() + A0_0:GetActiveZoomLevel().FocalOffset)\n"
        "    end\n"
        "    return L8_8, L10_10\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.CalculateMovement = function(A0_0, A1_1)\n"
        "    if Timing.GetProgramFrame() ~= A0_0.MovementCalculationFrame then\n"
        "    local r2 = CVector3((0), (0), (0))\n"
        "    A0_0.PrevMovementCentre = A1_1\n"
        "    A0_0.MovementCalculationFrame = Timing.GetProgramFrame()\n"
        "    A0_0.MovementThisFrame = ((A0_0.PrevMovementCentre and (A1_1 - A0_0.PrevMovementCentre)) or r2)\n"
        "    end\n"
        "    return A0_0.MovementThisFrame\n"
        "  end\n",
        "  SACCamera.CalculateMovement = function(A0_0, A1_1)\n"
        "    if Timing.GetProgramFrame() ~= A0_0.MovementCalculationFrame then\n"
        "    local L2_2 = CVector3((0), (0), (0))\n"
        "    if A0_0.PrevMovementCentre then\n"
        "    L2_2 = A1_1 - A0_0.PrevMovementCentre\n"
        "    end\n"
        "    A0_0.PrevMovementCentre = A1_1\n"
        "    A0_0.MovementCalculationFrame = Timing.GetProgramFrame()\n"
        "    A0_0.MovementThisFrame = L2_2\n"
        "    end\n"
        "    return A0_0.MovementThisFrame\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.CalculateXY = function(A0_0, A1_1, A2_2, A3_3, A4_4)\n"
        "    local L5_5 = A0_0:CalculateMovement(A4_4)\n"
        "    if not A0_0.PlayerIsManuallyRotatingCamera then\n"
        "    local L6_6 = A0_0:GetActiveZoomLevel().TrackAmount\n"
        "    if 0 < ((not L6_6) and (0)) or L6_6 then\n"
        "    local A1_1 = (A1_1 + (L5_5 * (((not A0_0:GetActiveZoomLevel().TrackAmount) and (0)) or L6_6)))\n"
        "    else\n"
        "    Camera.GetRightVector(A0_0.Camera):SetZ((0))\n"
        "    Camera.GetRightVector(A0_0.Camera):Normalise()\n"
        "    A1_1 = (A1_1 + ((Camera.GetRightVector(A0_0.Camera) * L5_5:GetDot(Camera.GetRightVector(A0_0.Camera))) * (((not A0_0:GetActiveZoomLevel().TrackAmount) and (0)) or L6_6)))\n"
        "    end\n"
        "    end\n"
        "    local L6_6 = A1_1 - A4_4\n"
        "    L6_6:SetZ((0))\n"
        "    if L6_6:GetLength() == 0 then\n"
        "    return (A4_4 + CVector3(A2_2, (0), (0)))\n"
        "    end\n"
        "    L6_6:Normalise()\n"
        "    if A0_0:IsBlending() or A0_0.SnapThisFrame then\n"
        "    local A2_2 = A3_3\n"
        "    end\n"
        "    L7_7 = math.max(A2_2, L7_7)\n"
        "    return (A4_4 + (L6_6 * math.min(CameraFunctions.Interpolate(L7_7, A0_0.CurrentMovementFactor, A2_2), L7_7)))\n"
        "  end\n",
        "  SACCamera.CalculateXY = function(A0_0, A1_1, A2_2, A3_3, A4_4)\n"
        "    local L5_5 = A0_0:CalculateMovement(A4_4)\n"
        "    if not A0_0.PlayerIsManuallyRotatingCamera then\n"
        "    local L6_6 = A0_0:GetActiveZoomLevel().TrackAmount\n"
        "    L6_6 = L6_6 or 0\n"
        "    if 0 < L6_6 then\n"
        "    A1_1 = A1_1 + (L5_5 * L6_6)\n"
        "    else\n"
        "    local L7_7 = Camera.GetRightVector(A0_0.Camera)\n"
        "    L7_7:SetZ((0))\n"
        "    L7_7:Normalise()\n"
        "    local L8_8 = L5_5:GetDot(L7_7)\n"
        "    A1_1 = A1_1 + ((L7_7 * L8_8) * L6_6)\n"
        "    end\n"
        "    end\n"
        "    local L6_6 = A1_1 - A4_4\n"
        "    L6_6:SetZ((0))\n"
        "    local L7_7 = L6_6:GetLength()\n"
        "    if L7_7 == 0 then\n"
        "    return (A4_4 + CVector3(A2_2, (0), (0)))\n"
        "    end\n"
        "    L6_6:Normalise()\n"
        "    if A0_0:IsBlending() or A0_0.SnapThisFrame then\n"
        "    A2_2 = A3_3\n"
        "    end\n"
        "    L7_7 = math.max(A2_2, L7_7)\n"
        "    A3_3 = CameraFunctions.Interpolate(L7_7, A0_0.CurrentMovementFactor, A3_3)\n"
        "    L7_7 = math.min(A3_3, L7_7)\n"
        "    return (A4_4 + (L6_6 * L7_7))\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.CalculateSlew = function(A0_0)\n"
        "    A0_0.RotateRight = (((not A0_0.RotateRight) and (0)) or A0_0.RotateRight)\n"
        "    return (A0_0.RotateRight * (0))\n"
        "  end\n",
        "  SACCamera.CalculateSlew = function(A0_0)\n"
        "    A0_0.RotateRight = A0_0.RotateRight or 0\n"
        "    return (A0_0.RotateRight * (0))\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.GetHeight = function(A0_0, A1_1, A2_2, A3_3)\n"
        "    local L6_6 = (A1_1 - A2_2)\n"
        "    L6_6 = L6_6:GetLengthXY()\n"
        "    local L8_8 = math.atan(((A0_0:GetActiveZoomLevel().HeightOffset * (A3_3 / A0_0:GetActiveZoomLevel().CageRadius)) / L6_6))\n"
        "    local L10_10 = 0\n"
        "    if A0_0:GetActiveZoomLevel().DisregardsGroundSlope then\n"
        "    if A0_0:GetActiveZoomLevel().DisregardsGroundSlope ~= true or not 1 then\n"
        "    end\n"
        "    L10_10 = ((1) - L11_11)\n"
        "    end\n"
        "    local L11_11 = A2_2 - A1_1\n"
        "    L11_11:SetZ((0))\n"
        "    L11_11:Normalise()\n"
        "    A0_0.SlopeAngle = CameraFunctions.GetRollingAverage(A0_0.SlopeAngle, (A0_0:GetLimitedPitchBetweenPositions(A1_1, (A2_2 + (L11_11 * (2)))) * L10_10), SACCamera.SlopeDamping)\n"
        "    if Camera.DrawDebug then\n"
        "    local L14_14 = (A2_2 + L14_14)\n"
        "    Debug.DrawLine((A2_2 + CVector3((0), (0), L5_5)), L14_14, (0), (255), 0)\n"
        "    end\n"
        "    local L12_12 = L6_6\n"
        "    if L8_8 < 0 then\n"
        "    L13_13 = L13_13(L6_6, A0_0:GetActiveZoomLevel().CageRadius)\n"
        "    L12_12 = L13_13\n"
        "    end\n"
        "    L14_14 = L8_8\n"
        "    L13_13 = (L12_12 * L13_13)\n"
        "    L14_14 = (L6_6 * L14_14)\n"
        "    L5_5 = (L13_13 + L14_14)\n"
        "    L13_13 = L13_13.DrawDebug\n"
        "    if L13_13 then\n"
        "    L13_13 = L13_13.DrawLine\n"
        "    r14 = 0\n"
        "    local r15 = 0\n"
        "    L14_14 = (A2_2 + L14_14)\n"
        "    r15 = 0\n"
        "    r14 = (A2_2 + r14)\n"
        "    L13_13(L14_14, r14, (0), (255), 0)\n"
        "    end\n"
        "    L13_13 = L13_13(A0_0.ManualHeightOffset, (-A0_0.HeightOffsetLowerBound))\n"
        "    A0_0.ManualHeightOffset = L13_13\n"
        "    L13_13 = L13_13(A0_0.ManualHeightOffset, A0_0.HeightOffsetUpperBound)\n"
        "    A0_0.ManualHeightOffset = L13_13\n"
        "    if not A0_0.CurrentManualHeightOffset then\n"
        "    A0_0.CurrentManualHeightOffset = A0_0.ManualHeightOffset\n"
        "    end\n"
        "    A0_0.CurrentManualHeightOffset = L13_13\n"
        "    L13_13 = L13_13(A0_0.CurrentManualHeightOffset, A0_0.ManualHeightOffset, SACCamera.HeightDamp)\n"
        "    A0_0.CurrentManualHeightOffset = L13_13\n"
        "    L13_13 = L13_13(A0_0.ManualHeightOffset, (0), (20))\n"
        "    A0_0.ManualHeightOffset = L13_13\n"
        "    L13_13 = (L6_6 * L13_13)\n"
        "    L14_14 = (L14_14 + L5_5)\n"
        "    L14_14 = (L14_14 + L13_13)\n"
        "    if A0_0.Camera then\n"
        "    if Camera.IsOverWaterAtPosition(A0_0.Camera, A1_1) then\n"
        "    r14 = (r14 + (1))\n"
        "    L14_14 = math.max(L14_14, r14)\n"
        "    end\n"
        "    end\n"
        "    if Camera.DrawDebug then\n"
        "    local r17 = 0\n"
        "    local r19 = 0\n"
        "    r16 = (A2_2 + r16)\n"
        "    Debug.DrawLine(A2_2, r16, (0), (255), 0)\n"
        "    end\n"
        "    return L14_14\n"
        "  end\n",
        "  SACCamera.GetHeight = function(A0_0, A1_1, A2_2, A3_3)\n"
        "    local L4_4 = A3_3 / A0_0:GetActiveZoomLevel().CageRadius\n"
        "    local L5_5 = A0_0:GetActiveZoomLevel().HeightOffset * L4_4\n"
        "    local L6_6 = (A1_1 - A2_2):GetLengthXY()\n"
        "    local L7_7 = math.atan(L5_5 / L6_6)\n"
        "    local L8_8 = 0\n"
        "    local L9_9 = 0\n"
        "    if A0_0:GetActiveZoomLevel().DisregardsGroundSlope then\n"
        "    local L10_10 = ((A0_0:GetActiveZoomLevel().DisregardsGroundSlope ~= true and 1) or A0_0:GetActiveZoomLevel().DisregardsGroundSlope)\n"
        "    L9_9 = (1) - L10_10\n"
        "    end\n"
        "    local L10_10 = A2_2 - A1_1\n"
        "    L10_10:SetZ((0))\n"
        "    L10_10:Normalise()\n"
        "    L8_8 = A0_0:GetLimitedPitchBetweenPositions(A1_1, A2_2 + (L10_10 * (2)))\n"
        "    A0_0.SlopeAngle = CameraFunctions.GetRollingAverage(A0_0.SlopeAngle, (L8_8 * L9_9), SACCamera.SlopeDamping)\n"
        "    if Camera.DrawDebug then\n"
        "    Debug.DrawLine((A2_2 + CVector3((0), (0), L5_5)), (A2_2 + CVector3((0), (1), L5_5)), (0), (255), (0))\n"
        "    end\n"
        "    local L11_11 = L6_6\n"
        "    if L7_7 < 0 then\n"
        "    L11_11 = math.min(L6_6, A0_0:GetActiveZoomLevel().CageRadius)\n"
        "    end\n"
        "    local L12_12 = math.tan(L7_7) * L11_11\n"
        "    local L13_13 = math.tan(A0_0.SlopeAngle) * L6_6\n"
        "    L5_5 = L12_12 + L13_13\n"
        "    if Camera.DrawDebug then\n"
        "    Debug.DrawLine((A2_2 + CVector3((0), (0), L5_5)), (A2_2 + CVector3((0), (1), L5_5)), (0), (255), (0))\n"
        "    end\n"
        "    A0_0.ManualHeightOffset = math.max(A0_0.ManualHeightOffset, -A0_0.HeightOffsetLowerBound)\n"
        "    A0_0.ManualHeightOffset = math.min(A0_0.ManualHeightOffset, A0_0.HeightOffsetUpperBound)\n"
        "    if not A0_0.CurrentManualHeightOffset then\n"
        "    A0_0.CurrentManualHeightOffset = A0_0.ManualHeightOffset\n"
        "    end\n"
        "    A0_0.CurrentManualHeightOffset = CameraFunctions.GetRollingAverage(A0_0.CurrentManualHeightOffset, A0_0.ManualHeightOffset, SACCamera.HeightDamp)\n"
        "    A0_0.ManualHeightOffset = CameraFunctions.GetRollingAverage(A0_0.ManualHeightOffset, (0), (20))\n"
        "    local L12_12 = A0_0.CurrentManualHeightOffset * L6_6\n"
        "    local L13_13 = A2_2:GetZ() + L5_5 + L12_12\n"
        "    if A0_0.Camera then\n"
        "    if Camera.IsOverWaterAtPosition(A0_0.Camera, A1_1) then\n"
        "    local L14_14 = Camera.GetWaterPositionAtPosition(A0_0.Camera, A1_1):GetZ() + (1)\n"
        "    L13_13 = math.max(L13_13, L14_14)\n"
        "    end\n"
        "    end\n"
        "    if Camera.DrawDebug then\n"
        "    Debug.DrawLine(A2_2, (A2_2 + CVector3((0), (1), (0))), (0), (255), (0))\n"
        "    end\n"
        "    return L13_13\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "  SACCamera.MoveBehindHeroOrHenchman = function(A0_0)\n"
        "    if A0_0.SafeToSwitchDistance then\n"
        "    if not A0_0.MySetBehindHero then\n"
        "    if not SACCamera.SetBehindHero then\n"
        "    if SACCamera.SetAndCutBehindHero then\n"
        "    local L1_1 = A0_0.Primary:GetPosition()\n"
        "    if not A0_0.CutBehindHero then\n"
        "    A0_0.StartInterpolate = not SACCamera.SetAndCutBehindHero\n"
        "    else\n"
        "    A0_0.StartInterpolate = false\n"
        "    end\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    A0_0.CutBehindHero = false\n"
        "    A0_0.JustChangedDirectionPreviousPos = L1_1\n"
        "    end\n"
        "    end\n"
        "    else\n"
        "    L1_1 = L1_1:GetPosition()\n"
        "    if not A0_0.CutBehindHero then\n"
        "    A0_0.StartInterpolate = not SACCamera.SetAndCutBehindHero\n"
        "    else\n"
        "    A0_0.StartInterpolate = false\n"
        "    end\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    A0_0.CutBehindHero = false\n"
        "    A0_0.JustChangedDirectionPreviousPos = L1_1\n"
        "    end\n"
        "    if A0_0.MySetBehindHenchman then\n"
        "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
        "    L1_1 = L1_1:GetPosition()\n"
        "    A0_0.StartInterpolate = true\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    A0_0.JustChangedDirectionPreviousPos = L1_1\n"
        "    end\n"
        "    end\n"
        "    if not SACCamera.CutDirection then\n"
        "    if SACCamera.SetDirection then\n"
        "    L1_1 = A0_0:CalculateSimpleCentre(A0_0.Primary)\n"
        "    if not SACCamera.CutDirection then\n"
        "    SACCamera.SetDirection.SetZ(nil, nil)\n"
        "    else\n"
        "    L2_2.SetZ(nil, nil)\n"
        "    end\n"
        "    L2_2:Normalise()\n"
        "    A0_0:MoveToFaceDirection(A0_0.Camera, L1_1, L2_2)\n"
        "    A0_0.StartInterpolate = (not SACCamera.CutDirection)\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    SACCamera.CutDirection = nil\n"
        "    SACCamera.SetDirection = nil\n"
        "    A0_0.JustChangedDirectionPreviousPos = previous_pos\n"
        "    end\n"
        "    end\n"
        "    L1_1 = A0_0:CalculateSimpleCentre(A0_0.Primary)\n"
        "    if not SACCamera.CutDirection then\n"
        "    SACCamera.SetDirection.SetZ(nil, nil)\n"
        "    else\n"
        "    L2_2.SetZ(nil, nil)\n"
        "    end\n"
        "    L2_2:Normalise()\n"
        "    A0_0:MoveToFaceDirection(A0_0.Camera, L1_1, L2_2)\n"
        "    A0_0.StartInterpolate = (not SACCamera.CutDirection)\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    SACCamera.CutDirection = nil\n"
        "    SACCamera.SetDirection = nil\n"
        "    A0_0.JustChangedDirectionPreviousPos = previous_pos\n"
        "    else\n"
        "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
        "    L1_1 = nil\n"
        "    if A0_0.MySetBehindHero or SACCamera.SetBehindHero then\n"
        "    else\n"
        "    if A0_0.MySetBehindHenchman then\n"
        "    end\n"
        "    end\n"
        "    if L1_1 then\n"
        "    if L1_1 ~= A0_0.Primary then\n"
        "    L2_2 = A0_0.Primary\n"
        "    A0_0.Primary = A0_0.Secondary\n"
        "    A0_0.Secondary = L2_2\n"
        "    end\n"
        "    L2_2 = A0_0:CalculateSimpleCentre(A0_0.Primary)\n"
        "    local L3_3 = (L3_3.GetPosition:GetPosition(L3_3) - A0_0.Primary:GetPosition())\n"
        "    L3_3:SetZ((0))\n"
        "    L3_3:Normalise()\n"
        "    A0_0:MoveToFaceDirection(A0_0.Camera, L2_2, L3_3)\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if Timing.GetProgramFrame() == A0_0.InitialFrame and true or false then\n"
        "    A0_0.StartInterpolate = false\n"
        "    end\n"
        "    A0_0.MySetBehindHero = nil\n"
        "    SACCamera.SetBehindHero = nil\n"
        "    SACCamera.SetAndCutBehindHero = nil\n"
        "    A0_0.MySetBehindHenchman = nil\n"
        "  end\n",
        "  SACCamera.MoveBehindHeroOrHenchman = function(A0_0)\n"
        "    if A0_0.SafeToSwitchDistance then\n"
        "    if A0_0.MySetBehindHero or SACCamera.SetBehindHero or SACCamera.SetAndCutBehindHero then\n"
        "    local L1_1 = A0_0.Camera:GetPosition()\n"
        "    local L2_2 = A0_0:MoveToBehindTarget(A0_0.Camera, A0_0.Player)\n"
        "    if not A0_0.CutBehindHero then\n"
        "    A0_0.StartInterpolate = not SACCamera.SetAndCutBehindHero\n"
        "    else\n"
        "    A0_0.StartInterpolate = false\n"
        "    end\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    A0_0.CutBehindHero = false\n"
        "    A0_0.JustChangedDirectionPreviousPos = L1_1\n"
        "    elseif A0_0.MySetBehindHenchman and CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
        "    local L1_1 = A0_0.Camera:GetPosition()\n"
        "    local L2_2 = A0_0:MoveToBehindTarget(A0_0.Camera, GetPlayerHenchman())\n"
        "    A0_0.StartInterpolate = true\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    A0_0.JustChangedDirectionPreviousPos = L1_1\n"
        "    elseif SACCamera.CutDirection or SACCamera.SetDirection then\n"
        "    local L1_1 = A0_0:CalculateSimpleCentre(A0_0.Primary)\n"
        "    local L2_2 = SACCamera.CutDirection or SACCamera.SetDirection\n"
        "    L2_2:SetZ((0))\n"
        "    L2_2:Normalise()\n"
        "    A0_0:MoveToFaceDirection(A0_0.Camera, L1_1, L2_2)\n"
        "    A0_0.StartInterpolate = (not SACCamera.CutDirection)\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    SACCamera.CutDirection = nil\n"
        "    SACCamera.SetDirection = nil\n"
        "    A0_0.JustChangedDirectionPreviousPos = previous_pos\n"
        "    end\n"
        "    else\n"
        "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
        "    local L1_1 = nil\n"
        "    if A0_0.MySetBehindHero or SACCamera.SetBehindHero then\n"
        "    L1_1 = A0_0.Player\n"
        "    else\n"
        "    if A0_0.MySetBehindHenchman then\n"
        "    L1_1 = GetPlayerHenchman()\n"
        "    end\n"
        "    end\n"
        "    if L1_1 then\n"
        "    if L1_1 ~= A0_0.Primary then\n"
        "    local L2_2 = A0_0.Primary\n"
        "    A0_0.Primary = A0_0.Secondary\n"
        "    A0_0.Secondary = L2_2\n"
        "    end\n"
        "    local L2_2 = A0_0:CalculateSimpleCentre(A0_0.Primary)\n"
        "    local L3_3 = A0_0.Secondary:GetPosition() - A0_0.Primary:GetPosition()\n"
        "    L3_3:SetZ((0))\n"
        "    L3_3:Normalise()\n"
        "    A0_0:MoveToFaceDirection(A0_0.Camera, L2_2, L3_3)\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    local L1_1 = Timing.GetProgramFrame() ~= A0_0.InitialFrame and false or true\n"
        "    if L1_1 then\n"
        "    A0_0.StartInterpolate = false\n"
        "    end\n"
        "    A0_0.MySetBehindHero = nil\n"
        "    SACCamera.SetBehindHero = nil\n"
        "    SACCamera.SetAndCutBehindHero = nil\n"
        "    A0_0.MySetBehindHenchman = nil\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.MoveBehindHeroOrHenchman = function(A0_0)",
        "  SACCamera.MoveBehindHeroOrHenchman = function(A0_0)\n"
        "    if A0_0.SafeToSwitchDistance then\n"
        "    if A0_0.MySetBehindHero or SACCamera.SetBehindHero or SACCamera.SetAndCutBehindHero then\n"
        "    local L1_1 = A0_0.Camera:GetPosition()\n"
        "    local L2_2 = A0_0:MoveToBehindTarget(A0_0.Camera, A0_0.Player)\n"
        "    if not A0_0.CutBehindHero then\n"
        "    A0_0.StartInterpolate = not SACCamera.SetAndCutBehindHero\n"
        "    else\n"
        "    A0_0.StartInterpolate = false\n"
        "    end\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    A0_0.CutBehindHero = false\n"
        "    A0_0.JustChangedDirectionPreviousPos = L1_1\n"
        "    elseif A0_0.MySetBehindHenchman and CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
        "    local L1_1 = A0_0.Camera:GetPosition()\n"
        "    local L2_2 = A0_0:MoveToBehindTarget(A0_0.Camera, GetPlayerHenchman())\n"
        "    A0_0.StartInterpolate = true\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    A0_0.JustChangedDirectionPreviousPos = L1_1\n"
        "    elseif SACCamera.CutDirection or SACCamera.SetDirection then\n"
        "    local L1_1 = A0_0:CalculateSimpleCentre(A0_0.Primary)\n"
        "    local L2_2 = SACCamera.CutDirection or SACCamera.SetDirection\n"
        "    L2_2:SetZ((0))\n"
        "    L2_2:Normalise()\n"
        "    A0_0:MoveToFaceDirection(A0_0.Camera, L1_1, L2_2)\n"
        "    A0_0.StartInterpolate = (not SACCamera.CutDirection)\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    SACCamera.CutDirection = nil\n"
        "    SACCamera.SetDirection = nil\n"
        "    A0_0.JustChangedDirectionPreviousPos = previous_pos\n"
        "    end\n"
        "    else\n"
        "    if CameraFunctions.IsMultiplayer(A0_0.Player) then\n"
        "    local L1_1 = nil\n"
        "    if A0_0.MySetBehindHero or SACCamera.SetBehindHero then\n"
        "    L1_1 = A0_0.Player\n"
        "    else\n"
        "    if A0_0.MySetBehindHenchman then\n"
        "    L1_1 = GetPlayerHenchman()\n"
        "    end\n"
        "    end\n"
        "    if L1_1 then\n"
        "    if L1_1 ~= A0_0.Primary then\n"
        "    local L2_2 = A0_0.Primary\n"
        "    A0_0.Primary = A0_0.Secondary\n"
        "    A0_0.Secondary = L2_2\n"
        "    end\n"
        "    local L2_2 = A0_0:CalculateSimpleCentre(A0_0.Primary)\n"
        "    local L3_3 = A0_0.Secondary:GetPosition() - A0_0.Primary:GetPosition()\n"
        "    L3_3:SetZ((0))\n"
        "    L3_3:Normalise()\n"
        "    A0_0:MoveToFaceDirection(A0_0.Camera, L2_2, L3_3)\n"
        "    A0_0:SetSnapThisFrame()\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    local L1_1 = Timing.GetProgramFrame() ~= A0_0.InitialFrame and false or true\n"
        "    if L1_1 then\n"
        "    A0_0.StartInterpolate = false\n"
        "    end\n"
        "    A0_0.MySetBehindHero = nil\n"
        "    SACCamera.SetBehindHero = nil\n"
        "    SACCamera.SetAndCutBehindHero = nil\n"
        "    A0_0.MySetBehindHenchman = nil\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.ProcessGameAction = function(A0_0, A1_1, A2_2, A3_3)",
        "  SACCamera.ProcessGameAction = function(A0_0, A1_1, A2_2, A3_3)\n"
        "    if A1_1 == EGameAction.GAME_ACTION_ZOOM_CAMERA then\n"
        "    if GetCanRotateCamera() then\n"
        "    if A0_0.ManualHeightIsRelative then\n"
        "    if not (0.10000000149011612 < A3_3) and A3_3 < -0.10000000149011612 then\n"
        "    A0_0.ManualHeightOffset = A0_0.ManualHeightOffset + ((-A3_3) * A0_0.HeightAdjustSpeed) * (0.5 + Player.GetCameraSensitivity(A0_0.Player))\n"
        "    end\n"
        "    end\n"
        "    if 0.10000000149011612 < A3_3 or A3_3 < -0.10000000149011612 then\n"
        "    A0_0.ManualHeightOffset = ((A0_0.HeightOffsetUpperBound * (-A3_3)) * (0.89999997615814209))\n"
        "    end\n"
        "    end\n"
        "    else\n"
        "    if A1_1 == EGameAction.GAME_ACTION_PLAYER_MOVEMENT then\n"
        "    if CameraManager.GetCameraPhotoMode() then\n"
        "    if A0_0.LastZoomFrame then\n"
        "    if A0_0.LastZoomFrame + (1) >= Timing.GetProgramFrame() then\n"
        "    if math.abs(A3_3) < 0.5 then\n"
        "    A0_0.ClearToZoom = true\n"
        "    end\n"
        "    end\n"
        "    else\n"
        "    A0_0.ClearToZoom = true\n"
        "    end\n"
        "    A0_0.LastZoomFrame = Timing.GetProgramFrame()\n"
        "    if A0_0.ClearToZoom then\n"
        "    if A3_3 < -0.5 then\n"
        "    A0_0.ZoomLevel = A0_0.ZoomLevel + (1)\n"
        "    if A0_0.Camera then\n"
        "    if A0_0.Camera:IsAlive() then\n"
        "    Camera.SetZoomLevel(A0_0.Camera, A0_0.ZoomLevel)\n"
        "    end\n"
        "    end\n"
        "    A0_0.ClearToZoom = nil\n"
        "    end\n"
        "    if 0.5 < A3_3 then\n"
        "    A0_0.ZoomLevel = A0_0.ZoomLevel - (1)\n"
        "    if A0_0.Camera then\n"
        "    if A0_0.Camera:IsAlive() then\n"
        "    Camera.SetZoomLevel(A0_0.Camera, A0_0.ZoomLevel)\n"
        "    end\n"
        "    end\n"
        "    A0_0.ClearToZoom = nil\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    else\n"
        "    if A1_1 == EGameAction.GAME_ACTION_ROTATE_CAMERA then\n"
        "    if A0_0.LastRotateFrame then\n"
        "    if A0_0.LastRotateFrame + (1) >= Timing.GetProgramFrame() then\n"
        "    if math.abs(A2_2) < 0.80000001192092896 then\n"
        "    A0_0.ClearToRotate = true\n"
        "    end\n"
        "    end\n"
        "    else\n"
        "    A0_0.ClearToRotate = true\n"
        "    end\n"
        "    A0_0.LastRotateFrame = Timing.GetProgramFrame()\n"
        "    if A0_0:IsTargetLocking() then\n"
        "    L4_4 = false\n"
        "    if A0_0.ClearToRotate then\n"
        "    if 0.80000001192092896 < math.abs(A2_2) then\n"
        "    Targeting.CheckForBetterTargetInDirection(A0_0.Player, (((A2_2 < (0)) and true) or L4_4), 20)\n"
        "    A0_0:GrabTarget()\n"
        "    A0_0.ClearToRotate = false\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if not GetCanRotateCamera() then\n"
        "    if CameraManager.GetCameraPhotoMode() then\n"
        "    if A0_0.Camera then\n"
        "    if A0_0.Camera:IsAlive() then\n"
        "    A0_0.RotateRight = (-A2_2) * ((A0_0.RotateSpeed / Timing.GetTickRate()) * ((0.5) + Player.GetCameraSensitivity(A0_0.Player)))\n"
        "    A0_0.PlayerIsManuallyRotatingCamera = true\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if A0_0.Camera then\n"
        "    if A0_0.Camera:IsAlive() then\n"
        "    A0_0.RotateRight = (-A2_2) * ((A0_0.RotateSpeed / Timing.GetTickRate()) * ((0.5) + Player.GetCameraSensitivity(A0_0.Player)))\n"
        "    A0_0.PlayerIsManuallyRotatingCamera = true\n"
        "    end\n"
        "    end\n"
        "    else\n"
        "    if A1_1 == EGameAction.GAME_ACTION_TOGGLE_CAMERA_Z_MODE_ON then\n"
        "    A0_0.InVanityCounter = 15\n"
        "    else\n"
        "    if A1_1 == EGameAction.GAME_ACTION_TOGGLE_CAMERA_Z_MODE_OFF then\n"
        "    A0_0.InVanity = false\n"
        "    A0_0.InVanityCounter = nil\n"
        "    else\n"
        "    if A1_1 == EGameAction.GAME_ACTION_DEBUG_CAMERA_MOVE_BEHIND_HERO then\n"
        "    if 0.94999998807907104 < A0_0.CurrentMovementFactor then\n"
        "    if not CameraFunctions.GetSafeLookAtButtonDown(A0_0.Player) then\n"
        "    A0_0.MySetBehindHero = true\n"
        "    end\n"
        "    end\n"
        "    else\n"
        "    if A1_1 == EGameAction.GAME_ACTION_DEBUG_CAMERA_MOVE_BEHIND_HENCHMAN then\n"
        "    if 0.94999998807907104 < A0_0.CurrentMovementFactor then\n"
        "    if not CameraFunctions.GetSafeLookAtButtonDown(A0_0.Player) then\n"
        "    A0_0.MySetBehindHenchman = true\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.ProcessGameAction = function(A0_0, A1_1, A2_2, A3_3)",
        "  SACCamera.ProcessGameAction = function(A0_0, A1_1, A2_2, A3_3)\n"
        "    if A1_1 == EGameAction.GAME_ACTION_ZOOM_CAMERA then\n"
        "      if GetCanRotateCamera() then\n"
        "        if A0_0.ManualHeightIsRelative then\n"
        "          if not (0.10000000149011612 < A3_3) and A3_3 < -0.10000000149011612 then\n"
        "            A0_0.ManualHeightOffset = A0_0.ManualHeightOffset + ((-A3_3) * A0_0.HeightAdjustSpeed) * (0.5 + Player.GetCameraSensitivity(A0_0.Player))\n"
        "          end\n"
        "        elseif 0.10000000149011612 < A3_3 or A3_3 < -0.10000000149011612 then\n"
        "          A0_0.ManualHeightOffset = A0_0.HeightOffsetUpperBound * (-A3_3) * 0.89999997615814209\n"
        "        end\n"
        "      end\n"
        "    elseif A1_1 == EGameAction.GAME_ACTION_PLAYER_MOVEMENT then\n"
        "      if CameraManager.GetCameraPhotoMode() then\n"
        "        if A0_0.LastZoomFrame then\n"
        "          if Timing.GetProgramFrame() <= A0_0.LastZoomFrame + 1 then\n"
        "            if math.abs(A3_3) < 0.5 then\n"
        "              A0_0.ClearToZoom = true\n"
        "            end\n"
        "          end\n"
        "        else\n"
        "          A0_0.ClearToZoom = true\n"
        "        end\n"
        "        A0_0.LastZoomFrame = Timing.GetProgramFrame()\n"
        "        if A0_0.ClearToZoom then\n"
        "          if A3_3 < -0.5 then\n"
        "            A0_0.ZoomLevel = A0_0.ZoomLevel + 1\n"
        "            if A0_0.Camera then\n"
        "              if A0_0.Camera:IsAlive() then\n"
        "                Camera.SetZoomLevel(A0_0.Camera, A0_0.ZoomLevel)\n"
        "              end\n"
        "            end\n"
        "            A0_0.ClearToZoom = nil\n"
        "          elseif 0.5 < A3_3 then\n"
        "            A0_0.ZoomLevel = A0_0.ZoomLevel - 1\n"
        "            if A0_0.Camera then\n"
        "              if A0_0.Camera:IsAlive() then\n"
        "                Camera.SetZoomLevel(A0_0.Camera, A0_0.ZoomLevel)\n"
        "              end\n"
        "            end\n"
        "            A0_0.ClearToZoom = nil\n"
        "          end\n"
        "        end\n"
        "      end\n"
        "    elseif A1_1 == EGameAction.GAME_ACTION_ROTATE_CAMERA then\n"
        "      if A0_0.LastRotateFrame then\n"
        "        if Timing.GetProgramFrame() <= A0_0.LastRotateFrame + 1 then\n"
        "          if math.abs(A2_2) < 0.80000001192092896 then\n"
        "            A0_0.ClearToRotate = true\n"
        "          end\n"
        "        end\n"
        "      else\n"
        "        A0_0.ClearToRotate = true\n"
        "      end\n"
        "      A0_0.LastRotateFrame = Timing.GetProgramFrame()\n"
        "      if A0_0:IsTargetLocking() then\n"
        "        local L4_4 = false\n"
        "        if A2_2 < 0 then\n"
        "          L4_4 = true\n"
        "        end\n"
        "        if A0_0.ClearToRotate then\n"
        "          if 0.80000001192092896 < math.abs(A2_2) then\n"
        "            Targeting.CheckForBetterTargetInDirection(A0_0.Player, L4_4, 20)\n"
        "            A0_0:GrabTarget()\n"
        "            A0_0.ClearToRotate = false\n"
        "          end\n"
        "        end\n"
        "      end\n"
        "      if GetCanRotateCamera() or CameraManager.GetCameraPhotoMode() then\n"
        "        if A0_0.Camera then\n"
        "          if A0_0.Camera:IsAlive() then\n"
        "            A0_0.RotateRight = (-A2_2) * ((A0_0.RotateSpeed / Timing.GetTickRate()) * (0.5 + Player.GetCameraSensitivity(A0_0.Player)))\n"
        "            A0_0.PlayerIsManuallyRotatingCamera = true\n"
        "          end\n"
        "        end\n"
        "      end\n"
        "    elseif A1_1 == EGameAction.GAME_ACTION_TOGGLE_CAMERA_Z_MODE_ON then\n"
        "      A0_0.InVanityCounter = 15\n"
        "    elseif A1_1 == EGameAction.GAME_ACTION_TOGGLE_CAMERA_Z_MODE_OFF then\n"
        "      A0_0.InVanity = false\n"
        "      A0_0.InVanityCounter = nil\n"
        "    elseif A1_1 == EGameAction.GAME_ACTION_DEBUG_CAMERA_MOVE_BEHIND_HERO then\n"
        "      if 0.94999998807907104 < A0_0.CurrentMovementFactor then\n"
        "        if not CameraFunctions.GetSafeLookAtButtonDown(A0_0.Player) then\n"
        "          A0_0.MySetBehindHero = true\n"
        "        end\n"
        "      end\n"
        "    elseif A1_1 == EGameAction.GAME_ACTION_DEBUG_CAMERA_MOVE_BEHIND_HENCHMAN then\n"
        "      if 0.94999998807907104 < A0_0.CurrentMovementFactor then\n"
        "        if not CameraFunctions.GetSafeLookAtButtonDown(A0_0.Player) then\n"
        "          A0_0.MySetBehindHenchman = true\n"
        "        end\n"
        "      end\n"
        "    end\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.AssembleZoomLevelFlags = function(A0_0)",
        "  SACCamera.AssembleZoomLevelFlags = function(A0_0)\n"
        "    local L1_1 = SACCamera.ForceIndoors\n"
        "    if A0_0.Camera and not A0_0.SkipBuildingCheck and A0_0:ShouldDoSpreadAction(\"BuildingCheck\") then\n"
        "      local L2_2 = A0_0.Primary\n"
        "      A0_0.StoredDesiredInsideBuilding = Camera.IsBelowCameraRelevantBuilding(A0_0.Camera, L2_2:GetPosition() + CVector3(0, 0, 1))\n"
        "    end\n"
        "    L1_1 = L1_1 or A0_0.StoredDesiredInsideBuilding\n"
        "    A0_0.SkipBuildingCheck = false\n"
        "    if SACCamera.IgnoreIndoors then\n"
        "      L1_1 = false\n"
        "    end\n"
        "    A0_0.IsInsideBuilding = SACCamera:SupportHysteresis(A0_0.IsInsideBuilding, L1_1, \"BuildingCounter\", 5, 30)\n"
        "    local L2_2 = A0_0:GetDesiredCombatCamWeight()\n"
        "    local L3_3 = A0_0.MovementThisFrame\n"
        "    if L3_3 then\n"
        "      L3_3 = not (0.05000000074505806 < A0_0.MovementThisFrame:GetLengthXY())\n"
        "    end\n"
        "    A0_0.IsNearToDrop = SACCamera:SupportHysteresis(A0_0.IsNearToDrop, A0_0:IsPlayerApproachingDrop() and L2_2 == 0 and (A0_0.Secondary or not L3_3), \"DropCounter\", 1, 15)\n"
        "    if SACCamera.IgnoreDrop then\n"
        "      A0_0.IsNearToDrop = false\n"
        "    end\n"
        "    local L4_4 = not A0_0.Secondary and A0_0:CalculateIfPlayerIsSprinting()\n"
        "    A0_0.IsSprinting = SACCamera:SupportHysteresis(A0_0.IsSprinting, L4_4, \"SprintingCounter\", 15, 5)\n"
        "    local L5_5 = false\n"
        "    if not A0_0.Secondary then\n"
        "      if Player.IsAvailable(A0_0.Player) then\n"
        "        L5_5 = Player.HasPlayerMode(A0_0.Player, EPlayerMode.PLAYER_MODE_FALLING)\n"
        "        if not L5_5 then\n"
        "          if Physics.IsAvailable(A0_0.Player) then\n"
        "            L5_5 = Physics.IsSliding(A0_0.Player)\n"
        "          else\n"
        "            L5_5 = false\n"
        "          end\n"
        "        end\n"
        "      end\n"
        "    end\n"
        "    A0_0.IsFalling = SACCamera:SupportHysteresis(A0_0.IsFalling, L5_5, \"FallingCounter\", 2, 7)\n"
        "    local L6_6 = false\n"
        "    if not A0_0.Secondary then\n"
        "      if SpellManager.IsAvailable(A0_0.Player) then\n"
        "        L6_6 = SpellManager.IsEntityAssassinsRushActive(A0_0.Player)\n"
        "      end\n"
        "    end\n"
        "    A0_0.IsAssassin = L6_6\n"
        "    if A0_0.Secondary then\n"
        "      SACCamera.IsFightingTroll = false\n"
        "    else\n"
        "      SACCamera.IsFightingTroll = SACCamera.TrollImFighting\n"
        "    end\n"
        "    if A0_0.Secondary then\n"
        "      A0_0.IsICS = false\n"
        "    else\n"
        "      A0_0.IsICS = SACCamera.IsICSActive\n"
        "    end\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.FrameUpdate = function(A0_0)",
        "  SACCamera.FrameUpdate = function(A0_0)\n"
        "    if not A0_0.Camera or not A0_0.Camera:IsAlive() then\n"
        "      Debug.Error(\"AdamL:SACCamera's self.Camera is invalid at the start of SACCamera:FrameUpdate, please remember what has just happened and grab me.\")\n"
        "      return\n"
        "    end\n"
        "    A0_0:CheckForTeleport()\n"
        "    A0_0:SearchForFoesIfNeeded()\n"
        "    A0_0:SearchForInterestingEntitiesIfNeeded()\n"
        "    A0_0:RampCombatScale()\n"
        "    A0_0:RampMovementFactor()\n"
        "    A0_0:UpdateZoomLevels()\n"
        "    A0_0:SetMaxCoopDistance()\n"
        "    A0_0:MaintainExpressionVillager()\n"
        "    A0_0:MoveBehindHeroOrHenchman()\n"
        "    A0_0:CalculatePrimaryAndSecondary(A0_0.PreBoundCamPos or A0_0.Camera:GetPosition(), A0_0.JustChangedDirectionPreviousPos)\n"
        "    A0_0:SupportPostLoad()\n"
        "    A0_0:LoadUpRotationFromAiming()\n"
        "    A0_0:UpdateTarget()\n"
        "    A0_0:CalculateDesiredDirection()\n"
        "    local L1_1 = A0_0:GetDesiredTargetDirection()\n"
        "    if L1_1 then\n"
        "      A0_0:AlignToDirection(L1_1, A0_0:GetDesiredDirectionStrength(), A0_0:GetAvoidAligning())\n"
        "    end\n"
        "    local L2_2 = A0_0:CalculateSlew()\n"
        "    local L3_3 = A0_0.PreBoundCamPos\n"
        "    L3_3 = A0_0:ApplyRotateVector(L3_3)\n"
        "    L3_3 = A0_0:SupportDebugMoveToCut(L3_3)\n"
        "    local L4_4, L5_5, L6_6\n"
        "    local L7_7, L8_8 = A0_0:CalculateCentreAndCage()\n"
        "    L6_6 = L8_8\n"
        "    L4_4 = L7_7\n"
        "    if Camera.DrawDebug then\n"
        "      Debug.DrawLine(L4_4, L4_4 + CVector3(0, 0, 3), 255, 255, 255)\n"
        "      DrawApproxCircle(L4_4, L6_6, 255, 255, 255)\n"
        "    end\n"
        "    L7_7 = A0_0:CalculateStringLength(L6_6)\n"
        "    if Camera.DrawDebug then\n"
        "      DrawApproxCircle(L4_4, L7_7, 255, 255, 255)\n"
        "    end\n"
        "    L5_5 = A0_0:CalculateXY(L3_3, L6_6, L7_7, L4_4)\n"
        "    L5_5:SetZ(A0_0:GetHeight(L5_5, L4_4, L6_6))\n"
        "    L8_8, L9_9 = A0_0:TendFocusAndPosVerticallyIfNeeded(L5_5, L4_4)\n"
        "    L4_4 = L9_9\n"
        "    L5_5 = L8_8\n"
        "    A0_0.PreBoundCamPos = CVector3(L5_5)\n"
        "    A0_0.PreBoundFocalPos = CVector3(L4_4)\n"
        "    L8_8 = nil\n"
        "    L4_4, L5_5, L8_8 = A0_0:DealWithCollision(L5_5, L4_4)\n"
        "    local L9_9 = A0_0:GetInterestingPoint()\n"
        "    if A0_0:GetOverriddenData().BlendToInterestingPointAmount then\n"
        "      if L9_9 then\n"
        "        if not A0_0.PlayerIsManuallyRotatingCamera then\n"
        "          L4_4 = CameraFunctions.Interpolate(L4_4, A0_0:GetOverriddenData().BlendToInterestingPointAmount, L9_9)\n"
        "        end\n"
        "      end\n"
        "    end\n"
        "    local L10_10 = L4_4\n"
        "    if A0_0:GetOverriddenData().SetDOFFocusToInterestingPoint then\n"
        "      if L9_9 then\n"
        "        L10_10 = L9_9\n"
        "      end\n"
        "    end\n"
        "    local L11_11 = CameraFunctions.GetRangeNormalToCamera(L5_5, L4_4, L10_10)\n"
        "    A0_0:SetDesiredDOF(CameraFunctions.AssembleDOFData(L11_11, A0_0.NonCageRadius, 5, 5, 0, 0, 0))\n"
        "    A0_0:SetDesiredRoll(L2_2, 3)\n"
        "    if A0_0.StartInterpolate then\n"
        "      if A0_0.JustChangedDirectionPreviousPos then\n"
        "        if Camera.DoesCameraRayIntersect(A0_0.Camera, A0_0.JustChangedDirectionPreviousPos, L5_5, CameraFunctions.RadiusToUseInCollision) then\n"
        "          A0_0.StartInterpolate = false\n"
        "          Camera.InterpolateFor(A0_0.Camera, 0)\n"
        "        end\n"
        "        A0_0.JustChangedDirectionPreviousPos = nil\n"
        "      end\n"
        "    end\n"
        "    if A0_0.StartInterpolate then\n"
        "      local L12_12 = SACCamera.CameraTransitionTime\n"
        "      A0_0:SetPulseBlur(SACCamera.TransitionBlur, L12_12)\n"
        "      Camera.InterpolateFor(A0_0.Camera, L12_12 * Timing.GetTickRate())\n"
        "      A0_0.StartInterpolate = false\n"
        "    else\n"
        "      if A0_0.SnapThisFrame then\n"
        "        A0_0:SetCutThisFrame()\n"
        "      end\n"
        "    end\n"
        "    local L12_12 = math.min(1, (L4_4 - L5_5):GetLengthXY() * 0.5)\n"
        "    A0_0:FadeAllEntitiesBasedOnPositionIfNeeded(L12_12)\n"
        "    if L8_8 then\n"
        "      A0_0:FadeEntityBasedOnPositionIfNeeded(A0_0.Primary, 0.5, 0.004999999888241291)\n"
        "    end\n"
        "    local L13_13 = A0_0:GetActiveZoomLevel().FOV\n"
        "    if A0_0:GetDesiredTargetDirection() then\n"
        "      L13_13 = L13_13 - 7\n"
        "    end\n"
        "    A0_0:SetDesiredFOV(L13_13, SACCamera.CameraFOVDamping)\n"
        "    if not A0_0.RollingCamHeight then\n"
        "      A0_0.RollingCamHeight = L5_5:GetZ()\n"
        "    end\n"
        "    if A0_0.SnapThisFrame or L8_8 or A0_0.IsAssassin then\n"
        "      A0_0.RollingCamHeight = L5_5:GetZ()\n"
        "    else\n"
        "      A0_0.RollingCamHeight = CameraFunctions.GetRollingAverage(A0_0.RollingCamHeight, L5_5:GetZ(), SACCamera.CameraMoveDamping)\n"
        "    end\n"
        "    L5_5:SetZ(A0_0.RollingCamHeight)\n"
        "    if not A0_0.RollingFocalPos then\n"
        "      A0_0.RollingFocalPos = L4_4\n"
        "    end\n"
        "    if A0_0.SnapThisFrame or L8_8 or A0_0.IsAssassin then\n"
        "      A0_0.RollingFocalPos = L4_4\n"
        "    else\n"
        "      A0_0.RollingFocalPos = CameraFunctions.GetRollingAverage(A0_0.RollingFocalPos, L4_4, SACCamera.CameraMoveDamping)\n"
        "    end\n"
        "    A0_0:SetDesiredPosition(L5_5, true)\n"
        "    A0_0:SetDesiredDirection(A0_0.RollingFocalPos - L5_5)\n"
        "    A0_0.SnapThisFrame = false\n"
        "    CameraBase.PureFrameUpdate(A0_0)\n"
        "    A0_0.PlayerIsManuallyRotatingCamera = false\n"
        "    A0_0:ClearTime()\n"
        "    A0_0.LevelJustLoaded = nil\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.ConstructZoomData = function(A0_0, A1_1, A2_2, A3_3, A4_4)",
        "  SACCamera.ConstructZoomData = function(A0_0, A1_1, A2_2, A3_3, A4_4)\n"
        "    local L5_5 = A0_0:GetLength()\n"
        "    local L6_6 = A0_0:GetLengthXY()\n"
        "    local L7_7 = {}\n"
        "    L7_7.StringLength = L6_6\n"
        "    L7_7.CageRadius = L6_6\n"
        "    local L8_8 = A1_1:GetZ() / A1_1:GetLengthXY()\n"
        "    L7_7.FocalOffset = A0_0:GetZ() + L6_6 * L8_8\n"
        "    L7_7.HeightOffset = A0_0:GetZ() - L7_7.FocalOffset\n"
        "    local L10_10 = A1_1\n"
        "    L10_10:SetZ(0)\n"
        "    local L11_11 = A0_0 * -1\n"
        "    L11_11:SetZ(0)\n"
        "    local L12_12 = L10_10:GetDot(L11_11) / (L6_6 * L10_10:GetLengthXY())\n"
        "    local L13_13 = L6_6 / L12_12\n"
        "    local L14_14 = L13_13 * L13_13 - L6_6 * L6_6\n"
        "    if 0 < L14_14 then\n"
        "      local L15_15 = math.sqrt(L14_14)\n"
        "      local L16_16 = 0 < L11_11:GetCross(L10_10):GetZ() and -L15_15 or L15_15\n"
        "      L7_7.HorizontalFocalOffset = L16_16\n"
        "    end\n"
        "    local L15_15 = A4_4 and 1 or 0\n"
        "    L7_7.TrackAmount = L15_15\n"
        "    if A3_3 then\n"
        "      self.PreBoundCamPos = self.Player:GetPosition() + A0_0\n"
        "    end\n"
        "    L7_7.FOV = A2_2\n"
        "    return L7_7\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.SearchForFoesIfNeeded = function(A0_0)",
        "  SACCamera.SearchForFoesIfNeeded = function(A0_0)\n"
        "    if A0_0.NearbyFoes == nil or A0_0:ShouldDoSpreadAction(\"SearchForFoes\") or Camera.Perf then\n"
        "      A0_0.NearbyFoes = A0_0:CalculateNearbyFoes()\n"
        "      SACCamera.NearbyFoes = A0_0.NearbyFoes\n"
        "      A0_0:CalculateDesiredCombatScale()\n"
        "      SACCamera.ClearValidityOfWeightedList(A0_0.WeightedNearbyFoes)\n"
        "      SACCamera.AddInterestingEntityListToWeightedList(A0_0.WeightedNearbyFoes, A0_0.NearbyFoes, SACCamera.InterestingEnemyDistance)\n"
        "    end\n"
        "    SACCamera.ValidateAndUpdatePositionOfWeightedList(A0_0.WeightedNearbyFoes)\n"
        "    SACCamera.ValidateAndUpdatePositionOfWeightedList(A0_0.GetInterestingEntities())\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.EstablishMyZoomLevels = function(A0_0)",
        "  SACCamera.EstablishMyZoomLevels = function(A0_0)\n"
        "    A0_0.MyVanityZoomLevels = SACCamera.VanityZoomLevels\n"
        "    local L1_1 = true\n"
        "    A0_0.MyExteriorZoomLevels = {L1_1 and SACCamera.SafeGatherZoomLevelsFromRegion(\"CameraExploreClose\") or SACCamera.ExteriorZoomLevels[1], Name = \"Exterior\"}\n"
        "    A0_0.MyInteriorZoomLevels = {L1_1 and SACCamera.SafeGatherZoomLevelsFromRegion(\"CameraInternal\") or SACCamera.InteriorZoomLevels[1], Name = \"Interior\"}\n"
        "    A0_0.MyNearToDropZoomLevels = {SACCamera.NearToDropZoomLevel, Name = \"NearToDrop\"}\n"
        "    A0_0.MySprintZoomLevels = {SACCamera.SprintZoomLevel, Name = \"Sprint\"}\n"
        "    A0_0.MyAssassinZoomLevels = {SACCamera.AssassinZoomLevel, Name = \"AssassinsRush\"}\n"
        "    A0_0.MyICSZoomLevels = {SACCamera.ICSZoomLevel, Name = \"ICS\"}\n"
        "    A0_0.MyICSChildZoomLevels = {SACCamera.ICSChildZoomLevel, Name = \"ICSChild\"}\n"
        "    A0_0.MyFallZoomLevels = {SACCamera.FallZoomLevel, Name = \"Falling\"}\n"
        "    A0_0.MyTrollZoomLevels = {SACCamera.TrollZoomLevel, Name = \"TrollFight\"}\n"
        "    A0_0.MyCombatInteriorZoomLevel = L1_1 and SACCamera.SafeGatherZoomLevelsFromRegion(\"CameraCombatInternal\") or SACCamera.CombatInteriorZoomLevel\n"
        "    A0_0.MyCombatExteriorZoomLevel = L1_1 and SACCamera.SafeGatherZoomLevelsFromRegion(\"CameraCombatExternal\") or SACCamera.CombatExteriorZoomLevel\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.PrintTime = function(A0_0, A1_1, A2_2)",
        "  SACCamera.PrintTime = function(A0_0, A1_1, A2_2)\n"
        "    if Camera.Perf then\n"
        "      if not A0_0.TimeStep then\n"
        "        A0_0.TimeStep = {}\n"
        "      end\n"
        "      if not A0_0.TimeName then\n"
        "        A0_0.TimeName = {}\n"
        "      end\n"
        "      if not A0_0.TimeValue then\n"
        "        A0_0.TimeValue = {}\n"
        "      end\n"
        "      local L3_3 = Debug.GetTime()\n"
        "      if A0_0.TimeIndent then\n"
        "        if A0_0.TimeIndent < A2_2 then\n"
        "          if A0_0.TimeValue[A2_2 - 1] then\n"
        "            A0_0.TimeValue[A2_2] = A0_0.TimeValue[A2_2 - 1]\n"
        "            A0_0.TimeStep[A2_2] = 0\n"
        "          end\n"
        "        end\n"
        "      end\n"
        "      A0_0.TimeStep[A2_2] = A0_0.TimeStep[A2_2] and A0_0.TimeStep[A2_2] + 1 or 0\n"
        "      if A0_0.TimeValue[A2_2] then\n"
        "        local L4_4 = L3_3 - A0_0.TimeValue[A2_2]\n"
        "        A0_0.TimeName[A2_2] = A1_1\n"
        "        local L5_5 = \"\"\n"
        "        for L9_9 = 1, A2_2, 1 do\n"
        "          if L9_9 < A2_2 then\n"
        "            L5_5 = L5_5 .. \"    \"\n"
        "          else\n"
        "            L5_5 = L5_5 .. \"Step - \" .. A0_0.TimeStep[L9_9] .. \" \" .. A0_0.TimeName[L9_9] .. \" \"\n"
        "          end\n"
        "        end\n"
        "        L5_5 = L5_5 .. \". Time - \" .. L4_4\n"
        "        print(L5_5)\n"
        "      end\n"
        "      A0_0.TimeValue[A2_2] = L3_3\n"
        "      A0_0.TimeIndent = A2_2\n"
        "    end\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.LoadUpRotationFromAimingPlayer = function(A0_0, A1_1)",
        "  SACCamera.LoadUpRotationFromAimingPlayer = function(A0_0, A1_1)\n"
        "    if Targeting.GetInAiming(A1_1) then\n"
        "      local L3_3 = Targeting.GetTargetedPosition(A1_1) - A1_1:GetPosition()\n"
        "      local L4_4 = Targeting.GetCameraSafeTargetedPosition(A1_1)\n"
        "      local L5_5 = L4_4 - A0_0.Camera:GetPosition()\n"
        "      local L6_6 = 1\n"
        "      if 0 < L6_6 then\n"
        "        L3_3:SetZ(0)\n"
        "        L3_3:Normalise()\n"
        "        L5_5:SetZ(0)\n"
        "        L5_5:Normalise()\n"
        "        local L7_7 = Camera.GetForwardVector(A0_0.Camera)\n"
        "        local L8_8 = Camera.GetRightVector(A0_0.Camera)\n"
        "        L8_8:SetZ(0)\n"
        "        L8_8:Normalise()\n"
        "        L6_6 = math.min(L6_6, 1)\n"
        "        local L9_9 = L5_5:GetDot(L8_8)\n"
        "        if SACCamera.TargetingTurnBuffer < math.abs(L9_9) then\n"
        "          if 0 < L9_9 then\n"
        "            L9_9 = L9_9 - SACCamera.TargetingTurnBuffer * L6_6\n"
        "          else\n"
        "            L9_9 = L9_9 + SACCamera.TargetingTurnBuffer * L6_6\n"
        "          end\n"
        "          A0_0.RotateRight = A0_0.RotateRight or 0\n"
        "          A0_0.RotateRight = A0_0.RotateRight + L9_9 * SACCamera.TargetingTurnPower\n"
        "        end\n"
        "        local L10_10 = L4_4 - A0_0.Camera:GetPosition()\n"
        "        local L11_11 = Camera.GetUpVector(A0_0.Camera)\n"
        "        L10_10:Normalise()\n"
        "        local L12_12 = L10_10:GetDot(L11_11)\n"
        "        if SACCamera.TargetingTiltBuffer < L12_12 then\n"
        "          A0_0.ManualHeightOffset = A0_0.ManualHeightOffset + (L12_12 - SACCamera.TargetingTiltBuffer) * SACCamera.TargetingTiltPower\n"
        "          A0_0.ManualHeightOffset = math.max(A0_0.ManualHeightOffset, -A0_0.HeightOffsetLowerBound * 0.25)\n"
        "        end\n"
        "      end\n"
        "    end\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.AlignToDirection = function(A0_0, A1_1, A2_2, A3_3)",
        "  SACCamera.AlignToDirection = function(A0_0, A1_1, A2_2, A3_3)\n"
        "    local L4_4 = Camera.GetForwardVector(A0_0.Camera)\n"
        "    L4_4:SetZ(0)\n"
        "    L4_4:Normalise()\n"
        "    local L5_5 = A1_1\n"
        "    L5_5:SetZ(0)\n"
        "    L5_5:Normalise()\n"
        "    local L6_6 = L5_5:GetDot(L4_4)\n"
        "    local L7_7 = 0 < L5_5:GetCross(L4_4):GetZ()\n"
        "    local L8_8 = 1 - (1 - A2_2) * 0.5\n"
        "    local L9_9 = 3\n"
        "    if not A0_0.RotateRight then\n"
        "      local L10_10 = 0\n"
        "      A0_0.RotateRight = L10_10\n"
        "    end\n"
        "    if L6_6 < A2_2 then\n"
        "      local L10_10 = (A2_2 - L6_6) / ((1 - A2_2) * L9_9)\n"
        "      L10_10 = math.min(L10_10, 1)\n"
        "      local L11_11 = A0_0.InwardsRotateSpeed / Timing.GetTickRate()\n"
        "      A0_0.RotateRight = A0_0.RotateRight + L11_11 * L10_10 * (L7_7 and -1 or 1)\n"
        "    elseif A3_3 and L8_8 < L6_6 then\n"
        "      local L10_10 = (L6_6 - L8_8) / ((1 - L8_8) * L9_9)\n"
        "      L10_10 = math.min(L10_10, 1)\n"
        "      local L11_11 = A0_0.OutwardsRotateSpeed / Timing.GetTickRate()\n"
        "      A0_0.RotateRight = A0_0.RotateRight + L11_11 * L10_10 * (L7_7 and 1 or -1)\n"
        "    end\n"
        "    A0_0.PlayerIsManuallyRotatingCamera = true\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.DealWithCollision = function(A0_0, A1_1, A2_2)",
        "  SACCamera.DealWithCollision = function(A0_0, A1_1, A2_2)\n"
        "    local L3_3 = Age.IsAvailable(A0_0.Primary) and Age.GetAgeGroup(A0_0.Primary) == EAgeGroup.EAGE_GROUP_CHILD and 0.20000000298023224 or 0\n"
        "    local L4_4 = A0_0:GetCentre(A0_0.Primary, L3_3)\n"
        "    if not A0_0.PrevCollisionCentre or 0.0099999997764825821 < (L4_4 - A0_0.PrevCollisionCentre):GetSquaredLength() then\n"
        "      if A0_0.Camera then\n"
        "        if Camera.IsOverWaterAtPosition(A0_0.Camera, L4_4) then\n"
        "          A0_0.CollisionWaterHeight = Camera.GetWaterPositionAtPosition(A0_0.Camera, L4_4):GetZ()\n"
        "        end\n"
        "      else\n"
        "        A0_0.CollisionWaterHeight = nil\n"
        "      end\n"
        "      A0_0.PrevCollisionCentre = L4_4\n"
        "    end\n"
        "    if A0_0.CollisionWaterHeight then\n"
        "      L4_4:SetZ(math.max(L4_4:GetZ(), A0_0.CollisionWaterHeight + 0.20000000298023224))\n"
        "      A1_1:SetZ(math.max(A1_1:GetZ(), A0_0.CollisionWaterHeight + 0.20000000298023224))\n"
        "      A2_2:SetZ(math.max(A2_2:GetZ(), A0_0.CollisionWaterHeight + 0.20000000298023224))\n"
        "    end\n"
        "    local L5_5 = L4_4\n"
        "    local L6_6 = nil\n"
        "    local L7_7 = A1_1\n"
        "    local L8_8 = nil\n"
        "    A1_1, L8_8, L6_6 = CameraFunctions.SmoothAdjustPosToAvoidOcclusion(A0_0.Camera, L4_4, A1_1)\n"
        "    local L9_9 = SACCamera.GetRightFromDirection(L4_4 - L7_7)\n"
        "    if A0_0.PreviousCollisionCentre then\n"
        "      local L10_10 = L4_4 - A0_0.PreviousCollisionCentre\n"
        "      if L10_10 ~= 0 then\n"
        "        local L11_11 = L10_10:GetDot(L9_9)\n"
        "        local L12_12 = L9_9 * L11_11\n"
        "        local L13_13 = (L7_7 - A0_0.PreviousCollisionCentre):GetLength()\n"
        "        local L14_14 = 0 < L11_11 and 0.80000001192092896 or -0.80000001192092896\n"
        "        local L15_15, L16_16 = CameraFunctions.GetSimpleCollision(A0_0.Camera, L4_4 + L9_9 * L14_14, L7_7 + L9_9 * L14_14)\n"
        "        if L8_8 < L16_16 then\n"
        "          local L17_17 = L16_16\n"
        "          local L18_18 = L13_13 - L17_17\n"
        "          L18_18 = math.max(L18_18, 1)\n"
        "          local L19_19 = L17_17 / L18_18\n"
        "          A0_0.PreBoundCamPos = A0_0.PreBoundCamPos - L12_12 * L19_19\n"
        "        end\n"
        "      end\n"
        "    end\n"
        "    if L6_6 then\n"
        "      A2_2 = SACCamera.MaintainDirectionAfterMovingPosition(A1_1, L7_7, A2_2, L4_4)\n"
        "    end\n"
        "    A2_2, A1_1 = A0_0:TendFocusBasedOnDistanceToCollisionCentre(A2_2, A1_1, L5_5)\n"
        "    A0_0.PreviousCollisionCentre = L4_4\n"
        "    return A2_2, A1_1, L6_6\n"
        "  end\n");
    changed |= ReplaceFunctionByHeader(
        source,
        "SACCamera.ConstructZoomDataUsingRecord = function(A0_0, A1_1, A2_2)",
        "  SACCamera.ConstructZoomDataUsingRecord = function(A0_0, A1_1, A2_2)\n"
        "    local L3_3 = GDB.GetRecord(A0_0)\n"
        "    if L3_3:IsNull() then\n"
        "      return\n"
        "    end\n"
        "    local L4_4 = L3_3:GetRecord(\"Position\")\n"
        "    local L5_5 = L4_4:GetFloat(\"X\")\n"
        "    local L6_6 = L4_4:GetFloat(\"Y\")\n"
        "    local L7_7 = L4_4:GetFloat(\"Z\")\n"
        "    local L8_8 = CVector3(L5_5, L6_6, L7_7)\n"
        "    local L9_9 = L3_3:GetRecord(\"Forward\")\n"
        "    local L10_10 = L9_9:GetFloat(\"X\")\n"
        "    local L11_11 = L9_9:GetFloat(\"Y\")\n"
        "    local L12_12 = L9_9:GetFloat(\"Z\")\n"
        "    local L13_13 = CVector3(L10_10, L11_11, L12_12)\n"
        "    local L14_14 = L3_3:GetFloat(\"FOV\")\n"
        "    L14_14 = L14_14 * (180 / math.pi)\n"
        "    return SACCamera.ConstructZoomData(L8_8, L13_13, L14_14, A1_1, A2_2)\n"
        "  end\n");
    changed |= ReplaceAllLiteral(
        source,
        "    L6_6:Normalise()\n"
        "    A0_0.PreviousInputCentre = A1_1\n"
        "    local L12_12 = A1_1 - A0_0.PreBoundFocalPos\n"
        "    L12_12:SetZ((0))\n"
        "    local L12_12 = L12_12(A0_0.Camera)\n"
        "    local L13_13 = L10_10:GetDot(L12_12)\n"
        "    local L14_14 = L6_6:GetDot(L12_12)\n",
        "    L6_6:Normalise()\n"
        "    A0_0.PreviousInputCentre = A1_1\n"
        "    local L8_8 = A0_0.PreBoundFocalPos\n"
        "    local L9_9 = A1_1\n"
        "    local L10_10 = L9_9 - L8_8\n"
        "    L10_10:SetZ((0))\n"
        "    local L11_11 = Camera.GetRightVector(A0_0.Camera)\n"
        "    local L12_12 = L10_10:GetDot(L11_11)\n"
        "    local L13_13 = L6_6:GetDot(L11_11)\n"
        "    local L14_14 = false\n"
        "    local L15_15 = false\n"
        "    local L16_16 = false\n");
    changed |= ReplaceAllLiteral(
        source,
        "    if A0_0.DesiredSlotOffset then\n"
        "    if L7_7 then\n"
        "    if A0_0.DesiredSlotOffset == 0 then\n"
        "    local r17 = (r17 % (2))\n"
        "    if Timing.GetProgramFrame() % (2) ~= 0 or not 0.0099999997764825821 then\n"
        "    r17 = -0.0099999997764825821\n"
        "    end\n"
        "    A0_0.DesiredSlotOffset = r17\n"
        "    else\n"
        "    A0_0.DesiredSlotOffset = (A0_0.DesiredSlotOffset - (L14_14 * SACCamera.TurnDriftRate))\n"
        "    if 0 >= A0_0.DesiredSlotOffset or not SACCamera.DriftRate then\n"
        "    r18 = -SACCamera.DriftRate\n"
        "    end\n"
        "    A0_0.DesiredSlotOffset = (A0_0.DesiredSlotOffset + r18)\n"
        "    end\n"
        "    A0_0.DesiredSlotOffset = math.max(A0_0.DesiredSlotOffset, (-L4_4))\n"
        "    A0_0.DesiredSlotOffset = math.min(A0_0.DesiredSlotOffset, L4_4)\n"
        "    L8_8 = (L8_8 + (L12_12 * (L13_13 - A0_0.DesiredSlotOffset)))\n"
        "    if 0 < A0_0.FramesOutsideSide then\n"
        "    local L16_16 = true\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if L4_4 < math.abs(r12) then\n"
        "    if 0 < r12 then\n"
        "    L8_8 = (L8_8 + (L12_12 * (r12 - L4_4)))\n"
        "    else\n"
        "    L8_8 = (L8_8 + (L12_12 * (r12 + L4_4)))\n"
        "    local r12 = -L4_4\n"
        "    end\n"
        "    L16_16 = L7_7\n"
        "    end\n"
        "    Camera.GetForwardVector(A0_0.Camera):SetZ(0)\n"
        "    r17:Normalise()\n"
        "    if L5_5 < math.abs(L10_10:GetDot(r17)) then\n"
        "    if 0 < r18 then\n"
        "    L8_8 = (L8_8 + (r17 * (r18 - L5_5)))\n"
        "    local L17_17 = L16_16\n"
        "    else\n"
        "    L8_8 = (L8_8 + (r17 * (r18 + L5_5)))\n"
        "    end\n"
        "    end\n"
        "    if L7_7 then\n"
        "    if not L17_17 then\n"
        "    if not false then\n"
        "    if L17_17 then\n"
        "    if not A0_0.DesiredSlotOffset then\n"
        "    A0_0.DesiredSlotOffset = r12\n"
        "    else\n"
        "    A0_0.DesiredSlotOffset = A0_0.DesiredSlotOffset\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if not A0_0.DesiredSlotOffset then\n"
        "    A0_0.DesiredSlotOffset = r12\n"
        "    else\n"
        "    A0_0.DesiredSlotOffset = A0_0.DesiredSlotOffset\n"
        "    end\n"
        "    else\n"
        "    A0_0.DesiredSlotOffset = nil\n"
        "    end\n"
        "    A0_0:UpdateEdgeFrames(L16_16, L17_17, false)\n",
        "    if A0_0.DesiredSlotOffset and L7_7 then\n"
        "    if A0_0.DesiredSlotOffset == 0 then\n"
        "    local L17_17 = Timing.GetProgramFrame() % 2 == 0 and 0.0099999997764825821 or -0.0099999997764825821\n"
        "    A0_0.DesiredSlotOffset = L17_17\n"
        "    else\n"
        "    A0_0.DesiredSlotOffset = A0_0.DesiredSlotOffset - L13_13 * SACCamera.TurnDriftRate\n"
        "    local L17_17 = A0_0.DesiredSlotOffset\n"
        "    local L18_18 = 0 < A0_0.DesiredSlotOffset and SACCamera.DriftRate or -SACCamera.DriftRate\n"
        "    A0_0.DesiredSlotOffset = L17_17 + L18_18\n"
        "    end\n"
        "    A0_0.DesiredSlotOffset = math.max(A0_0.DesiredSlotOffset, -L4_4)\n"
        "    A0_0.DesiredSlotOffset = math.min(A0_0.DesiredSlotOffset, L4_4)\n"
        "    L8_8 = L8_8 + L11_11 * (L12_12 - A0_0.DesiredSlotOffset)\n"
        "    if 0 < A0_0.FramesOutsideSide then\n"
        "    L14_14 = true\n"
        "    end\n"
        "    L12_12 = A0_0.DesiredSlotOffset\n"
        "    end\n"
        "    if L4_4 < math.abs(L12_12) then\n"
        "    if 0 < L12_12 then\n"
        "    L8_8 = L8_8 + L11_11 * (L12_12 - L4_4)\n"
        "    L12_12 = L4_4\n"
        "    else\n"
        "    L8_8 = L8_8 + L11_11 * (L12_12 + L4_4)\n"
        "    L12_12 = -L4_4\n"
        "    end\n"
        "    L14_14 = L7_7\n"
        "    end\n"
        "    local L17_17 = Camera.GetForwardVector(A0_0.Camera)\n"
        "    L17_17:SetZ(0)\n"
        "    L17_17:Normalise()\n"
        "    local L18_18 = L10_10:GetDot(L17_17)\n"
        "    if L5_5 < math.abs(L18_18) then\n"
        "    if 0 < L18_18 then\n"
        "    L8_8 = L8_8 + L17_17 * (L18_18 - L5_5)\n"
        "    L15_15 = L7_7\n"
        "    else\n"
        "    L8_8 = L8_8 + L17_17 * (L18_18 + L5_5)\n"
        "    L16_16 = L7_7\n"
        "    end\n"
        "    end\n"
        "    if L7_7 then\n"
        "    if not L15_15 and not L16_16 and not L14_14 then\n"
        "    A0_0.DesiredSlotOffset = A0_0.DesiredSlotOffset or L12_12\n"
        "    end\n"
        "    else\n"
        "    A0_0.DesiredSlotOffset = nil\n"
        "    end\n"
        "    A0_0:UpdateEdgeFrames(L14_14, L15_15, L16_16)\n");
    changed |= ReplaceAllLiteral(
        source,
        "    L6_6:Normalise()\n"
        "    A0_0.PreviousInputCentre = A1_1\n"
        "    (A1_1 - A0_0.PreBoundFocalPos):SetZ((0))\n"
        "    local L12_12 = L12_12(A0_0.Camera)\n"
        "    local L13_13 = L10_10:GetDot(L12_12)\n"
        "    local L14_14 = L6_6:GetDot(L12_12)\n",
        "    L6_6:Normalise()\n"
        "    A0_0.PreviousInputCentre = A1_1\n"
        "    local L8_8 = A0_0.PreBoundFocalPos\n"
        "    local L9_9 = A1_1\n"
        "    local L10_10 = L9_9 - L8_8\n"
        "    L10_10:SetZ((0))\n"
        "    local L11_11 = Camera.GetRightVector(A0_0.Camera)\n"
        "    local L12_12 = L10_10:GetDot(L11_11)\n"
        "    local L13_13 = L6_6:GetDot(L11_11)\n"
        "    local L14_14 = false\n"
        "    local L15_15 = false\n"
        "    local L16_16 = false\n");
    changed |= ReplaceAllLiteral(
        source,
        "    L6_6:Normalise()\n"
        "    A0_0.PreviousInputCentre = A1_1\n"
        "    local L12_12 = A1_1 - A0_0.PreBoundFocalPos\n"
        "    L12_12:SetZ((0))\n"
        "    local L12_12 = L12_12(A0_0.Camera)\n"
        "    local L13_13 = L10_10:GetDot(L12_12)\n"
        "    local L14_14 = L6_6:GetDot(L12_12)\n",
        "    L6_6:Normalise()\n"
        "    A0_0.PreviousInputCentre = A1_1\n"
        "    local L8_8 = A0_0.PreBoundFocalPos\n"
        "    local L9_9 = A1_1\n"
        "    local L10_10 = L9_9 - L8_8\n"
        "    L10_10:SetZ((0))\n"
        "    local L11_11 = Camera.GetRightVector(A0_0.Camera)\n"
        "    local L12_12 = L10_10:GetDot(L11_11)\n"
        "    local L13_13 = L6_6:GetDot(L11_11)\n"
        "    local L14_14 = false\n"
        "    local L15_15 = false\n"
        "    local L16_16 = false\n");
    changed |= ReplaceAllLiteral(
        source,
        "    if Camera.DrawDebug then\n"
        "    local r22 = 0\n"
        "    local r23 = 0\n"
        "    local r24 = 1\n"
        "    local r21 = (A1_1 + r21)\n"
        "    Debug.DrawLine(L8_8, r21, (0), (0), 0)\n"
        "    r22 = 0\n"
        "    r23 = 0\n"
        "    r24 = 2\n"
        "    r21 = (A1_1 + r21)\n"
        "    Debug.DrawLine(A1_1, r21, (0), (0), 0)\n"
        "    r20 = L8_8 + (L12_12 * L4_4)\n"
        "    r20 = (r20 + (r17 * L5_5))\n"
        "    r21 = L8_8 + (L12_12 * L4_4)\n"
        "    r21 = (r21 - (r17 * L5_5))\n"
        "    Debug.DrawLine(r20, r21, (0), (0), 255)\n"
        "    r20 = L8_8 - (L12_12 * L4_4)\n"
        "    r20 = (r20 + (r17 * L5_5))\n"
        "    r21 = L8_8 - (L12_12 * L4_4)\n"
        "    r21 = (r21 - (r17 * L5_5))\n"
        "    Debug.DrawLine(r20, r21, (0), (0), 255)\n"
        "    r20 = L8_8 + (L12_12 * L4_4)\n"
        "    r20 = (r20 + (r17 * L5_5))\n"
        "    r21 = L8_8 - (L12_12 * L4_4)\n"
        "    r21 = (r21 + (r17 * L5_5))\n"
        "    Debug.DrawLine(r20, r21, (0), (0), 255)\n"
        "    r20 = L8_8 + (L12_12 * L4_4)\n"
        "    r20 = (r20 - (r17 * L5_5))\n"
        "    r21 = L8_8 - (L12_12 * L4_4)\n"
        "    r21 = (r21 - (r17 * L5_5))\n"
        "    Debug.DrawLine(r20, r21, (0), (0), 255)\n"
        "    end\n",
        "    if Camera.DrawDebug then\n"
        "    Debug.DrawLine(L8_8, A1_1 + CVector3(0, 0, 1), 0, 0, 0)\n"
        "    Debug.DrawLine(A1_1, A1_1 + CVector3(0, 0, 2), 0, 0, 0)\n"
        "    Debug.DrawLine((L8_8 + L11_11 * L4_4) + L17_17 * L5_5, (L8_8 + L11_11 * L4_4) - L17_17 * L5_5, 0, 0, 255)\n"
        "    Debug.DrawLine((L8_8 - L11_11 * L4_4) + L17_17 * L5_5, (L8_8 - L11_11 * L4_4) - L17_17 * L5_5, 0, 0, 255)\n"
        "    Debug.DrawLine((L8_8 + L11_11 * L4_4) + L17_17 * L5_5, (L8_8 - L11_11 * L4_4) + L17_17 * L5_5, 0, 0, 255)\n"
        "    Debug.DrawLine((L8_8 + L11_11 * L4_4) - L17_17 * L5_5, (L8_8 - L11_11 * L4_4) - L17_17 * L5_5, 0, 0, 255)\n"
        "    end\n");
    changed |= ReplaceAllLiteral(
        source,
        "    if not A0_0.PreviousInputCentre then\n"
        "    A0_0.PreviousInputCentre = A1_1\n"
        "    else\n"
        "    A0_0.PreviousInputCentre = L6_6\n"
        "    end\n"
        "    if SACCamera.VelocityThresholdToPush / Timing.GetTickRate() < (A1_1 - A0_0.PreviousInputCentre):GetLength() then\n"
        "    local L7_7 = true\n"
        "    else\n"
        "    L7_7 = false\n"
        "    end\n"
        "    L6_6:Normalise()\n"
        "    A0_0.PreviousInputCentre = A1_1\n"
        "    local L12_12 = A1_1 - A0_0.PreBoundFocalPos\n"
        "    L12_12:SetZ((0))\n"
        "    local L12_12 = L12_12(A0_0.Camera)\n"
        "    local L13_13 = L10_10:GetDot(L12_12)\n"
        "    local L14_14 = L6_6:GetDot(L12_12)\n",
        "    A0_0.PreviousInputCentre = A0_0.PreviousInputCentre or A1_1\n"
        "    local L6_6 = A1_1 - A0_0.PreviousInputCentre\n"
        "    local L7_7 = L6_6:GetLength()\n"
        "    if SACCamera.VelocityThresholdToPush / Timing.GetTickRate() < L7_7 then\n"
        "    L7_7 = true\n"
        "    else\n"
        "    L7_7 = false\n"
        "    end\n"
        "    L6_6:Normalise()\n"
        "    A0_0.PreviousInputCentre = A1_1\n"
        "    local L8_8 = A0_0.PreBoundFocalPos\n"
        "    local L9_9 = A1_1\n"
        "    local L10_10 = L9_9 - L8_8\n"
        "    L10_10:SetZ((0))\n"
        "    local L11_11 = Camera.GetRightVector(A0_0.Camera)\n"
        "    local L12_12 = L10_10:GetDot(L11_11)\n"
        "    local L13_13 = L6_6:GetDot(L11_11)\n"
        "    local L14_14 = false\n"
        "    local L15_15 = false\n"
        "    local L16_16 = false\n");
    changed |= ReplaceAllLiteral(
        source,
        "    if SACCamera.IgnoreIndoors then\n"
        "    A0_0.IsInsideBuilding = SACCamera:SupportHysteresis(A0_0.IsInsideBuilding, false, \"BuildingCounter\", 5, 30)\n"
        "    else\n"
        "    A0_0.IsInsideBuilding = SACCamera:SupportHysteresis(A0_0.IsInsideBuilding, L4_4, \"BuildingCounter\", 5, 30)\n"
        "    end\n",
        "    if SACCamera.IgnoreIndoors then\n"
        "    L4_4 = false\n"
        "    end\n"
        "    A0_0.IsInsideBuilding = SACCamera:SupportHysteresis(A0_0.IsInsideBuilding, L4_4, \"BuildingCounter\", 5, 30)\n");

    changed |= ReplaceAllLiteral(
        source,
        "    if A0_0.MovementThisFrame then\n"
        "    end\n",
        "    local L5_5\n"
        "    if A0_0.MovementThisFrame then\n"
        "    L5_5 = not (0.05000000074505806 < A0_0.MovementThisFrame:GetLengthXY())\n"
        "    end\n");
    changed |= ReplaceAllLiteral(
        source,
        "    local r7 = (A0_0.Secondary or (not L3_3))\n",
        "    local r7 = (A0_0.Secondary or (not L5_5))\n");
    changed |= ReplaceAllLiteral(
        source,
        "    if not A0_0.Secondary then\n"
        "    else\n"
        "    L4_4 = false\n"
        "    end\n"
        "    A0_0.IsSprinting = SACCamera:SupportHysteresis(A0_0.IsSprinting, L4_4, \"SprintingCounter\", (15), (5))\n",
        "    local L6_6\n"
        "    if not A0_0.Secondary then\n"
        "    L6_6 = A0_0:CalculateIfPlayerIsSprinting()\n"
        "    else\n"
        "    L6_6 = false\n"
        "    end\n"
        "    A0_0.IsSprinting = SACCamera:SupportHysteresis(A0_0.IsSprinting, L6_6, \"SprintingCounter\", (15), (5))\n");
    changed |= ReplaceAllLiteral(
        source,
        "    if not A0_0.Secondary then\n"
        "    if Player.IsAvailable(A0_0.Player) then\n"
        "    if not Player.HasPlayerMode(A0_0.Player, EPlayerMode.PLAYER_MODE_FALLING) then\n"
        "    if Physics.IsAvailable(A0_0.Player) then\n"
        "    end\n"
        "    end\n"
        "    end\n"
        "    if L5_5(A0_0.Player) then\n"
        "    end\n"
        "    else\n"
        "    end\n"
        "    A0_0.IsFalling = SACCamera:SupportHysteresis(A0_0.IsFalling, false, \"FallingCounter\", (2), (7))\n",
        "    local L7_7\n"
        "    if not A0_0.Secondary then\n"
        "    if Player.IsAvailable(A0_0.Player) then\n"
        "    L7_7 = Player.HasPlayerMode(A0_0.Player, EPlayerMode.PLAYER_MODE_FALLING)\n"
        "    elseif Physics.IsAvailable(A0_0.Player) then\n"
        "    L7_7 = Physics.IsSliding(A0_0.Player)\n"
        "    else\n"
        "    L7_7 = false\n"
        "    end\n"
        "    else\n"
        "    L7_7 = false\n"
        "    end\n"
        "    A0_0.IsFalling = SACCamera:SupportHysteresis(A0_0.IsFalling, L7_7, \"FallingCounter\", (2), (7))\n");
    changed |= ReplaceAllLiteral(
        source,
        "    if not A0_0.Secondary then\n"
        "    if SpellManager.IsAvailable(A0_0.Player) then\n"
        "    end\n"
        "    else\n"
        "    end\n"
        "    A0_0.IsAssassin = r6\n",
        "    local r6\n"
        "    if not A0_0.Secondary then\n"
        "    if SpellManager.IsAvailable(A0_0.Player) then\n"
        "    r6 = SpellManager.IsEntityAssassinsRushActive(A0_0.Player)\n"
        "    else\n"
        "    r6 = false\n"
        "    end\n"
        "    else\n"
        "    r6 = false\n"
        "    end\n"
        "    A0_0.IsAssassin = r6\n");

    changed |= ReplaceAllLiteral(
        source,
        "    if CameraValues.ExtractCameraValuesFromRegionByName(A0_0).Valid then\n"
        "    return r1\n"
        "    end\n"
        "    return nil\n",
        "    local L1_1 = CameraValues.ExtractCameraValuesFromRegionByName(A0_0)\n"
        "    if L1_1.Valid then\n"
        "    return L1_1\n"
        "    end\n"
        "    return nil\n");
    changed |= ReplaceAllLiteral(
        source,
        "(A0_0.PreviousDropPos - r3).GetLengthXY:GetLengthXY((A0_0.PreviousDropPos - r3)):GetLengthXY()",
        "(A0_0.PreviousDropPos - r3):GetLengthXY()");

    changed |= ReplaceAllLiteral(
        source,
        "    if 5 < Physics.GetVelocity(A0_0.Player):GetLength() then\n"
        "    Camera.GetForwardVector(A0_0.Camera):SetZ((0))\n"
        "    L2_2:Normalise()\n"
        "    L1_1:SetZ((0))\n"
        "    L1_1:Normalise()\n"
        "    if 0.75 < L1_1:GetDot(Camera.GetForwardVector(A0_0.Camera)) then\n",
        "    local L1_1 = Physics.GetVelocity(A0_0.Player)\n"
        "    if 5 < L1_1:GetLength() then\n"
        "    local L2_2 = Camera.GetForwardVector(A0_0.Camera)\n"
        "    L2_2:SetZ((0))\n"
        "    L2_2:Normalise()\n"
        "    L1_1:SetZ((0))\n"
        "    L1_1:Normalise()\n"
        "    if 0.75 < L1_1:GetDot(L2_2) then\n");

    changed |= ReplaceAllLiteral(
        source,
        "    if A0_0:CalculateSimpleCentreBasedOnPrimaryAndSecondary() then\n"
        "    return r2\n"
        "    end\n",
        "    local r2 = A0_0:CalculateSimpleCentreBasedOnPrimaryAndSecondary()\n"
        "    if r2 then\n"
        "    return r2\n"
        "    end\n");
    changed |= ReplaceAllLiteral(
        source,
        "    local L1_1 = L1_1:GetPosition()\n",
        "    local L1_1 = A0_0.Primary:GetPosition()\n");
    changed |= ReplaceAllLiteral(
        source,
        "    local L2_2 = (L2_2.GetPosition:GetPosition(L2_2) - L1_1)\n"
        "    L2_2:Normalise()\n"
        "    L1_1 = (L1_1 + (L2_2 * math.min((L2_2:GetLengthXY() * (0.5)), (A0_0:GetMaxCageRadius() * A0_0:GetCageRadiusToViewRadiusFactor()))))\n",
        "    local L2_2 = (A0_0.Secondary:GetPosition() - L1_1)\n"
        "    local L3_3 = L2_2:GetLengthXY()\n"
        "    L2_2:Normalise()\n"
        "    L1_1 = (L1_1 + (L2_2 * math.min((L3_3 * (0.5)), (A0_0:GetMaxCageRadius() * A0_0:GetCageRadiusToViewRadiusFactor()))))\n");

    return changed;
}

bool ApplyFastCameraRawReadableRecoveryImpl(std::string* source) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    if (FoldFastStringAndCageCameraRawReadableRecovery(source)) {
        return true;
    }

    if (source->find("CameraManager.ManageLevelOverrides") !=
            std::string::npos &&
        source->find("CameraManager.GetLevelOverrideValues") !=
            std::string::npos) {
        *source = R"SCMLEVEL(  CameraManager.LevelOverrides = {}
  CameraManager.ManageLevelOverrides = function(A0_0, A1_1)
    A0_0:CatchTriggerMessages(A1_1)
    A0_0:ValidateExistingOverrides(A1_1)
  end
  CameraManager.CatchTriggerMessages = function(A0_0, A1_1)
    A0_0.LastMessage = A0_0.LastMessage or 0
    local L2_2
    local L3_3 = MessageEvents.GetAllMessages(EMessageEventType.MESSAGE_EVENT_TRIGGERED, A0_0.LastMessage)
    for r7, r8 in ipairs(L3_3) do
      if r8:GetEntitySentTo() == A1_1 then
        if CameraValues.IsAvailable(r8:GetEntitySentBy()) then
          A0_0:InsertCameraValues(r8:GetEntitySentBy())
          L2_2 = true
        end
      end
    end
    A0_0.LastMessage = MessageEvents.GetMostRecentMessageID()
    if L2_2 then
      A0_0:EvaluateTopCameraValue()
    end
  end
  CameraManager.InsertCameraValues = function(A0_0, A1_1)
    local L2_2 = GetIDFromEntity(A1_1)
    if CameraValues.IsAvailable(A1_1) then
      if CameraManager.LevelOverrides[L2_2] == nil then
        local L3_3 = CameraValues.GetCameraValues(A1_1)
        CameraManager.LevelOverrides[L2_2] = {
          Entity = A1_1,
          Time = Timing.GetProgramFrame(),
          Priority = L3_3.Priority
        }
      end
    end
  end
  CameraManager.EvaluateTopCameraValue = function(A0_0)
    local L1_1
    for r5, r6 in pairs(CameraManager.LevelOverrides) do
      if not L1_1 then
        L1_1 = r6
      end
      if r6.Priority > L1_1.Priority then
        L1_1 = r6
      elseif r6.Priority == L1_1.Priority then
        if r6.Time > L1_1.Time then
          L1_1 = r6
        end
      end
    end
    A0_0.CurrentLevelOverrideID = L1_1 and GetIDFromEntity(L1_1.Entity) or nil
    A0_0.CurrentLevelOverrideValues = L1_1 and CameraValues.GetCameraValues(L1_1.Entity) or nil
  end
  CameraManager.ValidateExistingOverrides = function(A0_0, A1_1)
    local L2_2
    for r6, r7 in pairs(CameraManager.LevelOverrides) do
      if not r7.Entity:IsAlive() or Trigger.IsAvailable(r7.Entity) and not Trigger.IsSpecificEntityInsideTriggerVolume(r7.Entity, A1_1) then
        CameraManager.LevelOverrides[r6] = nil
        L2_2 = true
      end
    end
    if L2_2 then
      A0_0:EvaluateTopCameraValue()
    end
  end
  CameraManager.GetLevelOverrideID = function(A0_0)
    return A0_0.CurrentLevelOverrideID
  end
  CameraManager.GetLevelOverrideValues = function(A0_0)
    return A0_0.CurrentLevelOverrideValues
  end)SCMLEVEL";
        return true;
    }

    if (source->find("CameraBase = ") != std::string::npos &&
        source->find("function CameraBase.FadeUpEntitiesIfNeeded") !=
            std::string::npos &&
        source->find("function CameraBase.PureFrameUpdate") != std::string::npos) {
        *source = R"SCAMBASE(  CameraBase = {}
  CameraBase.RumbleHandle = 0
  CameraBase.HandyCamHandle = 0
  CameraBase.TreeHeightThreshold = 3.5
  function CameraBase.Create(A0_0, A1_1)
    local L2_2 = {DebugName = A1_1}
    setmetatable(L2_2, A0_0)
    A0_0.__index = A0_0
    return L2_2
  end
  function CameraBase.Init(A0_0)
    A0_0.PreviousCamera = nil
    A0_0.Camera = nil
    A0_0.CurrentFOV = nil
    A0_0.DesiredFOV = CameraValues.FOV.DEFAULT
    A0_0.CurrentPosition = nil
    A0_0.DesiredPosition = CVector3((0), (0), (0))
    A0_0.DesiredTarget = CVector3((0), (0), (0))
    A0_0.DesiredAngles = CVector3((0), (0), (0))
    A0_0.UsingTargetPoint = true
    A0_0.FOVInterpolationRate = 1
    A0_0.TranslationDamp = 0
    A0_0.CurrentRoll = nil
    A0_0.DesiredRoll = 0
    A0_0.RollInterpolationRate = 1
    A0_0.CurrentBlur = nil
    A0_0.DesiredBlur = 0
    A0_0.BlurInterpolationRate = 1
  end
  function CameraBase.SetTranslationDamping(A0_0, A1_1)
    A0_0.TranslationDamp = A1_1
  end
  function CameraBase.SetDesiredPosition(A0_0, A1_1, A2_2)
    A0_0.DesiredPosition = A1_1
    A0_0.CurrentPosition = A2_2 and A1_1 or A0_0.CurrentPosition
  end
  function CameraBase.SetDesiredTarget(A0_0, A1_1)
    A0_0.DesiredTarget = A1_1
    A0_0.UsingTargetPoint = true
    A0_0.UsingDirection = false
  end
  function CameraBase.SetDesiredDirection(A0_0, A1_1)
    A0_0.DesiredDirection = A1_1
    A0_0.UsingTargetPoint = false
    A0_0.UsingDirection = true
  end
  function CameraBase.SetDesiredAngles(A0_0, A1_1)
    A0_0.DesiredAngles = A1_1
    A0_0.UsingTargetPoint = false
    A0_0.UsingDirection = false
  end
  function CameraBase.SetCutThisFrame(A0_0)
    A0_0.CutThisFrame = true
  end
  function CameraBase.SetDesiredFOV(A0_0, A1_1, A2_2)
    A0_0.DesiredFOV = A1_1
    A0_0.FOVInterpolationRate = (A2_2 or A0_0.FOVInterpolationRate)
  end
  function CameraBase.SetDesiredDOF(A0_0, A1_1)
    A0_0.DOFData = A1_1
  end
  function CameraBase.SetDesiredRoll(A0_0, A1_1, A2_2)
    A0_0.DesiredRoll = A1_1
    A0_0.RollInterpolationRate = (A2_2 or A0_0.RollInterpolationRate)
  end
  function CameraBase.SetDesiredBlur(A0_0, A1_1, A2_2)
    A0_0.DesiredBlur = A1_1
    A0_0.BlurInterpolationRate = (A2_2 or A0_0.BlurInterpolationRate)
  end
  function CameraBase.SetPulseBlur(A0_0, A1_1, A2_2)
    A0_0.PulseBlurTime = Timing.GetProgramFrame()
    A0_0.PulseBlur = A1_1
    A0_0.PulseBlurDuration = (A2_2 * Timing.GetTickRate())
  end
  function CameraBase.GetPulseBlur(A0_0)
    if not A0_0.PulseBlurTime then
    return nil
    end
    local L1_1 = Timing.GetProgramFrame()
    if L1_1 - A0_0.PulseBlurTime < A0_0.PulseBlurDuration then
    return A0_0.PulseBlur
    else
    A0_0.PulseBlurTime = nil
    return nil
    end
  end
  function CameraBase.SetFOV(A0_0, A1_1)
    A0_0.DesiredFOV = A1_1
    if not CameraManager.DebugFOV then
    A0_0.CurrentFOV = A1_1
    end
  end
  function CameraBase.SetRoll(A0_0, A1_1)
    A0_0.CurrentRoll = A1_1
    A0_0.DesiredRoll = A1_1
  end
  function CameraBase.SetBlur(A0_0, A1_1)
    A0_0.CurrentBlur = A1_1
    A0_0.DesiredBlur = A1_1
  end
  function CameraBase.Activate(A0_0, A1_1, A2_2, A3_3)
    A0_0.Camera = A2_2
    A0_0.PreviousCamera = A3_3
    A0_0.TranslationDamp = 0
    A0_0:SetDesiredPosition(A0_0.Camera:GetPosition(), true)
    A0_0:SetDesiredAngles(Camera.GetAngles(A0_0.Camera))
    A0_0:SetDesiredFOV(Camera.GetFOV(A0_0.Camera))
    A0_0:SetDesiredRoll((0))
    A0_0:SetDesiredBlur((0.12999999523162842))
    A0_0:SetDesiredDOF(CameraFunctions.AssembleDOFData((5), (1), (1), (10), (0)))
    A0_0.CutThisFrame = false
    A0_0:SetOverriddenData(CameraManager.GetActiveOverriddenData(A1_1).OverrideData)
    A0_0.BlendFrameDuration = CameraBase.BlendOut or A0_0:GetOverriddenData() and A0_0:GetOverriddenData().BlendInSeconds and A0_0:GetOverriddenData().BlendInSeconds * Timing.GetTickRate() or A0_0.BlendFrameDuration or 0
    if A0_0.BlendFrameDuration == 0 then
    A0_0:CutFromPreviousCamera()
    else
    A0_0:InterpolateFromPreviousCamera(A0_0.BlendFrameDuration)
    end
  end
  function CameraBase.SetOverriddenData(A0_0, A1_1)
    A0_0.OverriddenData = A1_1
  end
  function CameraBase.GetOverriddenData(A0_0)
    if A0_0.OverriddenData == nil then
    A0_0.OverriddenData = {}
    end
    return A0_0.OverriddenData
  end
  function CameraBase.PrepareOverriddenDataUsingValues(A0_0, A1_1)
    A0_0:GetOverriddenData()
    for r5, r6 in pairs(A1_1) do
    A0_0.OverriddenData[r5] = A0_0.OverriddenData[r5] or r6
    end
  end
  function CameraBase.Deactivate(A0_0)
    CameraBase.BlendOut = A0_0:GetOverriddenData() and A0_0:GetOverriddenData().BlendOutSeconds and A0_0:GetOverriddenData().BlendOutSeconds * Timing.GetTickRate() or A0_0.BlendOutFrameDuration
    A0_0.Camera = nil
    A0_0.PreviousCamera = nil
    A0_0.OverriddenData = nil
  end
  function CameraBase.CleanUp(A0_0)
    A0_0.Camera = nil
    A0_0.PreviousCamera = nil
    A0_0.OverriddenData = nil
  end
  function CameraBase.InterpolateFromPreviousCamera(A0_0, A1_1)
    if A0_0.PreviousCamera then
    if A0_0.PreviousCamera:IsAlive() then
    local L2_2 = Camera.GetAngles(A0_0.PreviousCamera)
    Camera.SetAnglesCut(A0_0.Camera, L2_2:GetX(), L2_2:GetY(), L2_2:GetZ())
    Camera.MoveToCut(A0_0.Camera, A0_0.PreviousCamera:GetPosition())
    Camera.SetFOVCut(A0_0.Camera, Camera.GetFOV(A0_0.PreviousCamera))
    Camera.SetRollCut(A0_0.Camera, Camera.GetRoll(A0_0.PreviousCamera))
    Camera.SetBlurCut(A0_0.Camera, Camera.GetBlur(A0_0.PreviousCamera))
    CameraFunctions.SetDOFDataCut(A0_0.Camera, CameraFunctions.GrabDOFData(A0_0.PreviousCamera))
    end
    end
    Camera.InterpolateFor(A0_0.Camera, A1_1)
  end
  function CameraBase.CutFromPreviousCamera(A0_0)
    A0_0:SetCutThisFrame()
    Camera.InterpolateFor(A0_0.Camera, (0))
  end
  function CameraBase.FrameUpdate(A0_0)
    A0_0.CurrentPosition = CameraFunctions.GetRollingAverage(A0_0.CurrentPosition, A0_0.DesiredPosition, A0_0.TranslationDamp)
    Camera.MoveTo(A0_0.Camera, A0_0.CurrentPosition)
    A0_0.CurrentRoll = A0_0.CurrentRoll or Camera.GetRoll(A0_0.Camera)
    A0_0.CurrentRoll = CameraFunctions.GetRollingAverage(A0_0.CurrentRoll, A0_0.DesiredRoll, A0_0.RollInterpolationRate)
    Camera.SetRoll(A0_0.Camera, A0_0.CurrentRoll)
    A0_0.CurrentBlur = A0_0.CurrentBlur or Camera.GetBlur(A0_0.Camera)
    A0_0.CurrentBlur = CameraFunctions.GetRollingAverage(A0_0.CurrentBlur, A0_0.DesiredBlur, A0_0.BlurInterpolationRate)
    Camera.SetBlur(A0_0.Camera, A0_0:GetPulseBlur() or A0_0.CurrentBlur)
    if A0_0.UsingDirection then
    Camera.SetDirection(A0_0.Camera, A0_0.DesiredDirection)
    else
    if A0_0.UsingTargetPoint then
    Camera.SetDirection(A0_0.Camera, (A0_0.DesiredTarget - A0_0.CurrentPosition))
    else
    Camera.SetAngles(A0_0.Camera, A0_0.DesiredAngles:GetX(), A0_0.DesiredAngles:GetY(), A0_0.DesiredAngles:GetZ())
    end
    end
    if not CameraManager.DebugFOV then
    A0_0.CurrentFOV = A0_0.CurrentFOV or Camera.GetFOV(A0_0.Camera)
    A0_0.CurrentFOV = CameraFunctions.GetRollingAverage(A0_0.CurrentFOV, A0_0.DesiredFOV, A0_0.FOVInterpolationRate)
    Camera.SetFOV(A0_0.Camera, A0_0.CurrentFOV)
    end
    CameraFunctions.SetDOFData(A0_0.Camera, A0_0.DOFData)
    Camera.AddCameraShake(A0_0.Camera, CameraBase.RumbleHandle)
    Camera.SetCutThisFrame(A0_0.Camera, A0_0.CutThisFrame)
    A0_0.CutThisFrame = false
    A0_0:FadeUpEntitiesIfNeeded()
    A0_0:UpdateTreeFading()
    CameraManager.CameraPosition = A0_0.CurrentPosition
    CameraManager.PrimedFromLoad = nil
  end
  function CameraBase.UpdateTreeFading(A0_0)
    local L1_1 = Camera.GetGroundPositionAtPosition(A0_0.Camera, A0_0.Camera:GetPosition())
    local L2_2 = A0_0.Camera:GetPosition():GetZ() - L1_1:GetZ()
    local L3_3 = A0_0.Camera:GetPosition():GetZ()
    L3_3 = L3_3 - Debug.GetHero():GetPosition():GetZ()
    if CameraBase.TreeHeightThreshold < L2_2 or CameraBase.TreeHeightThreshold < L3_3 then
    Camera.EnableTreeCanopyFading(true)
    else
    Camera.EnableTreeCanopyFading(false)
    end
  end
  function CameraBase.PureFrameUpdate(A0_0)
    Camera.MoveTo(A0_0.Camera, A0_0.DesiredPosition)
    A0_0.CurrentPosition = A0_0.DesiredPosition
    Camera.SetRoll(A0_0.Camera, A0_0.DesiredRoll)
    Camera.SetBlur(A0_0.Camera, A0_0:GetPulseBlur() or A0_0.DesiredBlur)
    if A0_0.UsingDirection then
    Camera.SetDirection(A0_0.Camera, A0_0.DesiredDirection)
    else
    if A0_0.UsingTargetPoint then
    Camera.SetDirection(A0_0.Camera, (A0_0.DesiredTarget - A0_0.CurrentPosition))
    else
    Camera.SetAngles(A0_0.Camera, A0_0.DesiredAngles:GetX(), A0_0.DesiredAngles:GetY(), A0_0.DesiredAngles:GetZ())
    end
    end
    if not CameraManager.DebugFOV then
    Camera.SetFOV(A0_0.Camera, A0_0.DesiredFOV)
    end
    CameraFunctions.SetDOFData(A0_0.Camera, A0_0.DOFData)
    Camera.AddCameraShake(A0_0.Camera, CameraBase.RumbleHandle)
    Camera.SetCutThisFrame(A0_0.Camera, A0_0.CutThisFrame)
    A0_0.CutThisFrame = false
    A0_0:FadeUpEntitiesIfNeeded()
    A0_0:UpdateTreeFading()
    CameraManager.CameraPosition = A0_0.CurrentPosition
    CameraManager.PrimedFromLoad = nil
  end
  CameraBase.UseFastFading = false
  function CameraBase.FadeAllEntitiesBasedOnPositionIfNeeded(A0_0, A1_1, A2_2, A3_3)
    if CameraBase.UseFastFading then
    CameraManager.NGDSetFadedCameraObjects(A0_0.CurrentPosition, (A1_1 * (2)), A2_2, A3_3)
    else
    local L4_4 = CameraManager.GetFadedCameraObjects(A0_0.CurrentPosition, A1_1 * 2)
    local L5_5 = Debug.GetHero()
    local L6_6 = GetPlayerHenchman()
    for r10, r11 in ipairs(L4_4) do
    if (A3_3 or r11 ~= L5_5 and r11 ~= L6_6) and r11 ~= A2_2 then
    if GraphicAppearance.IsAvailable(r11) then
    if GraphicAppearance.GetFadedByCamera(r11) then
    local L12_12 = GraphicAppearance.GetFadedRange(r11) or 0
    local L13_13 = Creature.IsAvailable(r11) and 0 or 0.05000000074505806
    A0_0:FadeEntityBasedOnPositionIfNeeded(r11, A1_1 + L12_12, L13_13)
    end
    end
    end
    end
    end
  end
  function CameraBase.FadeEntityBasedOnPositionIfNeeded(A0_0, A1_1, A2_2, A3_3, A4_4)
    CameraBase.FadedEntities = CameraBase.FadedEntities or {}
    A2_2 = A2_2 or 0.5
    if Creature.IsAvailable(A1_1) then
    A2_2 = A2_2 * 0.5
    end
    local L5_5 = A4_4 or 0
    local L6_6 = GetIDFromEntity(A1_1)
    if CameraBase.FadedEntities[L6_6] == nil then
    CameraBase.FadedEntities[L6_6] = {Entity = A1_1}
    end
    local L7_7 = A1_1:GetPosition()
    local L8_8 = A0_0.Camera:GetPosition()
    local L9_9 = (L7_7 - L8_8):GetLengthXY()
    local L10_10 = (L9_9 - A2_2) / A2_2
    L10_10 = math.min(L10_10, 1)
    L10_10 = math.max(L10_10, A3_3 or 0.5)
    CameraBase.FadedEntities[L6_6].FadeAmount = L10_10
    CameraBase.FadedEntities[L6_6].FrameUpdated = Timing.GetProgramFrame() + L5_5
    GraphicAppearance.SetCameraAlpha(A1_1, L10_10)
  end
  function CameraBase.FadeUpEntitiesIfNeeded(A0_0, A1_1)
    if CameraBase.UseFastFading then
    CameraManager.NGDFadeUpEntitiesIfNeeded()
    end
    CameraBase.FadedEntities = CameraBase.FadedEntities or {}
    local L2_2 = Timing.GetProgramFrame()
    for r6, r7 in pairs(CameraBase.FadedEntities) do
    if not r7.Entity:IsAlive() then
    CameraBase.FadedEntities[r6] = nil
    else
    if r7.FrameUpdated < L2_2 then
    r7.FadeAmount = r7.FadeAmount + 0.05000000074505806
    local L8_8 = math.min(r7.FadeAmount, 1)
    GraphicAppearance.SetCameraAlpha(r7.Entity, L8_8)
    if 1 < r7.FadeAmount then
    CameraBase.FadedEntities[r6] = nil
    end
    end
    end
    end
  end
  function CameraBase.OnLevelLoad(A0_0)
  end)SCAMBASE";
        return true;
    }

    if (ReplaceAllLiteral(
            source,
            "TrackingCamera.Target.IsAlive:IsAlive(TrackingCamera.Target)",
            "TrackingCamera.Target:IsAlive()")) {
        return true;
    }

    if (source->find("CutSceneCamera.DebugName = \"CutSceneCamera\"") !=
        std::string::npos) {
        bool changed = false;
        changed |= ReplaceAllLiteral(
            source,
            "  function CutSceneCamera.Create(A0_0)\n"
            "    setmetatable(CutSceneCamera, A0_0)\n"
            "    A0_0.__index = A0_0\n"
            "    return L1_1\n"
            "  end\n",
            "  function CutSceneCamera.Create(A0_0)\n"
            "    local L1_1 = {}\n"
            "    setmetatable(L1_1, A0_0)\n"
            "    A0_0.__index = A0_0\n"
            "    return L1_1\n"
            "  end\n");
        changed |= ReplaceAllLiteral(
            source,
            "    A0_0.AngleXY = Camera.GetAngles(A3_3):GetX()\n"
            "    A0_0.AngleYZ = r4:GetY()\n"
            "    A0_0.AngleXZ = r4:GetZ()\n",
            "    local L4_4 = Camera.GetAngles(A3_3)\n"
            "    A0_0.AngleXY = L4_4:GetX()\n"
            "    A0_0.AngleYZ = L4_4:GetY()\n"
            "    A0_0.AngleXZ = L4_4:GetZ()\n");
        if (changed) {
            return true;
        }
    }

    if (source->find("FreezeCamera = CameraBase:Create(\"FreezeCamera\")") !=
        std::string::npos) {
        bool changed = false;
        changed |= ReplaceAllLiteral(
            source,
            "    Camera.SetAnglesCut(A2_2, Camera.GetAngles(A3_3):GetX(), Camera.GetAngles(A3_3):GetY(), Camera.GetAngles(A3_3):GetZ())\n"
            "    Camera.MoveToCut(A2_2, A3_3:GetPosition())\n",
            "    local L4_4 = Camera.GetAngles(A3_3)\n"
            "    Camera.SetAnglesCut(A2_2, L4_4:GetX(), L4_4:GetY(), L4_4:GetZ())\n"
            "    Camera.MoveToCut(A2_2, A3_3:GetPosition())\n");
        changed |= ReplaceAllLiteral(
            source,
            "    if 0 < Camera.GetDebugMoveToCut(A0_0.Camera):GetSquaredLength() then\n"
            "    Camera.MoveToCut(A0_0.Camera, L1_1)\n"
            "    Camera.ClearDebugMoveToCut(A0_0.Camera)\n"
            "    end\n"
            "    if 0 < Camera.GetDebugFocusToCut(A0_0.Camera):GetSquaredLength() then\n"
            "    Camera.SetTargetCut(A0_0.Camera, L1_1)\n"
            "    Camera.ClearDebugFocusToCut(A0_0.Camera)\n",
            "    local L1_1 = Camera.GetDebugMoveToCut(A0_0.Camera)\n"
            "    if L1_1:GetSquaredLength() > 0 then\n"
            "    Camera.MoveToCut(A0_0.Camera, L1_1)\n"
            "    Camera.ClearDebugMoveToCut(A0_0.Camera)\n"
            "    end\n"
            "    local L2_2 = Camera.GetDebugFocusToCut(A0_0.Camera)\n"
            "    if L2_2:GetSquaredLength() > 0 then\n"
            "    Camera.SetTargetCut(A0_0.Camera, L1_1)\n"
            "    Camera.ClearDebugFocusToCut(A0_0.Camera)\n");
        if (changed) {
            return true;
        }
    }

    if (source->find("MagicSACCamera = SACCamera:Create(\"MagicSACCamera\")") !=
        std::string::npos) {
        bool changed = false;
        changed |= ReplaceAllLiteral(
            source,
            "  MagicSACCamera.InterestingEntities = MagicSACCamera\n",
            "  MagicSACCamera.InterestingEntities = {}\n");
        changed |= ReplaceAllLiteral(
            source,
            "    MagicSACCamera.InterestingEntities = CameraBase\n",
            "    MagicSACCamera.InterestingEntities = {}\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function MagicSACCamera.FrameUpdate(A0_0)",
            "  function MagicSACCamera.FrameUpdate(A0_0)\n"
            "    local L1_1 = PlayerSpellManager.IsTargetedDirCastMode(A0_0.Player)\n"
            "    if A0_0.IsInTargetingMode ~= L1_1 then\n"
            "    A0_0.IsInTargetingMode = L1_1\n"
            "    if not A0_0.IsInTargetingMode then\n"
            "    SACCamera.ClearValidityOfWeightedList(MagicSACCamera.InterestingEntities)\n"
            "    end\n"
            "    A0_0.StartInterpolate = true\n"
            "    A0_0.SnapThisFrame = true\n"
            "    end\n"
            "    if A0_0.IsInTargetingMode then\n"
            "    local L2_2 = PlayerSpellManager.GetCurrentTargetEntity(A0_0.Player)\n"
            "    if L2_2:IsAlive() then\n"
            "    SACCamera.AddInterestingEntityToWeightedList(MagicSACCamera.InterestingEntities, L2_2, MagicSACCamera.MyInterestingEnemyDistance)\n"
            "    end\n"
            "    else\n"
            "    local L2_2 = PlayerSpellManager.GetCurrentChargedSlotIdx(A0_0.Player)\n"
            "    MagicSACCamera.ZoomLevelAreaScale = 1 + L2_2 * MagicSACCamera.ZoomLevelAreaScaleIncr\n"
            "    end\n"
            "    SACCamera.FrameUpdate(A0_0)\n"
            "  end\n");
        if (changed) {
            return true;
        }
    }

    if (source->find(
            "InteractiveCutSceneCamera = CameraBase:Create(\"InteractiveCutSceneCamera\")") !=
        std::string::npos) {
        bool changed = false;
        changed |= ReplaceAllLiteral(
            source,
            "  InteractiveCutSceneCamera.NonOverriddenValues = InteractiveCutSceneCamera\n",
            "  InteractiveCutSceneCamera.NonOverriddenValues = {}\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function InteractiveCutSceneCamera.IsValid(A0_0)",
            "  function InteractiveCutSceneCamera.IsValid(A0_0)\n"
            "    return CameraFunctions.IsPointOfInterestLocked(A0_0) and InteractiveCutSceneCamera.Primed\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function InteractiveCutSceneCamera.Activate(A0_0, A1_1, A2_2, A3_3)",
            "  function InteractiveCutSceneCamera.Activate(A0_0, A1_1, A2_2, A3_3)\n"
            "    A0_0.BlendFrameDuration = A0_0.Values and A0_0.Values.BlendInFrames or 0\n"
            "    CameraBase.Activate(A0_0, A1_1, A2_2, A3_3)\n"
            "    A0_0:SetFOV(A0_0:GetFOV(0))\n"
            "    A0_0:PrepareOverriddenDataUsingValues(InteractiveCutSceneCamera.NonOverriddenValues)\n"
            "    A0_0.Player = A1_1\n"
            "    A0_0:GetCameraParamsForTarget()\n"
            "    A0_0:SetTranslationDamping(InteractiveCutSceneCamera.CameraMoveDamping)\n"
            "    local L4_4 = A0_0:GetOverriddenData().XOffset or 0\n"
            "    local L5_5 = A0_0:GetOverriddenData().YOffset or 0\n"
            "    local L6_6, L7_7 = A0_0:GetPosAndFocusBasedOnPlayerAndTarget(A0_0.Player, A0_0.Target, L4_4, L5_5, 0)\n"
            "    A0_0.SafePlayerPosition = L6_6\n"
            "    A0_0.SafeTargetPosition = L7_7\n"
            "    A0_0:SetDesiredPosition(L6_6, true)\n"
            "    GraphicAppearance.SetAsDrawable(A0_0.Player, false)\n"
            "    A0_0.StartTime = Timing.GetWorldFrame()\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function InteractiveCutSceneCamera.GetCameraParamsForTarget(A0_0)",
            "  function InteractiveCutSceneCamera.GetCameraParamsForTarget(A0_0)\n"
            "    local L1_1 = Targeting.GetPointOfInterestData(A0_0.Player)\n"
            "    A0_0:SetDesiredFOV(A0_0:GetOverriddenData().FOV or L1_1.FOV, 8)\n"
            "    A0_0.Target = A0_0:GetOverriddenData().FocusEntity or L1_1.Target\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function InteractiveCutSceneCamera.GetPosAndFocusBasedOnPlayerAndTarget(A0_0, A1_1, A2_2, A3_3, A4_4, A5_5)",
            "  function InteractiveCutSceneCamera.GetPosAndFocusBasedOnPlayerAndTarget(A0_0, A1_1, A2_2, A3_3, A4_4, A5_5)\n"
            "    local L6_6 = CameraFunctions.GetObjectPosition(A1_1)\n"
            "    local L7_7 = CameraFunctions.GetObjectPosition(A2_2)\n"
            "    if L6_6 == nil or L7_7 == nil then\n"
            "      return nil, nil\n"
            "    end\n"
            "    local L8_8 = L6_6 - L7_7\n"
            "    local L9_9 = L8_8:GetLength()\n"
            "    if A0_0:GetOverriddenData().DDistance then\n"
            "      L9_9 = L9_9 + A0_0:GetOverriddenData().DDistance * A5_5\n"
            "    end\n"
            "    if A0_0:GetOverriddenData().MinDistanceFromTarget then\n"
            "      L9_9 = math.max(L9_9, A0_0:GetOverriddenData().MinDistanceFromTarget)\n"
            "    end\n"
            "    if A0_0:GetOverriddenData().MaxDistanceFromTarget then\n"
            "      L9_9 = math.min(L9_9, A0_0:GetOverriddenData().MaxDistanceFromTarget)\n"
            "    end\n"
            "    L8_8:Normalise()\n"
            "    L6_6 = L7_7 + L8_8 * L9_9\n"
            "    A3_3 = A3_3 or 0\n"
            "    A4_4 = A4_4 or 0\n"
            "    L7_7 = L7_7 + InteractiveCutSceneCamera.GetOffsetPosByXAndYOfPosition(L7_7, L8_8, A3_3, A4_4 + InteractiveCutSceneCamera.StandardOffsetY) * L9_9 * 0.20000000298023224\n"
            "    return L6_6, L7_7\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function InteractiveCutSceneCamera.GetOffsetPosByXAndYOfPosition(A0_0, A1_1, A2_2, A3_3)",
            "  function InteractiveCutSceneCamera.GetOffsetPosByXAndYOfPosition(A0_0, A1_1, A2_2, A3_3)\n"
            "    local r4, r5, r6 = CameraFunctions.GetXYZFromDirection(A1_1)\n"
            "    local r7 = r4 * -A2_2\n"
            "    local r8 = r5 * A3_3\n"
            "    local r9 = r7 + r8\n"
            "    return r9\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function InteractiveCutSceneCamera.GetFOV(A0_0, A1_1)",
            "  function InteractiveCutSceneCamera.GetFOV(A0_0, A1_1)\n"
            "    local L2_2 = 45\n"
            "    if A0_0:GetOverriddenData().FOV then\n"
            "    L2_2 = A0_0:GetOverriddenData().FOV\n"
            "    end\n"
            "    if A0_0:GetOverriddenData().DFOV then\n"
            "    L2_2 = L2_2 + A1_1 * A0_0:GetOverriddenData().DFOV\n"
            "    end\n"
            "    L2_2 = math.max(L2_2, InteractiveCutSceneCamera.Limits.MinFOV)\n"
            "    L2_2 = math.min(L2_2, InteractiveCutSceneCamera.Limits.MaxFOV)\n"
            "    return L2_2\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function InteractiveCutSceneCamera.FrameUpdate(A0_0)",
            "  function InteractiveCutSceneCamera.FrameUpdate(A0_0)\n"
            "    local L1_1 = Timing.GetSecondsSince(A0_0.StartTime)\n"
            "    local L2_2 = A0_0.ControlX or 0\n"
            "    do\n"
            "      local L3_3 = A0_0:GetOverriddenData().XOffset or 0\n"
            "      L2_2 = L2_2 + L3_3\n"
            "    end\n"
            "    local L3_3 = A0_0.ControlY or 0\n"
            "    do\n"
            "      local L4_4 = A0_0:GetOverriddenData().YOffset or 0\n"
            "      L3_3 = L3_3 + L4_4\n"
            "    end\n"
            "    local L4_4, L5_5 = A0_0:GetPosAndFocusBasedOnPlayerAndTarget(A0_0.Player, A0_0.Target, L2_2, L3_3, L1_1)\n"
            "    A0_0.SafePlayerPosition = L4_4 or A0_0.SafePlayerPosition\n"
            "    A0_0.SafeTargetPosition = L5_5 or A0_0.SafeTargetPosition\n"
            "    if A0_0.CurrentFocus == nil then\n"
            "    A0_0.CurrentFocus = A0_0.SafeTargetPosition\n"
            "    else\n"
            "    A0_0.CurrentFocus = CameraFunctions.GetRollingAverage(A0_0.CurrentFocus, A0_0.SafeTargetPosition, InteractiveCutSceneCamera.CameraMoveDamping)\n"
            "    end\n"
            "    A0_0:SetDesiredPosition(A0_0.SafePlayerPosition)\n"
            "    A0_0:SetDesiredTarget(A0_0.CurrentFocus)\n"
            "    A0_0:SetDesiredFOV(A0_0:GetFOV(L1_1), (40))\n"
            "    A0_0:SetDesiredFOV(A0_0:GetFOV(L1_1), (40))\n"
            "    CameraBase.FrameUpdate(A0_0)\n"
            "  end\n");
        if (changed) {
            return true;
        }
    }

    if (source->find("RangedCombatCamera = CameraBase:Create(\"RangedCombatCamera\")") !=
        std::string::npos) {
        bool changed = false;
        changed |= ReplaceFunctionByHeader(
            source,
            "function RangedCombatCamera.Activate(A0_0, A1_1, A2_2, A3_3)",
            "  function RangedCombatCamera.Activate(A0_0, A1_1, A2_2, A3_3)\n"
            "    assert(A2_2)\n"
            "    assert(A2_2:IsAlive())\n"
            "    assert(A1_1 and A1_1:IsAlive())\n"
            "    A0_0.Player = A1_1\n"
            "    local L4_4 = A2_2:GetPosition()\n"
            "    local L5_5 = Physics.GetFacingVector(A2_2)\n"
            "    local L6_6 = A0_0.ExtraBlendFrames * ((1 - L5_5:GetDot(A0_0:GetDesiredDirection(L4_4))) * 0.5)\n"
            "    A0_0.BlendFrameDuration = A0_0.BaseBlendFrames + L6_6\n"
            "    A0_0.BlendOutFrameDuration = 1 * Timing.GetTickRate()\n"
            "    if A3_3 then\n"
            "    if A3_3:IsAlive() then\n"
            "    Camera.SetBlurCut(A3_3, RangedCombatCamera.StartBlurAmount)\n"
            "    end\n"
            "    end\n"
            "    CameraBase.Activate(A0_0, A1_1, A2_2, A3_3)\n"
            "    A0_0:SetDesiredPosition(L4_4)\n"
            "    A0_0:SetDesiredTarget(L4_4 + L5_5 * A0_0.LookAtRadius)\n"
            "    A0_0.CurrentZoom = CameraValues.FOV.RANGED\n"
            "    A0_0:SetFOV(A0_0.CurrentZoom)\n"
            "    A0_0:SetTranslationDamping(0)\n"
            "    Targeting.SetInTargetingCamera(A0_0.Player, true)\n"
            "    local L7_7 = Carrying.GetEntityInSlot(A1_1, DummyObjects.HAND_RIGHT)\n"
            "    A0_0.IsPistol = Weapon.IsPistol(L7_7)\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function RangedCombatCamera.GetDesiredDirection(A0_0, A1_1)",
            "  function RangedCombatCamera.GetDesiredDirection(A0_0, A1_1)\n"
            "    local L2_2 = Targeting.GetCameraSafeTargetedPosition(A0_0.Player)\n"
            "    local L3_3 = A1_1 or A0_0.CurrentPosition\n"
            "    local L4_4 = L2_2 - L3_3\n"
            "    local L5_5 = A0_0:GetOffsetDegrees()\n"
            "    if A0_0.IsPistol then\n"
            "    L5_5 = -L5_5\n"
            "    end\n"
            "    L4_4:RotateAroundDirectionByAngle(CVector3((0), (0), (1)), L5_5)\n"
            "    L4_4:Normalise()\n"
            "    A0_0.CurrentDesired = A0_0.CurrentDesired or L4_4\n"
            "    A0_0.CurrentDesired = CameraFunctions.GetRollingAverage(A0_0.CurrentDesired, L4_4, RangedCombatCamera.DesiredDirectionDamping)\n"
            "    return A0_0.CurrentDesired\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function RangedCombatCamera.UpdateCurrentDirection(A0_0)",
            "  function RangedCombatCamera.UpdateCurrentDirection(A0_0)\n"
            "    if not A0_0.CurrentDirection then\n"
            "    A0_0.CurrentDirection = A0_0:GetDesiredDirection()\n"
            "    end\n"
            "    local L1_1 = A0_0:GetDesiredDirection()\n"
            "    local L2_2 = A0_0.CurrentDirection:GetDot(L1_1)\n"
            "    local L3_3 = A0_0:GetTightness()\n"
            "    local L4_4 = math.pi / 180\n"
            "    L4_4 = L4_4 * Camera.GetFOV(A0_0.Camera)\n"
            "    L4_4 = L4_4 * 0.5\n"
            "    L4_4 = L4_4 * L3_3\n"
            "    local L5_5 = math.cos(L4_4)\n"
            "    if L2_2 < L5_5 then\n"
            "    local L6_6 = (L5_5 - L2_2) / (L5_5 + 1)\n"
            "    local L7_7 = L6_6 * A0_0:GetTendNess()\n"
            "    L7_7 = math.min(L7_7, 1)\n"
            "    A0_0.CurrentDirection = CameraFunctions.Interpolate(A0_0.CurrentDirection, L7_7, L1_1)\n"
            "    A0_0.CurrentDirection:Normalise()\n"
            "    end\n"
            "    local L6_6 = math.asin(A0_0.CurrentDirection:GetZ() / 1)\n"
            "    if math.abs(L6_6) > A0_0.AngleLimit then\n"
            "    L6_6 = 0 < L6_6 and A0_0.AngleLimit or -A0_0.AngleLimit\n"
            "    A0_0.CurrentDirection:SetZ(A0_0.CurrentDirection:GetLengthXY() * math.tan(L6_6))\n"
            "    end\n"
            "    A0_0.CurrentDirection:Normalise()\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function RangedCombatCamera.GetDesiredCameraPos(A0_0)",
            "  function RangedCombatCamera.GetDesiredCameraPos(A0_0)\n"
            "    local L1_1 = A0_0:GetCurrentDirection() * (-1)\n"
            "    L1_1:Normalise()\n"
            "    local L2_2 = A0_0.IsPistol and -A0_0.DistSideHero or A0_0.DistSideHero\n"
            "    L1_1 = (L1_1 * A0_0.DistBehindHero) + (L1_1:GetCross(CVector3((0), (0), (-1))) * L2_2)\n"
            "    local L3_3 = L1_1:GetLength()\n"
            "    L1_1:Normalise()\n"
            "    local L4_4 = Creature.GetCollisionHeadPosition(A0_0.Player)\n"
            "    local L5_5 = L4_4 + (L1_1 * L3_3)\n"
            "    if Camera.DoesCameraRayIntersect(A0_0.Camera, L4_4, L5_5, CameraFunctions.RadiusToUseInCollision) then\n"
            "    L5_5 = Camera.GetLastCameraRayIntersectionPoint(A0_0.Camera)\n"
            "    local L6_6 = L5_5 - L4_4\n"
            "    smaller_delta_length = L6_6:GetLength()\n"
            "    L3_3 = math.max(smaller_delta_length, 0.20000000298023224)\n"
            "    A0_0.MovedIn = true\n"
            "    else\n"
            "    A0_0.MovedIn = nil\n"
            "    end\n"
            "    return L4_4 + (L1_1 * L3_3)\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function RangedCombatCamera.UpdateZoom(A0_0)",
            "  function RangedCombatCamera.UpdateZoom(A0_0)\n"
            "    local L1_1 = RangedCombatCamera.ZoomFOVOverride or Targeting.GetZoomLevel(A0_0.Player)\n"
            "    if A0_0.CurrentZoom ~= L1_1 then\n"
            "    local L2_2 = L1_1 < A0_0.CurrentZoom and RangedCombatCamera.ZoomInDamping or RangedCombatCamera.ZoomOutDamping\n"
            "    A0_0.CurrentZoom = L1_1\n"
            "    A0_0:SetDesiredFOV(A0_0.CurrentZoom, L2_2)\n"
            "    end\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function RangedCombatCamera.GetZoomTendAmount(A0_0)",
            "  function RangedCombatCamera.GetZoomTendAmount(A0_0)\n"
            "    A0_0.TendAmount = A0_0.TendAmount or 0\n"
            "    if A0_0.LastTendTime ~= Timing.GetProgramFrame() then\n"
            "    if A0_0.CurrentZoom ~= RangedCombatCamera.DefaultZoom then\n"
            "    local L1_1 = (A0_0.CurrentZoom - CameraValues.FOV.RANGED_TIGHT) / (CameraValues.FOV.RANGED - CameraValues.FOV.RANGED_TIGHT)\n"
            "    L1_1 = 1 - L1_1\n"
            "    L1_1 = math.max(L1_1, 0)\n"
            "    L1_1 = math.min(L1_1, 1)\n"
            "    A0_0.TendAmount = CameraFunctions.GetRollingAverage(A0_0.TendAmount, L1_1, RangedCombatCamera.TendDamping)\n"
            "    A0_0.LastTendTime = Timing.GetProgramFrame()\n"
            "    end\n"
            "    end\n"
            "    return A0_0.TendAmount\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function RangedCombatCamera.GetZoomTendAmountMultiplied(A0_0, A1_1)",
            "  function RangedCombatCamera.GetZoomTendAmountMultiplied(A0_0, A1_1)\n"
            "    local L2_2 = A0_0:GetZoomTendAmount() * A1_1\n"
            "    L2_2 = math.max(L2_2, 0)\n"
            "    L2_2 = math.min(L2_2, 1)\n"
            "    return L2_2\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function RangedCombatCamera.UpdateDOF(A0_0, A1_1, A2_2)",
            "  function RangedCombatCamera.UpdateDOF(A0_0, A1_1, A2_2)\n"
            "    local L3_3 = Creature.GetCollisionHeadPosition(A0_0.Player)\n"
            "    local L4_4 = 50\n"
            "    local L5_5 = 1\n"
            "    local L6_6 = 50\n"
            "    local L7_7 = 0\n"
            "    local L8_8 = 0.40000000596046448\n"
            "    A0_0.OnTargetAmount = A0_0.OnTargetAmount or 0\n"
            "    if Targeting.IsAvailable(A0_0.Player) and Targeting.GetTarget(A0_0.Player) and Targeting.GetTarget(A0_0.Player):IsAlive() then\n"
            "    local L9_9 = Targeting.GetTarget(A0_0.Player)\n"
            "    L3_3 = L9_9:GetPosition()\n"
            "    A0_0.OnTargetAmount = CameraFunctions.GetRollingAverage(A0_0.OnTargetAmount, 1, RangedCombatCamera.OnTargetBlendDamping)\n"
            "    L4_4 = CameraFunctions.Interpolate(50, A0_0.OnTargetAmount, 10)\n"
            "    L5_5 = CameraFunctions.Interpolate(50, A0_0.OnTargetAmount, 10)\n"
            "    L6_6 = CameraFunctions.Interpolate(50, A0_0.OnTargetAmount, 5)\n"
            "    L7_7 = CameraFunctions.Interpolate(0.20000000298023224, A0_0.OnTargetAmount, 0.5)\n"
            "    L8_8 = CameraFunctions.Interpolate(0.5, A0_0.OnTargetAmount, 0.5)\n"
            "    else\n"
            "    A0_0.OnTargetAmount = CameraFunctions.GetRollingAverage(A0_0.OnTargetAmount, 0, RangedCombatCamera.OffTargetBlendDamping)\n"
            "    end\n"
            "    local L9_9 = CameraFunctions.GetRangeNormalToCamera(A1_1, A1_1 + A2_2, L3_3)\n"
            "    A0_0.DOFFocalDistance = A0_0.DOFFocalDistance or L9_9\n"
            "    A0_0.DOFFocalDistance = CameraFunctions.GetRollingAverage(A0_0.DOFFocalDistance, L9_9, RangedCombatCamera.TargetBlendDamping)\n"
            "    A0_0.DOFFarDistance = A0_0.DOFFarDistance or L4_4\n"
            "    A0_0.DOFFarDistance = CameraFunctions.GetRollingAverage(A0_0.DOFFarDistance, L4_4, RangedCombatCamera.TargetBlendDamping)\n"
            "    A0_0.DOFCloseDistance = A0_0.DOFCloseDistance or L5_5\n"
            "    A0_0.DOFCloseDistance = CameraFunctions.GetRollingAverage(A0_0.DOFCloseDistance, L5_5, RangedCombatCamera.TargetBlendDamping)\n"
            "    A0_0.DOFClearDistance = A0_0.DOFClearDistance or L6_6\n"
            "    A0_0.DOFClearDistance = CameraFunctions.GetRollingAverage(A0_0.DOFClearDistance, L6_6, RangedCombatCamera.TargetBlendDamping)\n"
            "    A0_0.DOFCloseAmount = A0_0.DOFCloseAmount or L7_7\n"
            "    A0_0.DOFCloseAmount = CameraFunctions.GetRollingAverage(A0_0.DOFCloseAmount, L7_7, RangedCombatCamera.TargetBlendDamping)\n"
            "    A0_0.DOFFarAmount = A0_0.DOFFarAmount or L8_8\n"
            "    A0_0.DOFFarAmount = CameraFunctions.GetRollingAverage(A0_0.DOFFarAmount, L8_8, RangedCombatCamera.TargetBlendDamping)\n"
            "    A0_0:SetDesiredDOF(CameraFunctions.AssembleDOFData(A0_0.DOFFocalDistance, A0_0.DOFClearDistance, A0_0.DOFCloseDistance, A0_0.DOFFarDistance, A0_0.DOFCloseAmount, A0_0.DOFFarAmount, (1)))\n"
            "  end\n");
        changed |= ReplaceFunctionByHeader(
            source,
            "function RangedCombatCamera.FrameUpdate(A0_0)",
            "  function RangedCombatCamera.FrameUpdate(A0_0)\n"
            "    A0_0:UpdateCurrentDirection()\n"
            "    A0_0:UpdateZoom()\n"
            "    local L1_1 = A0_0:GetDesiredCameraPos()\n"
            "    A0_0:SetDesiredPosition(L1_1)\n"
            "    A0_0:SetDesiredDirection(A0_0:GetCurrentDirection())\n"
            "    if A0_0.MovedIn then\n"
            "    A0_0:FadeEntityBasedOnPositionIfNeeded(A0_0.Player, (0.5), (0.019999999552965164))\n"
            "    end\n"
            "    A0_0:UpdateDOF(L1_1, A0_0:GetCurrentDirection())\n"
            "    CameraBase.FrameUpdate(A0_0)\n"
            "  end\n");
        if (changed) {
            return true;
        }
    }

    if (source->find("CameraValues.FOV") != std::string::npos &&
        source->find("CameraValues.DOFClearDistance") != std::string::npos) {
        return ReplaceAllLiteral(
            source,
            "  if not CameraValues then\n"
            "  end\n"
            "  CameraValues = L0_0\n",
            "  CameraValues = CameraValues or {}\n");
    }

    if (source->find("CameraFunctions = {}") != std::string::npos &&
        source->find("function CameraFunctions.CollisionDebugTest") != std::string::npos) {
        *source = R"SCAMERAFUNCTIONS(  CameraFunctions = {}
  function CameraFunctions.AlwaysValid(A0_0)
    return true
  end
  function CameraFunctions.HasOverride(A0_0)
    if CameraManager.GetActiveOverriddenData(A0_0).CameraEnum ~= ECameraMode.CAMERA_MODE_DEFAULT then
    if CameraManager.GetActiveOverriddenData(A0_0).OverrideData then
    if CameraManager.GetActiveOverriddenData(A0_0).OverrideData.SupercedesCombat then
    return true
    end
    end
    if not CombatRegister:IsInCombat(A0_0) then
    return true
    end
    end
    return false
  end
  function CameraFunctions.GetSafeLookAtButtonDown(A0_0)
    if Player.IsAvailable(GetPlayerHero()) then
      local r1 = Player.GetLookAtButtonDown(GetPlayerHero())
      local r2 = Player.GetHenchmanLookAtButtonDown(GetPlayerHero())
      if GetPlayerHenchman() then
        if Player.IsAvailable(GetPlayerHenchman()) then
          if Player.GetLookAtButtonDown(GetPlayerHenchman()) then
            r2 = true
          end
        end
      end
      return r1 or r2
    end
    return false
  end
  function CameraFunctions.IsMultiplayer(A0_0, A1_1)
    local r2 = GetPlayerHenchman():IsAlive() and not CameraManager.IgnoreMultiplayerCameras
    return r2
  end
  function CameraFunctions.IsInCombat(A0_0)
    return CombatRegister:IsInCombat(A0_0, (10))
  end
  function CameraFunctions.IsPlayingSequence(A0_0)
    return false
  end
  function CameraFunctions.IsPointOfInterestLocked(A0_0)
    if Player.IsAvailable(A0_0) then
      return Player.HasPlayerMode(A0_0, EPlayerMode.PLAYER_MODE_TARGET_LOCK) and Targeting.GetPointOfInterestTarget(A0_0):IsAlive()
    end
    return false
  end
  function CameraFunctions.IsInFirstPerson(A0_0)
    if Player.IsAvailable(A0_0) then
      return Player.IsInFirstPerson(A0_0) and not CameraFunctions:IsMultiplayer(A0_0)
    end
    return false
  end
  function CameraFunctions.IsRangedCombat(A0_0)
    if Player.IsAvailable(A0_0) then
      return Player.HasPlayerMode(A0_0, EPlayerMode.PLAYER_MODE_AIM_RANGED) and not CameraFunctions:IsMultiplayer(A0_0)
    end
    return false
  end
  function CameraFunctions.IsMagicCombat(A0_0)
    if CameraFunctions.DisableMagicCamera or SACCamera.IsFightingTroll then
      return false
    end
    if Player.IsAvailable(A0_0) then
      return ((Player.HasPlayerMode(A0_0, EPlayerMode.PLAYER_MODE_MAGIC_CHARGE) and PlayerSpellManager.IsFullCharging(A0_0)) or (Player.HasPlayerMode(A0_0, EPlayerMode.PLAYER_MODE_MAGIC_CAST) and PlayerSpellManager.IsFullCasting(A0_0))) and not GetPlayerHenchman():IsAlive()
    end
    return false
  end
  function CameraFunctions.IsRangedOrMagicCombat(A0_0)
    if Player.IsAvailable(A0_0) then
      return (Player.HasPlayerMode(A0_0, EPlayerMode.PLAYER_MODE_AIM_RANGED) or Player.HasPlayerMode(A0_0, EPlayerMode.PLAYER_MODE_MAGIC_CHARGE)) and not GetPlayerHenchman():IsAlive()
    end
    return false
  end
  function CameraFunctions.GetRollingAverage(A0_0, A1_1, A2_2)
    return (((A0_0 * A2_2) + A1_1) / (A2_2 + (1)))
  end
  function CameraFunctions.TendPositionToFocusHelper(A0_0, A1_1, A2_2)
    local L3_3 = A0_0 - A1_1
    local L4_4 = L3_3 * A2_2
    A0_0 = A1_1 + L4_4
    return A0_0
  end
  function CameraFunctions.HandHeldHelper(A0_0, A1_1, A2_2)
    local L3_3 = A0_0:GetX()
    local L4_4 = A0_0:GetY()
    local L5_5 = A0_0:GetZ()
    L5_5 = L5_5 + (math.sin((L3_3 + L4_4) * A2_2) * A1_1)
    L3_3 = L3_3 + ((math.sin((L4_4 + L5_5) * A2_2) * A1_1) * 0.5)
    L4_4 = L4_4 + ((math.sin((L5_5 + L3_3) * A2_2) * A1_1) * 0.5)
    local L6_6 = CVector3(L3_3, L4_4, L5_5)
    return L6_6
  end
  function CameraFunctions.HandHeld(A0_0, A1_1, A2_2)
    local L3_3 = math.max((A0_0 - A1_1):GetLength(), 1)
    local L4_4 = 6
    local L5_5 = 2
    local L6_6 = (L3_3 - L5_5) / (L4_4 - L5_5)
    L6_6 = math.min(L6_6, 1)
    L6_6 = math.max(L6_6, 0)
    local r7 = 1 * math.pi
    local r8 = 1 * math.pi
    local r9 = r7 + ((r8 - r7) * L6_6)
    local r10 = 0.10000000149011612
    local r11 = 0.20000000298023224
    local r12 = A2_2 * (r10 + ((r11 - r10) * L6_6))
    local r13 = CameraFunctions.HandHeldHelper(A0_0, r12, r9)
    local r14 = 0.5
    local r15 = CameraFunctions.HandHeldHelper(A1_1, r12 * r14, r9)
    return r13, r15
  end
  function CameraFunctions.GrabDOFData(A0_0)
    local A1_1
    local L2_2 = {}
    A1_1, L2_2.NearBlur, L2_2.FarBlur, L2_2.NearPlane, L2_2.NearFocalPlane, L2_2.FarFocalPlane, L2_2.FarPlane = Camera.GetDOF(A0_0)
    L2_2.Alpha = Camera.GetDOFAlpha(A0_0)
    return L2_2
  end
  function CameraFunctions.SetDOFData(A0_0, A1_1)
    if not A1_1 then
    return
    end
    Camera.SetDOF(A0_0, A1_1.NearBlur, A1_1.FarBlur, A1_1.NearPlane, A1_1.NearFocalPlane, A1_1.FarFocalPlane, A1_1.FarPlane)
    Camera.SetDOFAlpha(A0_0, A1_1.Alpha)
  end
  function CameraFunctions.SetDOFDataCut(A0_0, A1_1)
    if not A1_1 then
    return
    end
    Camera.SetDOFCut(A0_0, A1_1.NearBlur, A1_1.FarBlur, A1_1.NearPlane, A1_1.NearFocalPlane, A1_1.FarFocalPlane, A1_1.FarPlane)
    Camera.SetDOFAlphaCut(A0_0, A1_1.Alpha)
  end
  function CameraFunctions.AssembleDOFData(A0_0, A1_1, A2_2, A3_3, A4_4, A5_5, A6_6)
    local L7_7 = {}
    L7_7.NearBlur = A4_4 or 0
    L7_7.NearFocalPlane = math.max((A0_0 and A1_1 and (A0_0 - A1_1)) or CameraValues.DOFDistance.GAME_MID, 0.5)
    L7_7.NearPlane = math.max(L7_7.NearFocalPlane - (A2_2 or CameraValues.DOFDistance.GAME_MID), 0.25)
    L7_7.FarBlur = A5_5 or 0
    L7_7.FarFocalPlane = (A0_0 and A1_1 and (A0_0 + A1_1)) or CameraValues.DOFDistance.GAME_MID
    L7_7.FarPlane = math.max(L7_7.FarFocalPlane + (A3_3 or CameraValues.DOFDistance.GAME_MID), L7_7.NearPlane)
    L7_7.Alpha = A6_6 or (((0 < L7_7.NearBlur) or L7_7.FarBlur) and 1 or 0)
    return L7_7
  end
  function CameraFunctions.GetRangeNormalToCamera(A0_0, A1_1, A2_2)
    local L3_3 = A1_1 - A0_0
    L3_3:Normalise()
    local L4_4 = (A2_2 - A0_0):GetDot(L3_3)
    return L4_4
  end
  function CameraFunctions.GetXYZFromDirection(A0_0)
    local L1_1 = A0_0
    L1_1:Normalise()
    local L2_2 = L1_1:GetCross(CVector3(0, 0, 1))
    L2_2:Normalise()
    local L3_3 = L2_2:GetCross(L1_1)
    L3_3:Normalise()
    return L2_2, L3_3, L1_1
  end
  function CameraFunctions.Interpolate(A0_0, A1_1, A2_2)
    if A0_0 == nil then
    return A2_2
    end
    if A2_2 == nil then
    return A0_0
    end
    return (A0_0 + ((A2_2 - A0_0) * A1_1))
  end
  function CameraFunctions.ScaleTable(A0_0, A1_1)
    local L2_2 = {}
    for r6, r7 in pairs(A0_0) do
    if type(r7) == "number" then
    L2_2[r6] = (r7 * A1_1)
    else
    L2_2[r6] = r7
    end
    end
    return L2_2
  end
  function CameraFunctions.CreateGenericClosure(L0_0, L1_1)
    if not L0_0 then
    return nil
    end
    return function(A0_0, A1_1, A2_2, A3_3)
      return L0_0(L1_1, A0_0, A1_1, A2_2, A3_3)
    end
  end
  function CameraFunctions.GetObjectPositionWithDummy(A0_0, A1_1)
    local L2_2 = A1_1 or "Character.Focal.Mouth"
    if A0_0:IsAlive() then
    if Creature.IsAvailable(A0_0) then
    if GraphicAppearance.HasDummyObject(A0_0, A1_1, 0) then
    return GraphicAppearance.GetDummyObjectPosition(A0_0, A1_1, (0))
    end
    end
    return A0_0:GetPosition()
    else
    return nil
    end
  end
  function CameraFunctions.GetObjectPosition(A0_0)
    return CameraFunctions.GetObjectPositionWithDummy(A0_0, "Character.Focal.Mouth")
  end
  CameraFunctions.MaxDisplacementToAvoidCollision = 1.5
  CameraFunctions.DisplacementStepSize = 0.25
  CameraFunctions.RadiusToUseInCollision = 0.20000000298023224
  CameraFunctions.ClosestToDestination = 0.30000001192092896
  CameraFunctions.PopRange = 1
  function CameraFunctions.SmoothAdjustPosToAvoidOcclusion(A0_0, A1_1, A2_2)
    local L3_3 = CameraFunctions.GetDirectionOfLine(A1_1, A2_2)
    if Camera.DoesCameraRayIntersect(A0_0, A1_1, A2_2, CameraFunctions.RadiusToUseInCollision) then
    local L4_4 = Camera.GetLastCameraRayIntersectionPoint(A0_0)
    local L5_5 = Camera.GetLastCameraRayIntersectionPointScore(A0_0)
    local L6_6 = (A1_1 - A2_2):GetLength()
    A2_2 = A1_1 + (L3_3 * math.max(L6_6 - L5_5 - CameraFunctions.RadiusToUseInCollision, CameraFunctions.ClosestToDestination))
    A2_2 = Camera.PopPosForwardIfInsideObject(A0_0, A2_2, (A1_1 - A2_2), CameraFunctions.PopRange)
    return A2_2, L5_5, true
    end
    if Camera.DoesSmoothCameraRayIntersect(A0_0, (A1_1 + (L3_3 * CameraFunctions.RadiusToUseInCollision)), A2_2) then
    A2_2 = Camera.GetLastCameraRayIntersectionPoint(A0_0)
    local L4_4 = Camera.GetLastCameraRayIntersectionPointScore(A0_0)
    A2_2 = Camera.PopPosForwardIfInsideObject(A0_0, A2_2, (A1_1 - A2_2), CameraFunctions.PopRange)
    return A2_2, L4_4, true
    end
    A2_2 = Camera.PopPosForwardIfInsideObject(A0_0, A2_2, (A1_1 - A2_2), CameraFunctions.PopRange)
    return A2_2, 0, false
  end
  function CameraFunctions.GetSimpleCollision(A0_0, A1_1, A2_2)
    local L3_3 = CameraFunctions.GetDirectionOfLine(A1_1, A2_2)
    if Camera.DoesBigCameraRayIntersect(A0_0, (A1_1 + (L3_3 * CameraFunctions.RadiusToUseInCollision)), A2_2) then
    return Camera.GetLastCameraRayIntersectionPoint(A0_0), Camera.GetLastCameraRayIntersectionPointScore(A0_0)
    end
    return A2_2, (0)
  end
  function CameraFunctions.EnforceMaximumDistanceFromTargetWhileMaintainingAngle(A0_0, A1_1, A2_2, A3_3)
    local L4_4 = (A0_0 - A2_2):GetLength()
    if A3_3 < L4_4 then
    local L5_5 = A3_3 / L4_4
    A2_2 = A0_0 + ((A2_2 - A0_0) * L5_5)
    A1_1 = A0_0 + ((A1_1 - A0_0) * L5_5)
    end
    return A1_1, A2_2
  end
  function CameraFunctions.GetDirectionOfLine(A0_0, A1_1)
    local L2_2 = A1_1 - A0_0
    L2_2:Normalise()
    return L2_2
  end
  function CameraFunctions.GetLengthAlongLineAndBackward(A0_0, A1_1, A2_2)
    local L3_3 = A2_2 - A0_0
    local L4_4 = A1_1 - A0_0
    L4_4:Normalise()
    return L3_3:GetDot(L4_4), L4_4
  end
  function CameraFunctions.DrawMirrorLine(A0_0, A1_1, A2_2, A3_3, A4_4)
    Debug.DrawLine(A0_0, A1_1, A2_2, A3_3, A4_4)
    local r5 = CVector3((0.001500000013038516), (0.001500000013038516), (0.001500000013038516))
    Debug.DrawLine((A0_0 + r5), (A1_1 + r5), ((255) - A2_2), ((255) - A3_3), ((255) - A4_4))
  end
  function CameraFunctions.DrawPoint(A0_0, A1_1, A2_2, A3_3, A4_4)
    CameraFunctions.DrawMirrorLine((A0_0 + CVector3((A1_1 * (4)), (0), (0))), (A0_0 + CVector3(((-A1_1) * (4)), (0), (0))), A2_2, A3_3, A4_4)
    CameraFunctions.DrawMirrorLine((A0_0 + CVector3((0), (A1_1 * (4)), (0))), (A0_0 + CVector3((0), ((-A1_1) * (4)), (0))), A2_2, A3_3, A4_4)
    CameraFunctions.DrawMirrorLine((A0_0 + CVector3((0), (0), (A1_1 * (4)))), (A0_0 + CVector3((0), (0), ((-A1_1) * (4)))), A2_2, A3_3, A4_4)
  end
  function CameraFunctions.CollisionDebugTest(A0_0, A1_1, A2_2, A3_3)
    local L4_4, L5_5, L6_6 = CameraFunctions.GetXYZFromDirection(A2_2 - A1_1)
    CameraFunctions.DrawMirrorLine((A1_1 + (L4_4 * A3_3)), (A1_1 + (L5_5 * A3_3)), (255), (255), (255))
    CameraFunctions.DrawMirrorLine((A1_1 - (L4_4 * A3_3)), (A1_1 + (L5_5 * A3_3)), (255), (255), (255))
    CameraFunctions.DrawMirrorLine((A1_1 + (L4_4 * A3_3)), (A1_1 - (L5_5 * A3_3)), (255), (255), (255))
    CameraFunctions.DrawMirrorLine((A1_1 - (L4_4 * A3_3)), (A1_1 - (L5_5 * A3_3)), (255), (255), (255))
    CameraFunctions.DrawMirrorLine((A2_2 + (L4_4 * A3_3)), (A2_2 + (L5_5 * A3_3)), (255), 255, (255))
    CameraFunctions.DrawMirrorLine((A2_2 - (L4_4 * A3_3)), (A2_2 + (L5_5 * A3_3)), (255), (255), 255)
    CameraFunctions.DrawMirrorLine((A2_2 + (L4_4 * A3_3)), (A2_2 - (L5_5 * A3_3)), (255), (255), 255)
    CameraFunctions.DrawMirrorLine((A2_2 - (L4_4 * A3_3)), (A2_2 - (L5_5 * A3_3)), (255), (255), 255)
    CameraFunctions.DrawMirrorLine((A1_1 + (L4_4 * A3_3)), (A2_2 + (L4_4 * A3_3)), (255), (255), 255)
    CameraFunctions.DrawMirrorLine((A1_1 - (L4_4 * A3_3)), (A2_2 - (L4_4 * A3_3)), (255), (255), 255)
    CameraFunctions.DrawMirrorLine((A1_1 + (L5_5 * A3_3)), (A2_2 + (L5_5 * A3_3)), (255), (255), 255)
    CameraFunctions.DrawMirrorLine((A1_1 - (L5_5 * A3_3)), (A2_2 - (L5_5 * A3_3)), (255), (255), 255)
    if Camera.DoesCameraRayIntersect(A0_0, A1_1, A2_2, A3_3) then
    local L7_7 = Camera.GetLastCameraRayIntersectionPoint(A0_0)
    CameraFunctions.DrawMirrorLine(A1_1, L7_7, (255), (255), 255)
    CameraFunctions.DrawPoint(L7_7, A3_3, (255), 255, 255)
    end
  end
)SCAMERAFUNCTIONS";
        return true;
    }

    if (source->find(":Create(\"FreeCamera\")") != std::string::npos &&
        source->find("FreeCamera.FrameUpdate = function") != std::string::npos) {
        *source = R"SFREECAMERA(FreeCamera = CameraBase:Create("FreeCamera")
FreeDOFCDist = 10
FreeDOFFDist = 10
FreeDOFClearDist = 5
FreeDOFStrength = 0
local L0_0 = {Active = false}
function FreeCamera.CreateDeltaFunc(A0_0, A1_1, A2_2, A3_3)
  local L4_4 = A0_0
  local L5_5 = A1_1
  local L6_6 = A2_2
  local L7_7 = A3_3
  return function()
    FreeCamera[L4_4] = Bounds(L6_6, FreeCamera[L4_4] + L5_5, L7_7)
  end
end
if Debug.InertialFreeCam then
  FreeCamera.RotateSpeed = 0.5
  FreeCamera.MoveSpeed = 0.029999999329447746
  FreeCamera.Inertia = 0.94999998807907104
  FreeCamera.AngularInertia = 0.85000002384185791
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F1, FreeCamera:CreateDeltaFunc("MoveSpeed", -0.0099999997764825821, 0, 5))
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F2, FreeCamera:CreateDeltaFunc("MoveSpeed", 0.0099999997764825821, 0, 5))
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F3, FreeCamera:CreateDeltaFunc("Inertia", -0.0099999997764825821, 0, 1))
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F4, FreeCamera:CreateDeltaFunc("Inertia", 0.0099999997764825821, 0, 1))
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F5, FreeCamera:CreateDeltaFunc("RotateSpeed", -0.0099999997764825821, 0, 1))
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F6, FreeCamera:CreateDeltaFunc("RotateSpeed", 0.0099999997764825821, 0, 1))
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F7, FreeCamera:CreateDeltaFunc("AngularInertia", -0.0099999997764825821, 0, 1))
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F8, FreeCamera:CreateDeltaFunc("AngularInertia", 0.0099999997764825821, 0, 1))
  local L1_1 = {Active = false}
  L1_1.Update = function(A0_0)
    while true do
      if A0_0.Active then
        local L1_1 = "Move Speed [F1/F2] = " .. tostring(FreeCamera.MoveSpeed) .. "\nMove Inertia [F3/F4] = " .. tostring(FreeCamera.Inertia)
        local L2_2 = "\n\nTurn Speed [F5/F6] = " .. tostring(FreeCamera.RotateSpeed) .. "\nTurn Inertia [F7/F8] = " .. tostring(FreeCamera.AngularInertia)
        Debug.DrawText(L1_1 .. L2_2, CI32Vector2(20, 100))
      end
      coroutine.yield()
    end
  end
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F9, function()
    L1_1.Active = not L1_1.Active
  end)
  GeneralScriptManager.AddScript(L1_1)
else
  FreeCamera.RotateSpeed = 3
  FreeCamera.MoveSpeed = 0.30000001192092896
  FreeCamera.Inertia = 0
  FreeCamera.AngularInertia = 0
  local L1_1 = function()
    local L0_0 = Debug.IsGDCDemo
    Debug.IsGDCDemo = true
    Debug.ReloadCameras()
    Debug.IsGDCDemo = L0_0
  end
end
Debug.AddLuaDebugKeyFunc(EInputKey.KB_F9, function()
  FreeCamera.DisplayPosition = not FreeCamera.DisplayPosition
end)
Debug.AddLuaDebugKeyFunc(EInputKey.KB_LBRACKET, FreeCamera.CreateDeltaFunc("FOV", -1, 10, 200))
Debug.AddLuaDebugKeyFunc(EInputKey.KB_RBRACKET, FreeCamera.CreateDeltaFunc("FOV", 1, 10, 200))
Debug.AddLuaDebugKeyFunc(EInputKey.KB_F8, function()
  FreeCamera.DebugCollision = not FreeCamera.DebugCollision
end)
function FreeCamera.IsValid()
  return Debug.GetUseFreeCamera()
end
function FreeCamera.Init(A0_0)
  CameraBase.Init(A0_0)
  A0_0.AngleXY = 0
  A0_0.AngleYZ = 0
  A0_0.AngleXZ = 0
end
function FreeCamera.Activate(A0_0, A1_1, A2_2, A3_3)
  assert(A2_2)
  assert(A2_2:IsAlive())
  A0_0.BlendFrameDuration = 0
  CameraBase.Activate(A0_0, A1_1, A2_2, A3_3)
  FreeCamera.FOV = Camera.GetFOV(A2_2)
  if A3_3 then
    if A3_3:IsAlive() then
      local L4_4 = Camera.GetAngles(A3_3)
      Camera.SetAnglesCut(A0_0.Camera, L4_4:GetX(), L4_4:GetY(), L4_4:GetZ())
      A0_0:SetDesiredPosition(A3_3:GetPosition(), true)
      FreeCamera.FOV = Camera.GetFOV(A3_3)
    end
  end
  A0_0:SetDesiredDOF(CameraFunctions.AssembleDOFData(15, 5, 10, 10, 0, 0))
  A0_0.Velocity = CVector3(0, 0, 0)
  A0_0.AngularVelocity = CVector3(0, 0, 0)
end
function FreeCamera.IsFreeCamera(A0_0)
  return true
end
function FreeCamera.IsPrimary(A0_0)
  return false
end
function FreeCamera.Deactivate(A0_0)
  CameraBase.Deactivate(A0_0)
  A0_0.Camera = nil
end
function FreeCamera.CleanUp(A0_0)
  CameraBase.CleanUp(A0_0)
  A0_0.Camera = nil
end
function FreeCamera.ProcessGameAction(A0_0, A1_1, A2_2, A3_3)
  local L4_4 = Camera.GetAngles(A0_0.Camera)
  local L5_5 = Camera.GetForwardVector(A0_0.Camera)
  local L6_6 = Camera.GetUpVector(A0_0.Camera)
  local L7_7 = Camera.GetRightVector(A0_0.Camera)
  local L8_8 = A0_0.Camera:GetPosition()
  local L9_9 = CVector3(0, 0, 0)
  local L10_10 = CVector3(0, 0, 0)
  local L11_11 = A0_0.MoveSpeed
  local L12_12 = A0_0.RotateSpeed
  if Camera.IsMovingSlowly(A0_0.Camera) then
    L11_11 = L11_11 / 4
    L12_12 = L12_12 / 2
  end
  if Camera.IsMovingQuickly(A0_0.Camera) then
    L11_11 = L11_11 * 4
    L12_12 = L12_12 * 2
  end
  A2_2 = A2_2 * math.abs(A2_2)
  A3_3 = A3_3 * math.abs(A3_3)
  if A1_1 == EGameAction.GAME_ACTION_DEBUG_CAMERA_SNAP_TO_HERO then
    if QuestManager.HeroEntity:IsAlive() then
      A0_0:SetDesiredAngles(CVector3(A0_0.AngleXY, A0_0.AngleYZ, A0_0.AngleXZ))
      player_pos = QuestManager.HeroEntity:GetPosition()
      A0_0:SetDesiredPositin((player_pos - (L5_5 * 4)) + CVector3(0, 0, 1))
      return
    end
  elseif A1_1 == EGameAction.GAME_ACTION_CAMERA_MOVEMENT then
    L9_9 = ((L7_7 * A2_2) * L11_11) + ((L5_5 * A3_3) * L11_11)
  elseif A1_1 == EGameAction.GAME_ACTION_DEBUG_CAMERA_MOVEMENT then
    L9_9 = ((L7_7 * A2_2) * L11_11) + ((L5_5 * (-A3_3)) * L11_11)
  elseif A1_1 == EGameAction.GAME_ACTION_ROTATE_CAMERA then
    L10_10 = L10_10 + (CVector3(A2_2, A3_3, 0) * L12_12)
  elseif A1_1 == EGameAction.GAME_ACTION_DEBUG_ROTATE_CAMERA then
    L10_10 = L10_10 + (CVector3(A2_2, -A3_3, 0) * L12_12)
  end
  A0_0.Velocity = A0_0.Velocity + L9_9
  A0_0.AngularVelocity = A0_0.AngularVelocity + L10_10
end
function FreeCamera.FrameUpdate(A0_0)
  local L1_1 = Camera.GetDebugMoveToCut(A0_0.Camera)
  if L1_1:GetSquaredLength() > 0 then
    Camera.MoveToCut(A0_0.Camera, L1_1)
    A0_0:SetDesiredPosition(L1_1, true)
    Camera.ClearDebugMoveToCut(A0_0.Camera)
  end
  local L2_2 = Camera.GetDebugFocusToCut(A0_0.Camera)
  if L2_2:GetSquaredLength() > 0 then
    Camera.SetTargetCut(A0_0.Camera, L2_2)
    Camera.ClearDebugFocusToCut(A0_0.Camera)
  end
  local L3_3 = Camera.GetAngles(A0_0.Camera)
  A0_0:SetDesiredAngles(CVector3(L3_3:GetX() + A0_0.AngularVelocity:GetX(), L3_3:GetY() + A0_0.AngularVelocity:GetY(), L3_3:GetZ() + A0_0.AngularVelocity:GetZ()))
  A0_0:SetDesiredPosition(A0_0.CurrentPosition + A0_0.Velocity)
  A0_0:SetDesiredFOV(FreeCamera.FOV)
  A0_0:SetDesiredDOF(CameraFunctions.AssembleDOFData(dof_focal_distance, FreeDOFClearDist, FreeDOFCDist, FreeDOFFDist, FreeDOFStrength, FreeDOFStrength))
  CameraBase.FrameUpdate(A0_0)
  if FreeCamera.DebugCollision then
    local L4_4 = Camera.GetForwardVector(A0_0.Camera)
    local L5_5, L6_6, L7_7 = CameraFunctions.GetXYZFromDirection(L4_4)
    local L8_8 = A0_0.CurrentPosition + (L5_5 * 0.15000000596046448)
    L8_8 = L8_8 - (L6_6 * 0.15000000596046448)
    L8_8 = L8_8 + (L7_7 * 0.5)
    local L9_9 = L8_8 + (L4_4 * 1)
    CameraFunctions.CollisionDebugTest(A0_0.Camera, L8_8, L9_9, 0.10000000149011612)
  end
  A0_0.Velocity = A0_0.Velocity * A0_0.Inertia
  A0_0.AngularVelocity = A0_0.AngularVelocity * A0_0.AngularInertia
  if FreeCamera.DisplayPosition then
    local L4_4 = "\n\nPosition (x,y,z) = " .. tostring(A0_0.CurrentPosition:GetX()) .. "," .. tostring(A0_0.CurrentPosition:GetY()) .. "," .. tostring(A0_0.CurrentPosition:GetZ())
    local L5_5 = Camera.GetForwardVector(A0_0.Camera)
    local L6_6 = A0_0.CurrentPosition + L5_5
    local L7_7 = "\nFocus (x,y,z) = " .. tostring(L6_6:GetX()) .. "," .. tostring(L6_6:GetY()) .. "," .. tostring(L6_6:GetZ())
    local L8_8 = "\nFOV = " .. tostring(A0_0.FOV)
    Debug.DrawText(L4_4 .. L7_7 .. L8_8, CI32Vector2(20, 100))
  end
end
)SFREECAMERA";
        return true;
    }

    if (source->find("CombatCannedActionCamera = CameraBase:Create(\"CombatCannedActionCamera\")") !=
        std::string::npos) {
        *source = R"SCAMERA(  CombatCannedActionCamera = CameraBase:Create("CombatCannedActionCamera")
  CombatCannedActionCamera.Active = false
  CombatCannedActionCamera.Values = {}
  CombatCannedActionCamera.FocusMinHeight = 0.5
  CombatCannedActionCamera.PositionMinHeight = 0.5
  function CombatCannedActionCamera.IsValid()
    return CombatCannedActionCamera.Active
  end
  function CombatCannedActionCamera.ScaleVals(A0_0, A1_1)
    A0_0.InitialZoom = (A0_0.InitialZoom * A1_1)
    A0_0.DZoom = (A0_0.DZoom * A1_1)
    if A0_0.MaxZoom then
    A0_0.MaxZoom = (A0_0.MaxZoom * A1_1)
    end
    return A0_0
  end
  function CombatCannedActionCamera.SwitchOnWithVals(A0_0)
    CombatCannedActionCamera.SelfSwitch = true
    CombatCannedActionCamera.Active = true
    CombatCannedActionCamera.Values = A0_0
    CombatCannedActionCamera.InitialFrame = Timing.GetWorldFrame()
  end
  function CombatCannedActionCamera.SetCentreFunction(A0_0)
    CombatCannedActionCamera.Values.CentreFunction = A0_0
  end
  function CombatCannedActionCamera.SetFocusFunction(A0_0)
    CombatCannedActionCamera.Values.FocusFunction = A0_0
  end
  function CombatCannedActionCamera.SetDOFRange(A0_0)
    CombatCannedActionCamera.Values.DOFRange = A0_0
  end
  function CombatCannedActionCamera.SwitchOff()
    CombatCannedActionCamera.Active = false
  end
  function CombatCannedActionCamera.Init(A0_0)
    CameraBase.Init(A0_0)
  end
  function CombatCannedActionCamera.IsFreeCamera(A0_0)
    return false
  end
  function CombatCannedActionCamera.IsPrimary(A0_0)
    return false
  end
  function CombatCannedActionCamera.GetVectorFromRotations(A0_0, A1_1, A2_2)
    local r3 = CVector3(math.sin(A0_0), math.cos(A0_0), (0))
    local r4 = r3:GetLength()
    r3:SetZ((r4 * math.tan(A1_1)))
    r3:Normalise()
    r3 = (r3 * A2_2)
    return r3
  end
  function CombatCannedActionCamera.GetRotationsFromVector(A0_0)
    local r1 = math.atan2(A0_0:GetX(), A0_0:GetY())
    local r2 = math.atan2(A0_0:GetZ(), A0_0:GetLengthXY())
    local r3 = A0_0:GetLength()
    return r1, r2, r3
  end
  CombatCannedActionCamera.Damping = 5
  function CombatCannedActionCamera.Activate(A0_0, A1_1, A2_2, A3_3)
    CameraBase.BlendOut = nil
    A0_0.BlendFrameDuration = A0_0.Values.BlendInFrames or 0
    CameraBase.Activate(A0_0, A1_1, A2_2, A3_3)
    A0_0:SetFOV(A0_0.Values.InitialFOV)
    A0_0:SetRoll(A0_0.Values.InitialRoll or 0)
    A0_0:SetTranslationDamping(CombatCannedActionCamera.Damping)
    A0_0.Player = A1_1
    CombatCannedActionCamera.PreviousCameraPosition = A3_3:GetPosition()
    Debug.SetECLookEnabled(false)
    A0_0.BlendOutFrameDuration = A0_0.Values.BlendOutFrames or 0
    A0_0.CurrentFocus = nil
  end
  function CombatCannedActionCamera.Deactivate(A0_0)
    CameraBase.Deactivate(A0_0)
    A0_0.Player = nil
    Debug.SetECLookEnabled(true)
  end
  function CombatCannedActionCamera.CleanUp(A0_0)
    CameraBase.CleanUp(A0_0)
  end
  function CombatCannedActionCamera.ProcessGameAction(A0_0, A1_1, A2_2, A3_3)
  end
  function CombatCannedActionCamera.PreliminaryTest(A0_0, A1_1)
    local L2_2 = 0.5
    local L3_3 = math.min(math.floor(A1_1 / L2_2), 4)
    local L4_4 = A1_1 / L3_3
    if A0_0.DYaw == 0 and A0_0.DPitch then
      L3_3 = 0
    end
    local L5_5 = CameraManager.GetCurrentCamera()
    local L6_6 = L5_5.Camera
    for r10 = 0, L3_3, 1 do
      local r11 = L4_4 * r10
      local r12, r13 = CombatCannedActionCamera.GetFocusAndPositionFromVals(A0_0, r11)
      local r14, r15 = CombatCannedActionCamera.ApplyMinHeight(r13, CombatCannedActionCamera.FocusMinHeight, L6_6)
      r12 = CombatCannedActionCamera.ApplyMinHeight(r12, CombatCannedActionCamera.PositionMinHeight, L6_6, r15)
      local r16, r17 = CombatCannedActionCamera.OccludeTest(r12, r14, L6_6)
      if not r17 then
        return false
      end
    end
    return true
  end
  function CombatCannedActionCamera.GetFocusAndPositionFromVals(A0_0, A1_1)
    local L2_2 = (A0_0.InitialYaw + (A1_1 * A0_0.DYaw))
    local L3_3 = (A0_0.InitialPitch + (A1_1 * A0_0.DPitch))
    local L4_4 = (A0_0.InitialZoom + (A1_1 * A0_0.DZoom))
    if math.abs(L3_3) > 0.5 then
      if L3_3 < 0 then
        L3_3 = -0.5
      else
        L3_3 = 0.5
      end
    end
    if A0_0.MaxZoom then
      L4_4 = math.min(L4_4, A0_0.MaxZoom)
    end
    local L5_5 = CombatCannedActionCamera.GetVectorFromRotations(L2_2, L3_3, L4_4)
    local L6_6 = A0_0.CentreFunction and A0_0.CentreFunction() or A0_0.FocusPosition
    local L7_7 = L6_6 + L5_5
    return L6_6, L7_7
  end
  function CombatCannedActionCamera.OccludeTest(A0_0, A1_1, A2_2)
    local L3_3 = A1_1 - A0_0
    local L4_4 = L3_3:GetLength()
    L3_3:Normalise()
    local L5_5 = math.min(L4_4, 0.5)
    local L6_6 = A0_0 + (L3_3 * math.max(L4_4 - 0.5, L5_5))
    local L7_7, r8
    A0_0, r8, L7_7 = CameraFunctions.SmoothAdjustPosToAvoidOcclusion(A2_2, L6_6, A0_0)
    return A0_0, not L7_7
  end
  function CombatCannedActionCamera.ApplyMinHeight(A0_0, A1_1, A2_2, A3_3)
    local L4_4 = Camera.GetGroundPositionAtPosition(A2_2, A0_0)
    local L5_5 = L4_4:GetZ()
    if A3_3 then
      L5_5 = math.max(L5_5, A3_3)
    end
    local L6_6 = A1_1 + L5_5
    L6_6 = math.max(L6_6, A0_0:GetZ())
    A0_0:SetZ(L6_6)
    return A0_0, L5_5
  end
  function CombatCannedActionCamera.FrameUpdate(A0_0)
    local L1_1 = Timing.GetSecondsSince(A0_0.InitialFrame)
    local L2_2, L3_3 = CombatCannedActionCamera.GetFocusAndPositionFromVals(A0_0.Values, L1_1)
    local L4_4, L5_5 = CombatCannedActionCamera.ApplyMinHeight(L2_2, CombatCannedActionCamera.FocusMinHeight, A0_0.Camera)
    L3_3 = CombatCannedActionCamera.ApplyMinHeight(L3_3, CombatCannedActionCamera.PositionMinHeight, A0_0.Camera, L5_5)
    A0_0.PreviousFocusPosition = L2_2
    L3_3 = CombatCannedActionCamera.OccludeTest(L3_3, L4_4, A0_0.Camera)
    local L6_6 = A0_0.Values.InitialFOV + (L1_1 * A0_0.Values.DFOV)
    A0_0:SetDesiredFOV(L6_6, 0)
    local L7_7 = A0_0.Values.InitialRoll or 0
    L7_7 = L7_7 + (L1_1 * (A0_0.Values.DRoll or 0))
    L7_7 = math.max(L7_7, -40)
    L7_7 = math.min(L7_7, 40)
    A0_0:SetDesiredRoll(L7_7, 0)
    if A0_0.Values.FocusFunction then
      L2_2 = A0_0.Values.FocusFunction(L2_2)
    end
    if A0_0.Values.Blur then
      A0_0:SetDesiredBlur(A0_0.Values.Blur, 0.5)
    end
    if A0_0.CurrentFocus then
      A0_0.CurrentFocus = CameraFunctions.GetRollingAverage(A0_0.CurrentFocus, L2_2, CombatCannedActionCamera.Damping)
    else
      A0_0.CurrentFocus = L2_2
    end
    L2_2 = A0_0.CurrentFocus
    A0_0:FadeAllEntitiesBasedOnPositionIfNeeded(2, A0_0.Values.ImportantEnemy)
    if CombatCannedActionCamera.Values.DOFRange then
      local L8_8 = CameraFunctions.GetRangeNormalToCamera(L3_3, L2_2, L2_2)
      A0_0:SetDesiredDOF(CameraFunctions.AssembleDOFData(L8_8, CameraValues.DOFClearDistance.CLOSE, CombatCannedActionCamera.Values.DOFRange, CombatCannedActionCamera.Values.DOFRange, CameraValues.DOFStrength.MAX, CameraValues.DOFStrength.MAX))
    end
    A0_0:SetDesiredPosition(L3_3, CombatCannedActionCamera.SelfSwitch)
    A0_0:SetDesiredTarget(L2_2)
    CombatCannedActionCamera.SelfSwitch = nil
    CameraBase.FrameUpdate(A0_0)
  end
)SCAMERA";
        return true;
    }
    return false;
}

}  // namespace

bool ApplyFastStringAndCageCameraRawReadableRecovery(std::string* source) {
    return FoldFastStringAndCageCameraRawReadableRecovery(source);
}

bool ApplyFastCameraRawReadableRecovery(std::string* source) {
    return ApplyFastCameraRawReadableRecoveryImpl(source);
}

}  // namespace refii::lua::workspace
