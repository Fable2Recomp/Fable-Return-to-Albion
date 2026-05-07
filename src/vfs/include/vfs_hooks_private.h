#pragma once

#include "loose_asset_hooks.h"
#include "banks/hash_bank_runtime.h"
#include "banks/manifest_resolution.h"
#include "banks/synthetic_bank_runtime.h"
#include "core/guest_state.h"
#include "frontend/loading_sequence.h"
#include "frontend/post_gameface.h"
#include "frontend/post_loading.h"
#include "frontend/post_video.h"
#include "frontend/texture_handoff.h"
#include "index/asset_aliases.h"
#include "index/concrete_asset_resolution.h"
#include "index/open_pipeline.h"
#include "index/ownership.h"
#include "index/resource_hash_index.h"
#include "index/resource_hash_resolution.h"
#include "index/runtime_alias_cache.h"
#include "index/runtime_tables.h"
#include "resources/resource_wrappers.h"
#include "resources/texture_tracing.h"
#include "streams/host_streams.h"
#include "streams/resource_callbacks.h"
#include "streams/resource_wrapper_handlers.h"
#include "vfs_hook_constants.h"
#include "vfs_hook_types.h"
#include "core/native_lua_hooks.h"
#include "hooks/ppc_externs.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <fstream>
#include <future>
#include <initializer_list>
#include <limits>
#include <memory>
#include <mutex>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#include <fmt/format.h>
#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>
#include <rex/system/xtypes.h>
#include <tracy/Tracy.hpp>

#if !defined(PPC_FUNC_PROLOGUE)
#define PPC_FUNC_PROLOGUE() ((void)0)
#endif
