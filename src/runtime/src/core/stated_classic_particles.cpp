// refii — the classic particle streams a title states (core/payload_bridge.h): each particle
// group's stream as the guest's own writer lays it into the frame's vertex buffer, keyed by the
// sim data it was written from, and the render object each sim data was last posted to.
//
// It crosses from a title's hooks (the payload, on the guest's particle thread) to its host
// bridge (on Godot's), which live in different modules; this is the one table both link.
//
// WHOLE PER WRITE. A write replaces the data's stream in one step under the lock, so a read never
// sees half of one.

#include <cstdint>
#include <iterator>
#include <mutex>
#include <unordered_map>
#include <vector>

#include "core/payload_bridge.h"

namespace {

struct Stream {
  uint32_t kind = 0u;
  uint32_t n = 0u;
  uint32_t c = 0u;
  bool bound = false;
  uint32_t id = 0u;
  std::vector<float> floats;
};

constexpr size_t kMaxStreams = 4096u;

std::mutex g_lock;
std::unordered_map<uint32_t, Stream>* g_streams = nullptr;
std::unordered_map<uint32_t, uint32_t>* g_ids = nullptr;

std::unordered_map<uint32_t, Stream>& Streams() {
  if (g_streams == nullptr) {
    g_streams = new std::unordered_map<uint32_t, Stream>();
  }
  return *g_streams;
}

std::unordered_map<uint32_t, uint32_t>& Ids() {
  if (g_ids == nullptr) {
    g_ids = new std::unordered_map<uint32_t, uint32_t>();
  }
  return *g_ids;
}

/// Streams no object was ever posted with are dropped once the table is full.
void Trim(std::unordered_map<uint32_t, Stream>& streams) {
  if (streams.size() <= kMaxStreams) {
    return;
  }
  for (auto it = streams.begin(); it != streams.end();) {
    it = it->second.bound ? std::next(it) : streams.erase(it);
  }
}

}  // namespace

extern "C" {

void refii_bridge_classic_particle_written(uint32_t data, uint32_t kind, uint32_t n, uint32_t c,
                                           const float* floats, uint32_t float_count) {
  if (data == 0u || float_count > REFII_CLASSIC_PARTICLE_MAX_FLOATS) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  auto& streams = Streams();
  Stream& stream = streams[data];
  stream.kind = kind;
  stream.n = n;
  stream.c = c;
  if (floats == nullptr) {
    float_count = 0u;
  }
  stream.floats.assign(floats, floats + float_count);
  Trim(streams);
}

void refii_bridge_classic_particle_bound(uint32_t id, uint32_t data) {
  if (data == 0u) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  auto& ids = Ids();
  auto& streams = Streams();
  const auto was = ids.find(id);
  if (was != ids.end() && was->second != data) {
    const auto old = streams.find(was->second);
    if (old != streams.end() && old->second.bound && old->second.id == id) {
      streams.erase(old);
    }
  }
  ids[id] = data;
  Stream& stream = streams[data];
  stream.bound = true;
  stream.id = id;
}

void refii_bridge_classic_particle_removed(uint32_t id) {
  std::lock_guard<std::mutex> guard(g_lock);
  auto& ids = Ids();
  const auto was = ids.find(id);
  if (was == ids.end()) {
    return;
  }
  auto& streams = Streams();
  const auto stream = streams.find(was->second);
  if (stream != streams.end() && stream->second.id == id) {
    streams.erase(stream);
  }
  ids.erase(was);
}

uint32_t refii_bridge_classic_particle_ids(uint32_t* out_ids, uint32_t capacity) {
  std::lock_guard<std::mutex> guard(g_lock);
  uint32_t written = 0u;
  for (const auto& [id, data] : Ids()) {
    if (written >= capacity || out_ids == nullptr) {
      break;
    }
    out_ids[written++] = id;
  }
  return written;
}

int32_t refii_bridge_classic_particle_read(uint32_t id, uint32_t* out_meta, float* out,
                                           uint32_t capacity) {
  std::lock_guard<std::mutex> guard(g_lock);
  const auto data = Ids().find(id);
  if (data == Ids().end()) {
    return 0;
  }
  const auto stream = Streams().find(data->second);
  if (stream == Streams().end()) {
    return 0;
  }
  const Stream& s = stream->second;
  const uint32_t count = static_cast<uint32_t>(s.floats.size());
  if (out_meta != nullptr) {
    out_meta[0] = s.kind;
    out_meta[1] = s.n;
    out_meta[2] = s.c;
    out_meta[3] = count;
  }
  if (out == nullptr || count > capacity) {
    return 0;
  }
  for (uint32_t i = 0; i < count; ++i) {
    out[i] = s.floats[i];
  }
  return 1;
}

}  // extern "C"
