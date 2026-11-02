// refii — THE CAST PAIRS: one co-op cast's spell on this machine named against the same cast's
// spell on the peer, so a mod sees one identity for it on both.
//
// Online the game makes a cast's spell on BOTH machines: the deciding machine's cast action
// replicates its own Start as message 0x5B (an area release), 0x5C (a targeted release) or 0x5E
// (a stated spell cast), and the other machine rebuilds that action from the message and runs the
// same Start, whose spell manager then launches through its own factory with its own serial. The
// messages carry no serial, no spell type and no sequence number; what they carry is the caster's
// network id (`sub_821B2390`) and the action's own fields, and the receiver stores those bytes back
// into the same fields of the same class (mod_events.cpp quotes each store). So THE KEY of a cast
// is the message id, the caster's network id and those fields' raw bits, read at the action's
// Start: equal on both machines by construction.
//
// ORDER. The three messages travel on one XRNM channel sent guaranteed and sequential (the
// reliable list's `Net_XrnmSend(link, data, size, 0x2B, 0x7FFFFFFE)`; XRNM delivers a sequential
// sub only at the channel's next sequence number), so the casts of identical keys start on the
// receiver in the order the sender started them. Each machine announces every cast it starts, in
// Start order, on the route (ordered and acknowledged), so identical keys pair in that order: the
// oldest unpaired cast of a key here with the oldest unpaired cast of that key from the peer.
// Every unpaired cast is dropped after kUnpairedLife, so a cast one side never started (its
// receiver could not name the caster) cannot shift a later pair of that key for longer than that.
//
// A cast's spells are its factory calls while its action is the caster's current action, numbered
// in creation order (a release cascade launches several); spell k here pairs with spell k there.
// The machine deciding the caster stamps the cast; the other adopts its tag and its SPELL STATE —
// what that machine decided about the spell that the game's copy on the other machine cannot know
// (SpellState below), carried on the spell's own line. Until the pair forms the copy is AWAITING
// (Table::AwaitsPeer), bounded by the age past which its cast can no longer pair.
//
// Pure: no guest memory, no clock of its own (the caller passes seconds); tools/tests/
// cast_pairing_test.cpp holds the rule.

#pragma once

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <deque>
#include <vector>

namespace refii::cast_pairing {

/// The message ids the three cast actions replicate as (each message vtable's id slot:
/// `li r3,0x5B` 0x82326c08, `li r3,0x5C` 0x82326c10, `li r3,0x5E` 0x825e8068).
constexpr uint32_t kMessageAreaRelease = 0x5B;
constexpr uint32_t kMessageTargetedRelease = 0x5C;
constexpr uint32_t kMessageSpellCast = 0x5E;

/// FNV-1a 64 over the key's canonical bytes, each value big-endian.
class Key {
 public:
  Key(uint32_t message, uint64_t caster) {
    Byte(static_cast<uint8_t>(message));
    U64(caster);
  }
  Key& Byte(uint8_t value) {
    hash_ = (hash_ ^ value) * 1099511628211ull;
    return *this;
  }
  Key& U32(uint32_t value) {
    for (int shift = 24; shift >= 0; shift -= 8) {
      Byte(static_cast<uint8_t>(value >> shift));
    }
    return *this;
  }
  Key& U64(uint64_t value) {
    U32(static_cast<uint32_t>(value >> 32));
    return U32(static_cast<uint32_t>(value));
  }
  uint64_t Value() const { return hash_; }

 private:
  uint64_t hash_ = 14695981039346656037ull;
};

/// 0x5B: the anim (+0x58) and the one float (+0x7C) the area release sends.
inline uint64_t AreaReleaseKey(uint64_t caster, uint32_t anim, uint32_t float_7c) {
  return Key(kMessageAreaRelease, caster).U32(anim).U32(float_7c).Value();
}

/// 0x5C: the anim (+0x58), the vector's three floats (+0x80; the writer sends x, y, z only), the
/// target's network id (0 for none or an entity off the network), the floats +0x90 and +0x94.
inline uint64_t TargetedReleaseKey(uint64_t caster, uint32_t anim, const uint32_t xyz[3], uint64_t target,
                                   uint32_t float_90, uint32_t float_94) {
  return Key(kMessageTargetedRelease, caster)
      .U32(anim)
      .U32(xyz[0])
      .U32(xyz[1])
      .U32(xyz[2])
      .U64(target)
      .U32(float_90)
      .U32(float_94)
      .Value();
}

/// 0x5E: the anim (+0x58), the spell type (+0x78), targeted (+0x7C), level (+0x80), the byte +0x84
/// and the target's network id.
inline uint64_t SpellCastKey(uint64_t caster, uint32_t anim, uint32_t type, uint32_t targeted, uint32_t level,
                             uint8_t flag, uint64_t target) {
  return Key(kMessageSpellCast, caster).U32(anim).U32(type).U32(targeted).U32(level).Byte(flag).U64(target).Value();
}

/// THE SPELL STATE, bits: what the deciding machine decided about one spell that its copy on the
/// other machine follows once paired.
///   kSpellStateNoLift: a vortex whose VortexStartedEvent `lifts` is false — the update's
///     classification answers a creature as one it cannot lift (mod_events.cpp,
///     rex_VortexComponent_Classify).
constexpr uint32_t kSpellStateNoLift = 1u;

/// THE LANE'S LINES (the route's `pc:` lane, core/event_relay.h), every number decimal but the key:
///   pc:c,<seq>,<decided 0|1>,<message>,<key, 16 hex>   a cast action's Start here
///   pc:s,<seq>,<index>,<serial>,<tag>,<state>         spell <index> of cast <seq>; <state> its SpellState
/// A spell line without <state> reads as state 0.
struct CastLine {
  uint32_t seq = 0;
  bool decided = false;
  uint32_t message = 0;
  uint64_t key = 0;
};
struct SpellLine {
  uint32_t seq = 0;
  int32_t index = -1;
  uint32_t serial = 0;
  int32_t tag = 0;
  uint32_t state = 0;
};

/// Returns the line's length, 0 when it does not fit.
inline size_t FormatLine(const CastLine& c, char* out, size_t capacity) {
  const int n = std::snprintf(out, capacity, "pc:c,%u,%d,%u,%016llx", c.seq, c.decided ? 1 : 0, c.message,
                              static_cast<unsigned long long>(c.key));
  return n > 0 && static_cast<size_t>(n) < capacity ? static_cast<size_t>(n) : 0u;
}
inline size_t FormatLine(const SpellLine& s, char* out, size_t capacity) {
  const int n = std::snprintf(out, capacity, "pc:s,%u,%d,%u,%d,%u", s.seq, s.index, s.serial, s.tag, s.state);
  return n > 0 && static_cast<size_t>(n) < capacity ? static_cast<size_t>(n) : 0u;
}

/// The kind of a lane line, 'c' or 's' with its fields filled; 0 for anything else.
inline char ParseLine(const char* text, CastLine* cast, SpellLine* spell) {
  if (text == nullptr || text[0] != 'p' || text[1] != 'c' || text[2] != ':' || text[3] == 0 || text[4] != ',') {
    return 0;
  }
  if (text[3] == 'c') {
    unsigned int seq = 0;
    unsigned int decided = 0;
    unsigned int message = 0;
    unsigned long long key = 0;
    if (std::sscanf(text + 5, "%u,%u,%u,%llx", &seq, &decided, &message, &key) != 4 || seq == 0) {
      return 0;
    }
    *cast = CastLine{seq, decided != 0, message, key};
    return 'c';
  }
  if (text[3] == 's') {
    unsigned int seq = 0;
    int index = -1;
    unsigned int serial = 0;
    int tag = 0;
    unsigned int state = 0;
    if (std::sscanf(text + 5, "%u,%d,%u,%d,%u", &seq, &index, &serial, &tag, &state) < 4) {
      return 0;
    }
    *spell = SpellLine{seq, index, serial, tag, state};
    return 's';
  }
  return 0;
}

/// A spell here and its partner on the peer, formed once each.
struct SpellPair {
  uint32_t local_serial = 0;
  uint32_t peer_serial = 0;
  /// The peer decided the cast and this machine did not: the local spell takes `tag` and `state`.
  bool adopt = false;
  int32_t tag = 0;
  uint32_t state = 0;
};

class Table {
 public:
  static constexpr double kUnpairedLife = 8.0;
  /// A paired cast still takes spells this long after its Start (a cascade, a late launch).
  static constexpr double kPairedLife = 30.0;
  static constexpr size_t kMaxCasts = 96;
  static constexpr int32_t kMaxSpells = 8;
  static constexpr size_t kMaxPairs = 256;
  /// An action's Start is bound to the spells created while it is current at most this long.
  static constexpr double kBindLife = 30.0;

  /// A cast action's Start here. Returns the sequence number this machine announces it under.
  uint32_t LocalCast(uint64_t key, bool decided, uint32_t action, uint32_t caster, double now,
                     std::vector<SpellPair>* formed) {
    Prune(now);
    Cast cast;
    cast.seq = ++local_seq_;
    cast.key = key;
    cast.decided = decided;
    cast.at = now;
    cast.action = action;
    cast.caster = caster;
    local_.push_back(cast);
    Bound(local_, peer_);
    if (Cast* peer = OldestUnpaired(peer_, key, now)) {
      Link(local_.back(), *peer, formed);
    }
    return cast.seq;
  }

  /// The factory made `serial` for `caster` while `action` was its current action: bound to the
  /// newest Start of that action by that caster. False when no Start is recorded for it. `state`
  /// is the spell's SpellState as this machine decided it.
  bool LocalSpell(uint32_t action, uint32_t caster, uint32_t serial, int32_t tag, double now, uint32_t* seq,
                  int32_t* index, std::vector<SpellPair>* formed, uint32_t state = 0) {
    if (action == 0 || caster == 0 || serial == 0) {
      return false;
    }
    for (auto it = local_.rbegin(); it != local_.rend(); ++it) {
      if (it->action != action || it->caster != caster) {
        continue;
      }
      if (now - it->at > kBindLife || it->launched >= kMaxSpells) {
        return false;
      }
      const int32_t k = it->launched++;
      it->spells[k] = Spell{serial, tag, state, true, false};
      *seq = it->seq;
      *index = k;
      if (Cast* peer = it->paired ? FindBySeq(peer_, it->partner) : nullptr) {
        MatchSpell(*it, *peer, k, formed);
      }
      return true;
    }
    return false;
  }

  /// The peer started a cast (its announcement, in the peer's Start order).
  void PeerCast(uint32_t seq, uint64_t key, bool decided, double now, std::vector<SpellPair>* formed) {
    Prune(now);
    // The route delivers each line once, so a seq already held is a restarted peer's: the old cast goes.
    for (auto it = peer_.begin(); it != peer_.end(); ++it) {
      if (it->seq == seq) {
        Orphan(*it, local_);
        peer_.erase(it);
        break;
      }
    }
    Cast cast;
    cast.seq = seq;
    cast.key = key;
    cast.decided = decided;
    cast.at = now;
    peer_.push_back(cast);
    Bound(peer_, local_);
    if (Cast* local = OldestUnpaired(local_, key, now)) {
      Link(*local, peer_.back(), formed);
    }
  }

  /// The peer's factory made spell `index` of its cast `seq`, with the peer's SpellState for it.
  void PeerSpell(uint32_t seq, int32_t index, uint32_t serial, int32_t tag, std::vector<SpellPair>* formed,
                 uint32_t state = 0) {
    Cast* peer = FindBySeq(peer_, seq);
    if (peer == nullptr || index < 0 || index >= kMaxSpells || serial == 0 || peer->spells[index].present) {
      return;
    }
    peer->spells[index] = Spell{serial, tag, state, true, false};
    if (Cast* local = peer->paired ? FindBySeq(local_, peer->partner) : nullptr) {
      MatchSpell(*local, *peer, index, formed);
    }
  }

  /// The peer announced `peer_serial` and its local partner is not made yet (its cast unpaired,
  /// or paired with that spell still to come here).
  bool PeerSpellPending(uint32_t peer_serial) const {
    for (const Cast& peer : peer_) {
      for (const Spell& s : peer.spells) {
        if (s.present && s.serial == peer_serial) {
          return !s.paired;
        }
      }
    }
    return false;
  }

  /// The local spell `local_serial` is a copy still waiting for the decider's state: its cast here
  /// is not decided by this machine, the spell is not paired, and the cast started at most
  /// kUnpairedLife ago. Past that age an unpaired cast never pairs (OldestUnpaired refuses it,
  /// Prune drops it), and a paired cast whose partner spell has not come is held to the same bound,
  /// so a copy that never pairs follows its own default from then on. False for a spell no Start
  /// bound (no cast recorded: a caster off the network, no peer, offline) and once the cast is gone.
  /// Once false for a spell it stays false.
  bool AwaitsPeer(uint32_t local_serial, double now) const {
    if (local_serial == 0) {
      return false;
    }
    for (const Cast& c : local_) {
      for (int32_t k = 0; k < c.launched; ++k) {
        if (c.spells[k].present && c.spells[k].serial == local_serial) {
          return !c.decided && !c.spells[k].paired && now - c.at <= kUnpairedLife;
        }
      }
    }
    return false;
  }

  uint32_t LocalOfPeer(uint32_t peer_serial) const {
    for (auto it = pairs_.rbegin(); it != pairs_.rend(); ++it) {
      if (peer_serial != 0 && it->peer_serial == peer_serial) {
        return it->local_serial;
      }
    }
    return 0;
  }

  uint32_t PeerOfLocal(uint32_t local_serial) const {
    for (auto it = pairs_.rbegin(); it != pairs_.rend(); ++it) {
      if (local_serial != 0 && it->local_serial == local_serial) {
        return it->peer_serial;
      }
    }
    return 0;
  }

  void Clear() {
    local_.clear();
    peer_.clear();
    pairs_.clear();
  }

 private:
  struct Spell {
    uint32_t serial = 0;
    int32_t tag = 0;
    uint32_t state = 0;
    bool present = false;
    bool paired = false;
  };
  struct Cast {
    uint32_t seq = 0;
    uint64_t key = 0;
    bool decided = false;
    double at = 0.0;
    uint32_t action = 0;
    uint32_t caster = 0;
    /// Paired once (never again, even after the partner is dropped); `partner` its seq there.
    bool paired = false;
    uint32_t partner = 0;
    int32_t launched = 0;
    Spell spells[kMaxSpells];
  };

  static Cast* FindBySeq(std::deque<Cast>& side, uint32_t seq) {
    for (Cast& c : side) {
      if (c.seq == seq) {
        return &c;
      }
    }
    return nullptr;
  }

  static Cast* OldestUnpaired(std::deque<Cast>& side, uint64_t key, double now) {
    for (Cast& c : side) {
      if (!c.paired && c.key == key && now - c.at <= kUnpairedLife) {
        return &c;
      }
    }
    return nullptr;
  }

  /// `cast` is going: its partner on `other` keeps its spells' pairs and pairs no further.
  static void Orphan(const Cast& cast, std::deque<Cast>& other) {
    if (Cast* partner = cast.paired ? FindBySeq(other, cast.partner) : nullptr) {
      partner->partner = 0;
    }
  }

  void Link(Cast& local, Cast& peer, std::vector<SpellPair>* formed) {
    local.paired = true;
    local.partner = peer.seq;
    peer.paired = true;
    peer.partner = local.seq;
    for (int32_t k = 0; k < kMaxSpells; ++k) {
      MatchSpell(local, peer, k, formed);
    }
  }

  void MatchSpell(Cast& local, Cast& peer, int32_t k, std::vector<SpellPair>* formed) {
    Spell& mine = local.spells[k];
    Spell& theirs = peer.spells[k];
    if (!mine.present || !theirs.present || mine.paired) {
      return;
    }
    mine.paired = true;
    theirs.paired = true;
    SpellPair pair{mine.serial, theirs.serial, peer.decided && !local.decided, theirs.tag, theirs.state};
    pairs_.push_back(pair);
    while (pairs_.size() > kMaxPairs) {
      pairs_.pop_front();
    }
    if (formed != nullptr) {
      formed->push_back(pair);
    }
  }

  void Prune(double now) {
    for (std::deque<Cast>* side : {&local_, &peer_}) {
      std::deque<Cast>& casts = *side;
      std::deque<Cast>& other = side == &local_ ? peer_ : local_;
      for (auto it = casts.begin(); it != casts.end();) {
        const double age = now - it->at;
        if ((!it->paired && age > kUnpairedLife) || age > kPairedLife) {
          Orphan(*it, other);
          it = casts.erase(it);
        } else {
          ++it;
        }
      }
    }
  }

  static void Bound(std::deque<Cast>& side, std::deque<Cast>& other) {
    while (side.size() > kMaxCasts) {
      Orphan(side.front(), other);
      side.pop_front();
    }
  }

  std::deque<Cast> local_;
  std::deque<Cast> peer_;
  std::deque<SpellPair> pairs_;
  uint32_t local_seq_ = 0;
};

}  // namespace refii::cast_pairing
