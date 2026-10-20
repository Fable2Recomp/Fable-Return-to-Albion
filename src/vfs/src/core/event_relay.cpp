// refii — THE EVENT RELAY. The contract and the wire are in core/event_relay.h; the payload's C
// entries are declared in core/payload_bridge.h.

#include "core/event_relay.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <vector>

#include "core/payload_bridge.h"

namespace {

using Clock = std::chrono::steady_clock;

/// How long a posted event is restated, and how often.
constexpr auto kKeep = std::chrono::milliseconds(400);
constexpr auto kRestate = std::chrono::milliseconds(60);
/// How long a received event waits for the payload's tick before it is stale (a load, a pause).
constexpr auto kInboxLife = std::chrono::milliseconds(1500);
constexpr size_t kMaxOutbox = 128;
constexpr size_t kMaxInbox = 128;
constexpr size_t kMaxEvent = 400;
/// A datagram's line, kept well under the channel's limit (as the player state's).
constexpr size_t kMaxMessage = 900;
constexpr std::string_view kPrefix = "ps.e=";

struct Posted {
  uint32_t seq = 0;
  std::string text;
  Clock::time_point at{};
};

struct Arrived {
  std::string text;
  Clock::time_point at{};
};

struct State {
  std::mutex mutex;
  uint32_t epoch = 0;
  uint32_t seq = 0;
  std::deque<Posted> outbox;
  uint32_t sent_seq = 0;
  Clock::time_point sent_at{};
  uint32_t peer_epoch = 0;
  uint32_t peer_seq = 0;
  std::deque<Arrived> inbox;
};

State& Relay() {
  // Built once, thread-safely, on first use by either side; never destroyed (the payload may post
  // during process teardown).
  static State* const state = [] {
    auto* made = new State;
    const auto now = static_cast<uint64_t>(Clock::now().time_since_epoch().count());
    const uint32_t epoch = static_cast<uint32_t>(now ^ (now >> 32));
    made->epoch = epoch != 0 ? epoch : 1u;
    return made;
  }();
  return *state;
}

bool Valid(std::string_view text) {
  return !text.empty() && text.size() <= kMaxEvent && text.find_first_of(";|\n\r") == std::string_view::npos;
}

void Append(char* out, size_t capacity, size_t& written, const std::string& line) {
  if (written + line.size() + 1 > capacity) {
    return;
  }
  std::memcpy(out + written, line.data(), line.size());
  written += line.size();
  out[written++] = '\n';
}

}  // namespace

extern "C" void refii_bridge_event_relay_post(const char* text) {
  if (text == nullptr || !Valid(text)) {
    return;
  }
  State& s = Relay();
  std::lock_guard lock(s.mutex);
  if (s.outbox.size() >= kMaxOutbox) {
    s.outbox.pop_front();
  }
  s.outbox.push_back(Posted{++s.seq, text, Clock::now()});
}

extern "C" int32_t refii_bridge_event_relay_take(char* out, uint32_t capacity) {
  if (out == nullptr || capacity == 0) {
    return 0;
  }
  State& s = Relay();
  std::lock_guard lock(s.mutex);
  const Clock::time_point now = Clock::now();
  while (!s.inbox.empty()) {
    Arrived next = std::move(s.inbox.front());
    s.inbox.pop_front();
    if (now - next.at > kInboxLife || next.text.size() + 1 > capacity) {
      continue;
    }
    std::memcpy(out, next.text.data(), next.text.size());
    out[next.text.size()] = 0;
    return 1;
  }
  return 0;
}

namespace refii::relay {

size_t TakeOutgoing(char* out, size_t capacity) {
  if (out == nullptr || capacity == 0) {
    return 0;
  }
  State& s = Relay();
  std::lock_guard lock(s.mutex);
  const Clock::time_point now = Clock::now();
  while (!s.outbox.empty() && now - s.outbox.front().at > kKeep) {
    s.outbox.pop_front();
  }
  if (s.outbox.empty() || (s.outbox.back().seq == s.sent_seq && now - s.sent_at < kRestate)) {
    return 0;
  }
  s.sent_seq = s.outbox.back().seq;
  s.sent_at = now;

  char head[32];
  std::snprintf(head, sizeof(head), "%.*s%u|", static_cast<int>(kPrefix.size()), kPrefix.data(), s.epoch);
  size_t written = 0;
  std::string line = head;
  for (const Posted& posted : s.outbox) {
    const std::string item = std::to_string(posted.seq) + ":" + posted.text;
    if (line.size() > std::strlen(head) && line.size() + item.size() + 1 > kMaxMessage) {
      Append(out, capacity, written, line);
      line = head;
    }
    if (line.size() > std::strlen(head)) {
      line += ';';
    }
    line += item;
  }
  Append(out, capacity, written, line);
  return written;
}

bool Receive(std::string_view message) {
  if (!message.starts_with(kPrefix)) {
    return false;
  }
  const std::string_view rest = message.substr(kPrefix.size());
  const size_t bar = rest.find('|');
  if (bar == std::string_view::npos) {
    return true;
  }
  const uint32_t epoch = static_cast<uint32_t>(std::strtoul(std::string(rest.substr(0, bar)).c_str(), nullptr, 10));
  std::string_view body = rest.substr(bar + 1);
  State& s = Relay();
  std::lock_guard lock(s.mutex);
  if (epoch != s.peer_epoch) {
    // A new sender (the peer's process restarted, or a new peer): everything it restates is new.
    s.peer_epoch = epoch;
    s.peer_seq = 0;
  }
  const Clock::time_point now = Clock::now();
  uint32_t highest = s.peer_seq;
  while (!body.empty()) {
    const size_t semi = body.find(';');
    const std::string_view item = body.substr(0, semi);
    body = semi == std::string_view::npos ? std::string_view() : body.substr(semi + 1);
    const size_t colon = item.find(':');
    if (colon == std::string_view::npos) {
      continue;
    }
    const uint32_t seq = static_cast<uint32_t>(std::strtoul(std::string(item.substr(0, colon)).c_str(), nullptr, 10));
    const std::string_view text = item.substr(colon + 1);
    if (seq <= s.peer_seq || !Valid(text)) {
      continue;
    }
    if (s.inbox.size() >= kMaxInbox) {
      s.inbox.pop_front();
    }
    s.inbox.push_back(Arrived{std::string(text), now});
    highest = seq > highest ? seq : highest;
  }
  s.peer_seq = highest;
  return true;
}

}  // namespace refii::relay
