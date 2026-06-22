#pragma once

#include <string>

namespace refii::lua::workspace {

bool FoldFastBoundedHotspotAbilitiesManagerExactShape(std::string* source);
bool FoldFastBoundedHotspotQBEventTranslatorInlineRegistrations(std::string* source);
bool FoldFastBoundedHotspotGUIInputExactShape(std::string* source);
bool FoldFastBoundedHotspotDogCombatExactShape(std::string* source);
bool FoldFastBoundedHotspotChildLeaveHeroExactShape(std::string* source);
bool FoldFastBoundedHotspotCrateCarryingGroupMindExactShape(std::string* source);
bool FoldFastBoundedHotspotFollowGroupMindExactShape(std::string* source);
bool FoldFastBoundedHotspotDeadRiserFollowExactShape(std::string* source);
bool FoldFastBoundedHotspotCombatGetHelpExactShape(std::string* source);
bool FoldFastBoundedHotspotQuestsManagerExactShape(std::string* source);
bool FoldFastBoundedHotspotShardExactShape(std::string* source);
bool FoldFastBoundedHotspotLuaPlusAiTestExactShape(std::string* source);
bool FoldFastBoundedHotspotStartupStrippedExactShape(std::string* source);

}  // namespace refii::lua::workspace
