// refii — PLAYER STATE. The contract is in core/player_state.h; the payload's C entries are
// declared in core/payload_bridge.h.
//
// THE WIRE (the co-op channel, one datagram per line; the host bridge sends and receives):
//   ps.s=<p>,<epoch>,<version>,<op epoch>,<op seq>,<part>,<parts>|<entries>
//       the owner's values for player p. Entries are `;`-separated `<v|s><key>=<value>` (v a
//       session value, s a saved one), or `<t|T><key>=<base64>` for a TEXT (t session, T saved). <op epoch>/<op seq> is the last operation it applied from
//       the peer. A set too long for one datagram goes in parts, applied once all have arrived.
//   ps.o=<p>,<op epoch>|<seq>:<s|a><v|s><key>=<value>;...
//       the peer's pending operations on player p (set or add, or `t` a text set whose value is
//       base64), restated until the owner's state says it applied them.
//   ps.b=<p>,<version>,<part>,<parts>,<key>|<base64>
//       one part of the owner's saved blob <key> for player p, sent when it changes and restated
//       every few seconds; the peer keeps it once every part of that version has arrived.
//   ps.k=<p>|<key>;<key>;...
//       the owner's blob keys for player p, with each restatement: a blob not listed is gone.

#include "core/player_state.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <sstream>
#include <string>
#include <system_error>
#include <tuple>
#include <vector>

#include <rex/logging.h>

#include "core/payload_bridge.h"

namespace {

using Clock = std::chrono::steady_clock;

constexpr size_t kMaxKeyLength = 128;
/// A text value's bytes (REFII_MOD_PLAYER_TEXT_MAX): its base64 keeps an entry well inside a datagram.
constexpr size_t kMaxTextLength = 256;
/// A text's prefix in refii_player.txt: `key=t:<base64>`; a number is written bare.
constexpr std::string_view kTextSavePrefix = "t:";
constexpr std::string_view kBlobPrefix = "refii_blob_";
constexpr std::string_view kBlobSuffix = ".bin";
constexpr size_t kMaxBlobBytes = 4u << 20;
/// A blob part's base64 (450 bytes), and how many parts go out a frame.
constexpr size_t kBlobPartChars = 600;
constexpr size_t kBlobPartsPerTake = 4;
constexpr auto kBlobRestateInterval = std::chrono::seconds(5);
/// A datagram's line, kept well under the channel's limit.
constexpr size_t kMaxMessage = 900;
constexpr auto kChangedInterval = std::chrono::milliseconds(50);
constexpr auto kRestateInterval = std::chrono::milliseconds(500);
constexpr auto kOperationInterval = std::chrono::milliseconds(100);

struct Entry {
  float value = 0.0f;
  bool saved = false;
  /// A TEXT value (refii_bridge_player_write_text): `text` holds it and `value` is 0. A key holds
  /// one type; a number written over a text replaces it, and the other way round.
  bool is_text = false;
  std::string text;
};

using Entries = std::map<std::string, Entry>;

struct Operation {
  uint32_t seq = 0;
  bool add = false;
  bool saved = false;
  std::string key;
  float value = 0.0f;
  /// A text set: `str` is the text, `add` and `value` unused.
  bool is_text = false;
  std::string str;
};

struct Assembly {
  uint32_t epoch = 0;
  uint32_t version = 0;
  uint32_t op_epoch = 0;
  uint32_t op_seq = 0;
  uint32_t parts = 0;
  std::vector<bool> have;
  Entries entries;
};

struct BlobAssembly {
  uint32_t version = 0;
  std::vector<std::string> parts;
  size_t have = 0;
};

struct Store {
  /// What this machine reads: the owner's values (with this machine's unapplied operations on
  /// top when it is not the owner).
  Entries entries;
  /// Not the owner: the owner's last complete statement, the base pending operations replay on.
  Entries received;
  uint32_t epoch = 0;
  uint32_t version = 0;
  /// Whether the saved values are this hero's and may be written into its save: true from the
  /// start (a new game), after a load, and after a new game; false once the store has changed
  /// owner, until a load says whose hero this is.
  bool trusted = true;
  /// The save the saved values were last loaded from, or empty.
  std::string loaded_from;
  /// Saved bytes by key (refii_bridge_player_blob_*): written beside the save as
  /// kBlobPrefix<key>.bin and read back with the saved values; the owner's go to the peer (ps.b).
  std::map<std::string, std::vector<uint8_t>> blobs;
  // The owner's side of the blobs: each one's version, the version last queued, the parts queued.
  std::map<std::string, uint32_t> blob_versions;
  std::map<std::string, uint32_t> blob_queued;
  Clock::time_point blobs_restated_at{};
  std::deque<std::string> blob_out;
  // The peer's side: blobs arriving in parts.
  std::map<std::string, BlobAssembly> blob_in;
  // The owner's side.
  uint32_t sent_version = 0;
  Clock::time_point sent_at{};
  bool sent_once = false;
  uint32_t peer_op_epoch = 0;
  uint32_t peer_op_seq = 0;
  // The writer's side.
  std::deque<Operation> pending;
  Clock::time_point ops_sent_at{};
  Assembly assembly;
};

struct State {
  std::mutex mutex;
  Store stores[2];
  uint32_t owned = 3u;
  uint32_t op_epoch = 0;
  uint32_t op_seq = 0;
  std::string root;
  refii::players::SaveRootResolver resolver = nullptr;
};

/// `State::owned`, written under the mutex, so the guest's per-tick call that nearly always
/// restates the same mask and the per-call readers skip the lock.
std::atomic<uint32_t> g_owned_mirror{3u};

uint32_t NewEpoch() {
  static uint32_t counter = 0;
  const auto now = static_cast<uint64_t>(Clock::now().time_since_epoch().count());
  const uint32_t epoch = static_cast<uint32_t>(now ^ (now >> 32)) + (++counter * 0x9E3779B9u);
  return epoch != 0u ? epoch : 1u;
}

State& Tables() {
  static State state;
  static const bool seeded = [] {
    state.op_epoch = NewEpoch();
    state.stores[0].epoch = NewEpoch();
    state.stores[1].epoch = NewEpoch();
    return true;
  }();
  (void)seeded;
  return state;
}

bool KeyChar(char c) {
  return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' ||
         c == '-';
}

bool ValidKey(std::string_view key) {
  return !key.empty() && key.size() <= kMaxKeyLength && std::all_of(key.begin(), key.end(), KeyChar);
}

int Index(int32_t player) { return player == 1 ? 0 : (player == 2 ? 1 : -1); }

bool Owns(const State& s, int index) { return ((s.owned >> index) & 1u) != 0u; }

void Apply(Entries& entries, const Operation& op) {
  Entry& entry = entries[op.key];
  if (op.is_text) {
    entry.is_text = true;
    entry.text = op.str;
    entry.value = 0.0f;
  } else {
    if (entry.is_text) {
      entry.is_text = false;
      entry.text.clear();
      entry.value = 0.0f;
    }
    entry.value = op.add ? entry.value + op.value : op.value;
  }
  entry.saved = entry.saved || op.saved;
}

/// Whether the saved values differ — what a mod's menus list (a level, a pick). A change to one
/// moves the UI data generation like the game's own levels do; the per-tick pools do not.
bool SavedDiffer(const Entries& before, const Entries& after) {
  const auto saved_only = [](const Entries& entries) {
    std::vector<std::tuple<std::string_view, float, bool, std::string_view>> out;
    for (const auto& [key, entry] : entries) {
      if (entry.saved) {
        out.emplace_back(key, entry.value, entry.is_text, entry.text);
      }
    }
    return out;
  };
  return saved_only(before) != saved_only(after);
}

/// Not the owner: the owner's statement with what has not landed yet on top.
void Rebuild(Store& store) {
  store.entries = store.received;
  for (const Operation& op : store.pending) {
    Apply(store.entries, op);
  }
}

std::string Number(float value) {
  char text[32];
  std::snprintf(text, sizeof(text), "%.9g", static_cast<double>(value));
  return text;
}

bool ParseFloat(std::string_view text, float& out) {
  const std::string copy(text);
  char* end = nullptr;
  const float value = std::strtof(copy.c_str(), &end);
  if (end == copy.c_str() || *end != '\0') {
    return false;
  }
  out = value;
  return true;
}

bool ParseU32(std::string_view text, uint32_t& out) {
  const std::string copy(text);
  char* end = nullptr;
  const unsigned long value = std::strtoul(copy.c_str(), &end, 10);
  if (end == copy.c_str() || *end != '\0') {
    return false;
  }
  out = static_cast<uint32_t>(value);
  return true;
}

std::vector<std::string_view> Split(std::string_view text, char separator) {
  std::vector<std::string_view> parts;
  size_t start = 0;
  while (start <= text.size()) {
    const size_t at = text.find(separator, start);
    const size_t end = at == std::string_view::npos ? text.size() : at;
    parts.push_back(text.substr(start, end - start));
    if (at == std::string_view::npos) {
      break;
    }
    start = at + 1;
  }
  return parts;
}

std::string Base64(const uint8_t* data, size_t size);
bool Unbase64(std::string_view text, std::vector<uint8_t>& out);

/// A text from its base64, refused past kMaxTextLength.
bool ParseText(std::string_view text, std::string& out) {
  std::vector<uint8_t> bytes;
  if (!Unbase64(text, bytes) || bytes.size() > kMaxTextLength) {
    return false;
  }
  out.assign(bytes.begin(), bytes.end());
  return true;
}

std::string TextBase64(const std::string& text) {
  return Base64(reinterpret_cast<const uint8_t*>(text.data()), text.size());
}

/// `<v|s><key>=<value>`, or `<t|T><key>=<base64>` for a text.
bool ParseEntry(std::string_view text, std::string& key, Entry& entry) {
  const size_t equals = text.find('=');
  if (text.size() < 3 || equals == std::string_view::npos) {
    return false;
  }
  const char kind = text[0];
  if (kind != 'v' && kind != 's' && kind != 't' && kind != 'T') {
    return false;
  }
  key = std::string(text.substr(1, equals - 1));
  if (!ValidKey(key)) {
    return false;
  }
  if (kind == 't' || kind == 'T') {
    entry.saved = kind == 'T';
    entry.is_text = true;
    entry.value = 0.0f;
    return ParseText(text.substr(equals + 1), entry.text);
  }
  entry.saved = kind == 's';
  return ParseFloat(text.substr(equals + 1), entry.value);
}

void Append(char* out, size_t capacity, size_t& written, std::string_view line) {
  if (written + line.size() + 1 > capacity) {
    return;
  }
  std::memcpy(out + written, line.data(), line.size());
  written += line.size();
  out[written++] = '\n';
}

/// The owner's statement of one player, in as many parts as it takes.
void StateMessages(const Store& store, int index, std::vector<std::string>& lines) {
  std::vector<std::string> bodies(1);
  for (const auto& [key, entry] : store.entries) {
    std::string item = entry.is_text ? std::string(1, entry.saved ? 'T' : 't') + key + "=" + TextBase64(entry.text)
                                     : std::string(1, entry.saved ? 's' : 'v') + key + "=" + Number(entry.value);
    if (!bodies.back().empty() && bodies.back().size() + item.size() + 1 > kMaxMessage - 96) {
      bodies.emplace_back();
    }
    if (!bodies.back().empty()) {
      bodies.back() += ';';
    }
    bodies.back() += item;
  }
  for (size_t part = 0; part < bodies.size(); ++part) {
    char head[128];
    std::snprintf(head, sizeof(head), "ps.s=%d,%u,%u,%u,%u,%zu,%zu|", index + 1, store.epoch, store.version,
                  store.peer_op_epoch, store.peer_op_seq, part, bodies.size());
    lines.push_back(std::string(head) + bodies[part]);
  }
}

constexpr char kBase64[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

std::string Base64(const uint8_t* data, size_t size) {
  std::string out;
  out.reserve((size + 2) / 3 * 4);
  for (size_t i = 0; i < size; i += 3) {
    const uint32_t n = (static_cast<uint32_t>(data[i]) << 16) | (i + 1 < size ? data[i + 1] << 8 : 0u) |
                       (i + 2 < size ? data[i + 2] : 0u);
    out += kBase64[(n >> 18) & 63u];
    out += kBase64[(n >> 12) & 63u];
    out += i + 1 < size ? kBase64[(n >> 6) & 63u] : '=';
    out += i + 2 < size ? kBase64[n & 63u] : '=';
  }
  return out;
}

bool Unbase64(std::string_view text, std::vector<uint8_t>& out) {
  uint32_t n = 0;
  int bits = 0;
  for (const char c : text) {
    if (c == '=') {
      break;
    }
    const char* at = std::strchr(kBase64, c);
    if (at == nullptr || c == '\0') {
      return false;
    }
    n = (n << 6) | static_cast<uint32_t>(at - kBase64);
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out.push_back(static_cast<uint8_t>((n >> bits) & 0xFFu));
    }
  }
  return true;
}

/// The owner's blobs for one player, queued in parts: those changed since last queued, and every
/// one (with the key list) at each restatement.
void QueueBlobs(Store& store, int index, Clock::time_point now) {
  const bool restate = now - store.blobs_restated_at >= kBlobRestateInterval;
  for (const auto& [key, data] : store.blobs) {
    const uint32_t version = store.blob_versions[key];
    auto queued = store.blob_queued.find(key);
    if (!restate && queued != store.blob_queued.end() && queued->second == version) {
      continue;
    }
    store.blob_queued[key] = version;
    const std::string text = Base64(data.data(), data.size());
    const size_t parts = std::max<size_t>(1u, (text.size() + kBlobPartChars - 1) / kBlobPartChars);
    for (size_t part = 0; part < parts; ++part) {
      char head[192];
      std::snprintf(head, sizeof(head), "ps.b=%d,%u,%zu,%zu,%s|", index + 1, version, part, parts, key.c_str());
      store.blob_out.push_back(std::string(head) + text.substr(part * kBlobPartChars, kBlobPartChars));
    }
  }
  if (restate) {
    store.blobs_restated_at = now;
    std::string list = "ps.k=" + std::to_string(index + 1) + "|";
    bool first = true;
    for (const auto& [key, data] : store.blobs) {
      list += (first ? "" : ";") + key;
      first = false;
    }
    store.blob_out.push_back(list);
  }
}

/// A blob part, or the key list, from the owner of player `index`.
void TakeBlob(Store& store, std::string_view head, std::string_view body, bool list) {
  if (list) {
    std::erase_if(store.blobs, [&](const auto& item) {
      for (const std::string_view key : Split(body, ';')) {
        if (key == item.first) {
          return false;
        }
      }
      return true;
    });
    return;
  }
  const std::vector<std::string_view> fields = Split(head, ',');
  uint32_t version = 0;
  uint32_t part = 0;
  uint32_t parts = 0;
  if (fields.size() != 5 || !ParseU32(fields[1], version) || !ParseU32(fields[2], part) ||
      !ParseU32(fields[3], parts) || parts == 0u || part >= parts || parts > kMaxBlobBytes / 450u ||
      !ValidKey(fields[4])) {
    return;
  }
  const std::string key(fields[4]);
  BlobAssembly& assembly = store.blob_in[key];
  if (assembly.version != version || assembly.parts.size() != parts) {
    assembly = BlobAssembly{};
    assembly.version = version;
    assembly.parts.resize(parts);
  }
  if (assembly.parts[part].empty() && !body.empty()) {
    assembly.parts[part] = std::string(body);
    ++assembly.have;
  }
  if (assembly.have != parts) {
    return;
  }
  std::string text;
  for (const std::string& chunk : assembly.parts) {
    text += chunk;
  }
  std::vector<uint8_t> data;
  if (Unbase64(text, data) && data.size() <= kMaxBlobBytes) {
    store.blobs[key] = std::move(data);
  }
  store.blob_in.erase(key);
}

void OperationMessages(const State& s, const Store& store, int index, std::vector<std::string>& lines) {
  char head[64];
  std::snprintf(head, sizeof(head), "ps.o=%d,%u|", index + 1, s.op_epoch);
  std::string line = head;
  bool any = false;
  for (const Operation& op : store.pending) {
    char seq[24];
    std::snprintf(seq, sizeof(seq), "%u:", op.seq);
    std::string item = std::string(seq) + (op.is_text ? 't' : (op.add ? 'a' : 's')) + (op.saved ? 's' : 'v') + op.key +
                       "=" + (op.is_text ? TextBase64(op.str) : Number(op.value));
    if (any && line.size() + item.size() + 1 > kMaxMessage) {
      lines.push_back(line);
      line = head;
      any = false;
    }
    if (any) {
      line += ';';
    }
    line += item;
    any = true;
  }
  if (any) {
    lines.push_back(line);
  }
}

void TakeState(State& s, int index, std::string_view head, std::string_view body) {
  const std::vector<std::string_view> fields = Split(head, ',');
  uint32_t epoch = 0, version = 0, op_epoch = 0, op_seq = 0, part = 0, parts = 0;
  if (fields.size() != 7 || !ParseU32(fields[1], epoch) || !ParseU32(fields[2], version) ||
      !ParseU32(fields[3], op_epoch) || !ParseU32(fields[4], op_seq) || !ParseU32(fields[5], part) ||
      !ParseU32(fields[6], parts) || parts == 0 || parts > 64 || part >= parts) {
    return;
  }
  if (Owns(s, index)) {
    return;  // this machine plays the hero; the peer's copy is a stale one
  }

  Store& store = s.stores[index];
  if (epoch == store.epoch && version <= store.version) {
    return;
  }

  Assembly& assembly = store.assembly;
  if (assembly.epoch != epoch || assembly.version != version || assembly.parts != parts) {
    assembly = Assembly{};
    assembly.epoch = epoch;
    assembly.version = version;
    assembly.op_epoch = op_epoch;
    assembly.op_seq = op_seq;
    assembly.parts = parts;
    assembly.have.assign(parts, false);
  }
  if (assembly.have[part]) {
    return;
  }
  assembly.have[part] = true;
  if (!body.empty()) {
    for (std::string_view item : Split(body, ';')) {
      std::string key;
      Entry entry;
      if (ParseEntry(item, key, entry)) {
        assembly.entries[key] = entry;
      }
    }
  }
  if (std::find(assembly.have.begin(), assembly.have.end(), false) != assembly.have.end()) {
    return;
  }

  store.received = std::move(assembly.entries);
  store.epoch = epoch;
  store.version = version;
  if (assembly.op_epoch == s.op_epoch) {
    while (!store.pending.empty() && store.pending.front().seq <= assembly.op_seq) {
      store.pending.pop_front();
    }
  }
  assembly = Assembly{};
  const Entries before = store.entries;
  Rebuild(store);
  if (SavedDiffer(before, store.entries)) {
    refii_bridge_ui_data_changed();
  }
}

void TakeOperations(State& s, int index, std::string_view head, std::string_view body) {
  const std::vector<std::string_view> fields = Split(head, ',');
  uint32_t op_epoch = 0;
  if (fields.size() != 2 || !ParseU32(fields[1], op_epoch) || !Owns(s, index)) {
    return;
  }
  Store& store = s.stores[index];
  if (op_epoch != store.peer_op_epoch) {
    store.peer_op_epoch = op_epoch;
    store.peer_op_seq = 0;
  }
  bool changed = false;
  for (std::string_view item : Split(body, ';')) {
    const size_t colon = item.find(':');
    uint32_t seq = 0;
    if (colon == std::string_view::npos || !ParseU32(item.substr(0, colon), seq) || seq <= store.peer_op_seq) {
      continue;
    }
    const std::string_view rest = item.substr(colon + 1);
    if (rest.size() < 4 || (rest[0] != 's' && rest[0] != 'a' && rest[0] != 't')) {
      continue;
    }
    std::string key;
    Entry entry;
    // A text set's body is `<v|s><key>=<base64>`: read as the text entry it states.
    std::string body(rest.substr(1));
    if (rest[0] == 't' && (body[0] == 'v' || body[0] == 's')) {
      body[0] = body[0] == 's' ? 'T' : 't';
    } else if (rest[0] != 't' && (body[0] == 't' || body[0] == 'T')) {
      continue;
    }
    if (!ParseEntry(body, key, entry)) {
      continue;
    }
    Operation op;
    op.seq = seq;
    op.add = rest[0] == 'a';
    op.saved = entry.saved;
    op.key = std::move(key);
    op.value = entry.value;
    op.is_text = rest[0] == 't';
    op.str = std::move(entry.text);
    Apply(store.entries, op);
    store.peer_op_seq = seq;
    changed = true;
    if (op.saved) {
      refii_bridge_ui_data_changed();
    }
  }
  if (changed) {
    ++store.version;
  }
}

bool SafeName(std::string_view name) {
  return !name.empty() && name.size() < 64 && name != "." && name != ".." &&
         name.find_first_of("/\\") == std::string_view::npos;
}

const std::string& Root(State& s) {
  if (s.root.empty() && s.resolver != nullptr) {
    s.root = s.resolver();
  }
  return s.root;
}

/// The folder of the save named `name`: `<root>/<name>/`, the root being the running title's
/// savegame folder of the signed-in profile.
std::filesystem::path SaveFolder(const std::string& root, std::string_view name) {
  namespace fs = std::filesystem;
  std::error_code error;
  if (root.empty() || !SafeName(name)) {
    return {};
  }
  const fs::path candidate = fs::path(root) / std::string(name);
  return fs::is_directory(candidate, error) ? candidate : fs::path{};
}

/// Nesting depth of replica dispatches on this thread (the guest tick thread runs every event).
thread_local int g_replica = 0;

}  // namespace

extern "C" void refii_bridge_player_replica(int32_t on) {
  g_replica += on != 0 ? 1 : (g_replica > 0 ? -1 : 0);
}

extern "C" int32_t refii_bridge_player_in_replica(void) { return g_replica > 0 ? 1 : 0; }

extern "C" int32_t refii_bridge_player_get(int32_t player, const char* key, size_t key_length, float* out) {
  const int index = Index(player);
  if (index < 0 || key == nullptr || key_length > kMaxKeyLength) {
    return 0;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  const auto found = s.stores[index].entries.find(std::string(key, key_length));
  if (found == s.stores[index].entries.end() || found->second.is_text) {
    return 0;
  }
  if (out != nullptr) {
    *out = found->second.value;
  }
  return 1;
}

extern "C" int32_t refii_bridge_player_get_text(int32_t player, const char* key, size_t key_length, char* out,
                                                uint32_t capacity) {
  const int index = Index(player);
  if (index < 0 || key == nullptr || key_length > kMaxKeyLength) {
    return -1;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  const auto found = s.stores[index].entries.find(std::string(key, key_length));
  if (found == s.stores[index].entries.end() || !found->second.is_text) {
    return -1;
  }
  const std::string& text = found->second.text;
  if (out != nullptr && capacity != 0) {
    const size_t n = std::min<size_t>(text.size(), capacity - 1);
    std::memcpy(out, text.data(), n);
    out[n] = 0;
  }
  return static_cast<int32_t>(text.size());
}

extern "C" void refii_bridge_player_write(int32_t player, const char* key, size_t key_length, float value,
                                          int32_t add, int32_t saved) {
  const int index = Index(player);
  if (index < 0 || key == nullptr || !ValidKey(std::string_view(key, key_length)) || g_replica > 0) {
    return;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  Store& store = s.stores[index];
  Operation op;
  op.add = add != 0;
  op.saved = saved != 0;
  op.key = std::string(key, key_length);
  op.value = value;
  if (Owns(s, index)) {
    const auto found = store.entries.find(op.key);
    if (!op.add && found != store.entries.end() && !found->second.is_text && found->second.value == value &&
        found->second.saved == op.saved) {
      return;
    }
    if (op.add && value == 0.0f) {
      return;
    }
    Apply(store.entries, op);
    ++store.version;
    if (op.saved) {
      refii_bridge_ui_data_changed();
    }
    return;
  }
  const bool saved_op = op.saved;
  op.seq = ++s.op_seq;
  store.pending.push_back(std::move(op));
  Rebuild(store);
  if (saved_op) {
    refii_bridge_ui_data_changed();
  }
}

extern "C" void refii_bridge_player_write_text(int32_t player, const char* key, size_t key_length, const char* text,
                                               size_t length, int32_t saved) {
  const int index = Index(player);
  if (index < 0 || key == nullptr || !ValidKey(std::string_view(key, key_length)) || g_replica > 0) {
    return;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  Store& store = s.stores[index];
  Operation op;
  op.is_text = true;
  op.saved = saved != 0;
  op.key = std::string(key, key_length);
  op.str = text != nullptr ? std::string(text, std::min(length, kMaxTextLength)) : std::string();
  if (Owns(s, index)) {
    const auto found = store.entries.find(op.key);
    if (found != store.entries.end() && found->second.is_text && found->second.text == op.str &&
        found->second.saved == op.saved) {
      return;
    }
    Apply(store.entries, op);
    ++store.version;
    if (op.saved) {
      refii_bridge_ui_data_changed();
    }
    return;
  }
  const bool saved_op = op.saved;
  op.seq = ++s.op_seq;
  store.pending.push_back(std::move(op));
  Rebuild(store);
  if (saved_op) {
    refii_bridge_ui_data_changed();
  }
}

extern "C" void refii_bridge_player_set_owned(uint32_t mask) {
  if ((mask & 3u) == g_owned_mirror.load(std::memory_order_acquire)) {
    return;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  mask &= 3u;
  if (mask == s.owned) {
    return;
  }
  for (int index = 0; index < 2; ++index) {
    const bool was = Owns(s, index);
    const bool now = ((mask >> index) & 1u) != 0u;
    Store& store = s.stores[index];
    if (now && !was) {
      // Played here from now on (the peer left): what it held was the peer's hero's, so it
      // starts empty, under a new epoch, and is not written into a save until a load says
      // whose hero this is.
      store = Store{};
      store.epoch = NewEpoch();
      store.trusted = false;
    } else if (was && !now) {
      // The peer plays it now: nothing is known until the peer says.
      store = Store{};
      store.trusted = false;
    }
  }
  REXLOG_INFO("Refii player state: this machine plays {}{}", (mask & 1u) != 0u ? "player one " : "",
              (mask & 2u) != 0u ? "player two" : (mask == 0u ? "no player" : ""));
  s.owned = mask;
  g_owned_mirror.store(mask, std::memory_order_release);
}

extern "C" uint32_t refii_bridge_player_owned(void) {
  return g_owned_mirror.load(std::memory_order_acquire);
}

extern "C" void refii_bridge_player_new_hero(int32_t player) {
  const int index = Index(player);
  if (index < 0) {
    return;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  if (!Owns(s, index)) {
    return;
  }
  Entries& entries = s.stores[index].entries;
  const size_t before = entries.size();
  std::erase_if(entries, [](const auto& item) { return !item.second.saved; });
  if (entries.size() != before) {
    ++s.stores[index].version;
  }
}

extern "C" void refii_bridge_player_clear(int32_t player, int32_t fresh) {
  const int index = Index(player);
  if (index < 0) {
    return;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  Store& store = s.stores[index];
  if (!Owns(s, index)) {
    return;
  }
  store.trusted = fresh != 0;
  store.loaded_from.clear();
  store.blobs.clear();
  if (store.entries.empty()) {
    return;
  }
  store.entries.clear();
  ++store.version;
  REXLOG_INFO("Refii player state: player {}'s values cleared here ({})", player,
              fresh != 0 ? "a new hero" : "the player left");
}

extern "C" void refii_bridge_player_load_saved(int32_t player, const char* save_name) {
  const int index = Index(player);
  if (index < 0 || save_name == nullptr) {
    return;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  Entries loaded;
  const std::filesystem::path folder = SaveFolder(Root(s), save_name);
  if (folder.empty()) {
    REXLOG_WARN("Refii player state: no folder for save '{}' under '{}'; player {} keeps its saved values", save_name,
                s.root, player);
    return;
  }
  std::ifstream file(folder / std::string(refii::players::kSaveFileName));
  std::string line;
  while (file && std::getline(file, line)) {
    if (!line.empty() && line.back() == '\r') {
      line.pop_back();
    }
    const size_t equals = line.find('=');
    if (line.empty() || line[0] == '#' || equals == std::string::npos ||
        !ValidKey(std::string_view(line).substr(0, equals))) {
      continue;
    }
    const std::string_view value_text = std::string_view(line).substr(equals + 1);
    Entry entry;
    entry.saved = true;
    if (value_text.starts_with(kTextSavePrefix)) {
      entry.is_text = true;
      if (!ParseText(value_text.substr(kTextSavePrefix.size()), entry.text)) {
        continue;
      }
    } else if (!ParseFloat(value_text, entry.value)) {
      continue;
    }
    loaded[line.substr(0, equals)] = std::move(entry);
  }
  Store& store = s.stores[index];
  std::erase_if(store.entries, [](const auto& item) { return item.second.saved; });
  for (const auto& [key, entry] : loaded) {
    store.entries[key] = entry;
  }
  ++store.version;
  store.trusted = true;
  store.loaded_from = save_name;
  store.blobs.clear();
  std::error_code listing;
  for (const std::filesystem::directory_entry& item : std::filesystem::directory_iterator(folder, listing)) {
    const std::string file_name = item.path().filename().string();
    if (!file_name.starts_with(kBlobPrefix) || !file_name.ends_with(kBlobSuffix)) {
      continue;
    }
    const std::string key =
        file_name.substr(kBlobPrefix.size(), file_name.size() - kBlobPrefix.size() - kBlobSuffix.size());
    std::error_code sized;
    const auto bytes = std::filesystem::file_size(item.path(), sized);
    if (!ValidKey(key) || sized || bytes > kMaxBlobBytes) {
      continue;
    }
    std::ifstream blob_file(item.path(), std::ios::binary);
    std::vector<uint8_t> data(static_cast<size_t>(bytes));
    if (blob_file.read(reinterpret_cast<char*>(data.data()), static_cast<std::streamsize>(data.size()))) {
      store.blobs[key] = std::move(data);
      ++store.blob_versions[key];
    }
  }
  REXLOG_INFO("Refii player state: player {} loaded {} saved value(s) and {} blob(s) from save '{}'", player,
              loaded.size(), store.blobs.size(), save_name);
}

extern "C" int32_t refii_bridge_player_loaded_from(int32_t player, const char* save_name) {
  const int index = Index(player);
  if (index < 0 || save_name == nullptr) {
    return 0;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  return s.stores[index].loaded_from == save_name ? 1 : 0;
}

extern "C" int32_t refii_bridge_player_write_saved(int32_t player, const char* save_name) {
  namespace fs = std::filesystem;
  const int index = Index(player);
  if (index < 0 || save_name == nullptr) {
    return 0;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  if (!s.stores[index].trusted) {
    REXLOG_WARN("Refii player state: player {}'s values are not known to be this hero's (its owner changed and "
                "no save was loaded since); NOT written into save '{}'",
                player, save_name);
    return 0;
  }
  const fs::path folder = SaveFolder(Root(s), save_name);
  if (folder.empty()) {
    REXLOG_WARN("Refii player state: no folder for save '{}' under '{}'; player {}'s saved values NOT written",
                save_name, s.root, player);
    return 0;
  }
  std::ostringstream text;
  text << "# Refii: this save's hero's mod values (refii_mod_player_*), key=value (a text: key=t:<base64>)\n";
  size_t count = 0;
  for (const auto& [key, entry] : s.stores[index].entries) {
    if (entry.saved) {
      text << key << '=' << (entry.is_text ? std::string(kTextSavePrefix) + TextBase64(entry.text) : Number(entry.value))
           << '\n';
      ++count;
    }
  }
  const fs::path target = folder / std::string(refii::players::kSaveFileName);
  const fs::path temporary = folder / (std::string(refii::players::kSaveFileName) + ".tmp");
  {
    std::ofstream file(temporary, std::ios::binary | std::ios::trunc);
    file << text.str();
    if (!file) {
      REXLOG_WARN("Refii player state: could not write '{}'", temporary.string());
      return 0;
    }
  }
  std::error_code error;
  fs::rename(temporary, target, error);
  if (error) {
    REXLOG_WARN("Refii player state: could not replace '{}': {}", target.string(), error.message());
    return 0;
  }
  const auto& blobs = s.stores[index].blobs;
  for (const auto& [key, data] : blobs) {
    const fs::path blob_target = folder / (std::string(kBlobPrefix) + key + std::string(kBlobSuffix));
    const fs::path blob_temporary = fs::path(blob_target.string() + ".tmp");
    {
      std::ofstream file(blob_temporary, std::ios::binary | std::ios::trunc);
      file.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
      if (!file) {
        REXLOG_WARN("Refii player state: could not write '{}'", blob_temporary.string());
        continue;
      }
    }
    fs::rename(blob_temporary, blob_target, error);
    if (error) {
      REXLOG_WARN("Refii player state: could not replace '{}': {}", blob_target.string(), error.message());
    }
  }
  // A blob dropped since the last write goes from the save too.
  for (const fs::directory_entry& item : fs::directory_iterator(folder, error)) {
    const std::string file_name = item.path().filename().string();
    if (file_name.starts_with(kBlobPrefix) && file_name.ends_with(kBlobSuffix) &&
        !blobs.contains(file_name.substr(kBlobPrefix.size(),
                                         file_name.size() - kBlobPrefix.size() - kBlobSuffix.size()))) {
      std::error_code removed;
      fs::remove(item.path(), removed);
    }
  }
  REXLOG_INFO("Refii player state: player {} wrote {} saved value(s) and {} blob(s) into save '{}'", player, count,
              blobs.size(), save_name);
  return 1;
}

extern "C" int32_t refii_bridge_player_blob_get(int32_t player, const char* key, uint8_t* out, uint32_t capacity) {
  const int index = Index(player);
  if (index < 0 || key == nullptr) {
    return -1;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  const auto found = s.stores[index].blobs.find(key);
  if (found == s.stores[index].blobs.end()) {
    return -1;
  }
  const auto size = static_cast<int32_t>(found->second.size());
  if (out != nullptr && capacity >= found->second.size()) {
    std::memcpy(out, found->second.data(), found->second.size());
  }
  return size;
}

extern "C" void refii_bridge_player_blob_set(int32_t player, const char* key, const uint8_t* data, uint32_t size) {
  const int index = Index(player);
  if (index < 0 || key == nullptr || !ValidKey(key) || size > kMaxBlobBytes) {
    return;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  Store& store = s.stores[index];
  if (data == nullptr) {
    store.blobs.erase(key);
    store.blob_versions.erase(key);
    store.blob_queued.erase(key);
    return;
  }
  auto& held = store.blobs[key];
  if (held.size() == size && (size == 0u || std::memcmp(held.data(), data, size) == 0)) {
    return;
  }
  held.assign(data, data + size);
  ++store.blob_versions[key];
}

namespace refii::players {

void SetSaveRootResolver(SaveRootResolver resolver) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  s.resolver = resolver;
  s.root.clear();
}

size_t TakeOutgoing(char* out, size_t capacity) {
  if (out == nullptr || capacity == 0) {
    return 0;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  const Clock::time_point now = Clock::now();
  std::vector<std::string> lines;
  for (int index = 0; index < 2; ++index) {
    Store& store = s.stores[index];
    if (Owns(s, index)) {
      const bool changed = !store.sent_once || store.version != store.sent_version;
      const auto since = now - store.sent_at;
      if ((changed && since >= kChangedInterval) || since >= kRestateInterval) {
        StateMessages(store, index, lines);
        store.sent_version = store.version;
        store.sent_at = now;
        store.sent_once = true;
      }
      QueueBlobs(store, index, now);
      for (size_t n = 0; n < kBlobPartsPerTake && !store.blob_out.empty(); ++n) {
        lines.push_back(std::move(store.blob_out.front()));
        store.blob_out.pop_front();
      }
    } else if (!store.pending.empty() && now - store.ops_sent_at >= kOperationInterval) {
      OperationMessages(s, store, index, lines);
      store.ops_sent_at = now;
    }
  }
  size_t written = 0;
  for (const std::string& line : lines) {
    Append(out, capacity, written, line);
  }
  return written;
}

bool Receive(std::string_view message) {
  const bool state = message.starts_with("ps.s=");
  const bool blob = message.starts_with("ps.b=");
  const bool keys = message.starts_with("ps.k=");
  if (!state && !blob && !keys && !message.starts_with("ps.o=")) {
    return false;
  }
  const std::string_view rest = message.substr(5);
  const size_t bar = rest.find('|');
  if (bar == std::string_view::npos) {
    return true;
  }
  const std::string_view head = rest.substr(0, bar);
  const std::string_view body = rest.substr(bar + 1);
  const int index = head.starts_with("1,") ? 0 : (head.starts_with("2,") ? 1 : -1);
  if (index < 0) {
    return true;
  }
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  if (blob || keys) {
    if (!Owns(s, index)) {
      TakeBlob(s.stores[index], head, body, keys);
    }
  } else if (state) {
    TakeState(s, index, head, body);
  } else {
    TakeOperations(s, index, head, body);
  }
  return true;
}

std::string Describe() {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  std::string text = "owned=" + std::to_string(s.owned);
  for (int index = 0; index < 2; ++index) {
    const Store& store = s.stores[index];
    text += " p" + std::to_string(index + 1) + "{v" + std::to_string(store.version) + " pending " +
            std::to_string(store.pending.size());
    for (const auto& [key, entry] : store.entries) {
      text += " " + key + (entry.saved ? "*" : "") + "=" + (entry.is_text ? "\"" + entry.text + "\"" : Number(entry.value));
    }
    text += "}";
  }
  return text;
}

}  // namespace refii::players
