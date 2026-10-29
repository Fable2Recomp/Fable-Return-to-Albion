// refii - Fable 3's answers to the shell's title feeds: nothing stated, yet.
//
// THE SHELL BINDS ONE ABI. Every export Godot's shared HostBridge declares is answered by every
// title's bridge: the core's (src/hostcore) for what is the same for each title, the title's own
// for what reads its guest. Fable 2 reads each of these out of its binary. Fable 3's are not
// found yet, so each answers as a guest that states nothing does: no rows, no entity, no screen,
// a write that takes no effect. A feed moves out of this file into host_bridge.cpp when the call
// that states it is found in Fable 3's binary.
//
// Signatures are Fable 2's, export for export (Fable2/src/hostbridge/src/host_bridge.cpp), so a
// managed declaration binds either title. TitleBoundaryTests checks the set.

#include <algorithm>
#include <cstdint>

#include "host_abi.h"

namespace {

template <typename T>
void Zero(T* out) {
    if (out != nullptr) {
        *out = 0;
    }
}

}  // namespace

extern "C" {

REFII_HOST_ABI_EXPORT uint32_t RefiiHostDescribeGameFlowQueue(char* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostFrontEndPresentation(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetActiveFx(uint32_t* /*out*/, uint32_t /*max_effects*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetActiveLayers(uint32_t* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetActiveScenario(char* /*scenario*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimMask(uint32_t /*node*/, uint8_t* /*out_bytes*/, uint32_t /*capacity*/, uint32_t* /*out_skeleton_id*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimPostOps(uint32_t /*entity*/, uint32_t* /*out_words*/, uint32_t /*word_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimWalkStats(uint32_t* /*out_words*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAttachmentGrips(uint32_t* /*out_words*/, uint32_t /*max_records*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAttachments(uint32_t* /*out_words*/, uint32_t /*max_records*/, char* /*out_names*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetBoneMorph(uint32_t /*entity*/, uint32_t* /*out_words*/, uint32_t /*word_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetBoneMorphHeight(uint32_t /*entity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetCamera(float* /*out*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetCameraPair(float* /*out*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicFrame(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicLights(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicLocalLights(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicMists(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicParticles(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicSceneLights(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicShadowSpheres(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicTheme(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicThemeTextures(uint32_t* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicTrails(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicWeather(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCollectedKeys(uint32_t* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopPlayerEntities(uint32_t* /*out_one*/, uint32_t* /*out_two*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCurrentCulture(char* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCurrentLevel(char* /*world*/, char* /*level*/, char* /*entrance*/, uint32_t /*capacity*/, uint32_t* /*out_key*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDeactivatedLayers(uint32_t* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDeadEntities(uint64_t* /*out_uids*/, uint32_t /*max_uids*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDebugCallResult(uint64_t* /*out_seq*/, uint32_t* /*out_addr*/, uint64_t* /*out_r3*/, char* /*out_note*/, uint32_t /*note_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDecals(uint32_t* /*out*/, uint32_t /*capacity*/, uint32_t* /*out_serial*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDisplacementQuads(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDisplayBox(uint32_t* /*out_vtable*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDrunkPlayers(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntities(uint32_t* /*out*/, uint32_t /*max_entities*/, uint32_t* /*out_unknown*/, uint32_t* /*out_unknown_vtables*/, uint32_t /*max_unknown_vtables*/, uint32_t* /*out_unknown_vtable_count*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityAnimState(uint32_t /*entity*/, uint32_t* /*out_words*/, uint32_t /*word_capacity*/, uint32_t* /*out_flags*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityBoneNames(uint32_t /*entity*/, char* /*out_names*/, uint32_t /*stride*/, uint32_t /*max_bones*/) {
    return 0;
}

/// 0xFFFFFFFF: the entity has no renderable to state a caster word.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityCasterFlags(uint32_t /*entity*/) {
    return 0xFFFFFFFFu;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityComponents(uint32_t /*entity*/, uint32_t* /*out_pairs*/, uint32_t /*max_pairs*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityDisplacementParts(uint32_t /*entity*/, uint32_t* /*out_hashes*/, uint32_t /*max_hashes*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetEntityDrawRange(uint32_t /*entity*/, float* /*out_distance*/, float* /*out_fade_factor*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityInteriorMeshes(uint32_t /*entity*/, uint32_t* /*out_hashes*/, uint32_t /*max_hashes*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityLightZone(uint32_t /*entity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityLocomotion(uint32_t /*entity*/, uint32_t* /*out_words*/, uint32_t /*word_count*/, char* /*out_name*/, uint32_t /*name_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityMeshParts(uint32_t /*entity*/, uint32_t* /*out_hashes*/, uint32_t /*max_hashes*/, uint32_t* /*out_total*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityMorphParts(uint32_t /*entity*/, uint32_t* /*out_hashes*/, uint32_t /*max_hashes*/, uint32_t* /*out_built*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPartHides(uint32_t /*entity*/, char* /*out_text*/, uint32_t /*text_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPartMorphs(uint32_t /*entity*/, uint32_t* /*out_words*/, uint32_t /*word_capacity*/, uint32_t* /*out_generation*/, uint64_t* /*out_statements*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPose(uint32_t /*entity*/, float* /*out_transforms*/, int32_t* /*out_parents*/, uint32_t /*max_bones*/, uint32_t* /*out_space*/, uint32_t* /*out_torn*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityRestPose(uint32_t /*entity*/, float* /*out_transforms*/, int32_t* /*out_parents*/, uint32_t /*max_bones*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityTextureLayers(uint32_t /*entity*/, uint32_t* /*out_words*/, uint32_t /*word_capacity*/, uint32_t* /*out_generation*/, uint64_t* /*out_statements*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint64_t RefiiHostGetEntityUid(uint32_t /*entity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityWornItems(uint32_t /*entity*/, uint32_t* /*out_words*/, uint32_t /*word_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetExpressionMeter(float* /*out_values*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostFrontEndGuiIsUp(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetFrontEnd(uint32_t* /*out*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetFrontEndInit(uint32_t* /*out*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFrontEndState(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFxMeshes(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetGameLoopPacer(uint32_t* /*words*/, uint32_t /*word_capacity*/, double* /*values*/, uint32_t /*value_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroVitals(uint32_t* /*words*/, uint32_t /*word_capacity*/, float* /*values*/, uint32_t /*value_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudMeters(uint32_t* /*out_hashes*/, float* /*out_values*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudPulserStates(uint32_t* /*out_hashes*/, float* /*out_phases*/, uint32_t* /*out_modes*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudPulsers(uint32_t* /*out_hashes*/, float* /*out_phases*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudScriptMeters(uint32_t* /*out_hashes*/, float* /*out_ratios*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudScriptTexts(uint32_t* /*out_hashes*/, uint32_t* /*out_text_ids*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudTexts(uint32_t* /*out_hashes*/, uint32_t /*capacity*/, char* /*text*/, uint32_t /*text_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetInGameScreen(uint32_t* /*out*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostGetInitMarkers(uint32_t* out) {
    if (out != nullptr) {
        std::fill_n(out, 4, 0u);
    }
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetInventoryScreenMode(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLanguages(uint32_t* /*out_current*/, char* /*names*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLevelChange(char* /*world*/, char* /*level*/, char* /*entrance*/, uint32_t /*capacity*/, uint32_t* /*out_state*/, uint32_t* /*out_travel_type*/, uint32_t* /*out_full_load*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLightHalos(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetLoadingScreen(uint32_t* /*out_manager*/, uint32_t* /*out_screen*/, float* /*out_fade*/, uint32_t* /*out_requested*/, uint32_t* /*out_done*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLoadingScreenArt(uint32_t* /*out_type*/, uint32_t* /*out_body*/, char* /*name*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMapMarkers(uint32_t* /*out_hashes*/, float* /*out_values*/, uint32_t /*cap*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuBox(uint32_t* /*out_selected*/, uint32_t* /*out_flags*/, uint32_t* /*out_enabled_mask*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuRow(uint32_t /*index*/, char* /*out_name*/, uint32_t /*name_capacity*/, char* /*out_parent*/, uint32_t /*parent_capacity*/, uint32_t* /*out_id*/, int32_t* /*out_event_type*/, uint64_t* /*out_generation*/, int32_t* /*out_rating*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuRowLabel(uint32_t /*index*/, char* /*out_label*/, uint32_t /*label_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuScreenId(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetOptionsSliders(float* /*out_gamma*/, float* /*out_sfx*/, float* /*out_music*/, float* /*out_speech*/, uint32_t* /*out_speaker*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetPauseState(uint32_t* /*out_screen_id*/, uint32_t* /*out_modal_id*/, uint32_t* /*out_modal_mode*/, uint32_t* /*out_current_tab*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerCamera(int /*key*/, float* /*out*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerTwoHoldPrompt(float* /*out_at*/, char* /*title*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPresentations(uint32_t* /*words*/, uint32_t /*word_capacity*/, char* /*names*/, uint32_t /*name_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPromptTitles(char* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetRenderFence(uint32_t* /*out_manager*/, double* /*out_submitted*/, double* /*out_retired*/, uint32_t* /*out_busy*/, uint32_t* /*out_arms*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSaveCards(uint32_t* /*out_cursor*/, uint32_t* /*out_base_slot*/, char* /*text*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSaveState(uint32_t* /*out_signed_in*/, int32_t* /*out_count*/, uint32_t* /*out_limit*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostGetSceneStreams(uint32_t* out) {
    if (out != nullptr) {
        std::fill_n(out, 24, 0u);
    }
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetScreenFade(float* /*out_rgba*/, float* /*out_progress*/, uint32_t* /*out_locked*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSimClock(double* /*out_seconds*/, uint32_t* /*out_tick*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSimTick(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetSpellAssign(uint32_t* /*out*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetSpellBlade(uint32_t* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetSpellWheel(uint32_t /*player*/, uint32_t* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSpotFrustums(float* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetStartLevel(char* /*world*/, char* /*level*/, char* /*entrance*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetStartScenario(char* /*scenario*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetStatedFx(uint32_t* /*out_words*/, uint32_t /*word_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetStatedFxCount(void) {
    return 0;
}

/// -1: not stated (Fable 2's 1 on, 0 off).
REFII_HOST_ABI_EXPORT int RefiiHostGetSubtitles(void) {
    return -1;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetThemeBlends(uint32_t* /*out_words*/, uint32_t /*max_entries*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetThemeStack(uint32_t* /*out_words*/, uint32_t /*max_entries*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetTimeOfDay(float* /*out_fraction*/, int32_t* /*out_day*/, int32_t* /*out_frozen*/, int32_t* /*out_moon_phase*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetUiAttributes(uint32_t /*requested_presentation*/, const uint32_t* /*ids*/, uint32_t /*id_count*/, uint32_t* /*out*/, uint32_t /*out_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetUiElements(uint32_t /*requested_presentation*/, uint32_t* /*words*/, uint32_t /*word_capacity*/, uint16_t* /*text*/, uint32_t /*text_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetUiMovie(uint32_t /*presentation*/, uint32_t* /*out_ordinal*/, char* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetUiTextureHashes(uint32_t /*presentation*/, uint32_t* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetUiTextureStats(uint32_t /*presentation*/, uint32_t* /*out*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetVideoFrame(uint8_t* /*out_rgba*/, uint32_t /*capacity*/, uint32_t* /*out_width*/, uint32_t* /*out_height*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetVideoPlanes(uint8_t* /*out_y*/, uint32_t /*y_capacity*/, uint8_t* /*out_cr*/, uint32_t /*cr_capacity*/, uint8_t* /*out_cb*/, uint32_t /*cb_capacity*/, uint32_t* /*out_desc*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWeather(float* /*out_snow_size*/, float* /*out_snow_fall_speed*/, float* /*out_rain_size*/, float* /*out_rain_density*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWorldIcons(uint32_t* /*out_flags*/, uint32_t* /*out_types*/, uint32_t* /*out_entities*/, uint32_t /*max_icons*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWorldMapRegion(uint32_t* /*out_index*/, char* /*name*/, uint32_t /*name_capacity*/, char* /*distance*/, uint32_t /*distance_capacity*/, char* /*travel*/, uint32_t /*travel_capacity*/, char* /*counts*/, uint32_t /*counts_capacity*/, char* /*merchants*/, uint32_t /*merchants_capacity*/, int32_t* /*out_stars*/, char* /*level_name*/, uint32_t /*level_capacity*/, uint32_t* /*out_level_key*/, char* /*scenario*/, uint32_t /*scenario_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWorldSprites(uint32_t* /*words*/, uint32_t /*word_capacity*/, float* /*values*/, uint32_t /*value_capacity*/, char* /*text*/, uint32_t /*text_capacity*/, uint32_t /*max_icons*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWornItems(uint32_t* /*words*/, uint32_t /*word_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGuestAddress(uint32_t /*tu1_address*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGuestAddressToTu1(uint32_t /*live_address*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int32_t RefiiHostGuestBuild(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostGuiNativeCounters(uint64_t* out_intercepted, uint32_t* out_pending, uint32_t* out_named) {
    Zero(out_intercepted);
    Zero(out_pending);
    Zero(out_named);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostIsInGame(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostMenuChunkCounters(uint64_t* out_recorded, uint32_t* out_pending, uint64_t* out_dropped) {
    Zero(out_recorded);
    Zero(out_pending);
    Zero(out_dropped);
}

REFII_HOST_ABI_EXPORT int RefiiHostMenuPopulating(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostPauseTabSelect(uint32_t /*tab*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostRefreshSaveState(void) {}

REFII_HOST_ABI_EXPORT void RefiiHostSetGuiPredicate(const char* /*name*/, uint32_t /*kind*/, uint32_t /*bits*/) {}

REFII_HOST_ABI_EXPORT void RefiiHostSetHighlightedRow(uint32_t /*screen*/, int32_t /*event_type*/, int32_t /*id*/, const char* /*name*/, const char* /*label*/) {}

REFII_HOST_ABI_EXPORT void RefiiHostSetWorldBuildPending(uint32_t /*pending*/) {}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeGuiNativeCalls(char* /*out_utf8*/, uint32_t /*utf8_capacity*/, uint8_t* /*out_kinds*/, uint32_t /*kinds_capacity*/, uint32_t /*max_records*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeMenuChunks(char* /*out_utf8*/, uint32_t /*utf8_capacity*/, uint8_t* /*out_kinds*/, uint32_t /*kinds_capacity*/, uint32_t /*max_records*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeToldStatements(uint32_t* /*out_words*/, uint32_t /*word_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeToldTexts(uint32_t* /*out_words*/, uint32_t /*word_capacity*/, uint16_t* /*units*/, uint32_t /*unit_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeUiElementChanges(uint32_t* /*out_words*/, uint32_t /*word_capacity*/, uint8_t* /*out_kinds*/, uint32_t /*kind_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostUiDataGeneration(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostUiElementCounters(uint64_t* out_recorded, uint32_t* out_pending, uint64_t* out_dropped) {
    Zero(out_recorded);
    Zero(out_pending);
    Zero(out_dropped);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostWorldIsActive(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostWorldIsReady(void) {
    return 0;
}

}  // extern "C"
