#!/usr/bin/env bash
# Unattended run + verdict for the packaged game.
#
#   tools/refii_smoke.sh [seconds] [--walk] [--no-autostart] [--drop-core] [--gdb]
#
# Drives a run with nobody at the keyboard and classifies how it ended. It does NOT build:
# it runs whatever is in out/package, so a stale package silently tests old code — the
# preflight prints the package's build time for exactly that reason.
#
# The two levers are the game's own, not new ones: REFII_AUTO_START=1 presses A through the
# title and main menu until the front end hands over, and REFII_FAKE_STICK=1 holds the left
# stick forward so the hero walks.
#
# WALKING IS OPT-IN (`--walk`). It was the default while the crash under investigation only
# appeared once NPCs were repathing, and it made the game unusable to drive by hand — the hero
# runs off on his own the moment you take over. Ask for it when the thing being tested needs
# the world moving; leave it off to watch a scene, reproduce something manually, or hand the
# controls back.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PKG="$ROOT/out/package/Fable 2 Return to Albion"
EXE="$PKG/Fable 2 Return to Albion.x86_64"
GUEST_LOGS="$PKG/logs"
GODOT_LOG="$HOME/.local/share/godot/app_userdata/Fable 2 Launcher/logs/godot.log"
OUT="$HOME/.cache/refii-scratch/smoke"      # NEVER the root partition
SECONDS_BUDGET=180
WALK=0
AUTOSTART=1
DROP_CORE=0
UNDER_GDB=0

for arg in "$@"; do
  case "$arg" in
    --walk)         WALK=1 ;;
    --no-walk)      WALK=0 ;;
    --no-autostart) AUTOSTART=0 ;;
    --drop-core)    DROP_CORE=1 ;;
    --gdb)          UNDER_GDB=1 ;;
    ''|*[!0-9]*)    echo "unknown argument: $arg" >&2; exit 2 ;;
    *)              SECONDS_BUDGET="$arg" ;;
  esac
done

mkdir -p "$OUT"
STAMP="$(date +%Y%m%d-%H%M%S)"
REPORT="$OUT/$STAMP.txt"
exec > >(tee "$REPORT") 2>&1

say() { printf '%s\n' "$*"; }
rule() { printf -- '---- %s\n' "$*"; }

# utime+stime+cutime+cstime for a pid. The comm field is parenthesised AND contains spaces
# here ("Fable 2 Return "), so fields are counted from the last ')' rather than by position.
cpu_ticks() {
  awk '{ n = index($0, ")"); while (index(substr($0, n + 1), ")") > 0) n += index(substr($0, n + 1), ")");
         split(substr($0, n + 2), f, " "); print f[12] + f[13] + f[14] + f[15] }' \
    "/proc/$1/stat" 2>/dev/null || echo ""
}

# ---------------------------------------------------------------- preflight
rule preflight

[ -x "$EXE" ] || { say "FAIL: no packaged binary at $EXE"; exit 2; }
say "package built: $(stat -c %y "$EXE" | cut -c1-19)"
for lib in librefii_vfs.so librefii_hostbridge.so librefii_generated.so; do
  [ -f "$PKG/bin/$lib" ] && say "  $lib: $(stat -c %y "$PKG/bin/$lib" | cut -c1-19)"
done

# Sources newer than the package are code this run will NOT be testing.
NEWER=$(find "$ROOT/src" "$ROOT/Godot/src" -name '*.cpp' -o -name '*.cs' -o -name '*.h' 2>/dev/null \
        | while read -r f; do [ "$f" -nt "$EXE" ] && printf '%s\n' "${f#"$ROOT"/}"; done)
if [ -n "$NEWER" ]; then
  say "WARNING: $(printf '%s\n' "$NEWER" | wc -l) source file(s) are NEWER than the package:"
  printf '%s\n' "$NEWER" | sed 's/^/  /' | head -12
  say "  -> this run tests the OLD code for those."
fi

# The self-match trap: our own command line would match a bare pattern.
#
# WAITED OUT RATHER THAN REFUSED. A run this harness has just killed takes a few seconds to
# finish tearing down its window and its guest, and back-to-back invocations were failing
# preflight against a process that was already on its way out.
for _ in $(seq 1 12); do
  pgrep -f 'Albion[.]x86_64' >/dev/null || break
  sleep 2
done
if pgrep -f 'Albion[.]x86_64' >/dev/null; then
  say "FAIL: an instance is still running after 24s; not starting a second"
  exit 2
fi

# Crashed runs leak their 4.5 GB guest arena into /dev/shm, and /dev/shm is RAM. Left alone
# they accumulate until the machine swaps and every later run reads as a crash.
STALE=$(ls /dev/shm/xenia_memory_* 2>/dev/null | wc -l)
if [ "$STALE" -gt 0 ] && ! grep -lq xenia_memory /proc/*/maps 2>/dev/null; then
  rm -f /dev/shm/xenia_memory_*
  say "reclaimed $STALE abandoned guest arena(s) from /dev/shm"
fi

CORES_BEFORE=$(coredumpctl list --no-pager 2>/dev/null | grep -c 'Albion' || true)

# ---------------------------------------------------------------- run
rule "run (${SECONDS_BUDGET}s budget, walk=$WALK autostart=$AUTOSTART)"

ENVV=()
[ "$AUTOSTART" = 1 ] && ENVV+=("REFII_AUTO_START=1")
[ "$WALK" = 1 ]      && ENVV+=("REFII_FAKE_STICK=1")

# Under gdb the game is gdb's CHILD, which is the only way to get a stack while
# /proc/sys/kernel/yama/ptrace_scope is 1.
if [ "$UNDER_GDB" = 1 ]; then
  ( cd "$PKG" && env "${ENVV[@]}" gdb -q -batch \
      -ex run -ex "thread apply all bt 12" --args "$EXE" >"$OUT/$STAMP.stdout" 2>&1 ) &
else
  ( cd "$PKG" && env "${ENVV[@]}" "$EXE" >"$OUT/$STAMP.stdout" 2>&1 ) &
fi
RUNNER=$!
sleep 2
GAME_PID=$(pgrep -f 'Albion[.]x86_64' | head -1)
say "pid ${GAME_PID:-unknown}"

# CODEGEN IS NOT A STALL. When the payload fingerprint changes — which an overlay install
# always does — the launcher rebuilds the recompiled module before the game appears, and that
# takes minutes during which godot.log is silent by design. Watching only godot.log declared a
# hang 30s in and killed the build. The launcher writes logs/refii-launcher.log throughout, so
# progress on EITHER log means alive, and time spent in codegen does not spend the run budget.
LAUNCHER_LOG="$GUEST_LOGS/refii-launcher.log"
DEADLINE=$(( $(date +%s) + SECONDS_BUDGET ))
LAST_GODOT=0; LAST_LAUNCHER=0; LAST_CPU=""; STALL_TICKS=0; VERDICT=""; ANNOUNCED_CODEGEN=0
while [ "$(date +%s)" -lt "$DEADLINE" ]; do
  sleep 5
  if ! kill -0 "$GAME_PID" 2>/dev/null; then VERDICT="exited"; break; fi

  SIZE_G=$(stat -c %s "$GODOT_LOG" 2>/dev/null || echo 0)
  SIZE_L=$(stat -c %s "$LAUNCHER_LOG" 2>/dev/null || echo 0)
  CPU=$(cpu_ticks "$GAME_PID")

  if [ "$SIZE_L" != "$LAST_LAUNCHER" ] || [ "$CPU" != "$LAST_CPU" ]; then
    # Alive. Codegen in particular is silent in both logs for minutes at a time while a
    # compile runs, so the log alone declared a hang and killed the build — twice. CPU is
    # the signal that does not go quiet: cutime/cstime accumulate as each compiler child
    # exits, and the game's own threads move utime once it is up.
    if [ "$SIZE_L" != "$LAST_LAUNCHER" ] || [ "$SIZE_G" = "$LAST_GODOT" ]; then
      if [ "$ANNOUNCED_CODEGEN" = 0 ] && [ "$SIZE_G" = "$LAST_GODOT" ]; then
        say "alive with both logs quiet (compiling, or starting up) — budget paused"
        ANNOUNCED_CODEGEN=1
      fi
      # Only refund the budget while godot.log is NOT moving; once the game is up and
      # logging, the budget should measure gameplay.
      [ "$SIZE_G" = "$LAST_GODOT" ] && DEADLINE=$(( $(date +%s) + SECONDS_BUDGET ))
    fi
    STALL_TICKS=0
  elif [ "$SIZE_G" != "$LAST_GODOT" ]; then
    STALL_TICKS=0
  else
    STALL_TICKS=$((STALL_TICKS+1))
  fi
  LAST_GODOT=$SIZE_G; LAST_LAUNCHER=$SIZE_L; LAST_CPU=$CPU

  # Twelve quiet polls is a full minute with neither log moving.
  [ "$STALL_TICKS" -ge 12 ] && { VERDICT="hang"; break; }
done

if [ -z "$VERDICT" ]; then VERDICT="budget-reached"; fi

# A hang is the one state worth a stack before it is killed — but only if we can get one.
if [ "$VERDICT" = "hang" ] && kill -0 "$GAME_PID" 2>/dev/null; then
  say "no log growth and no CPU for 60s"
  if [ "$(cat /proc/sys/kernel/yama/ptrace_scope 2>/dev/null || echo 0)" != "0" ]; then
    # yama restricts ptrace to descendants, so attaching to a process we merely started in a
    # subshell fails. Say so instead of writing a stacks file containing only that failure.
    say "  cannot attach: ptrace_scope is not 0 — rerun with --gdb to launch it under gdb"
  else
    timeout 120 gdb -q -batch -p "$GAME_PID" -ex "set pagination off" \
      -ex "thread apply all bt 12" >"$OUT/$STAMP.stacks" 2>&1 && say "  -> $STAMP.stacks"
  fi
fi

if kill -0 "$GAME_PID" 2>/dev/null; then
  kill -TERM "$GAME_PID" 2>/dev/null
  sleep 5
  kill -0 "$GAME_PID" 2>/dev/null && kill -KILL "$GAME_PID" 2>/dev/null
fi
wait "$RUNNER" 2>/dev/null

# ---------------------------------------------------------------- verdict
rule verdict

GUEST_LOG=$(ls -t "$GUEST_LOGS"/refii_0*.log 2>/dev/null | head -1)
say "godot log: $GODOT_LOG"
say "guest log: ${GUEST_LOG:-none}"

grepq() { grep -aq "$1" "$2" 2>/dev/null; }

CLEAN_EXIT=0
grepq 'close_requested' "$GODOT_LOG" && grepq 'Title terminated' "${GUEST_LOG:-/dev/null}" && CLEAN_EXIT=1

# A new core is only evidence when the run did NOT exit cleanly: the shutdown path
# hard-exits through abort(), so every tidy quit also dumps one.
CORES_AFTER=$(coredumpctl list --no-pager 2>/dev/null | grep -c 'Albion' || true)
NEW_CORE=0
[ "$CORES_AFTER" -gt "$CORES_BEFORE" ] && NEW_CORE=1

if [ "$NEW_CORE" = 1 ] && [ "$CLEAN_EXIT" = 0 ]; then
  CORE_PID=$(coredumpctl list --no-pager 2>/dev/null | grep 'Albion' | tail -1 | awk '{print $5}')
  say "new core: pid $CORE_PID"
  timeout 300 coredumpctl info "$CORE_PID" 2>/dev/null | sed -n '/Stack trace/,$p' | head -20 \
    | tee "$OUT/$STAMP.core.txt"
  if grep -aq 'xboxkrnl_rtl\|RtlEnterCriticalSection' "$OUT/$STAMP.core.txt"; then
    say "RESULT: crash-critical-section (the RtlEnterCriticalSection owning_thread assert)"
  else
    say "RESULT: crash-other — see $STAMP.core.txt"
  fi
  # Cores land in /var/lib/systemd/coredump on the ROOT partition, which is small and has
  # already hit ENOSPC in this project. Report the cost; drop it only when asked.
  say "coredump store: $(du -sh /var/lib/systemd/coredump 2>/dev/null | cut -f1), root free: $(df -h / | awk 'NR==2{print $4}')"
  if [ "$DROP_CORE" = 1 ]; then
    find /var/lib/systemd/coredump -name "core.*.$CORE_PID.*" -delete 2>/dev/null &&
      say "dropped the core for pid $CORE_PID"
  fi
  exit 1
fi

SETTLED=$(grep -a 'game-entities. settled after' "$GODOT_LOG" 2>/dev/null | tail -1)
LOADSCREEN=$(grep -a 'guest-loadscreen' "$GODOT_LOG" 2>/dev/null | tail -1)
COMPOSITES=$(grep -a 'composite characters:' "$GODOT_LOG" 2>/dev/null | tail -1)
[ -n "$SETTLED" ]    && say "$SETTLED"
[ -n "$COMPOSITES" ] && say "$COMPOSITES"
[ -n "$LOADSCREEN" ] && say "$LOADSCREEN"

if [ -n "$LOADSCREEN" ] && printf '%s' "$LOADSCREEN" | grep -q 'requested=True'; then
  say "RESULT: stuck-loading-screen — the world is up but the guest is still parked in"
  say "        sub_82361458's wait; nothing cleared manager+4, so Godot keeps the screen."
  exit 1
fi

if [ "$VERDICT" = "hang" ]; then
  say "RESULT: hang — process alive, neither log moving and no CPU burned for 60s"
  exit 1
fi

if [ -n "$SETTLED" ]; then
  say "RESULT: reached-gameplay (walk=$WALK) over ${SECONDS_BUDGET}s"
  exit 0
fi

say "RESULT: never-reached-gameplay (ended: $VERDICT) — see $STAMP.stdout"
exit 1
