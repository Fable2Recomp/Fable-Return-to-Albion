#pragma once

// The host bridge's C ABI decoration, shared by its translation units. Godot reaches every export
// through P/Invoke; bump RefiiHostBridgeAbiVersion (host_bridge.cpp) with any change.
#if defined(_WIN32)
#define REFII_HOST_ABI_EXPORT __declspec(dllexport)
#else
#define REFII_HOST_ABI_EXPORT __attribute__((visibility("default")))
#endif
