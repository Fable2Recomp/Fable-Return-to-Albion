#!/usr/bin/env python3
"""Replays a measured frame pattern through the draw-latency control loop.

    python3 tools/tick_clock_sim.py

WHY THIS EXISTS. Everything the guest states on its tick — the camera, every body, every bone —
is drawn at `(now - origin)/period - latency`. Any drift in `latency` is therefore not a tuning
detail: it is the whole world played fast or slow. The old loop sized its slack from a PEAK-HOLD
of frame time, so one 65 ms collection pause set the operating point for the next fifty frames and
the latency sawtoothed continuously. Measured live in town it swung 1.49 to 1.93 ticks.

This model reproduces that and checks the replacement, which is a high percentile of recent frames
plus a slew bounded as a fraction of ELAPSED TIME rather than a flat step per frame. The per-frame
step was the subtler half of the bug: being per-frame, it re-timed the world faster the better the
frame rate got, which is why the jitter appeared as performance improved.

Expected output — the second line is the contract, playback at 1.0:

    old: latency swing ~0.48 ticks, mean rate error ~1.4%, p99 ~8.7%
    new: latency swing ~0.08 ticks, mean rate error ~0.02%, p99 0.0%

Keep this in step with Godot/src/Engine/core/Engine_TickClock.cs.
"""

TICK = 1e6 / 15.0
GUEST = 0.5
MINLAT = GUEST + 0.25
MAXLAT = 4.0

def frames(seconds=30.0, fps=65.0, spike_every=4.0, spike_ms=65.0):
    """Measured shape: steady frames with a collection pause every few seconds."""
    out, t, next_spike = [], 0.0, spike_every
    base = 1e6 / fps
    while t < seconds * 1e6:
        if t >= next_spike * 1e6:
            out.append(spike_ms * 1000.0); next_spike += spike_every
        else:
            out.append(base)
        t += out[-1]
    return out

def run(new):
    lat, ft, slack = 1.0, 0.5, 0.35
    ring, rates, lats = [], [], []
    sampled = starved = 0
    for i, dt in enumerate(frames()):
        f = dt / TICK
        if 0.0 < f < 4.0:
            if new:
                ring.append(f)
                if len(ring) > 64: ring.pop(0)
                srt = sorted(ring)
                ft = srt[int(0.90 * (len(srt) - 1))]
            else:
                ft = max(f, ft * 0.98)

        target = min(max(GUEST + ft + slack, MINLAT), MAXLAT)
        if new:
            want = target - lat
            tol = 0.02 if want > 0 else 0.002
            budget = (dt / TICK) * tol
            moved = max(-budget, min(budget, want))
        else:
            moved = max(-0.02, min(0.02, target - lat))
        lat += moved
        lats.append(lat)
        # rate error from the latency term alone (origin is bounded to 0.05% in both)
        rates.append(abs(moved * TICK / dt))
    return lats, rates

for label, new in (("old", False), ("new", True)):
    lats, rates = run(new)
    lats_s = sorted(lats)
    print(f"{label}: latency min {min(lats):.3f} max {max(lats):.3f} "
          f"swing {max(lats)-min(lats):.3f} ticks ({(max(lats)-min(lats))*TICK/1000:.1f} ms)")
    print(f"     playback rate error: mean {sum(rates)/len(rates)*100:.3f}%  "
          f"p99 {sorted(rates)[int(0.99*len(rates))]*100:.3f}%  worst {max(rates)*100:.3f}%")
