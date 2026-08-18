// refii — the payload/host C ABI for the ReXGlue runtime entries the payload calls.
//
// Same contract as core/payload_bridge.h: C linkage, explicit-width scalars, nothing owned
// crossing. Objects are passed as opaque void* — the host casts them back to the rex type
// it already owns, so no rex layout is duplicated here.

#pragma once

#include <stddef.h>
#include <stdint.h>

extern "C" {

uint64_t refii_bridge_rex_query_guest_tick_count(void);
void* refii_bridge_rex_resolve_indirect_function(uint32_t guest_address);
void refii_bridge_rex_increment_counter(uint32_t counter_id, int64_t delta);

uint32_t refii_bridge_rex_system_heap_alloc(void* memory, uint32_t size, uint32_t alignment,
                                            uint32_t flags);
void refii_bridge_rex_system_heap_free(void* memory, uint32_t address,
                                       uint32_t* out_region_size);
const void* refii_bridge_rex_lookup_heap(const void* memory, uint32_t address);
int32_t refii_bridge_rex_query_region_info(void* heap, uint32_t base_address, void* out_info);

void* refii_bridge_rex_mmio_global_handler(void);
int32_t refii_bridge_rex_mmio_check_load(void* handler, uint32_t address, uint32_t* out_value);
int32_t refii_bridge_rex_mmio_check_store(void* handler, uint32_t address, uint32_t value);

void* refii_bridge_rex_runtime_instance(void);
void* refii_bridge_rex_kernel_state(void);

void* refii_bridge_rex_xthread_current(void);
int32_t refii_bridge_rex_xthread_is_in_thread(void);
void refii_bridge_rex_xthread_set_last_error(uint32_t error_code);
void refii_bridge_rex_xthread_enqueue_apc(void* thread, uint32_t normal_routine,
                                          uint32_t normal_context, uint32_t arg1,
                                          uint32_t arg2);

void refii_bridge_rex_vd_query_video_mode(void* video_mode);
uint32_t refii_bridge_rex_nt_set_event(uint32_t handle, void* previous_state);

void* refii_bridge_rex_default_logger(void);
void refii_bridge_rex_logger_flush(void* logger);
void* refii_bridge_rex_global_critical_region_mutex(void);

// CVAR REGISTRATION. The payload defines cvars of its own -- the Lua runtime's ten, since its
// sources compile into the payload rather than shipping as a library -- and rex::cvar's
// registry lives in the runtime. FlagEntry cannot cross: it holds std::function and
// std::string. So the scalars and text cross as C, and the two accessors cross as function
// pointers the payload owns, keyed BY NAME so one fixed pair serves every cvar instead of a
// trampoline each.
//
// `lifecycle` and `is_debug_only` are passed rather than defaulted because the chain methods
// (.lifecycle(), .debug_only()) mutate them after construction, and re-registering is how the
// payload side replays that. A custom validator cannot cross and is not supported here.
int32_t refii_bridge_rex_cvar_register(
    const char* name, uint32_t type, const char* category, const char* description,
    const char* default_value, uint32_t lifecycle, int32_t is_debug_only,
    int32_t (*setter)(const char* name, const char* value),
    size_t (*getter)(const char* name, char* out, size_t capacity));
void refii_bridge_rex_cvar_unregister(const char* name);

}  // extern "C"
