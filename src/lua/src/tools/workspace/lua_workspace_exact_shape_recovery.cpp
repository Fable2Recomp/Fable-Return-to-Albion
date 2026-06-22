#include "tools/workspace/lua_workspace_exact_shape_recovery.h"

#include "tools/workspace/lua_workspace_text_repair.h"

#include <initializer_list>
#include <string>
#include <string_view>

namespace refii::lua::workspace {

namespace {

bool FastApplyLiteralRules(std::string* source,
                           std::initializer_list<text_repair::LiteralRule> rules) {
    return text_repair::ApplyLiteralRules(source, rules);
}

}  // namespace

bool FoldFastBoundedHotspotAbilitiesManagerExactShape(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("function AbilitiesManager.UpdateDescriptionPageVisibility") ==
            std::string::npos ||
        source->find("function AbilitiesManager.SetGraphic") == std::string::npos) {
        return false;
    }

    constexpr std::string_view kGraphicChildChain =
        "A0_0.State.Image:getChildByIndex((1)):getChildByIndex((1))";
    const bool changed = FastApplyLiteralRules(
        source,
        {
            {
                "    local L2_2 = A0_0.State.DescriptionComponent.slide\n"
                "    if g_Menu.CurrWheelNode.ChildMenuItems[g_Menu.CurrWheelNode.HighlightedIdx] then",
                "    local L1_1 = g_Menu.CurrWheelNode.ChildMenuItems[g_Menu.CurrWheelNode.HighlightedIdx]\n"
                "    local L2_2 = A0_0.State.DescriptionComponent.slide\n"
                "    if L1_1 then",
            },
            {
                "    if L1_1 then\n"
                "    if g_Menu.Const.MENU_ITEM_TYPE_MENU_BUTTON == L1_1.MenuItemType then",
                "    if L1_1 then\n"
                "    local L3_3 = A0_0.State.DescriptionComponent:getTimeContext()\n"
                "    if g_Menu.Const.MENU_ITEM_TYPE_MENU_BUTTON == L1_1.MenuItemType then",
            },
            {
                "A0_0.State.DescriptionComponent:getTimeContext().time",
                "L3_3.time",
            },
            {
                "A0_0.State.DescriptionComponent:getTimeContext().paused",
                "L3_3.paused",
            },
            {
                "A0_0.State.DescriptionComponent:getTimeContext():play()",
                "L3_3:play()",
            },
            {
                "    if 2 == L2_2 then\n"
                "    GUI_ComponentSetSlide(A0_0.State.DescriptionComponent, (1))\n"
                "    end\n"
                "    if L3_3.time == 0 then",
                "    if 2 == L2_2 then\n"
                "    GUI_ComponentSetSlide(A0_0.State.DescriptionComponent, (1))\n"
                "    elseif L3_3.time == 0 then",
            },
            {
                "    A0_0.State.Image:getChildByIndex((1)):getChildByIndex((1)).positionu = A1_1\n"
                "    A0_0.State.Image:getChildByIndex((1)):getChildByIndex((1)).positionv = A2_2",
                "    local L3_3 = A0_0.State.Image:getChildByIndex((1)):getChildByIndex((1))\n"
                "    L3_3.positionu = A1_1\n"
                "    L3_3.positionv = A2_2",
            },
            {
                "    A0_0.State.Image:getChildByIndex((1)):getChildByIndex((1)).positionu = A1_1\n"
                "    L3_3.positionv = A2_2",
                "    local L3_3 = A0_0.State.Image:getChildByIndex((1)):getChildByIndex((1))\n"
                "    L3_3.positionu = A1_1\n"
                "    L3_3.positionv = A2_2",
            },
        });

    return changed && source->find(kGraphicChildChain) != std::string::npos;
}

bool FoldFastBoundedHotspotQBEventTranslatorInlineRegistrations(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("function g_QBEventTranslator.onInitialize") == std::string::npos ||
        source->find("registerForEvent") == std::string::npos ||
        source->find("upvalue_0.UIContract") == std::string::npos) {
        return false;
    }

    return FastApplyLiteralRules(
        source,
        {
            {
                "    local r4 = function()\n"
                "      fireEvent(\"OnPressA\", upvalue_0.UIContract)\n"
                "    end\n"
                "    registerForEvent(\"onSelect\", A0_0.UIContract, r4)",
                "    registerForEvent(\"onSelect\", A0_0.UIContract, function()\n"
                "      fireEvent(\"OnPressA\", A0_0.UIContract)\n"
                "    end)",
            },
            {
                "    r4 = function()\n"
                "      fireEvent(\"OnPressB\", upvalue_0.UIContract)\n"
                "    end\n"
                "    registerForEvent(\"onBack\", A0_0.UIContract, r4)",
                "    registerForEvent(\"onBack\", A0_0.UIContract, function()\n"
                "      fireEvent(\"OnPressB\", A0_0.UIContract)\n"
                "    end)",
            },
            {
                "    r4 = function()\n"
                "      fireEvent(\"OnPressUp\", upvalue_0.UIContract)\n"
                "    end\n"
                "    registerForEvent(\"onUp\", A0_0.UIContract, r4)",
                "    registerForEvent(\"onUp\", A0_0.UIContract, function()\n"
                "      fireEvent(\"OnPressUp\", A0_0.UIContract)\n"
                "    end)",
            },
            {
                "    r4 = function()\n"
                "      fireEvent(\"OnPressDown\", upvalue_0.UIContract)\n"
                "    end\n"
                "    registerForEvent(\"onDown\", A0_0.UIContract, r4)",
                "    registerForEvent(\"onDown\", A0_0.UIContract, function()\n"
                "      fireEvent(\"OnPressDown\", A0_0.UIContract)\n"
                "    end)",
            },
        });
}

bool FoldFastBoundedHotspotGUIInputExactShape(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("function GUI.SetDirectionToHeld") == std::string::npos ||
        source->find("function GUI.UpdateInput") == std::string::npos ||
        source->find("function GUI.DoNavigate") == std::string::npos) {
        return false;
    }

    return FastApplyLiteralRules(
        source,
        {
            {
                "getAttribute(g_Param.element, \"String1\") == \"Up\"",
                "\"Up\" == getAttribute(g_Param.element, \"String1\")",
            },
            {
                "getAttribute(g_Param.element, \"String1\") == \"Down\"",
                "\"Down\" == getAttribute(g_Param.element, \"String1\")",
            },
            {
                "getAttribute(g_Param.element, \"String1\") == \"Left\"",
                "\"Left\" == getAttribute(g_Param.element, \"String1\")",
            },
            {
                "getAttribute(g_Param.element, \"String1\") == \"Right\"",
                "\"Right\" == getAttribute(g_Param.element, \"String1\")",
            },
            {"    GUI.Input = GUI.Input\n", ""},
            {"    GUI.Timing = GUI.Input\n", ""},
            {"    GUI.Timing = GUI.Timing\n", ""},
            {"    GUI.State = GUI.State\n", ""},
            {
                "    local r2 = (GUI.Timing.TimeSinceLastNavigate + GUI.State.TimeElapsed)\n"
                "    GUI.Timing.TimeSinceLastNavigate = r2",
                "    GUI.Timing.TimeSinceLastNavigate = GUI.Timing.TimeSinceLastNavigate + GUI.State.TimeElapsed",
            },
            {
                "    if math.abs(GUI.State.LeftThumbStickX) < GUI.Input.THUMBSTICK_RELEASE_THRESHOLD then\n"
                "    if math.abs(GUI.State.LeftThumbStickY) < GUI.Input.THUMBSTICK_RELEASE_THRESHOLD then",
                "    local L1_1 = math.abs(GUI.State.LeftThumbStickX)\n"
                "    local L2_2 = math.abs(GUI.State.LeftThumbStickY)\n"
                "    if L1_1 < GUI.Input.THUMBSTICK_RELEASE_THRESHOLD then\n"
                "    if L2_2 < GUI.Input.THUMBSTICK_RELEASE_THRESHOLD then",
            },
            {
                "GUI.Timing.NavigateInterval <= GUI.Timing.TimeSinceLastNavigate",
                "GUI.Timing.TimeSinceLastNavigate >= GUI.Timing.NavigateInterval",
            },
            {
                "  function GUI.DoNavigate(A0_0)\n"
                "    GUI.Timing.TimeSinceLastNavigate = 0\n"
                "    local L2_2 = (GUI.Input.NavigationCount + (1))\n"
                "    GUI.Input = GUI.Timing\n"
                "    GUI.Input.NavigationCount = L2_2\n"
                "    if 1 < GUI.Input.NavigationCount then\n"
                "    L2_2 = GUI.Timing\n"
                "    GUI.Timing.NavigateInterval = L2_2.SUBSEQUENT_NAVIGATE_INTERVAL\n"
                "    end\n"
                "  end",
                "  function GUI.DoNavigate(A0_0)\n"
                "    GUI.Timing.TimeSinceLastNavigate = 0\n"
                "    GUI.Input.NavigationCount = GUI.Input.NavigationCount + (1)\n"
                "    if 1 < GUI.Input.NavigationCount then\n"
                "    GUI.Timing.NavigateInterval = GUI.Timing.SUBSEQUENT_NAVIGATE_INTERVAL\n"
                "    end\n"
                "  end",
            },
            {
                "GUI.ResetInputState:ResetInputState(GUI)",
                "GUI:ResetInputState()",
            },
            {
                "GUI.Manager.HandleA:HandleA(GUI.Manager)",
                "GUI.Manager:HandleA()",
            },
            {
                "GUI.Manager.HandleB:HandleB(GUI.Manager)",
                "GUI.Manager:HandleB()",
            },
            {
                "GUI.Manager.HandleX:HandleX(GUI.Manager)",
                "GUI.Manager:HandleX()",
            },
            {
                "GUI.Manager.HandleY:HandleY(GUI.Manager)",
                "GUI.Manager:HandleY()",
            },
            {
                "GUI.Input.THUMBSTICK_PUSH_THRESHOLD < GUI.State.LeftThumbStickY * (-1)",
                "(GUI.State.LeftThumbStickY * (-1)) > GUI.Input.THUMBSTICK_PUSH_THRESHOLD",
            },
            {
                "GUI.Input.THUMBSTICK_PUSH_THRESHOLD < GUI.State.LeftThumbStickX * (-1)",
                "(GUI.State.LeftThumbStickX * (-1)) > GUI.Input.THUMBSTICK_PUSH_THRESHOLD",
            },
            {
                "GUI.Input.THUMBSTICK_PUSH_THRESHOLD < GUI.State.LeftThumbStickY",
                "GUI.State.LeftThumbStickY > GUI.Input.THUMBSTICK_PUSH_THRESHOLD",
            },
            {
                "GUI.Input.THUMBSTICK_PUSH_THRESHOLD < GUI.State.LeftThumbStickX",
                "GUI.State.LeftThumbStickX > GUI.Input.THUMBSTICK_PUSH_THRESHOLD",
            },
        });
}

bool FoldFastBoundedHotspotDogCombatExactShape(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("function BehaviourDogCombat.IsStillRunnable") == std::string::npos ||
        source->find("function BehaviourDogCombat.Update") == std::string::npos ||
        source->find("Dog.GetMaulTargetLaunchPosition") == std::string::npos) {
        return false;
    }

    return FastApplyLiteralRules(
        source,
        {
            {
                "    if CombatRegister:GetNumberOfEntitiesAttackingEntity(Dog.GetMaster(A0_0.Entity)) <= 0 then\n"
                "      return false\n"
                "    end\n"
                "    return true",
                "    if CombatRegister:GetNumberOfEntitiesAttackingEntity(Dog.GetMaster(A0_0.Entity)) > 0 then\n"
                "      return true\n"
                "    end\n"
                "    return false",
            },
            {
                "  function BehaviourDogCombat.PrepareToRun(A0_0)\n"
                "    if A0_0:IsStillRunnable() then\n"
                "      return true\n"
                "    end\n"
                "    return false\n"
                "  end",
                "  function BehaviourDogCombat.PrepareToRun(A0_0)\n"
                "    return A0_0:IsStillRunnable()\n"
                "  end",
            },
            {"    local A0_0 = {}\n", ""},
            {"    local A0_0 = A0_0\n", ""},
            {
                "    local L1_1 = Dog.GetMaulTargetEntity(A0_0.Entity)\n"
                "    local L3_3 = Dog.GetMaulTargetFacing(A0_0.Entity)",
                "    local L1_1 = Dog.GetMaulTargetEntity(A0_0.Entity)\n"
                "    local L2_2 = Dog.GetMaulTargetLaunchPosition(A0_0.Entity)\n"
                "    local L3_3 = Dog.GetMaulTargetFacing(A0_0.Entity)",
            },
            {
                "A0_0:IsCloseToPosition(Dog.GetMaulTargetLaunchPosition(A0_0.Entity), 0.20000000298023224)",
                "A0_0:IsCloseToPosition(L2_2, 0.20000000298023224)",
            },
            {
                "    Navigation.ResetTemporaryDestination(A0_0.Entity)\n"
                "    end\n"
                "    A0_0:LookAtEntity(Dog.GetMaulTargetEntity(A0_0.Entity))",
                "    Navigation.ResetTemporaryDestination(A0_0.Entity)\n"
                "    else\n"
                "    A0_0:LookAtEntity(L1_1)",
            },
            {
                "Navigation.SetTemporaryDestination(A0_0.Entity, Dog.GetMaulTargetLaunchPosition(A0_0.Entity), Dog.GetMaulTargetFacing(A0_0.Entity), ETemporaryDestinationSource.TEMPORARY_DESTINATION_SOURCE_ACTION)",
                "Navigation.SetTemporaryDestination(A0_0.Entity, L2_2, L3_3, ETemporaryDestinationSource.TEMPORARY_DESTINATION_SOURCE_ACTION)",
            },
            {
                "A0_0:MoveToPositionNoWait(Dog.GetMaulTargetLaunchPosition(A0_0.Entity), {speed = ENavigationSpeed.NAV_SPEED_RUN})",
                "A0_0:MoveToPositionNoWait(L2_2, {speed = ENavigationSpeed.NAV_SPEED_RUN})",
            },
            {
                "    if Dog.GetMaulTargetLaunchPosition(A0_0.Entity):GetZ() < Dog.ReleaseMaulTarget(A0_0.Entity) - A0_0.Z_DIFFERENCE_TO_GIVE_UP_ON then\n"
                "    Dog.ReleaseMaulTarget(A0_0.Entity)\n"
                "    end\n"
                "    else",
                "    local L4_4 = A0_0.Entity:GetPosition():GetZ() - A0_0.Z_DIFFERENCE_TO_GIVE_UP_ON\n"
                "    if L2_2:GetZ() < L4_4 then\n"
                "    Dog.ReleaseMaulTarget(A0_0.Entity)\n"
                "    end\n"
                "    end\n"
                "    else",
            },
        });
}

bool FoldFastBoundedHotspotChildLeaveHeroExactShape(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("function BehaviourChildLeaveHero.PrepareToRun") == std::string::npos ||
        source->find("function BehaviourChildLeaveHero.Update") == std::string::npos ||
        source->find("FamilyDivorceGroupMind") == std::string::npos) {
        return false;
    }

    return FastApplyLiteralRules(
        source,
        {
            {
                "  function BehaviourChildLeaveHero.PrepareToRun(A0_0)\n"
                "    if PlayerFamily.IsFamilyBreakingUp(GetPlayerHero(), A0_0.Entity) then",
                "  function BehaviourChildLeaveHero.PrepareToRun(A0_0)\n"
                "    local L1_1 = GetPlayerHero()\n"
                "    if PlayerFamily.IsFamilyBreakingUp(L1_1, A0_0.Entity) then",
            },
            {"    local A0_0 = {}\n", ""},
            {
                "    if A0_0.MyGroupMind ~= nil and A0_0.MyGroupMind:MeetUp(A0_0, (Timing.GetWorldFrame() + ((45) * Timing.GetTickRate()))) then",
                "    local L1_1 = Timing.GetWorldFrame() + ((45) * Timing.GetTickRate())\n"
                "    if A0_0.MyGroupMind ~= nil and A0_0.MyGroupMind:MeetUp(A0_0, L1_1) then",
            },
            {
                "    if A0_0.MyGroupMind:GetParent() ~= A0_0.Entity then\n"
                "    A0_0.MyGroupMind:MoveToPosition(A0_0)\n"
                "    end\n"
                "    Debug.Error(\"Somehow the child leaving is the lead!  This is BAD!\")\n"
                "    else",
                "    if A0_0.MyGroupMind:GetParent() ~= A0_0.Entity then\n"
                "    A0_0.MyGroupMind:MoveToPosition(A0_0)\n"
                "    else\n"
                "    Debug.Error(\"Somehow the child leaving is the lead!  This is BAD!\")\n"
                "    end\n"
                "    else",
            },
            {
                "    if not A0_0:MoveToPosition(A0_0.Exit.GetPosition:GetPosition(A0_0.Exit), 2.5) then",
                "    local L2_2 = A0_0.Exit:GetPosition()\n"
                "    if not A0_0:MoveToPosition(L2_2, 2.5) then",
            },
            {
                "A0_0.Entity.Destroy:Destroy(A0_0.Entity)",
                "A0_0.Entity:Destroy()",
            },
            {
                "    local A0_0 = {MyGroupMind = nil}",
                "    A0_0.MyGroupMind = nil",
            },
        });
}

bool FoldFastBoundedHotspotCrateCarryingGroupMindExactShape(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("function CrateCarryingGroupMind.New") == std::string::npos ||
        source->find("function CrateCarryingGroupMind.GetCrateToCarry") == std::string::npos ||
        source->find("function CrateCarryingGroupMind.IsAvailable") == std::string::npos) {
        return false;
    }

    return FastApplyLiteralRules(
        source,
        {
            {"    o.CrateStacks = A0_0\n", "    o.CrateStacks = {}\n"},
            {"    o.EntityAttachedTo = {}\n", "    o.EntityAttachedTo = A1_1\n"},
            {
                "  function CrateCarryingGroupMind.AddEntity(A0_0, A1_1, A2_2)\n"
                "    if not A2_2 then\n"
                "    end\n"
                "    GroupMind.AddEntity(A0_0, A1_1, A2_2)\n"
                "  end",
                "  function CrateCarryingGroupMind.AddEntity(A0_0, A1_1, A2_2)\n"
                "    if not A2_2 then\n"
                "    A2_2 = A0_0.CRATE_CARRIER_WALK_TO_CRATE\n"
                "    end\n"
                "    GroupMind.AddEntity(A0_0, A1_1, A2_2)\n"
                "  end",
            },
            {
                "    local A0_0 = {}\n    A0_0.Crates = A1_1",
                "    A0_0.Crates = A1_1",
            },
            {
                "    local A0_0 = A0_0\n    A0_0.CrateStacks = A1_1",
                "    A0_0.CrateStacks = A1_1",
            },
            {
                "    if CrateStack.GetCrate(r6, 0) then\n"
                "    if r7:IsAlive() then",
                "    if r7 then\n"
                "    if r7:IsAlive() then",
            },
            {
                "    A0_0.UnavailableResources = {UnavailableResources = {}}\n",
                "",
            },
            {
                "  function CrateCarryingGroupMind.IsAvailable(A0_0, A1_1)\n"
                "    if not A0_0.UnavailableResources[GetIDFromEntity(A1_1)] then\n"
                "      return true\n"
                "    end\n"
                "    return false\n"
                "  end",
                "  function CrateCarryingGroupMind.IsAvailable(A0_0, A1_1)\n"
                "    return not A0_0.UnavailableResources[GetIDFromEntity(A1_1)]\n"
                "  end",
            },
        });
}

bool FoldFastBoundedHotspotFollowGroupMindExactShape(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("function FollowGroupMind.New") == std::string::npos ||
        source->find("function FollowGroupMind.AddEntity") == std::string::npos ||
        source->find("function FollowGroupMind.GetAngle") == std::string::npos) {
        return false;
    }

    return FastApplyLiteralRules(
        source,
        {
            {
                "  function FollowGroupMind.New(A0_0, A1_1)\n"
                "    GroupMind.New(A0_0).EntityAttachedTo = A1_1\n"
                "    local r2 = {}\n"
                "    r2.Followers = r2\n"
                "    r2.angle = 135\n"
                "    r2.Prongs = {\n"
                "      {distance = 2, occupied = false, angle = 225},\n"
                "      {distance = 2, occupied = false, angle = 180},\n"
                "      {distance = 5, occupied = false, angle = 157},\n"
                "      {distance = 3.5, occupied = false, angle = 202},\n"
                "      {distance = 3.5, occupied = false},\n"
                "    }\n"
                "    return r2\n"
                "  end",
                "  function FollowGroupMind.New(A0_0, A1_1)\n"
                "    local L2_2 = GroupMind.New(A0_0)\n"
                "    L2_2.EntityAttachedTo = A1_1\n"
                "    L2_2.Followers = {}\n"
                "    L2_2.Prongs = {\n"
                "      {angle = 135, distance = 2, occupied = false},\n"
                "      {angle = 225, distance = 2, occupied = false},\n"
                "      {angle = 180, distance = 5, occupied = false},\n"
                "      {angle = 157, distance = 3.5, occupied = false},\n"
                "      {angle = 202, distance = 3.5, occupied = false},\n"
                "    }\n"
                "    return L2_2\n"
                "  end",
            },
            {
                "    if r8.occupied == false then\n"
                "    L3_3 = r7\n"
                "    end",
                "    if r8.occupied == false then\n"
                "    L3_3 = r7\n"
                "    break\n"
                "    end",
            },
            {
                "    table.insert(A0_0.Followers, {entity = A1_1, prong = L3_3})\n"
                "    A0_0.Prongs[L3_3] = {occupied = true}",
                "    local L4_4 = {}\n"
                "    L4_4.entity = A1_1\n"
                "    L4_4.prong = L3_3\n"
                "    table.insert(A0_0.Followers, L4_4)\n"
                "    A0_0.Prongs[L3_3].occupied = true",
            },
            {
                "    A0_0.Prongs[r6.prong] = {occupied = false}\n"
                "    table.remove(A0_0.Followers, r5)",
                "    A0_0.Prongs[r6.prong].occupied = false\n"
                "    table.remove(A0_0.Followers, r5)\n"
                "    break",
            },
            {
                "    local r2, r3 = nil\n"
                "    return r2, r3",
                "    return nil, nil",
            },
        });
}

bool FoldFastBoundedHotspotDeadRiserFollowExactShape(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("function BehaviourDeadRiserFollow.IsStillRunnable") == std::string::npos ||
        source->find("function BehaviourDeadRiserFollow.Update") == std::string::npos ||
        source->find("function BehaviourDeadRiserIdle.Update") == std::string::npos ||
        source->find("DeadRiser.GetCastingEntity") == std::string::npos) {
        return false;
    }

    return FastApplyLiteralRules(
        source,
        {
            {
                "  function BehaviourDeadRiserFollow.IsStillRunnable(A0_0)\n"
                "    if A0_0.Target and A0_0.Target.IsAlive:IsAlive(A0_0.Target) then\n"
                "    return false\n"
                "    end\n"
                "    return A0_0.CastingEntity or not A0_0.CastingEntity.IsAlive:IsAlive(A0_0.CastingEntity)\n"
                "  end",
                "  function BehaviourDeadRiserFollow.IsStillRunnable(A0_0)\n"
                "    if A0_0.Target and A0_0.Target:IsAlive() then\n"
                "    return false\n"
                "    end\n"
                "    if not A0_0.CastingEntity or not A0_0.CastingEntity:IsAlive() then\n"
                "    return false\n"
                "    end\n"
                "    return true\n"
                "  end",
            },
            {
                "  function BehaviourDeadRiserFollow.PrepareToRun(A0_0)\n"
                "    local A0_0 = {}\n"
                "    A0_0.CastingEntity = DeadRiser.GetCastingEntity(A0_0.Entity)\n"
                "    return A0_0:IsStillRunnable()\n"
                "  end",
                "  function BehaviourDeadRiserFollow.PrepareToRun(A0_0)\n"
                "    A0_0.CastingEntity = DeadRiser.GetCastingEntity(A0_0.Entity)\n"
                "    if A0_0:IsStillRunnable() then\n"
                "    return true\n"
                "    end\n"
                "    return false\n"
                "  end",
            },
            {
                "  function BehaviourDeadRiserFollow.Update(A0_0)\n"
                "    while A0_0:IsStillRunnable() do\n"
                "    A0_0:MoveToEntityNoWaitForFollowing(A0_0.CastingEntity, 2)\n"
                "    if CombatRegister:FindTargetForCombatant(A0_0.Entity) then\n"
                "    if CombatRegister:FindTargetForCombatant(A0_0.Entity):IsAlive() then\n"
                "      break\n"
                "    end\n"
                "    if CombatRegister:FindTargetForCombatant(A0_0.Entity) == A0_0.Target then\n"
                "      break\n"
                "    end\n"
                "    if CombatRegister:FindTargetForCombatant(A0_0.Entity):IsAlive() then\n"
                "    if CombatRegister:FindTargetForCombatant(A0_0.Entity) ~= A0_0.Target then\n"
                "    local A0_0 = {}\n"
                "    A0_0.Target = CombatRegister:FindTargetForCombatant(A0_0.Entity)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    coroutine.yield()\n"
                "    end\n"
                "    Navigation.StopMoving(A0_0.Entity)\n"
                "    Navigation.MoveToPosition(A0_0.Entity, {position = A0_0.Entity:GetPosition(), radius = 1})\n"
                "  end",
                "  function BehaviourDeadRiserFollow.Update(A0_0)\n"
                "    local L1_1 = ENavigationSpeed.NAV_SPEED_WALK\n"
                "    while A0_0:IsStillRunnable() do\n"
                "    A0_0:MoveToEntityNoWaitForFollowing(A0_0.CastingEntity, 2)\n"
                "    local L2_2 = CombatRegister:FindTargetForCombatant(A0_0.Entity)\n"
                "    if L2_2 and L2_2:IsAlive() then\n"
                "    if L2_2 ~= A0_0.Target then\n"
                "    A0_0.Target = L2_2\n"
                "    break\n"
                "    end\n"
                "    end\n"
                "    coroutine.yield()\n"
                "    end\n"
                "    Navigation.StopMoving(A0_0.Entity)\n"
                "    local L2_2 = {}\n"
                "    L2_2.position = A0_0.Entity:GetPosition()\n"
                "    L2_2.radius = 1\n"
                "    Navigation.MoveToPosition(A0_0.Entity, L2_2)\n"
                "  end",
            },
            {
                "  function BehaviourDeadRiserIdle.IsStillRunnable(A0_0)\n"
                "    if A0_0.Target and A0_0.Target.IsAlive:IsAlive(A0_0.Target) then\n"
                "    return false\n"
                "    end\n"
                "    return GetDistanceBetweenEntities(A0_0.Entity, A0_0.CastingEntity) <= DeadRiserFollowDistance\n"
                "  end",
                "  function BehaviourDeadRiserIdle.IsStillRunnable(A0_0)\n"
                "    if A0_0.Target and A0_0.Target:IsAlive() then\n"
                "    return false\n"
                "    end\n"
                "    if GetDistanceBetweenEntities(A0_0.Entity, A0_0.CastingEntity) > DeadRiserFollowDistance then\n"
                "    return false\n"
                "    end\n"
                "    return true\n"
                "  end",
            },
            {
                "  function BehaviourDeadRiserIdle.Update(A0_0)\n"
                "    Navigation.StopMoving(A0_0.Entity)\n"
                "    Navigation.MoveToPosition(A0_0.Entity, {position = A0_0.Entity.GetPosition:GetPosition(A0_0.Entity), radius = 1})\n"
                "    while true do\n"
                "    coroutine.yield()\n"
                "    end\n"
                "  end",
                "  function BehaviourDeadRiserIdle.Update(A0_0)\n"
                "    Navigation.StopMoving(A0_0.Entity)\n"
                "    local L1_1 = {}\n"
                "    L1_1.position = A0_0.Entity:GetPosition()\n"
                "    L1_1.radius = 1\n"
                "    Navigation.MoveToPosition(A0_0.Entity, L1_1)\n"
                "    while A0_0:IsStillRunnable() do\n"
                "    coroutine.yield()\n"
                "    end\n"
                "  end",
            },
        });
}

bool FoldFastBoundedHotspotCombatGetHelpExactShape(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("function BehaviourCombatGetHelp.PrepareToRun") == std::string::npos ||
        source->find("function BehaviourCombatGetHelp.Update") == std::string::npos ||
        source->find("function BehaviourCombatGetHelp.FastExit") == std::string::npos ||
        source->find("HowMuchHelpDoWeNeed") == std::string::npos) {
        return false;
    }

    return FastApplyLiteralRules(
        source,
        {
            {
                "    local A0_0 = {}\n"
                "    A0_0.CombatInfo.IgnoreChaseRadius = true",
                "    A0_0.CombatInfo = r1\n"
                "    A0_0.CombatInfo.IgnoreChaseRadius = true",
            },
            {
                "  function BehaviourCombatGetHelp.Update(A0_0)\n"
                "    A0_0:WaitForCurrentActionToFinish()\n"
                "    local L1_1 = Timing.GetWorldFrame()\n"
                "    local L2_2 = GetPlayerHero()\n"
                "    if A0_0:MoveToPositionNoWait(A0_0.CombatInfo.Group.RetreatTarget, {speed = ENavigationSpeed.NAV_SPEED_SPRINT, radius = 1}) then\n"
                "    while Navigation.IsPathfinding(A0_0.Entity) and true do\n"
                "    if A0_0.CombatInfo.Group.HowMuchHelpDoWeNeed:HowMuchHelpDoWeNeed(A0_0.CombatInfo.Group).Group:HowMuchHelpDoWeNeed() <= 0 then\n"
                "    A0_0.CombatInfo.Group:SetState(A0_0.Entity, CombatGroupMind.States.ATTACK)\n"
                "    return\n"
                "    end\n"
                "    if A0_0:IsCloseToPosition(A0_0.CombatInfo.Group.RetreatTarget, (1)) then\n"
                "    if not Perception.IsVisibleFrom(A0_0.Entity, L2_2) then\n"
                "    break\n"
                "    end\n"
                "    A0_0.CombatInfo.Group:SetState(A0_0.Entity, CombatGroupMind.States.ATTACK)\n"
                "    return\n"
                "    else\n"
                "    local L4_4 = (L4_4 - L1_1)\n"
                "    if L1_1 - L1_1 >= 2 then\n"
                "    if not Perception.IsVisibleFrom(A0_0.Entity, L2_2) then\n"
                "    end\n"
                "    end\n"
                "    L4_4 = L4_4.yield\n"
                "    L4_4()\n"
                "    end\n"
                "    end\n"
                "    if A0_0.CombatInfo.Group.HowMuchHelpDoWeNeed:HowMuchHelpDoWeNeed(L4_4) > 0 then\n"
                "    for r9, r10 in pairs(pairs) do\n"
                "    Creature.PlaceNear(r10, A0_0.Entity.GetPosition:GetPosition(A0_0.Entity))\n"
                "    if CombatRegister:GetCombatInfo(r10) then\n"
                "    local r11 = {IgnoreChaseRadius = true}\n"
                "    end\n"
                "    Perception.MakeAwareOf(r10, L2_2)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    local L3_3 = L3_3.Group\n"
                "    if L3_3 then\n"
                "    L3_3:SetState(A0_0.Entity, L6_6.ATTACK)\n"
                "    end\n"
                "  end",
                "  function BehaviourCombatGetHelp.Update(A0_0)\n"
                "    A0_0:WaitForCurrentActionToFinish()\n"
                "    local L1_1 = Timing.GetWorldFrame()\n"
                "    local L2_2 = GetPlayerHero()\n"
                "    if A0_0:MoveToPositionNoWait(A0_0.CombatInfo.Group.RetreatTarget, {speed = ENavigationSpeed.NAV_SPEED_SPRINT, radius = 1}) then\n"
                "    local L3_3 = true\n"
                "    while Navigation.IsPathfinding(A0_0.Entity) and L3_3 do\n"
                "    if A0_0.CombatInfo.Group:HowMuchHelpDoWeNeed() <= 0 then\n"
                "    A0_0.CombatInfo.Group:SetState(A0_0.Entity, CombatGroupMind.States.ATTACK)\n"
                "    return\n"
                "    end\n"
                "    if A0_0:IsCloseToPosition(A0_0.CombatInfo.Group.RetreatTarget, 1) then\n"
                "    if Perception.IsVisibleFrom(A0_0.Entity, L2_2) then\n"
                "    A0_0.CombatInfo.Group:SetState(A0_0.Entity, CombatGroupMind.States.ATTACK)\n"
                "    return\n"
                "    end\n"
                "    break\n"
                "    end\n"
                "    if Timing.GetWorldFrame() - L1_1 >= 2 then\n"
                "    if not Perception.IsVisibleFrom(A0_0.Entity, L2_2) then\n"
                "    break\n"
                "    end\n"
                "    L1_1 = Timing.GetWorldFrame()\n"
                "    end\n"
                "    coroutine.yield()\n"
                "    end\n"
                "    local L4_4 = A0_0.CombatInfo.Group:HowMuchHelpDoWeNeed()\n"
                "    if L4_4 > 0 then\n"
                "    local L5_5 = {}\n"
                "    L5_5 = A0_0.CombatInfo.Group:GetReinforcements(Creature.GetCreatureGeneratorLevel(A0_0.Entity), L4_4)\n"
                "    for r9, r10 in pairs(L5_5) do\n"
                "    Creature.PlaceNear(r10, A0_0.Entity:GetPosition())\n"
                "    local r11 = CombatRegister:GetCombatInfo(r10)\n"
                "    if r11 then\n"
                "    r11.IgnoreChaseRadius = true\n"
                "    end\n"
                "    Perception.MakeAwareOf(r10, L2_2)\n"
                "    end\n"
                "    end\n"
                "    end\n"
                "    if A0_0.CombatInfo.Group then\n"
                "    A0_0.CombatInfo.Group:SetState(A0_0.Entity, CombatGroupMind.States.ATTACK)\n"
                "    end\n"
                "  end",
            },
        });
}

bool FoldFastBoundedHotspotQuestsManagerExactShape(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("function g_QuestsManager.DisplayQuestStart") == std::string::npos ||
        source->find("function g_QuestsManager.DisplayAllQuests") == std::string::npos ||
        source->find("WheelGraph_ResetToRoot") == std::string::npos) {
        return false;
    }

    return FastApplyLiteralRules(
        source,
        {
            {
                "  g_QuestsManager.State = {}\n"
                "  g_QuestsManager.State = {Image = g_QuestsManager:GetObjectReference(\"Image\"), Name = g_QuestsManager:GetObjectReference(\"Name\"), Description = g_QuestsManager:GetObjectReference(\"Description\")}\n"
                "  g_QuestsManager.State.DescriptionComponent = g_QuestsManager:GetObjectReference(\"DescriptionComponent\")\n"
                "  g_QuestsManager.State.LastUpdateTime = Presentation.runTime\n"
                "  g_QuestsManager = {Const = g_QuestsManager.State, State = g_QuestsManager, State = g_QuestsManager}",
                "  g_QuestsManager.State = {}\n"
                "  g_QuestsManager.State.Image = g_QuestsManager:GetObjectReference(\"Image\")\n"
                "  g_QuestsManager.State.Name = g_QuestsManager:GetObjectReference(\"Name\")\n"
                "  g_QuestsManager.State.Description = g_QuestsManager:GetObjectReference(\"Description\")\n"
                "  g_QuestsManager.State.DescriptionComponent = g_QuestsManager:GetObjectReference(\"DescriptionComponent\")\n"
                "  g_QuestsManager.State.LastUpdateTime = Presentation.runTime\n"
                "  g_QuestsManager.Const = {}\n"
                "  g_QuestsManager.State = {}",
            },
            {
                "    if 0 < GUI.State.TilReadyToUpdate then",
                "    if GUI.State.TilReadyToUpdate > 0 then",
            },
            {
                "    g_QuestsManager.State.Title = {textString = A1_1}",
                "    g_QuestsManager.State.Title.textString = A1_1",
            },
            {
                "    g_QuestsManager.State.ObjectivesAndBoasts = g_QuestsManager.State.Title\n"
                "    g_QuestsManager.State.ObjectivesAndBoasts.textString = A1_1",
                "    g_QuestsManager.State.ObjectivesAndBoasts.textString = A1_1",
            },
            {
                "    g_QuestsManager.State.Description = g_QuestsManager.State.ObjectivesAndBoasts\n"
                "    g_QuestsManager.State.Description.textString = A1_1",
                "    g_QuestsManager.State.Description.textString = A1_1",
            },
            {
                "    g_QuestsManager.State.IconGraphic:getChildByIndex((1)):getChildByIndex((1)).positionu = A1_1\n"
                "    g_QuestsManager.State.IconGraphic:getChildByIndex((1)):getChildByIndex((1)).positionv = A2_2",
                "    local L3_3 = g_QuestsManager.State.IconGraphic:getChildByIndex(1):getChildByIndex(1)\n"
                "    L3_3.positionu = A1_1\n"
                "    L3_3.positionv = A2_2",
            },
            {
                "    g_QuestsManager.State.Title = {textColor = g_Theme.GetPageTitleColor:GetPageTitleColor(g_Theme)}\n"
                "    g_QuestsManager:SetTitle(GUI:GetText((GUI:GetText(\"TEXT_NEW_QUEST_TITLE\") .. \" - \" .. GUI:GetText(g_Param.String1))))",
                "    local L1_1 = GUI:GetText(\"TEXT_NEW_QUEST_TITLE\")\n"
                "    g_QuestsManager.State.Title.textColor = g_Theme:GetPageTitleColor()\n"
                "    g_QuestsManager:SetTitle(GUI:GetText((L1_1 .. \" - \" .. GUI:GetText(g_Param.String1))))",
            },
            {
                "    g_QuestsManager.State.Title = {textColor = g_Theme.GetPageTitleColor:GetPageTitleColor(g_Theme)}\n"
                "    g_QuestsManager:SetTitle(GUI:GetText((GUI:GetText(\"TEXT_COMPLETED_A_QUEST_TITLE\") .. \" - \" .. GUI:GetText(g_Param.String1))))",
                "    local L1_1 = GUI:GetText(\"TEXT_COMPLETED_A_QUEST_TITLE\")\n"
                "    g_QuestsManager.State.Title.textColor = g_Theme:GetPageTitleColor()\n"
                "    g_QuestsManager:SetTitle(GUI:GetText((L1_1 .. \" - \" .. GUI:GetText(g_Param.String1))))",
            },
            {
                "    g_QuestsManager.State.Title = {textColor = g_Theme.GetNegativeTextColor:GetNegativeTextColor(g_Theme)}\n"
                "    g_QuestsManager:SetTitle(GUI:GetText((GUI:GetText(\"TEXT_FAILED_A_QUEST_TITLE\") .. \" - \" .. GUI:GetText(g_Param.String1))))",
                "    local L1_1 = GUI:GetText(\"TEXT_FAILED_A_QUEST_TITLE\")\n"
                "    g_QuestsManager.State.Title.textColor = g_Theme:GetNegativeTextColor()\n"
                "    g_QuestsManager:SetTitle(GUI:GetText((L1_1 .. \" - \" .. GUI:GetText(g_Param.String1))))",
            },
            {
                "    g_QuestsManager.State = {Title = {textString = GUI:GetText(\"GUI_QUESTS_SCREEN\")}, Title = g_QuestsManager.State.Title}\n"
                "    g_QuestsManager.State.Title.textColor = g_Theme.GetPageTitleColor:GetPageTitleColor(g_Theme)",
                "    g_QuestsManager.State.Title.textString = GUI:GetText(\"GUI_QUESTS_SCREEN\")\n"
                "    g_QuestsManager.State.Title.textColor = g_Theme:GetPageTitleColor()",
            },
        });
}

bool FoldFastBoundedHotspotShardExactShape(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("function BehaviourShard.PrepareToRun") == std::string::npos ||
        source->find("function BehaviourShard.Update") == std::string::npos ||
        source->find("function BehaviourShard.PerformMeleeAttack") == std::string::npos ||
        source->find("Shard.StartBeamAttack") == std::string::npos) {
        return false;
    }

    const std::size_t start =
        source->find("  function BehaviourShard.PrepareToRun(A0_0)\n");
    if (start == std::string::npos ||
        source->find("    local A0_0 = {Stuck = false}", start) == std::string::npos ||
        source->find("Action.BreakSequence(A0_0.Entity, Action.SetCurrentAction(A0_0.Entity, {}))",
                     start) == std::string::npos) {
        return false;
    }

    source->replace(start, std::string::npos, R"REFII_SHARD(  function BehaviourShard.PrepareToRun(A0_0)
    if not Creature.DoesCreatureHaveSpecialisation(A0_0.Entity, ECreatureSpecialisation.CREATURE_SPECIALISE_EXTRA_RANGED_ATTACK) then
    if Combat.IsAvailable(A0_0.Entity) then
    if Combat.InValidModeToFight(A0_0.Entity) then
    A0_0.CombatInfo = CombatRegister:GetCombatInfo(A0_0.Entity)
    if A0_0.CombatInfo then
    local L1_1 = CombatRegister:FindTargetForCombatant(A0_0.Entity)
    if L1_1 then
    A0_0.Target = L1_1
    A0_0.NextTargetCheckFrame = (Timing.GetWorldFrame() + Perception.GetAttentionSpan(A0_0.Entity))
    A0_0.LastAttack = EShardStance.SHARD_NEUTRAL
    A0_0.LastStanceSwitch = Timing.GetWorldFrame()
    A0_0.AttackPending = false
    A0_0.DesiredNextAttack = EShardStance.SHARD_NEUTRAL
    A0_0.Defending = false
    A0_0.Stuck = false
    return true
    end
    end
    end
    end
    end
    return false
  end
  function BehaviourShard.Update(A0_0)
    local L1_1 = true
    A0_0:WaitForCurrentActionToFinish()
    CombatRegister:StartAttacking(A0_0.Entity, A0_0.Target)
    Shard.SetSpeedLevel(A0_0.Entity, ENavigationSpeed.NAV_SPEED_WALK)
    while L1_1 do
    A0_0:WaitForCurrentActionToFinish()
    A0_0:CheckForBetterTarget()
    if not CombatRegister:IsValidTarget(A0_0.Entity, A0_0.Target) then
    L1_1 = false
    end
    if A0_0:DoesCombatGroupMindStatePreventRunning() then
    L1_1 = false
    end
    A0_0.Defending = Shard.IsForcedShut(A0_0.Entity)
    if L1_1 then
    if Combat.InValidModeToFight(A0_0.Entity) then
    if A0_0.Stuck then
    A0_0:RecoverFromStuck()
    end
    if not A0_0.AttackPending then
    if Shard.GetCurrentStance(A0_0.Entity) ~= EShardStance.SHARD_NEUTRAL then
    Shard.SetVulnerability(A0_0.Entity, false)
    if Shard.GetCurrentStance(A0_0.Entity) == EShardStance.SHARD_BEAM_ATTACK then
    Shard.SetCurrentStanceWithSpeed(A0_0.Entity, EShardStance.SHARD_NEUTRAL, BehaviourShard.RangedToNeutralSpeedScale)
    else
    Shard.SetCurrentStanceWithSpeed(A0_0.Entity, EShardStance.SHARD_NEUTRAL, BehaviourShard.MeleeToNeutralSpeedScale)
    end
    A0_0:WaitForCurrentActionToFinish()
    A0_0.LastStanceSwitch = Timing.GetWorldFrame()
    end
    end
    if Combat.GetCanFight(A0_0.Entity) then
    if Shard.GetCurrentStance(A0_0.Entity) == EShardStance.SHARD_NEUTRAL then
    local L2_2 = false
    if not A0_0.Defending then
    if A0_0.Target then
    if A0_0.Target:IsAlive() then
    if not IsEntityKnockedDown(A0_0.Target) then
    local L3_3 = A0_0.Target:GetPosition() - A0_0.Entity:GetPosition()
    local L4_4 = L3_3:GetLength() - 0.5
    A0_0.DesiredNextAttack = EShardStance.SHARD_NEUTRAL
    if A0_0.LastAttack == EShardStance.SHARD_BEAM_ATTACK then
    A0_0.DesiredNextAttack = EShardStance.SHARD_MELEE_ATTACK
    else
    A0_0.DesiredNextAttack = EShardStance.SHARD_BEAM_ATTACK
    end
    if A0_0.DesiredNextAttack == EShardStance.SHARD_MELEE_ATTACK then
    L2_2 = true
    elseif A0_0.DesiredNextAttack == EShardStance.SHARD_BEAM_ATTACK then
    if L4_4 < BehaviourShard.MaxBeamAttackRange then
    L2_2 = true
    end
    end
    end
    end
    end
    end
    if not L2_2 then
    Shard.MoveToPos(A0_0.Entity, A0_0:GetMovePosition(A0_0.DesiredNextAttack))
    else
    if A0_0.DesiredNextAttack == EShardStance.SHARD_BEAM_ATTACK then
    A0_0:WaitUntilFacingTarget()
    if not A0_0.Defending then
    Shard.TrackTarget(A0_0.Entity, A0_0.Target, A0_0.BeamTargetInFront, BehaviourShard.BeamLookAhead, BehaviourShard.BeamCosmeticRotateSpeed)
    end
    end
    if not A0_0.Defending then
    if A0_0.DesiredNextAttack == EShardStance.SHARD_BEAM_ATTACK then
    Shard.SetCurrentStanceWithSpeed(A0_0.Entity, A0_0.DesiredNextAttack, BehaviourShard.NeutralToRangedSpeedScale)
    else
    Shard.SetCurrentStanceWithSpeed(A0_0.Entity, A0_0.DesiredNextAttack, BehaviourShard.NeutralToMeleeSpeedScale)
    end
    A0_0.AttackPending = true
    while Action.IsPerformingAnyAction(A0_0.Entity) do
    Shard.MoveToPos(A0_0.Entity, A0_0:GetMovePosition(A0_0.DesiredNextAttack))
    coroutine.yield()
    end
    Shard.SetVulnerability(A0_0.Entity, true)
    A0_0.LastStanceSwitch = Timing.GetWorldFrame()
    end
    end
    end
    end
    if A0_0.AttackPending then
    local L2_2 = Shard.GetCurrentStance(A0_0.Entity)
    local L3_3 = BehaviourShard.Timings[L2_2]
    Shard.MoveToPos(A0_0.Entity, A0_0:GetMovePosition(A0_0.DesiredNextAttack))
    if Timing.GetSecondsSince(A0_0.LastStanceSwitch) > L3_3 then
    if L2_2 == EShardStance.SHARD_MELEE_ATTACK then
    A0_0:PerformMeleeAttack()
    elseif L2_2 == EShardStance.SHARD_BEAM_ATTACK then
    A0_0:PerformBeamAttack()
    end
    A0_0.AttackPending = false
    A0_0.LastAttack = L2_2
    end
    end
    end
    end
    coroutine.yield()
    end
  end
  function BehaviourShard.FastExit(A0_0, A1_1)
    CombatRegister:StopAttacking(A0_0.Entity)
  end
  function BehaviourShard.SlowExit(A0_0, A1_1)
    A0_0:WaitForCurrentActionToFinish()
    if Shard.GetCurrentStance(A0_0.Entity) ~= EShardStance.SHARD_NEUTRAL then
    Shard.SetCurrentStance(A0_0.Entity, EShardStance.SHARD_NEUTRAL)
    A0_0:WaitForCurrentActionToFinish()
    end
  end
  function BehaviourShard.SetSpeedFromDistance(A0_0, A1_1, A2_2)
    local L3_3 = BehaviourShard.DistanceCapForSpeed
    local L4_4 = A1_1 / L3_3
    if A2_2 == EShardStance.SHARD_MELEE_ATTACK then
    L4_4 = L4_4 * BehaviourShard.MeleeSpeedFactor
    end
    if L4_4 > 1 then
    L4_4 = 1
    elseif L4_4 < 0.059999998658895493 then
    L4_4 = 0.059999998658895493
    end
    Shard.SetSpeedPercent(A0_0.Entity, L4_4)
  end
  function BehaviourShard.CheckForBetterTarget(A0_0)
    if Timing.GetWorldFrame() > A0_0.NextTargetCheckFrame then
    local L1_1 = CombatRegister:FindTargetForCombatant(A0_0.Entity)
    if L1_1 then
    if L1_1:IsAlive() then
    if L1_1 ~= A0_0.Target then
    A0_0.Target = L1_1
    CombatRegister:StartAttacking(A0_0.Entity, L1_1)
    end
    end
    end
    A0_0.NextTargetCheckFrame = (Timing.GetWorldFrame() + Perception.GetAttentionSpan(A0_0.Entity))
    return true
    end
    return false
  end
  function BehaviourShard.GetMovePosition(A0_0, A1_1)
    A0_0:CheckIfStuck()
    if A0_0.Target and A0_0.Target:IsAlive() then
    local L2_2 = A0_0.Target:GetPosition() - A0_0.Entity:GetPosition()
    local L3_3 = L2_2:Normalise()
    local L4_4 = A0_0.Entity:GetPosition()
    A0_0:SetSpeedFromDistance(L3_3, A1_1)
    if A1_1 == EShardStance.SHARD_BEAM_ATTACK then
    if BehaviourShard.IdealBeamAttackRange < L3_3 then
    L4_4 = A0_0.Target:GetPosition() - L2_2 * BehaviourShard.IdealBeamAttackRange
    end
    else
    if A1_1 == EShardStance.SHARD_MELEE_ATTACK then
    L4_4 = A0_0.Target:GetPosition()
    end
    end
    local L5_5 = Navigation.GetBestHeightFor(L4_4)
    L4_4:SetZ(L5_5)
    return L4_4
    else
    return A0_0.Entity:GetPosition()
    end
  end
  function BehaviourShard.PerformBeamAttack(A0_0)
    Shard.StartBeamAttack(A0_0.Entity, A0_0.BeamTargetInFront, BehaviourShard.MaxBeamAttackRange, BehaviourShard.BeamDuration, BehaviourShard.BeamSpeed, BehaviourShard.BeamDamagePerSecond, (0))
    while Shard.IsPerformingBeamAttack(A0_0.Entity) and not A0_0.Defending do
    coroutine.yield()
    end
    Shard.StopTrackingTarget(A0_0.Entity)
  end
  function BehaviourShard.WaitUntilFacingTarget(A0_0)
    local L1_1 = false
    local L2_2 = false
    local L3_3 = A0_0.Entity:GetPosition()
    local L4_4 = A0_0.Entity:GetPosition()
    while not L1_1 and not L2_2 and not A0_0.Defending do
    L3_3 = A0_0.Target:GetPosition() - A0_0.Entity:GetPosition()
    L4_4 = Physics.GetFacingVector(A0_0.Entity)
    local L5_5 = L3_3:Normalise()
    local L6_6 = L3_3:GetDot(L4_4)
    if math.cos(math.rad(90)) < L6_6 then
    L1_1 = true
    elseif L6_6 < math.cos(math.rad(135)) then
    L2_2 = true
    end
    Shard.MoveToPos(A0_0.Entity, A0_0:GetMovePosition(A0_0.DesiredNextAttack))
    coroutine.yield()
    end
    A0_0.BeamTargetInFront = L1_1
  end
  function BehaviourShard.PerformMeleeAttack(A0_0)
    local L1_1 = {
      Type = EScriptableAction.LOOP,
      IntoAction = {
        Type = EScriptableAction.SHARD_PLAY_ANIMATION,
        Anim = "AttackIdleInto",
      },
      LoopAction = {
        Type = EScriptableAction.SHARD_PLAY_ANIMATION,
        Anim = "AttackIdle",
      },
      NumLoops = 0,
    }
    local L2_2 = Timing.GetWorldFrame()
    local L3_3 = Action.SetCurrentAction(A0_0.Entity, L1_1)
    if L3_3 then
    while Timing.GetSecondsSince(L2_2) < BehaviourShard.MeleeAttackPreAttackTime and not A0_0.Defending do
    Shard.MoveToPos(A0_0.Entity, A0_0:GetMovePosition(EShardStance.SHARD_MELEE_ATTACK))
    coroutine.yield()
    end
    Action.BreakSequence(A0_0.Entity, L3_3)
    end
    A0_0:WaitForCurrentActionToFinish()
    local L4_4 = {
      Type = EScriptableAction.LOOP,
      IntoAction = {
        Type = EScriptableAction.SHARD_PLAY_ANIMATION,
        Anim = "AttackingInto",
      },
      LoopAction = {
        Type = EScriptableAction.SHARD_MELEE_ATTACK,
        Anim = "AttackingLoop",
      },
      OutOfAction = {
        Type = EScriptableAction.SHARD_PLAY_ANIMATION,
        Anim = "AttackingOutof",
      },
      NumLoops = 0,
    }
    L2_2 = Timing.GetWorldFrame()
    L3_3 = Action.SetCurrentAction(A0_0.Entity, L4_4)
    if L3_3 then
    while Timing.GetSecondsSince(L2_2) < BehaviourShard.MeleeAttackLength and not A0_0.Defending and A0_0.Target:IsAlive() and not IsEntityKnockedDown(A0_0.Target) do
    Shard.MoveToPos(A0_0.Entity, A0_0:GetMovePosition(EShardStance.SHARD_MELEE_ATTACK))
    coroutine.yield()
    end
    Action.BreakSequence(A0_0.Entity, L3_3)
    end
    A0_0:WaitForCurrentActionToFinish()
  end
  function BehaviourShard.CheckIfStuck(A0_0)
    if Shard.GetStuckCounter(A0_0.Entity) > 40 then
    A0_0.Stuck = true
    end
  end
  function BehaviourShard.RecoverFromStuck(A0_0)
    A0_0.Stuck = false
    if A0_0.Target then
    if A0_0.Target:IsAlive() then
    Shard.StopMoving(A0_0.Entity)
    Shard.SetVulnerability(A0_0.Entity, false)
    if Shard.GetCurrentStance(A0_0.Entity) == EShardStance.SHARD_BEAM_ATTACK then
    Shard.SetCurrentStanceWithSpeed(A0_0.Entity, EShardStance.SHARD_NEUTRAL, BehaviourShard.RangedToNeutralSpeedScale)
    else
    Shard.SetCurrentStanceWithSpeed(A0_0.Entity, EShardStance.SHARD_NEUTRAL, BehaviourShard.MeleeToNeutralSpeedScale)
    end
    A0_0:WaitForCurrentActionToFinish()
    A0_0.LastStanceSwitch = Timing.GetWorldFrame()
    ScriptFunction.TeleportEntityOut(A0_0.Entity, false, ETeleportType.TELEPORT_SHARD_SINGLE)
    Creature.PlaceNearButNotTooNear(A0_0.Entity, A0_0.Target, (3))
    ScriptFunction.TeleportEntityIn(A0_0.Entity, ETeleportType.TELEPORT_SHARD_SINGLE)
    end
    end
  end
)REFII_SHARD");
    return true;
}

bool FoldFastBoundedHotspotLuaPlusAiTestExactShape(std::string* source) {
    if (source == nullptr || source->empty() ||
        source->find("permanent_when_saving") == std::string::npos ||
        source->find("RegisterStateGroup(\"StateGroupEatThings\")") == std::string::npos) {
        return false;
    }

    if (source->find("function StateGroupBase.new(A0_0, A1_1)") != std::string::npos &&
        source->find("function StateGroupEatThings.IsRunnable(A0_0)") != std::string::npos) {
        *source = R"REFII_LUAPLUS(  StateGroupBase = {}
  permanent_when_saving = {[coroutine.yield] = 1, [TraceString] = 2, [RegisterStateGroup] = 3, [CreateEntities] = 4, [CreateAIChasers] = 5, [ResetAI] = 6, [SaveAIState] = 7, [LoadAIState] = 8, [DrawEntities] = 9, [AddTargetPoint] = 10, [GetRandomTarget] = 11, [GetRandomEntity] = 12, [coroutine] = 13, [coroutine.create] = 14, [coroutine.status] = 15, [Entity.__gc] = 16, [Entity.GetPosition] = 17, [Entity.Kill] = 18, [Entity.IsAlive] = 19, [CI32Vector2.__props] = 20, [CI32Vector2.__gc] = 21, [Entity.__eq] = 22, [CI32Vector2.__index] = 23, [CI32Vector2.__newindex] = 24, [CI32Vector2.__call] = 25, [StateGroupBase] = 26, [Entity] = 27, [CI32Vector2] = 28}
  permanent_when_loading = {
    [1] = coroutine.yield,
    [2] = TraceString,
    [3] = RegisterStateGroup,
    [4] = CreateEntities,
    [5] = CreateAIChasers,
    [6] = ResetAI,
    [7] = SaveAIState,
    [8] = LoadAIState,
    [9] = DrawEntities,
    [10] = AddTargetPoint,
    [11] = GetRandomTarget,
    [12] = GetRandomEntity,
    [13] = coroutine,
    [14] = coroutine.create,
    [15] = coroutine.status,
    [16] = Entity.__gc,
    [17] = Entity.GetPosition,
    [18] = Entity.Kill,
    [19] = Entity.IsAlive,
    [20] = CI32Vector2.__props,
    [21] = CI32Vector2.__gc,
    [22] = Entity.__eq,
    [23] = CI32Vector2.__index,
    [24] = CI32Vector2.__newindex,
    [25] = CI32Vector2.__call,
    [26] = StateGroupBase,
    [27] = Entity,
    [28] = CI32Vector2,
  }
  function CI32Vector2.__persist(old_coord)
    local x = old_coord.X
    local y = old_coord.Y
    return function()
      local new_coord = CI32Vector2(x, y)
      return new_coord
    end
  end
  function C3DCoordF.__persist(old_coord)
    local x = old_coord.X
    local y = old_coord.Y
    local z = old_coord.Z
    return function()
      local new_coord = C3DCoordF(x, y, z)
      return new_coord
    end
  end
  function Entity.__persist(old_entity)
    local id = old_entity:GetID()
    return function()
      new_entity = Entity(id)
      return new_entity
    end
  end
  function StateGroupBase:new(o)
    if not o then
    o = {}
    end
    setmetatable(o, self)
    self.__index = self
    return o
  end
  function StateGroupBase:RunUpdateFunction(update_function)
    if not self:IsTerminated() then
    if self.co_update == nil or coroutine.status(self.co_update) == "dead" then
    self.co_update = coroutine.create(update_function)
    end
    coroutine.resume(self.co_update, self)
    return true
    end
    self.co_update = nil
    return false
  end
  function StateGroupBase:Save()
    permanent_when_saving[self.IsTerminated] = ("IsTerminated" .. self:GetID())
    permanent_when_saving[self.IsAtPosition] = ("IsAtPosition" .. self:GetID())
    permanent_when_saving[self.IsCloseToPosition] = ("IsCloseToPosition" .. self:GetID())
    permanent_when_saving[self.MoveToPosition] = ("MoveToPosition" .. self:GetID())
    permanent_when_saving[self.SetSpeed] = ("SetSpeed" .. self:GetID())
    permanent_when_saving[self.GetSpeed] = ("GetSpeed" .. self:GetID())
    permanent_when_saving[self.GetEntity] = ("GetEntity" .. self:GetID())
    permanent_when_saving[self.MoveToEntity] = ("MoveToEntity" .. self:GetID())
    permanent_when_saving[self.SetAsChaser] = ("SetAsChaser" .. self:GetID())
    permanent_when_saving[self.GetID] = ("GetID" .. self:GetID())
  end
  function StateGroupBase:Load()
    permanent_when_loading[("IsTerminated" .. self:GetID())] = self.IsTerminated
    permanent_when_loading[("IsAtPosition" .. self:GetID())] = self.IsAtPosition
    permanent_when_loading[("IsCloseToPosition" .. self:GetID())] = self.IsCloseToPosition
    permanent_when_loading[("MoveToPosition" .. self:GetID())] = self.MoveToPosition
    permanent_when_loading[("SetSpeed" .. self:GetID())] = self.SetSpeed
    permanent_when_loading[("GetSpeed" .. self:GetID())] = self.GetSpeed
    permanent_when_loading[("GetEntity" .. self:GetID())] = self.GetEntity
    permanent_when_loading[("MoveToEntity" .. self:GetID())] = self.MoveToEntity
    permanent_when_loading[("SetAsChaser" .. self:GetID())] = self.SetAsChaser
    permanent_when_loading[("GetID" .. self:GetID())] = self.GetID
  end
  function StateGroupBase:IsRunnable()
    return false
  end
  function StateGroupBase:LuaUpdate()
    if self.Update == nil then
    TraceString("Undefined Update function!")
    else
    if not self:RunUpdateFunction(self.Update) then
    TraceString("Do some terminated stuff")
    end
    end
  end
  function StateGroupBase:IsFinished()
    if self.co_update == nil then
    return true
    end
    return coroutine.status(self.co_update) == "dead"
  end
  function StateGroupBase:Cleanup()
  end
  function StateGroupBase:WaitUntilAtPosition(position)
    while not self:IsAtPosition(position) do
    coroutine.yield()
    end
  end
  function StateGroupBase:WaitUntilAtEntity(entity)
    while entity:IsAlive() and not self:IsCloseToPosition(entity:GetPosition(), 2) do
    coroutine.yield()
    end
  end
  StateGroupWander = StateGroupBase:new()
  function StateGroupWander:Create(o)
    o = StateGroupWander:new(o)
    return o
  end
  function StateGroupWander:IsRunnable()
    self.TargetPoint = GetRandomTarget()
    return self.TargetPoint ~= nil
  end
  function StateGroupWander:Update()
    self:SetSpeed(2)
    self:MoveToPosition(self.TargetPoint)
    self:WaitUntilAtPosition(self.TargetPoint)
    TraceString("Message 2")
  end
  StateGroupWanderQuickly = StateGroupBase:new()
  function StateGroupWanderQuickly:Create(o)
    o = StateGroupWanderQuickly:new(o)
    o.SpecialTarget = GetRandomTarget()
    return o
  end
  function StateGroupWanderQuickly:IsRunnable()
    local is_runnable = self:IsAtPosition(self.SpecialTarget)
    if is_runnable then
    TraceString("Quick guy is runnable!")
    end
    return is_runnable
  end
  function StateGroupWanderQuickly:Update()
    local target_point = GetRandomTarget()
    self.OldSpeed = self:GetSpeed()
    self:SetSpeed(4)
    self:MoveToPosition(target_point)
    self:WaitUntilAtPosition(target_point)
  end
  function StateGroupWanderQuickly:Cleanup()
    self.SpecialTarget = GetRandomTarget()
    if self.OldSpeed ~= nil then
    self:SetSpeed(self.OldSpeed)
    end
  end
  StateGroupEatThings = StateGroupBase:new()
  function StateGroupEatThings:Create(o)
    o = StateGroupEatThings:new(o)
    return o
  end
  function StateGroupEatThings:IsRunnable()
    self.TargetThing = GetRandomEntity()
    return self.TargetThing ~= self:GetEntity() and self.TargetThing:IsAlive()
  end
  function StateGroupEatThings:Update()
    self:SetAsChaser(true)
    self.OldSpeed = self:GetSpeed()
    self:SetSpeed(2)
    self:MoveToEntity(self.TargetThing)
    self:WaitUntilAtEntity(self.TargetThing)
    if self.TargetThing:IsAlive() then
    self.TargetThing:Kill()
    end
  end
  function StateGroupEatThings:Cleanup()
    self:SetSpeed(self.OldSpeed)
    self:SetAsChaser(false)
  end
  RegisterStateGroup("StateGroupWander")
  RegisterStateGroup("StateGroupWanderQuickly")
  RegisterStateGroup("StateGroupEatThings")
  permanent_when_saving[StateGroupWander] = -1
  permanent_when_saving[StateGroupWanderQuickly] = -2
  permanent_when_saving[StateGroupEatThings] = -3
  permanent_when_loading[-1] = StateGroupWander
  permanent_when_loading[-2] = StateGroupWanderQuickly
  permanent_when_loading[-3] = StateGroupEatThings
  AddTargetPoint(CI32Vector2(100, 200))
  AddTargetPoint(CI32Vector2((300), (400)))
  AddTargetPoint(CI32Vector2((700), (20)))
  AddTargetPoint(CI32Vector2((20), (300)))
  AddTargetPoint(CI32Vector2((643), (521)))
  AddTargetPoint(CI32Vector2((587), (234)))
  CreateEntities(1)
)REFII_LUAPLUS";
        return true;
    }

    return FastApplyLiteralRules(
        source,
        {
            {
                "  permanent_when_loading = {\n"
                "    coroutine.yield,\n"
                "    TraceString,\n"
                "    RegisterStateGroup,\n"
                "    CreateEntities,\n"
                "    CreateAIChasers,\n"
                "    ResetAI,\n"
                "    SaveAIState,\n"
                "    LoadAIState,\n"
                "    DrawEntities,\n"
                "    AddTargetPoint,\n"
                "    GetRandomTarget,\n"
                "    GetRandomEntity,\n"
                "    coroutine,\n"
                "    coroutine.create,\n"
                "    coroutine.status,\n"
                "    Entity.__gc,\n"
                "    Entity.GetPosition,\n"
                "    Entity.Kill,\n"
                "    Entity.IsAlive,\n"
                "    CI32Vector2.__props,\n"
                "    CI32Vector2.__gc,\n"
                "    Entity.__eq,\n"
                "    CI32Vector2.__index,\n"
                "    CI32Vector2.__newindex,\n"
                "    CI32Vector2.__call,\n"
                "    StateGroupBase,\n"
                "    Entity,\n"
                "    CI32Vector2,\n"
                "  }",
                "  permanent_when_loading = {\n"
                "    [1] = coroutine.yield,\n"
                "    [2] = TraceString,\n"
                "    [3] = RegisterStateGroup,\n"
                "    [4] = CreateEntities,\n"
                "    [5] = CreateAIChasers,\n"
                "    [6] = ResetAI,\n"
                "    [7] = SaveAIState,\n"
                "    [8] = LoadAIState,\n"
                "    [9] = DrawEntities,\n"
                "    [10] = AddTargetPoint,\n"
                "    [11] = GetRandomTarget,\n"
                "    [12] = GetRandomEntity,\n"
                "    [13] = coroutine,\n"
                "    [14] = coroutine.create,\n"
                "    [15] = coroutine.status,\n"
                "    [16] = Entity.__gc,\n"
                "    [17] = Entity.GetPosition,\n"
                "    [18] = Entity.Kill,\n"
                "    [19] = Entity.IsAlive,\n"
                "    [20] = CI32Vector2.__props,\n"
                "    [21] = CI32Vector2.__gc,\n"
                "    [22] = Entity.__eq,\n"
                "    [23] = CI32Vector2.__index,\n"
                "    [24] = CI32Vector2.__newindex,\n"
                "    [25] = CI32Vector2.__call,\n"
                "    [26] = StateGroupBase,\n"
                "    [27] = Entity,\n"
                "    [28] = CI32Vector2,\n"
                "  }",
            },
            {
                "      local new_entity = Entity(id)\n"
                "      return new_entity",
                "      new_entity = Entity(id)\n"
                "      return new_entity",
            },
            {
                "    if coroutine.status(self.co_update) ~= \"dead\" then\n"
                "    return false\n"
                "    end\n"
                "    return true",
                "    return coroutine.status(self.co_update) == \"dead\"",
            },
            {
                "  StateGroupWander = StateGroupBase.new:new(StateGroupBase)",
                "  StateGroupWander = StateGroupBase:new()",
            },
            {
                "  StateGroupWanderQuickly = StateGroupBase.new:new(StateGroupBase)",
                "  StateGroupWanderQuickly = StateGroupBase:new()",
            },
            {
                "  StateGroupEatThings = StateGroupBase.new:new(StateGroupBase)",
                "  StateGroupEatThings = StateGroupBase:new()",
            },
            {
                "    if self.TargetPoint == nil then\n"
                "    return false\n"
                "    end\n"
                "    return true",
                "    return self.TargetPoint ~= nil",
            },
            {
                "    if self:GetEntity() ~= self.TargetThing then\n"
                "    else\n"
                "    return false\n"
                "    end\n"
                "    return GetRandomEntity()",
                "    return self.TargetThing ~= self:GetEntity() and self.TargetThing:IsAlive()",
            },
            {
                "    if self.TargetThing.IsAlive:IsAlive(self.TargetThing) then\n"
                "    self.TargetThing.Kill:Kill(self.TargetThing)\n"
                "    end",
                "    if self.TargetThing:IsAlive() then\n"
                "    self.TargetThing:Kill()\n"
                "    end",
            },
        });
}

bool FoldFastBoundedHotspotStartupStrippedExactShape(std::string* source) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    if (source->find("lua_error = error") != std::string::npos &&
        source->find("error = function(A0_0, A1_1)") != std::string::npos &&
        source->find("assert = function(A0_0, A1_1)") != std::string::npos) {
        *source = R"RSINIT(  lua_error = error
  error = function(message, level)
    if not level then
    level = 1
    end
    if Debug.IsDebuggerAttached() then
    Debug.DebuggerBreak((level + (1)))
    end
    lua_error(message, (level + (1)))
  end
  assert = function(v, message)
    if v then
    return
    end
    if Debug.IsDebuggerAttached() then
    Debug.DebuggerBreak(2)
    end
    if message then
    lua_error(message, (2))
    else
    lua_error("assertion failed!", (2))
    end
  end)RSINIT";
        return true;
    }

    if (source->find("Debug.IsGDCDemo = true") != std::string::npos &&
        source->find("function Debug.TweakDogNav()") != std::string::npos &&
        source->find("local L1_1 = function(A0_0, A1_1, A2_2, A3_3)") !=
            std::string::npos &&
        source->find("function r1.Update(A0_0)") != std::string::npos) {
        *source = R"RSGDC(  Debug.IsGDCDemo = true
  Debug.LoadControlSystem("GameControlsGDC")
  Debug.SetDrawGUI(false)
  Debug.SetHeroGodMode(true)
  Debug.SetHeroManaInfinite(true)
  Debug.SetExperienceOrbsEnabled(false)
  Debug.SetHeroSprintingEnabled(false)
  Debug.SetForceExtendedExpressionsToSucceed(true)
  Debug.SetDisableAllSpellsExceptHeal(true)
  Debug.SetDrawTargetingHighlight(false)
  Debug.SetDogTrackingCameraOnBackButton(true)
  Debug.SetDogTargetForHealSpell(true)
  Debug.SetDrawMilestoneVersionText(true)
  Debug.SetDrawPausedTextWhenGUIDisabled(true)
  Debug.SetPhysicsUseGroundLayerForNavigation(true)
  Debug.SetDebugDpadExpression(0, "ExpressionDogCommandPraise")
  Debug.SetDebugDpadExpression((1), "ExpressionDogCommandPunish")
  Debug.SetDebugDpadExpression((2), "ExpressionBloodlustRoar")
  Debug.SetDebugDpadExpression((3), "ExpressionFart")
  Debug.SetDebugLeftShoulderExpression("ExpressionWhistle")
  Debug.SetDebugRightShoulderExpression("ExpressionDogCommandThrowObject")
  function Debug.ApplyGDCAITweaks()
    CombatStyles.Hobbe.WitnessToFlee = nil
    CombatStyles.Hobbe.MinSecondsBetweenMeleeAttacks = 1
    CombatStyles.Hobbe.HealthOverride = 1
    CombatStyles.Hobbe.ForceWeaponType = WeaponTypes.Melee
    function CombatStyles.Hobbe.TargetFilter(entity)
      if Dog.IsAvailable(entity) then
      if Health.Get(entity) > 1 then
      return true
      end
      end
      return false
    end
    function CombatStyles.Dog.TargetFilter(entity)
      return false
    end
  end
  Debug.ApplyGDCAITweaks()
  function Debug.TweakDogNav()
    local MakeDeltaFunction = function(target_func, parent_table, val_name, delta_amount)
      local targ_func = target_func
      local get_func = ("Get" .. val_name)
      get_func = parent_table[get_func]
      local set_func = ("Set" .. val_name)
      set_func = parent_table[set_func]
      local delta = delta_amount
      return function()
        local targ = targ_func()
        if targ then
        if targ:IsAlive() then
        local current_val = get_func(targ)
        set_func(targ, (current_val + delta))
        end
        end
      end
    end
    Debug.AddLuaDebugKeyFunc(EInputKey.KB_F5, MakeDeltaFunction(GetDog, Navigation, "MaxTurnAnglePerSecond", -0.0099999997764825821))
    Debug.AddLuaDebugKeyFunc(EInputKey.KB_F6, MakeDeltaFunction(GetDog, Navigation, "MaxTurnAnglePerSecond", (0.0099999997764825821)))
    Debug.AddLuaDebugKeyFunc(EInputKey.KB_F7, MakeDeltaFunction(GetDog, Navigation, "MaxTurnAnglePerMetre", (-0.0099999997764825821)))
    Debug.AddLuaDebugKeyFunc(EInputKey.KB_F8, MakeDeltaFunction(GetDog, Navigation, "MaxTurnAnglePerMetre", (0.0099999997764825821)))
    local DogNavValDrawer = {}
    function DogNavValDrawer:Update()
      while true do
      local dog = GetDog()
      if dog then
      if dog:IsAlive() then
      local per_metre = tostring(Navigation.GetMaxTurnAnglePerMetre(dog))
      local per_second = tostring(Navigation.GetMaxTurnAnglePerSecond(dog))
      Debug.DrawText("Angle/Second [F5/F6] = " .. per_second .. "\nAngle/Metre [F7/F8] = " .. per_metre, CI32Vector2(20, 100))
      end
      end
      coroutine.yield()
      end
    end
    GeneralScriptManager.AddScript(DogNavValDrawer)
  end
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F10, SkipDogStuff)
  function Debug.RestartGDCDemo()
    EntityManager.GDCBodgeReload()
    Debug.LoadLevel("TestWorld", "DogBed", "")
    Debug.ReloadAI()
    Debug.ApplyGDCAITweaks()
    GeneralScriptManager.CallFunction(ScriptFunction.CreateDog)
  end
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F11, Debug.RestartGDCDemo)
  Timing.SetDaySpeed((0))
  Timing.SetTimeOfDay((6)))RSGDC";
        return true;
    }

    if (source->find("function Debug.ScaleConsoleForTelevisionScreen()") !=
            std::string::npos &&
        source->find("Debug.AddOptionalStartupConsoleScript(\"MyStartupConsoleScript.lua\")") !=
            std::string::npos &&
        source->find("DoStartupStuffAfterHeroCreated = function()") !=
            std::string::npos &&
        source->find("local hero = Debug.GetHero()") == std::string::npos) {
        *source = R"RSCONSOLE(  function Debug.ScaleConsoleForTelevisionScreen()
    Debug.SetConsoleScale(2)
    Debug.SetConsoleOffsetX(65)
  end
  function Debug.ScaleConsoleForMonitor()
    Debug.SetConsoleScale(1)
    Debug.SetConsoleOffsetX(0)
  end
  function Debug.ShowMissingLightmaps()
    Debug.EngineConfig.MissingLightmapAmbientOffsetOverride = 10
  end
  function Debug.DisableMainLight()
    EnvironmentTheme.SetOverrideBlending(true)
    EnvironmentTheme.SetColourParameter("MAIN_LIGHT_COLOUR", (0), (0), (0))
  end
  function Debug.DisableLightmaps()
    EnvironmentTheme.SetOverrideBlending(true)
    EnvironmentTheme.SetColourParameter("LIGHTMAP_COLOUR", (0), (0), (0))
  end
  function Debug.DisableAmbient()
    EnvironmentTheme.SetOverrideBlending(true)
    EnvironmentTheme.SetColourParameter("AMBIENT_COLOUR", (0), (0), (0))
  end
  function Debug.DisableLocalLights()
    Debug.EngineConfig.EnableSpotLights = false
    Debug.EngineConfig.EnablePointLights = false
  end
  function Debug.RestoreLightingDebug()
    EnvironmentTheme.SetOverrideBlending(false)
    Debug.EngineConfig.MissingLightmapAmbientOffsetOverride = -1
    Debug.EngineConfig.UseWhiteTexture = false
    Debug.EngineConfig.EnablePRT = true
    Debug.EngineConfig.EnableLightmaps = true
    Debug.EngineConfig.EnableToneMapping = true
    Debug.EngineConfig.EnableShadows = true
    Debug.EngineConfig.EnableSpotLights = true
    Debug.EngineConfig.EnablePointLights = true
  end
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F1, Debug.ScaleConsoleForTelevisionScreen)
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_F2, Debug.ScaleConsoleForMonitor)
  Debug.AddOptionalStartupConsoleScript("MyStartupConsoleScript.lua")
  Debug.AddConsoleKeyShortcut(EInputKey.KB_D, "Debug.")
  Debug.AddConsoleKeyShortcut(EInputKey.KB_F, "Fable2Scripts.")
  Debug.AddConsoleKeyShortcut(EInputKey.KB_H, "Debug.GetHero()")
  Debug.AddConsoleKeyShortcut(EInputKey.KB_T, "Debug.SetTextSpeechDebugMenuFilter('")
  Debug.AddConsoleKeyShortcut(EInputKey.KB_V, "CVector3(")
  Debug.AddLuaDebugKeyFunc(EInputKey.KB_L, Debug.ReloadEnvironmentThemes)
  Debug.SetVaultingCheckImpassability(false)
  Debug.SetUseImplicitBlends(true)
  DoStartupStuffAfterHeroCreated = function()
    local hero = Debug.GetHero()
    PhysicsCharacter.SetUseSliding(QuestManager.HeroEntity, true)
  end
  GeneralScriptManager.CallFunction(DoStartupStuffAfterHeroCreated)
  Debug.SetFootLockingEnabled2(false))RSCONSOLE";
        return true;
    }

    return false;
}


}  // namespace refii::lua::workspace
