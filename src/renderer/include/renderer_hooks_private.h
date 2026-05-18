#pragma once

#include "hooks/ppc_externs.h"
#include "renderer_hook_targets.h"
#include "renderer_runtime.h"
#include "renderdoc_debugger.h"
#include "runtime/cvars.h"

#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc.h>

#if !defined(REX_FUNC_PROLOGUE)
#define REX_FUNC_PROLOGUE() ((void)0)
#endif
