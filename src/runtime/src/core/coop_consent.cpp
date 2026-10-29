// refii — PARTNER CONSENT: a co-op script asks the other player before doing something that
// changes both players' game (Fable 3's bed consent: sleeping moves the clock for both, sex fades
// both screens). The script asks and waits (refii_bridge_consent_ask / _answer, Lua natives in
// mod_lua_natives.cpp); the host bridge hands the question to Godot, which puts it to the partner
// (their half's pad in local co-op, the peer's machine online) and hands the answer back.
//
// Here, in refii_runtime, because the payload (the natives) and the host bridge both link it.

#include <cstdint>
#include <cstdio>
#include <deque>
#include <mutex>
#include <string>
#include <unordered_map>

#include "core/payload_bridge.h"

namespace {

struct Ask {
  int32_t kind = 0;
  int32_t asker = 0;
  int32_t detail = 0;
  std::string text;
  bool taken = false;
};

constexpr size_t kMaxQuestion = 240;

struct State {
  std::mutex mutex;
  uint32_t next = 0;
  std::unordered_map<uint32_t, Ask> asks;
  std::unordered_map<uint32_t, int32_t> answers;
  std::deque<uint32_t> order;
};

State& Tables() {
  static State state;
  return state;
}

constexpr size_t kMaxRemembered = 64;

}  // namespace

static uint32_t Put(int32_t kind, int32_t asker, int32_t detail, const char* text) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  const uint32_t ticket = ++s.next;
  std::string question = text != nullptr ? text : "";
  if (question.size() > kMaxQuestion) {
    question.resize(kMaxQuestion);
  }
  s.asks[ticket] = Ask{kind, asker, detail, std::move(question), false};
  s.order.push_back(ticket);
  while (s.order.size() > kMaxRemembered) {
    s.asks.erase(s.order.front());
    s.answers.erase(s.order.front());
    s.order.pop_front();
  }
  return ticket;
}

extern "C" uint32_t refii_bridge_consent_ask(int32_t kind, int32_t asker, int32_t detail) {
  return Put(kind, asker, detail, nullptr);
}

extern "C" uint32_t refii_bridge_consent_ask_text(int32_t asker, const char* text) {
  return Put(0, asker, 0, text);
}

extern "C" int32_t refii_bridge_consent_answer(uint32_t ticket) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  const auto found = s.answers.find(ticket);
  return found != s.answers.end() ? found->second : -1;
}

extern "C" int32_t refii_bridge_consent_take(uint32_t* ticket, int32_t* kind, int32_t* asker, int32_t* detail,
                                             char* text, uint32_t capacity) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  for (const uint32_t at : s.order) {
    Ask& ask = s.asks[at];
    if (!ask.taken && s.answers.find(at) == s.answers.end()) {
      ask.taken = true;
      *ticket = at;
      *kind = ask.kind;
      *asker = ask.asker;
      *detail = ask.detail;
      if (text != nullptr && capacity != 0u) {
        std::snprintf(text, capacity, "%s", ask.text.c_str());
      }
      return 1;
    }
  }
  return 0;
}

extern "C" void refii_bridge_consent_give(uint32_t ticket, int32_t yes) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  if (s.asks.find(ticket) != s.asks.end()) {
    s.answers[ticket] = yes != 0 ? 1 : 0;
  }
}
