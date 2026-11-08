#!/usr/bin/env python3
"""Writes the skateboard's sound options as .wav files, all of them synthesised, nothing sampled, and copies
the ones in use to the names godot/skate.gd loads.

    make_sounds.py <folder for the options> <the mod's sounds/skate folder>
"""
import math, random, shutil, struct, sys, wave

RATE = 22050
CHOSEN = {"hop": "pop_a", "land": "land_a", "roll": "roll_a", "grind": "grind_b", "thrown": "wipe_c",
          "flourish": "flip_c", "grab": "grab_c", "wheelie": "wheelie_c"}


def noise(seconds, seed):
    rng = random.Random(seed)
    return [rng.uniform(-1.0, 1.0) for _ in range(int(seconds * RATE))]


def lowpass(signal, hz):
    a = 1.0 - math.exp(-math.tau * hz / RATE)
    out, y = [], 0.0
    for x in signal:
        y += a * (x - y)
        out.append(y)
    return out


def highpass(signal, hz):
    low = lowpass(signal, hz)
    return [x - y for x, y in zip(signal, low)]


def resonator(signal, hz, q):
    """A band of the signal, ringing at hz."""
    w = math.tau * hz / RATE
    alpha = math.sin(w) / (2 * q)
    a0 = 1 + alpha
    b0, b2 = alpha / a0, -alpha / a0
    a1, a2 = -2 * math.cos(w) / a0, (1 - alpha) / a0
    x1 = x2 = y1 = y2 = 0.0
    out = []
    for x in signal:
        y = b0 * x + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1, y2, y1 = x1, x, y1, y
        out.append(y)
    return out


def silence(seconds):
    return [0.0] * int(seconds * RATE)


def mix(*parts):
    length = max(len(p[0]) + int(p[2] * RATE) for p in parts)
    out = [0.0] * length
    for signal, gain, at in parts:
        start = int(at * RATE)
        for i, x in enumerate(signal):
            out[start + i] += x * gain
    return out


def decay(signal, seconds):
    return [x * math.exp(-i / (seconds * RATE)) for i, x in enumerate(signal)]


def thump(hz, seconds, fall=0.5):
    """A low knock, its pitch falling."""
    out, phase = [], 0.0
    for i in range(int(seconds * 5 * RATE)):
        t = i / RATE
        phase += math.tau * hz * (1 - fall + fall * math.exp(-t / (seconds * 0.5))) / RATE
        out.append(math.sin(phase) * math.exp(-t / seconds))
    return out


def click(seed, seconds=0.006, bright=5000):
    return decay(highpass(noise(seconds * 4, seed), bright * 0.25), seconds)


def wood(seed, seconds, tones, level=1.0):
    """Struck wood: a burst of noise through a few short resonances."""
    burst = decay(noise(seconds * 5, seed), seconds * 0.4)
    parts = [(resonator(burst, hz, q), gain * level, 0.0) for hz, q, gain in tones]
    return mix(*parts)


def fit(signal, peak=0.85):
    top = max(abs(x) for x in signal) or 1.0
    return [x / top * peak for x in signal]


def seamless(signal, overlap=0.08):
    """Folds a clip's end over its start, so it loops without a seam."""
    n = int(overlap * RATE)
    out = signal[:-n]
    for i in range(n):
        t = i / n
        out[i] = out[i] * t + signal[len(signal) - n + i] * (1 - t)
    return out


def texture(signal, hz, depth, seed):
    """Lets the level wander gently, hz being about how quickly, as a surface changes under the wheels."""
    wander = lowpass(noise(len(signal) / RATE, seed), hz)
    top = max(abs(x) for x in wander) or 1.0
    return [x * (1.0 + depth * w / top) for x, w in zip(signal, wander)]


def write(folder, name, signal):
    with wave.open(f"{folder}/{name}.wav", "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(RATE)
        out.writeframes(b"".join(struct.pack("<h", int(max(-1.0, min(1.0, x)) * 32000)) for x in signal))


def build(folder):
    # Rolling plays the whole time, so it stays quiet and has nothing in it to catch the ear: no knocks.
    # All three are at a mid speed. The game would change their pitch and volume with speed.
    hum = lowpass(lowpass(noise(3.0, 1), 110), 110)
    write(folder, "roll_a", fit(seamless(hum), 0.22))
    body = mix((lowpass(lowpass(noise(3.0, 2), 150), 150), 1.0, 0), (resonator(noise(3.0, 3), 340, 0.7), 0.012, 0))
    write(folder, "roll_b", fit(seamless(texture(body, 7, 0.3, 4)), 0.26))
    airy = mix((lowpass(noise(3.0, 5), 90), 1.0, 0), (resonator(noise(3.0, 6), 900, 0.6), 0.008, 0))
    write(folder, "roll_c", fit(seamless(texture(airy, 3, 0.2, 7)), 0.24))

    # The pop of a jump.
    write(folder, "pop_a", fit(mix((wood(11, 0.016, [(1450, 9, 1.0), (620, 7, 1.0), (2900, 10, 0.4)]), 1.0, 0),
          (thump(150, 0.035), 0.5, 0.002))))
    write(folder, "pop_b", fit(mix((wood(12, 0.024, [(760, 8, 1.2), (340, 6, 1.2), (1650, 9, 0.3)]), 1.0, 0),
          (thump(105, 0.05), 0.8, 0.0))))
    write(folder, "pop_c", fit(mix((click(13, 0.004), 0.55, 0),
          (wood(14, 0.012, [(2100, 12, 1.0), (980, 9, 0.8)]), 1.0, 0.001), (thump(190, 0.022), 0.35, 0.0))))

    # Coming down: the back wheels, then the front, then the deck.
    def landing(gap, deep, level, seed):
        wheels = [(330, 6, 1.0), (150, 4, 1.2), (900, 8, 0.25)]
        return fit(mix((wood(seed, 0.018, wheels, level), 1.0, 0), (wood(seed + 1, 0.018, wheels, level), 0.8, gap),
                       (thump(deep, 0.07), 0.9, 0.0), (thump(deep * 0.85, 0.06), 0.6, gap)))
    write(folder, "land_a", landing(0.028, 95, 1.0, 21))
    write(folder, "land_b", landing(0.012, 72, 1.3, 23))
    write(folder, "land_c", landing(0.05, 120, 0.8, 25))

    # Grinding: all on stone, which is most of Albion's edges.
    scrape = noise(3.0, 31)
    rng = random.Random(32)
    # A rough surface: the level wanders quickly.
    rough, level, aim = [], 0.5, 0.5
    for i in range(len(scrape)):
        if i % 180 == 0:
            aim = rng.uniform(0.25, 1.0)
        level += (aim - level) * 0.01
        rough.append(level)
    gritty = [x * r for x, r in zip(scrape, rough)]
    write(folder, "grind_a", fit(seamless(mix((resonator(gritty, 1500, 0.8), 1.0, 0), (resonator(gritty, 3400, 1.2), 0.5, 0),
          (lowpass(gritty, 220), 0.6, 0))), 0.6))
    write(folder, "grind_b", fit(seamless(mix((resonator(gritty, 2300, 6), 1.0, 0), (resonator(gritty, 3710, 9), 0.9, 0),
          (resonator(gritty, 5230, 12), 0.5, 0), (resonator(gritty, 900, 1.0), 0.5, 0))), 0.55))
    write(folder, "grind_c", fit(seamless(mix((resonator(gritty, 620, 1.0), 1.0, 0), (resonator(gritty, 1500, 1.4), 0.6, 0),
          (lowpass(gritty, 160), 1.0, 0))), 0.6))

    # A flip: the board turning through the air, and the feet catching it.
    def sweep(seconds, low, high, seed):
        burst = noise(seconds, seed)
        out, y1, y2 = [], 0.0, 0.0
        for i, x in enumerate(burst):
            t = i / len(burst)
            hz = low + (high - low) * math.sin(t * math.pi)
            w = math.tau * hz / RATE
            r = 0.97
            y = x * (1 - r) + 2 * r * math.cos(w) * y1 - r * r * y2
            y2, y1 = y1, y
            out.append(y * math.sin(t * math.pi))
        return out
    catch = wood(71, 0.012, [(1250, 9, 1.0), (560, 7, 0.9)])
    write(folder, "flip_a", fit(mix((sweep(0.3, 500, 1700, 72), 1.0, 0), (catch, 0.012, 0.3)), 0.7))
    rattle = []
    rng = random.Random(73)
    at = 0.0
    parts = []
    while at < 0.3:
        parts.append((wood(rng.randint(0, 1 << 30), 0.004, [(rng.uniform(1700, 2600), 12, 1.0)]), 0.6 + at, at))
        at += 0.045
    write(folder, "flip_b", fit(mix(*parts, (catch, 1.2, 0.33)), 0.7))
    write(folder, "flip_c", fit(sweep(0.26, 280, 800, 74), 0.5))

    # A grab: a hand slapping the deck.
    write(folder, "grab_a", fit(mix((decay(lowpass(noise(0.2, 81), 2400), 0.018), 1.0, 0), (thump(170, 0.03), 0.5, 0)), 0.7))
    write(folder, "grab_b", fit(mix((wood(82, 0.012, [(820, 7, 1.0), (360, 5, 1.0)]), 1.0, 0), (decay(lowpass(noise(0.1, 83), 1500), 0.01), 0.04, 0)), 0.7))
    write(folder, "grab_c", fit(mix((decay(lowpass(noise(0.25, 84), 900), 0.03), 1.0, 0), (thump(110, 0.04), 0.6, 0)), 0.55))

    # A wheelie: the nose coming up.
    creak = []
    rng = random.Random(91)
    burst = noise(0.32, 92)
    level, aim = 0.0, 1.0
    for i, x in enumerate(burst):
        if i % 280 == 0:
            aim = rng.uniform(0.2, 1.0)
        level += (aim - level) * 0.03
        creak.append(x * level * (1 - i / len(burst)))
    write(folder, "wheelie_a", fit(mix((resonator(creak, 420, 9), 1.0, 0), (resonator(creak, 980, 12), 0.6, 0)), 0.5))
    write(folder, "wheelie_b", fit(mix((thump(135, 0.035), 1.0, 0), (wood(93, 0.008, [(620, 6, 1.0)]), 0.5, 0.0)), 0.45))
    # Loops, for while it's held: only the back wheels are turning.
    write(folder, "wheelie_c", fit(seamless(mix((lowpass(lowpass(noise(3.0, 94), 240), 240), 1.0, 0),
          (resonator(noise(3.0, 95), 620, 2.0), 0.02, 0))), 0.2))

    # The board clattering away after the rider has gone.
    def wipeout(seed, hits, spread, deep):
        rng = random.Random(seed)
        parts = [(thump(deep, 0.11), 1.0, 0.0), (decay(lowpass(noise(0.5, seed), 900), 0.08), 0.8, 0.0)]
        at = 0.09
        for k in range(hits):
            tones = [(rng.uniform(380, 640), 8, 1.0), (rng.uniform(1100, 1700), 10, 0.6), (rng.uniform(170, 240), 5, 1.0)]
            parts.append((wood(seed + k + 1, 0.02, tones), 0.8 * 0.72 ** k, at))
            at += spread * rng.uniform(0.6, 1.3) * 0.82 ** k
        return fit(mix(*parts))
    write(folder, "wipe_a", wipeout(41, 6, 0.11, 70))
    write(folder, "wipe_b", wipeout(51, 3, 0.08, 58))
    write(folder, "wipe_c", wipeout(61, 9, 0.13, 85))


build(sys.argv[1])
for name, option in CHOSEN.items():
    shutil.copyfile(f"{sys.argv[1]}/{option}.wav", f"{sys.argv[2]}/{name}.wav")
