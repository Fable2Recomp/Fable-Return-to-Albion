// The cast pairing rule (Fable2/src/vfs/include/core/cast_pairing.h): keys, the order identical
// keys pair in, spells by index, the tag and spell state the deciding machine gives, a copy
// awaiting that state, the lane's lines, the lifetimes.
// Build: c++ -std=c++20 -I Fable2/src/vfs/include tools/tests/cast_pairing_test.cpp && ./a.out

#include <cassert>
#include <cstdio>
#include <cstring>
#include <vector>

#include "core/cast_pairing.h"

using refii::cast_pairing::AreaReleaseKey;
using refii::cast_pairing::CastLine;
using refii::cast_pairing::FormatLine;
using refii::cast_pairing::kSpellStateNoLift;
using refii::cast_pairing::ParseLine;
using refii::cast_pairing::SpellLine;
using refii::cast_pairing::SpellCastKey;
using refii::cast_pairing::SpellPair;
using refii::cast_pairing::Table;
using refii::cast_pairing::TargetedReleaseKey;

namespace {

void Keys() {
  const uint32_t xyz[3] = {0x3F800000u, 0x40000000u, 0xC1200000u};
  const uint32_t other[3] = {0x3F800000u, 0x40000000u, 0xC1200001u};
  const uint64_t a = TargetedReleaseKey(0x100000007ull, 12, xyz, 0x200000003ull, 0x3E800000u, 0x3F000000u);
  assert(a == TargetedReleaseKey(0x100000007ull, 12, xyz, 0x200000003ull, 0x3E800000u, 0x3F000000u));
  assert(a != TargetedReleaseKey(0x100000007ull, 12, other, 0x200000003ull, 0x3E800000u, 0x3F000000u));
  assert(a != TargetedReleaseKey(0x100000008ull, 12, xyz, 0x200000003ull, 0x3E800000u, 0x3F000000u));
  assert(a != TargetedReleaseKey(0x100000007ull, 12, xyz, 0, 0x3E800000u, 0x3F000000u));
  assert(a != TargetedReleaseKey(0x100000007ull, 12, xyz, 0x200000003ull, 0x3F000000u, 0x3E800000u));
  // The message id is part of the key: equal fields under another message never pair.
  assert(AreaReleaseKey(5, 12, 0x3F800000u) != SpellCastKey(5, 12, 0x3F800000u, 0, 0, 0, 0));
  assert(AreaReleaseKey(5, 12, 0x3F800000u) == AreaReleaseKey(5, 12, 0x3F800000u));
  assert(AreaReleaseKey(5, 12, 0x3F800000u) != AreaReleaseKey(5, 13, 0x3F800000u));
}

/// Identical keys pair in Start order, whichever side announces first.
void IdenticalKeysPairInOrder() {
  const uint64_t k = AreaReleaseKey(1, 2, 3);
  Table t;
  std::vector<SpellPair> formed;
  const uint32_t l1 = t.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  t.PeerCast(71, k, true, 0.1, &formed);
  const uint32_t l2 = t.LocalCast(k, false, 0x40002000u, 0x50000000u, 0.2, &formed);
  t.PeerCast(72, k, true, 0.3, &formed);
  assert(l1 != l2 && formed.empty());
  uint32_t seq = 0;
  int32_t index = -1;
  // The second cast's spell first: it still pairs with the peer's second.
  assert(t.LocalSpell(0x40002000u, 0x50000000u, 902, 0, 0.4, &seq, &index, &formed));
  assert(seq == l2 && index == 0 && formed.empty());
  t.PeerSpell(72, 0, 802, 9, &formed);
  assert(formed.size() == 1 && formed[0].local_serial == 902 && formed[0].peer_serial == 802);
  t.PeerSpell(71, 0, 801, 8, &formed);
  assert(t.LocalSpell(0x40001000u, 0x50000000u, 901, 0, 0.5, &seq, &index, &formed));
  assert(formed.size() == 2 && formed[1].local_serial == 901 && formed[1].peer_serial == 801);
  assert(t.LocalOfPeer(801) == 901 && t.LocalOfPeer(802) == 902);
  assert(t.PeerOfLocal(901) == 801 && t.PeerOfLocal(902) == 802);
  assert(t.LocalOfPeer(803) == 0 && t.PeerOfLocal(903) == 0);

  // Peer first on both, then the locals: the oldest unpaired peer cast of the key takes the oldest local.
  Table u;
  formed.clear();
  u.PeerCast(5, k, true, 0.0, &formed);
  u.PeerCast(6, k, true, 0.0, &formed);
  u.PeerSpell(5, 0, 50, 1, &formed);
  u.PeerSpell(6, 0, 60, 2, &formed);
  u.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.1, &formed);
  u.LocalCast(k, false, 0x40002000u, 0x50000000u, 0.2, &formed);
  assert(u.LocalSpell(0x40001000u, 0x50000000u, 500, 0, 0.3, &seq, &index, &formed));
  assert(u.LocalSpell(0x40002000u, 0x50000000u, 600, 0, 0.3, &seq, &index, &formed));
  assert(formed.size() == 2 && u.LocalOfPeer(50) == 500 && u.LocalOfPeer(60) == 600);
  assert(formed[0].tag == 1 && formed[1].tag == 2);
}

/// Different keys never cross; a cascade's spells pair by index.
void KeysAndCascade() {
  Table t;
  std::vector<SpellPair> formed;
  const uint64_t a = AreaReleaseKey(1, 2, 3);
  const uint64_t b = AreaReleaseKey(1, 2, 4);
  t.LocalCast(a, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  t.PeerCast(1, b, true, 0.0, &formed);
  t.PeerSpell(1, 0, 10, 0, &formed);
  uint32_t seq = 0;
  int32_t index = -1;
  assert(t.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.1, &seq, &index, &formed));
  assert(formed.empty() && t.LocalOfPeer(10) == 0);
  t.PeerCast(2, a, true, 0.2, &formed);
  t.PeerSpell(2, 0, 20, 7, &formed);
  t.PeerSpell(2, 1, 21, 7, &formed);
  t.PeerSpell(2, 2, 22, 7, &formed);
  assert(formed.size() == 1 && formed[0].local_serial == 100 && formed[0].peer_serial == 20);
  assert(t.PeerSpellPending(21) && t.PeerSpellPending(22) && !t.PeerSpellPending(20) && !t.PeerSpellPending(99));
  assert(t.LocalSpell(0x40001000u, 0x50000000u, 101, 0, 0.3, &seq, &index, &formed) && index == 1);
  assert(t.LocalSpell(0x40001000u, 0x50000000u, 102, 0, 0.3, &seq, &index, &formed) && index == 2);
  assert(t.LocalOfPeer(21) == 101 && t.LocalOfPeer(22) == 102 && !t.PeerSpellPending(22));
}

/// The tag is adopted only from the machine that decided the cast, by the one that did not.
void TagFromTheDecider() {
  const uint64_t k = AreaReleaseKey(1, 2, 3);
  std::vector<SpellPair> formed;
  uint32_t seq = 0;
  int32_t index = -1;
  Table replica;
  replica.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  replica.PeerCast(1, k, true, 0.0, &formed);
  replica.PeerSpell(1, 0, 10, 42, &formed);
  replica.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.1, &seq, &index, &formed);
  assert(formed.size() == 1 && formed[0].adopt && formed[0].tag == 42);
  Table decider;
  formed.clear();
  decider.LocalCast(k, true, 0x40001000u, 0x50000000u, 0.0, &formed);
  decider.PeerCast(1, k, false, 0.0, &formed);
  decider.PeerSpell(1, 0, 10, 0, &formed);
  decider.LocalSpell(0x40001000u, 0x50000000u, 100, 42, 0.1, &seq, &index, &formed);
  assert(formed.size() == 1 && !formed[0].adopt);
}

/// The spell state goes the same way as the tag, in both directions: the decider's state reaches
/// the copy whichever side's spell line comes first, and a copy's own state never reaches the decider.
void StateFromTheDecider() {
  const uint64_t k = AreaReleaseKey(1, 2, 3);
  std::vector<SpellPair> formed;
  uint32_t seq = 0;
  int32_t index = -1;
  // The joiner's copy of the host's cast, the host's line first: the pair forms at the local spell.
  Table joiner;
  joiner.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  joiner.PeerCast(1, k, true, 0.0, &formed);
  joiner.PeerSpell(1, 0, 10, 0, &formed, kSpellStateNoLift);
  joiner.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.1, &seq, &index, &formed, 0);
  assert(formed.size() == 1 && formed[0].adopt && formed[0].state == kSpellStateNoLift && formed[0].local_serial == 100);
  // The copy's spell first (its vortex already updating), the decider's line late: the pair forms then.
  Table late;
  formed.clear();
  late.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  late.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.1, &seq, &index, &formed, 0);
  assert(formed.empty());
  late.PeerCast(1, k, true, 0.6, &formed);
  assert(formed.empty());
  late.PeerSpell(1, 0, 10, 0, &formed, kSpellStateNoLift);
  assert(formed.size() == 1 && formed[0].adopt && formed[0].state == kSpellStateNoLift);
  // Player two's cast on the host (the joiner decides it): the host's copy adopts the joiner's state.
  Table host;
  formed.clear();
  host.PeerCast(1, k, true, 0.0, &formed);
  host.PeerSpell(1, 0, 10, 0, &formed, kSpellStateNoLift);
  host.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.1, &formed);
  host.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.1, &seq, &index, &formed, 0);
  assert(formed.size() == 1 && formed[0].adopt && formed[0].state == kSpellStateNoLift);
  // The decider pairs too, but adopts nothing from the copy.
  Table decider;
  formed.clear();
  decider.LocalCast(k, true, 0x40001000u, 0x50000000u, 0.0, &formed);
  decider.PeerCast(1, k, false, 0.0, &formed);
  decider.PeerSpell(1, 0, 10, 0, &formed, 0);
  decider.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.1, &seq, &index, &formed, kSpellStateNoLift);
  assert(formed.size() == 1 && !formed[0].adopt);
  // A cascade's states pair by index.
  Table cascade;
  formed.clear();
  cascade.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  cascade.PeerCast(1, k, true, 0.0, &formed);
  cascade.PeerSpell(1, 0, 10, 0, &formed, 0);
  cascade.PeerSpell(1, 1, 11, 0, &formed, kSpellStateNoLift);
  cascade.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.1, &seq, &index, &formed);
  cascade.LocalSpell(0x40001000u, 0x50000000u, 101, 0, 0.1, &seq, &index, &formed);
  assert(formed.size() == 2 && formed[0].state == 0 && formed[1].state == kSpellStateNoLift && formed[1].local_serial == 101);
}

/// A copy awaits the decider's state from its spell until the pair forms, and no longer than its
/// cast can still pair (kUnpairedLife since its Start); the decider never awaits.
void AwaitingCopy() {
  const uint64_t k = AreaReleaseKey(1, 2, 3);
  std::vector<SpellPair> formed;
  uint32_t seq = 0;
  int32_t index = -1;
  // The host's copy of player two's cast: awaits from its spell until the joiner's lines pair it.
  Table host;
  host.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  assert(!host.AwaitsPeer(100, 0.0));  // no spell bound yet
  host.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.0, &seq, &index, &formed);
  assert(host.AwaitsPeer(100, 0.1) && host.AwaitsPeer(100, 0.6));
  host.PeerCast(1, k, true, 0.7, &formed);
  assert(host.AwaitsPeer(100, 0.7));  // the cast paired, its spell not yet
  host.PeerSpell(1, 0, 10, 0, &formed, kSpellStateNoLift);
  assert(formed.size() == 1 && formed[0].adopt && !host.AwaitsPeer(100, 0.8));
  assert(!host.AwaitsPeer(0, 0.8) && !host.AwaitsPeer(999, 0.8));
  // The decider's own spell never awaits.
  Table decider;
  formed.clear();
  decider.LocalCast(k, true, 0x40001000u, 0x50000000u, 0.0, &formed);
  decider.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.0, &seq, &index, &formed, kSpellStateNoLift);
  assert(!decider.AwaitsPeer(100, 0.1));
  // A copy that never pairs awaits only while its cast can still pair, then follows its own
  // default for good: the peer's cast arriving later pairs nothing.
  Table lone;
  formed.clear();
  lone.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  lone.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.2, &seq, &index, &formed);
  assert(lone.AwaitsPeer(100, Table::kUnpairedLife));
  assert(!lone.AwaitsPeer(100, Table::kUnpairedLife + 0.01));
  lone.PeerCast(1, k, true, Table::kUnpairedLife + 0.5, &formed);
  lone.PeerSpell(1, 0, 10, 0, &formed, kSpellStateNoLift);
  assert(formed.empty() && !lone.AwaitsPeer(100, Table::kUnpairedLife + 0.6));
  // A paired cast whose partner spell never comes: the same bound.
  Table cascade;
  formed.clear();
  cascade.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  cascade.PeerCast(1, k, true, 0.0, &formed);
  cascade.PeerSpell(1, 0, 10, 0, &formed);
  cascade.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.1, &seq, &index, &formed);
  cascade.LocalSpell(0x40001000u, 0x50000000u, 101, 0, 0.1, &seq, &index, &formed);
  assert(!cascade.AwaitsPeer(100, 0.2) && cascade.AwaitsPeer(101, 0.2));
  assert(!cascade.AwaitsPeer(101, Table::kUnpairedLife + 0.01));
  // A cleared table (the session ended) holds nothing back.
  Table cleared;
  formed.clear();
  cleared.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  cleared.LocalSpell(0x40001000u, 0x50000000u, 100, 0, 0.0, &seq, &index, &formed);
  cleared.Clear();
  assert(!cleared.AwaitsPeer(100, 0.1));
}

/// The lane's lines round-trip; a spell line without its state reads as state 0; anything else is no line.
void Lines() {
  char line[96];
  const CastLine c{7, true, 0x5C, 0x0123456789ABCDEFull};
  assert(FormatLine(c, line, sizeof(line)) == std::strlen("pc:c,7,1,92,0123456789abcdef"));
  assert(std::strcmp(line, "pc:c,7,1,92,0123456789abcdef") == 0);
  CastLine cast;
  SpellLine spell;
  assert(ParseLine(line, &cast, &spell) == 'c');
  assert(cast.seq == 7 && cast.decided && cast.message == 0x5C && cast.key == 0x0123456789ABCDEFull);
  const SpellLine s{7, 2, 4000000000u, -3, kSpellStateNoLift};
  assert(FormatLine(s, line, sizeof(line)) != 0 && std::strcmp(line, "pc:s,7,2,4000000000,-3,1") == 0);
  assert(ParseLine(line, &cast, &spell) == 's');
  assert(spell.seq == 7 && spell.index == 2 && spell.serial == 4000000000u && spell.tag == -3 && spell.state == kSpellStateNoLift);
  assert(ParseLine("pc:s,7,0,900,5", &cast, &spell) == 's' && spell.serial == 900 && spell.tag == 5 && spell.state == 0);
  assert(ParseLine("pc:c,0,1,92,ff", &cast, &spell) == 0);
  assert(ParseLine("pc:s,7,0", &cast, &spell) == 0);
  assert(ParseLine("pc:x,1,2,3,4", &cast, &spell) == 0);
  assert(ParseLine("pm:s,1,2,3,4", &cast, &spell) == 0);
  assert(ParseLine("pc:", &cast, &spell) == 0);
  assert(FormatLine(s, line, 8) == 0);
}

/// An unpaired cast is dropped after kUnpairedLife, so a later cast of the key pairs with the
/// peer's later one; a paired cast never pairs again once its partner is gone.
void Lifetimes() {
  const uint64_t k = AreaReleaseKey(1, 2, 3);
  std::vector<SpellPair> formed;
  uint32_t seq = 0;
  int32_t index = -1;
  Table t;
  t.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.0, &formed);  // its peer cast never comes
  t.LocalCast(k, false, 0x40002000u, 0x50000000u, Table::kUnpairedLife + 1.0, &formed);
  t.PeerCast(9, k, true, Table::kUnpairedLife + 1.5, &formed);
  t.PeerSpell(9, 0, 90, 0, &formed);
  assert(t.LocalSpell(0x40002000u, 0x50000000u, 200, 0, Table::kUnpairedLife + 2.0, &seq, &index, &formed));
  assert(t.LocalOfPeer(90) == 200);
  // The dropped Start binds nothing.
  assert(!t.LocalSpell(0x40001000u, 0x50000000u, 100, 0, Table::kUnpairedLife + 2.0, &seq, &index, &formed));
  // A Start is bound only within kBindLife.
  Table b;
  b.LocalCast(k, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  b.PeerCast(1, k, true, 0.0, &formed);
  assert(!b.LocalSpell(0x40001000u, 0x50000000u, 1, 0, Table::kBindLife + 1.0, &seq, &index, &formed));
  // Unknown actions, casters and the null serial bind nothing.
  assert(!b.LocalSpell(0x40009000u, 0x50000000u, 1, 0, 0.1, &seq, &index, &formed));
  assert(!b.LocalSpell(0x40001000u, 0x50000001u, 1, 0, 0.1, &seq, &index, &formed));
  assert(!b.LocalSpell(0x40001000u, 0x50000000u, 0, 0, 0.1, &seq, &index, &formed));
}

/// The newest Start of a reused action object binds; a restarted peer's reused seq replaces the old cast.
void ReuseAndRestart() {
  const uint64_t k1 = AreaReleaseKey(1, 2, 3);
  const uint64_t k2 = AreaReleaseKey(1, 2, 4);
  std::vector<SpellPair> formed;
  uint32_t seq = 0;
  int32_t index = -1;
  Table t;
  const uint32_t first = t.LocalCast(k1, false, 0x40001000u, 0x50000000u, 0.0, &formed);
  const uint32_t second = t.LocalCast(k2, false, 0x40001000u, 0x50000000u, 1.0, &formed);
  assert(t.LocalSpell(0x40001000u, 0x50000000u, 300, 0, 1.1, &seq, &index, &formed));
  assert(seq == second && seq != first && index == 0);
  t.PeerCast(4, k1, true, 1.2, &formed);
  t.PeerCast(4, k2, true, 1.3, &formed);  // the peer restarted: seq 4 is a new cast
  t.PeerSpell(4, 0, 40, 0, &formed);
  assert(t.LocalOfPeer(40) == 300);
}

}  // namespace

int main() {
  Keys();
  IdenticalKeysPairInOrder();
  KeysAndCascade();
  TagFromTheDecider();
  StateFromTheDecider();
  AwaitingCopy();
  Lines();
  Lifetimes();
  ReuseAndRestart();
  std::puts("cast_pairing_test: ok");
  return 0;
}
