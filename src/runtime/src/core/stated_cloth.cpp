// refii — the cloth a title states (core/payload_bridge.h): each cloth piece's drawn vertices as
// the guest's own draw hands them to its vertex buffer, keyed by the piece, with the render
// instance and record slot that join it to an entity's model.
//
// It crosses from a title's hooks (the payload, on the guest's render thread) to its host bridge
// (on Godot's), which live in different modules; this is the one table both link. Neither side
// knows a title: a hook states what the guest's publish wrote, the bridge joins it to entities.
//
// WHOLE PER PUBLISH. A publish replaces the piece's current pose in one step under the lock, so a
// read never sees half of one. The pose of the previous game tick is kept beside it: a publish
// stamped with a new tick first moves the current pose there, so the shell blends from the last
// pose of tick T-1 to the newest of tick T at the shared draw tick, as it blends the bodies.

#include <cstdint>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <vector>

#include "core/payload_bridge.h"

namespace {

struct ClothPose {
  uint32_t tick = 0u;
  std::vector<float> floats;
};

struct ClothPiece {
  uint32_t handle = 0u;
  uint32_t slot = 0u;
  uint32_t flags = 0u;
  uint32_t vertex_count = 0u;
  uint32_t frame = 0u;
  uint32_t sequence = 0u;
  ClothPose current;
  ClothPose previous;
};

std::mutex g_cloth_lock;
std::unordered_map<uint32_t, ClothPiece>* g_cloth = nullptr;
uint64_t g_cloth_publishes = 0u;

std::unordered_map<uint32_t, ClothPiece>& Pieces() {
  if (g_cloth == nullptr) {
    g_cloth = new std::unordered_map<uint32_t, ClothPiece>();
  }
  return *g_cloth;
}

}  // namespace

extern "C" {

void refii_bridge_cloth_published(uint32_t key, uint32_t handle, uint32_t slot, uint32_t flags,
                                  uint32_t frame, uint32_t tick, const float* vertices,
                                  uint32_t vertex_count) {
  if (key == 0u || vertices == nullptr || vertex_count == 0u ||
      vertex_count > REFII_CLOTH_MAX_VERTICES) {
    return;
  }

  const size_t floats = static_cast<size_t>(vertex_count) * REFII_CLOTH_VERTEX_FLOATS;
  std::lock_guard<std::mutex> guard(g_cloth_lock);
  ++g_cloth_publishes;
  ClothPiece& piece = Pieces()[key];
  if (piece.vertex_count != vertex_count) {
    piece.current = ClothPose{};
    piece.previous = ClothPose{};
  } else if (piece.current.tick != tick && !piece.current.floats.empty()) {
    std::swap(piece.previous, piece.current);
  }

  piece.handle = handle;
  piece.slot = slot;
  piece.flags = flags;
  piece.vertex_count = vertex_count;
  piece.frame = frame;
  ++piece.sequence;
  piece.current.tick = tick;
  piece.current.floats.assign(vertices, vertices + floats);
}

void refii_bridge_cloth_dropped(uint32_t key) {
  std::lock_guard<std::mutex> guard(g_cloth_lock);
  Pieces().erase(key);
}

uint32_t refii_bridge_cloth_pieces(const uint32_t* handles, uint32_t handle_count,
                                   uint32_t* out_words, uint32_t max_pieces) {
  if (handles == nullptr || handle_count == 0u || out_words == nullptr || max_pieces == 0u) {
    return 0u;
  }

  std::lock_guard<std::mutex> guard(g_cloth_lock);
  uint32_t written = 0u;
  for (const auto& [key, piece] : Pieces()) {
    bool wanted = false;
    for (uint32_t h = 0u; h < handle_count && !wanted; ++h) {
      wanted = handles[h] == piece.handle;
    }
    if (!wanted) {
      continue;
    }
    if (written == max_pieces) {
      break;
    }

    uint32_t* row = out_words + (static_cast<size_t>(written) * REFII_CLOTH_PIECE_WORDS);
    row[0] = key;
    row[1] = piece.handle;
    row[2] = piece.slot;
    row[3] = piece.flags;
    row[4] = piece.vertex_count;
    row[5] = piece.frame;
    row[6] = piece.sequence;
    row[7] = piece.current.tick;
    row[8] = piece.previous.floats.empty() ? 0u : piece.previous.tick;
    ++written;
  }

  return written;
}

uint32_t refii_bridge_cloth_vertices(uint32_t key, uint32_t previous, float* out_floats,
                                     uint32_t max_vertices, uint32_t* out_tick,
                                     uint32_t* out_sequence) {
  if (out_tick != nullptr) *out_tick = 0u;
  if (out_sequence != nullptr) *out_sequence = 0u;
  if (out_floats == nullptr || max_vertices == 0u) {
    return 0u;
  }

  std::lock_guard<std::mutex> guard(g_cloth_lock);
  const auto found = Pieces().find(key);
  if (found == Pieces().end()) {
    return 0u;
  }

  const ClothPiece& piece = found->second;
  const ClothPose& pose = previous != 0u ? piece.previous : piece.current;
  if (pose.floats.empty() || piece.vertex_count > max_vertices) {
    return 0u;
  }

  std::memcpy(out_floats, pose.floats.data(), pose.floats.size() * sizeof(float));
  if (out_tick != nullptr) *out_tick = pose.tick;
  if (out_sequence != nullptr) *out_sequence = piece.sequence;
  return piece.vertex_count;
}

uint64_t refii_bridge_cloth_publish_count(void) {
  std::lock_guard<std::mutex> guard(g_cloth_lock);
  return g_cloth_publishes;
}

}  // extern "C"
