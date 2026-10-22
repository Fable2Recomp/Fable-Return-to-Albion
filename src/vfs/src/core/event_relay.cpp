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

// ---- THE ROUTE (core/event_relay.h): ordered and acknowledged ----------------------------------

constexpr std::string_view kRoutePrefix = "ps.r=";
/// How often unacknowledged lines are restated, and how long a line waits for the peer's ack.
constexpr auto kRouteRestate = std::chrono::milliseconds(120);
constexpr auto kRouteLife = std::chrono::seconds(20);
/// How long a taken line waits for the payload (a load, a pause).
constexpr auto kRouteInboxLife = std::chrono::seconds(30);
/// With nothing to send, the ack is restated this often while the peer has spoken lately.
constexpr auto kRouteAckRestate = std::chrono::milliseconds(500);
constexpr auto kRoutePeerQuiet = std::chrono::seconds(10);
constexpr size_t kMaxRouteOutbox = 512;
constexpr size_t kMaxRouteInbox = 512;
constexpr size_t kMaxRouteLine = 700;
/// Datagrams per take: the oldest lines first, the rest at the next restatement.
constexpr size_t kRouteDatagramsPerTake = 4;

struct RouteState {
  std::mutex mutex;
  uint32_t epoch = 0;
  uint32_t seq = 0;
  std::deque<Posted> outbox;
  uint32_t sent_last = 0;
  Clock::time_point sent_at{};
  Clock::time_point ack_sent_at{};
  bool ack_owed = false;
  uint32_t peer_epoch = 0;
  uint32_t peer_seq = 0;
  Clock::time_point heard_at{};
  std::deque<Arrived> inbox;
  /// THE MOD LANE: the lines prefixed kModLane (a mod's partner messages, refii_mod_send_to_partner),
  /// kept apart from the core co-op Lua's so each consumer takes only its own, in order.
  std::deque<Arrived> mod_inbox;
};

/// The prefix of a route line the mod bus drains (refii_bridge_route_take_mod) instead of the Lua.
constexpr std::string_view kModLane = "pm:";

RouteState& Route() {
  static RouteState* const state = [] {
    auto* made = new RouteState;
    const auto now = static_cast<uint64_t>(Clock::now().time_since_epoch().count());
    const uint32_t epoch = static_cast<uint32_t>((now ^ (now >> 32)) * 0x9E3779B1u);
    made->epoch = epoch != 0 ? epoch : 2u;
    return made;
  }();
  return *state;
}

bool RouteValid(std::string_view text) {
  return !text.empty() && text.size() <= kMaxRouteLine && text.find_first_of(";|\n\r") == std::string_view::npos;
}

uint32_t Number(std::string_view text) {
  return static_cast<uint32_t>(std::strtoul(std::string(text).c_str(), nullptr, 10));
}

/// The route lines this machine owes the peer now (see core/event_relay.h), appended to `out`.
size_t TakeRoute(char* out, size_t capacity) {
  RouteState& s = Route();
  std::lock_guard lock(s.mutex);
  const Clock::time_point now = Clock::now();
  while (!s.outbox.empty() && now - s.outbox.front().at > kRouteLife) {
    s.outbox.pop_front();
  }
  const bool fresh = !s.outbox.empty() && s.outbox.back().seq != s.sent_last;
  const bool restate = !s.outbox.empty() && now - s.sent_at >= kRouteRestate;
  const bool ack = s.ack_owed ||
                   (s.peer_epoch != 0 && now - s.heard_at < kRoutePeerQuiet && now - s.ack_sent_at >= kRouteAckRestate);
  if (!fresh && !restate && !ack) {
    return 0;
  }
  const uint32_t first = s.outbox.empty() ? s.seq + 1u : s.outbox.front().seq;
  char head[80];
  std::snprintf(head, sizeof(head), "%.*s%u,%u,%u,%u|", static_cast<int>(kRoutePrefix.size()), kRoutePrefix.data(),
                s.epoch, s.peer_epoch, s.peer_seq, first);
  const size_t head_size = std::strlen(head);
  size_t written = 0;
  size_t datagrams = 0;
  std::string line = head;
  for (const Posted& posted : s.outbox) {
    const std::string item = std::to_string(posted.seq) + ":" + posted.text;
    if (line.size() > head_size && line.size() + item.size() + 1 > kMaxMessage) {
      Append(out, capacity, written, line);
      line = head;
      if (++datagrams >= kRouteDatagramsPerTake) {
        break;
      }
    }
    if (line.size() > head_size) {
      line += ';';
    }
    line += item;
  }
  if (datagrams < kRouteDatagramsPerTake) {
    Append(out, capacity, written, line);
  }
  if (!s.outbox.empty()) {
    s.sent_last = s.outbox.back().seq;
    s.sent_at = now;
  }
  s.ack_owed = false;
  s.ack_sent_at = now;
  return written;
}

bool ReceiveRoute(std::string_view message) {
  if (!message.starts_with(kRoutePrefix)) {
    return false;
  }
  const std::string_view rest = message.substr(kRoutePrefix.size());
  const size_t bar = rest.find('|');
  if (bar == std::string_view::npos) {
    return true;
  }
  uint32_t head[4] = {};
  std::string_view fields = rest.substr(0, bar);
  for (uint32_t& value : head) {
    const size_t comma = fields.find(',');
    value = Number(fields.substr(0, comma));
    fields = comma == std::string_view::npos ? std::string_view() : fields.substr(comma + 1);
  }
  const uint32_t epoch = head[0];
  const uint32_t ack_epoch = head[1];
  const uint32_t ack_seq = head[2];
  const uint32_t first = head[3];
  std::string_view body = rest.substr(bar + 1);
  RouteState& s = Route();
  std::lock_guard lock(s.mutex);
  const Clock::time_point now = Clock::now();
  if (ack_epoch == s.epoch) {
    while (!s.outbox.empty() && s.outbox.front().seq <= ack_seq) {
      s.outbox.pop_front();
    }
  }
  if (epoch == 0) {
    return true;
  }
  if (epoch != s.peer_epoch) {
    // A new sender (the peer's process restarted, or a new peer): its oldest unacknowledged line on.
    s.peer_epoch = epoch;
    s.peer_seq = first != 0 ? first - 1u : 0u;
  } else if (first != 0 && first - 1u > s.peer_seq) {
    // Lines before <first> were abandoned by the sender: move on from there.
    s.peer_seq = first - 1u;
  }
  s.heard_at = now;
  if (!body.empty()) {
    s.ack_owed = true;
  }
  while (!body.empty()) {
    const size_t semi = body.find(';');
    const std::string_view item = body.substr(0, semi);
    body = semi == std::string_view::npos ? std::string_view() : body.substr(semi + 1);
    const size_t colon = item.find(':');
    if (colon == std::string_view::npos) {
      continue;
    }
    const uint32_t seq = Number(item.substr(0, colon));
    if (seq <= s.peer_seq) {
      continue;
    }
    if (seq != s.peer_seq + 1u) {
      break;  // a gap: the restatement fills it
    }
    s.peer_seq = seq;
    const std::string_view text = item.substr(colon + 1);
    if (!RouteValid(text)) {
      continue;
    }
    std::deque<Arrived>& lane = text.starts_with(kModLane) ? s.mod_inbox : s.inbox;
    if (lane.size() >= kMaxRouteInbox) {
      lane.pop_front();
    }
    lane.push_back(Arrived{std::string(text), now});
  }
  return true;
}

/// One line off a route lane, the oldest first; a line older than kRouteInboxLife or over `capacity` is skipped.
int32_t TakeRouteLine(std::deque<Arrived>& lane, char* out, uint32_t capacity) {
  const Clock::time_point now = Clock::now();
  while (!lane.empty()) {
    Arrived next = std::move(lane.front());
    lane.pop_front();
    if (now - next.at > kRouteInboxLife || next.text.size() + 1 > capacity) {
      continue;
    }
    std::memcpy(out, next.text.data(), next.text.size());
    out[next.text.size()] = 0;
    return 1;
  }
  return 0;
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

extern "C" void refii_bridge_route_post(const char* text) {
  if (text == nullptr || !RouteValid(text)) {
    return;
  }
  RouteState& s = Route();
  std::lock_guard lock(s.mutex);
  if (s.outbox.size() >= kMaxRouteOutbox) {
    s.outbox.pop_front();
  }
  s.outbox.push_back(Posted{++s.seq, text, Clock::now()});
}

extern "C" int32_t refii_bridge_route_take(char* out, uint32_t capacity) {
  if (out == nullptr || capacity == 0) {
    return 0;
  }
  RouteState& s = Route();
  std::lock_guard lock(s.mutex);
  return TakeRouteLine(s.inbox, out, capacity);
}

extern "C" int32_t refii_bridge_route_take_mod(char* out, uint32_t capacity) {
  if (out == nullptr || capacity == 0) {
    return 0;
  }
  RouteState& s = Route();
  std::lock_guard lock(s.mutex);
  return TakeRouteLine(s.mod_inbox, out, capacity);
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

size_t TakeEvents(char* out, size_t capacity);

size_t TakeOutgoing(char* out, size_t capacity) {
  if (out == nullptr || capacity == 0) {
    return 0;
  }
  const size_t routed = TakeRoute(out, capacity);
  return routed + TakeEvents(out + routed, capacity - routed);
}

size_t TakeEvents(char* out, size_t capacity) {
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
  if (ReceiveRoute(message)) {
    return true;
  }
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
