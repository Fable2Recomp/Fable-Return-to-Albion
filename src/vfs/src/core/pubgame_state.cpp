// refii — the pub-game seam's shared state: what Godot's game said, and what the guest answered.
//
// This file must be in librefii_vfs, not in the PPC hook set, for the same reason as
// guest_tick_pump_state.cpp beside it: the host posts (GamePubGames, on Godot's thread, through
// the host bridge) and the payload drains (the pump replacement in src/hooks/pubgame_pump.cpp,
// on the guest's render thread). A copy in each library would have the host filling one queue
// while the pump drains another, empty one.
//
// The host reaches these as ordinary C++ symbols; the payload through the C entries in
// core/payload_bridge.h.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>

#include "core/payload_bridge.h"

namespace refii::vfs {
namespace {

struct PubGameCommand {
  uint32_t kind = 0;
  std::string text;
  double numbers[3] = {0.0, 0.0, 0.0};
  int64_t integers[2] = {0, 0};
};

/// Far more than a session issues between two guest render frames; a host that outruns the
/// pump by this much has lost the guest, and dropping the oldest is the least-wrong answer.
constexpr size_t kQueueCapacity = 256;

std::mutex g_lock;
std::deque<PubGameCommand> g_commands;
std::atomic<int32_t> g_claimed{0};
std::atomic<uint32_t> g_events{0};
std::atomic<uint64_t> g_posted{0};
std::atomic<uint64_t> g_drained{0};

}  // namespace

/// Godot plays the pub games; the guest's own Scaleform module must not be created.
void ClaimPubGames(bool claimed) {
  g_claimed.store(claimed ? 1 : 0, std::memory_order_release);
  if (!claimed) {
    std::lock_guard<std::mutex> guard(g_lock);
    g_commands.clear();
  }
}

/// One fscommand from Godot's game, for the pump to hand to the guest's own handlers.
void PostPubGameCommand(uint32_t kind, const char* text, const double* numbers,
                        const int64_t* integers) {
  PubGameCommand command;
  command.kind = kind;
  command.text = text != nullptr ? text : "";
  for (int i = 0; i < 3 && numbers != nullptr; ++i) {
    command.numbers[i] = numbers[i];
  }

  for (int i = 0; i < 2 && integers != nullptr; ++i) {
    command.integers[i] = integers[i];
  }

  std::lock_guard<std::mutex> guard(g_lock);
  if (g_commands.size() >= kQueueCapacity) {
    g_commands.pop_front();
  }

  g_commands.push_back(std::move(command));
  g_posted.fetch_add(1u, std::memory_order_relaxed);
}

/// What the pump has told the movie since the last call — see kPubGameEvent* in host_bridge.cpp.
uint32_t TakePubGameEvents() { return g_events.exchange(0u, std::memory_order_acq_rel); }

/// Posted and drained since boot, for the heartbeat.
uint64_t PubGameCommandsPosted() { return g_posted.load(std::memory_order_relaxed); }
uint64_t PubGameCommandsDrained() { return g_drained.load(std::memory_order_relaxed); }

}  // namespace refii::vfs

// --- The payload's side. C only; see core/payload_bridge.h. --------------------------------

extern "C" {

int32_t refii_bridge_pubgame_claimed(void) {
  return refii::vfs::g_claimed.load(std::memory_order_acquire);
}

int32_t refii_bridge_pubgame_take_command(uint32_t* kind, char* text, size_t text_capacity,
                                          double* numbers, int64_t* integers) {
  refii::vfs::PubGameCommand command;
  {
    std::lock_guard<std::mutex> guard(refii::vfs::g_lock);
    if (refii::vfs::g_commands.empty()) {
      return 0;
    }

    command = std::move(refii::vfs::g_commands.front());
    refii::vfs::g_commands.pop_front();
  }

  refii::vfs::g_drained.fetch_add(1u, std::memory_order_relaxed);
  if (kind != nullptr) {
    *kind = command.kind;
  }

  if (text != nullptr && text_capacity > 0) {
    const size_t length = command.text.size() < text_capacity - 1 ? command.text.size()
                                                                   : text_capacity - 1;
    std::memcpy(text, command.text.data(), length);
    text[length] = '\0';
  }

  for (int i = 0; i < 3 && numbers != nullptr; ++i) {
    numbers[i] = command.numbers[i];
  }

  for (int i = 0; i < 2 && integers != nullptr; ++i) {
    integers[i] = command.integers[i];
  }

  return 1;
}

void refii_bridge_pubgame_note_event(uint32_t bits) {
  refii::vfs::g_events.fetch_or(bits, std::memory_order_acq_rel);
}

}  // extern "C"
