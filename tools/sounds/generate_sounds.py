"""Generates the Radio 1.3 mechanical display sound takes and listening demos.

Original synthesis only, no recordings. Recipes follow published descriptions of split-flap
boards, counter drums and chart recorders, then are voiced to sit with Sailwind's own sounds:
real object body below 600 Hz, a short room tail instead of a dry digital finish, and wide
event-to-event level variation. Every random value comes from a seeded generator, so a run
reproduces the same files.

Takes (24 kHz mono 16-bit) are short reusable events the runtime schedules from the real
text change: split-flap landings, drum index steps and stops, scroll clockwork ticks, belt
jogs, eraser sweeps and stylus strokes. Demos (48 kHz) mix whole display updates for
listening. Usage: python generate_sounds.py <out dir>
"""
import sys
import wave
from pathlib import Path

import numpy as np
from scipy import signal

SR = 48000
TAKE_SR = 24000
OUT = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parents[2] / "assets" / "authoring" / "sounds")


# ---------------------------------------------------------------- building blocks

def t_axis(seconds):
    return np.arange(samples(seconds)) / SR


def samples(seconds):
    return int(round(seconds * SR))


def db(x):
    return 10 ** (x / 20)


def band(x, fc, q, order=2):
    """Broad band-pass: bandwidth fc / q, clamped inside 60 Hz .. 0.45 SR."""
    lo, hi = max(60.0, fc - fc / q / 2), min(SR * 0.45, fc + fc / q / 2)
    sos = signal.butter(order, [lo, hi], btype="bandpass", fs=SR, output="sos")
    return signal.sosfilt(sos, x)


def lowpass(x, fc, order=4):
    return signal.sosfilt(signal.butter(order, fc, btype="lowpass", fs=SR, output="sos"), x)


def highpass(x, fc, order=2):
    return signal.sosfilt(signal.butter(order, fc, btype="highpass", fs=SR, output="sos"), x)


def mode(freq, tau, seconds, delay=0.0):
    t = t_axis(seconds)
    y = np.sin(2 * np.pi * freq * (t - delay)) * np.exp(-np.maximum(t - delay, 0) / tau)
    y[t < delay] = 0
    return y


def burst(rng, tau, seconds, delay=0.0):
    t = t_axis(seconds)
    y = rng.standard_normal(len(t)) * np.exp(-np.maximum(t - delay, 0) / tau)
    y[t < delay] = 0
    return y


def fractal_noise(rng, n, beta):
    """1/f^beta noise via spectral shaping."""
    spectrum = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / SR)
    f[0] = f[1]
    return np.fft.irfft(spectrum / f ** (beta / 2), n)


def tracked_band(rng, v, lo_fc, hi_fc, q, beta):
    """Friction noise whose band centre follows normalised velocity v (two-band crossfade)."""
    n = len(v)
    low = band(fractal_noise(rng, n, beta), lo_fc, q)
    high = band(fractal_noise(rng, n, beta), hi_fc, q)
    low /= np.max(np.abs(low)) + 1e-9
    high /= np.max(np.abs(high)) + 1e-9
    return (low * (1 - v) + high * v) * np.sqrt(np.clip(v, 0, 1))


def impulses(rng, rate, seconds, tau, fc_lo, fc_hi, alpha, envelope=None):
    """Sparse impulses with power-law energies (paper grain, crumbs)."""
    n = samples(seconds)
    out = np.zeros(n)
    count = rng.poisson(rate * seconds)
    kernel = np.exp(-np.arange(samples(tau * 6)) / (tau * SR))
    for _ in range(count):
        i = rng.integers(0, n)
        if envelope is not None and rng.random() > envelope[i]:
            continue
        energy = (1 - rng.random()) ** (-1 / (alpha - 1))
        seg = kernel[: n - i] * min(energy, 30) * rng.choice([-1, 1])
        out[i:i + len(seg)] += seg
    return band(out, (fc_lo + fc_hi) / 2, (fc_lo + fc_hi) / 2 / (fc_hi - fc_lo))


def body(rng, x, modes, tau, level):
    """Object body: the event excites a few low-mid cabinet modes (wood thump, brass ring)."""
    drive = lowpass(x, 2500)
    y = np.zeros(len(x))
    for f in modes:
        b, a = signal.iirpeak(f * rng.uniform(0.95, 1.05), max(2.0, f * tau * 3.1416), fs=SR)
        y += signal.lfilter(b, a, drive)
    return y / (np.max(np.abs(y)) + 1e-9) * np.max(np.abs(x)) * level


_ROOM = {}


def room_ir(rt60=0.26, wet=-15):
    """Small wooden room: three early reflections and a dark exponential tail (seeded, cached)."""
    key = (rt60, wet)
    if key not in _ROOM:
        rng = np.random.default_rng(9090)
        n = samples(rt60 * 1.2)
        tail = rng.standard_normal(n) * np.exp(-np.arange(n) / SR / (rt60 / 6.9))
        tail = lowpass(tail, 3800)
        tail *= np.minimum(1, np.arange(n) / samples(0.012))
        ir = tail / np.sqrt((tail ** 2).sum()) * db(wet)
        for delay, gain in ((0.006, -9), (0.011, -12), (0.019, -15)):
            ir[samples(delay)] += db(gain)
        ir[0] = 1.0
        _ROOM[key] = ir
    return _ROOM[key]


def finish(x, lp=10000, rt60=0.26, wet=-15):
    """Game voicing: low floor at 60 Hz, gentle low-pass, short room tail, -1 dBFS peak."""
    x = lowpass(highpass(x, 60), lp)
    x = signal.fftconvolve(x, room_ir(rt60, wet))
    fade = min(len(x), samples(0.0002))
    x[:fade] *= np.linspace(0, 1, fade)
    x[-samples(0.01):] *= np.linspace(1, 0, samples(0.01))
    return x / (np.max(np.abs(x)) + 1e-9) * db(-1)


def pitched(x, cents):
    """Real pitch change by resampling (also shifts duration, as Unity's pitch does)."""
    ratio = 2 ** (cents / 1200)
    n = max(8, int(round(len(x) / ratio)))
    return signal.resample(x, n)


def soft_clip(x):
    y = x.copy()
    over = np.abs(y) > 0.7
    y[over] = np.sign(y[over]) * (0.7 + 0.3 * np.tanh((np.abs(y[over]) - 0.7) / 0.3))
    return y


def write(path, x, rate):
    if rate != SR:
        x = signal.resample_poly(x, rate, SR)
    pcm = (np.clip(x, -1, 1) * 32767).astype("<i2")
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm.tobytes())


# ---------------------------------------------------------------- takes (B1, B3, B5, B6, B7)

def flap(rng):
    """B1: strike, bounce, spiky rattle body and a soft card thock."""
    length = 0.07
    fc = 3200 * rng.uniform(0.85, 1.15)
    tau = 0.001 * rng.uniform(0.75, 1.25)
    strike = band(burst(rng, tau, length), fc, 0.5)
    bounce = band(burst(rng, tau, length, delay=rng.uniform(0.008, 0.014)), fc, 0.5)
    t = t_axis(length)
    body_env = db(rng.uniform(-12, -7)) * np.exp(-t / rng.uniform(0.025, 0.05))
    rattle = impulses(rng, rng.uniform(3000, 6000), length, 0.0006, fc * 0.6, fc * 1.4, 2.5, body_env / body_env.max())
    rattle = rattle / (np.max(np.abs(rattle)) + 1e-9) * body_env
    thock = mode(rng.uniform(900, 1600), rng.uniform(0.003, 0.008), length) * db(rng.uniform(-18, -12))
    peak = lambda x: x / (np.max(np.abs(x)) + 1e-9)
    y = peak(strike) + peak(bounce) * db(rng.uniform(-14, -8)) + rattle + thock
    if rng.random() < 0.2:  # a flap that catches and lands twice
        y += np.roll(y, samples(rng.uniform(0.018, 0.03))) * db(rng.uniform(-12, -7))
    y += body(rng, y, (rng.uniform(380, 520), rng.uniform(700, 950), rng.uniform(1300, 1600)), 0.012, db(-20))
    return finish(y, lp=9000, rt60=0.12, wet=-27)


def index(rng, heavy=False):
    """B3: one drum index step in brass and steel: the pawl's bright click, a few short
    inharmonic metal partials as the brass wheel and its detent spring take the knock, and a
    damped low-mid metal thunk. Partials are high and die in milliseconds, so nothing reads as a
    pitched tone. heavy is the firmer final stop, with a second, lower knock."""
    length = 0.1
    pre = rng.uniform(0.001, 0.003)
    click = highpass(burst(rng, 0.0004 * rng.uniform(0.8, 1.2), length, pre), 2500)
    peak = lambda x: x / (np.max(np.abs(x)) + 1e-9)
    y = peak(lowpass(click, 11000)) * db(rng.uniform(-2, 1))
    # wheel and spring partials, inharmonic like small brass parts
    k = rng.uniform(0.94, 1.06)
    for f, tau, level in ((2650, 0.012, -8), (4180, 0.009, -10), (5930, 0.006, -13), (7720, 0.004, -16)):
        y += mode(f * k * rng.uniform(0.98, 1.02), tau * rng.uniform(0.8, 1.2), length, pre) * db(level + rng.uniform(-3, 2))
    # damped metal knock as the drum seats, low-mid with a short metallic edge
    knock_at = pre + rng.uniform(0.003, 0.006)
    knock = lowpass(burst(rng, rng.uniform(0.003, 0.005), length, knock_at), 1800)
    y += peak(knock) * db(rng.uniform(-7, -4))
    for f, tau in ((rng.uniform(380, 450), 0.01), (rng.uniform(610, 700), 0.008), (rng.uniform(930, 1050), 0.006)):
        y += mode(f, tau, length, knock_at) * db(rng.uniform(-15, -11))
    if heavy:
        y += np.roll(peak(knock), samples(rng.uniform(0.012, 0.02))) * db(rng.uniform(-10, -7))
        y += mode(rng.uniform(1900, 2200), 0.02, length, pre) * db(-12)
    return finish(y, lp=12000, rt60=0.16, wet=-22)


def velocity(seconds, peak):
    t = np.linspace(0, 1, samples(seconds))
    v = np.where(t < peak, 0.5 - 0.5 * np.cos(np.pi * t / peak), 0.5 + 0.5 * np.cos(np.pi * (t - peak) / (1 - peak)))
    return v


def escapement(rng):
    """B4: the scroll's clockwork stepping the stylus carriage one letter: a small dry click
    with the brief gear and spring rings the reference clockwork recordings show (~430 Hz and
    ~3 kHz, 10-12 ticks/s while running)."""
    length = 0.06
    click = highpass(burst(rng, 0.0004, length), 1500)
    y = click / (np.max(np.abs(click)) + 1e-9)
    y += mode(rng.uniform(410, 450), rng.uniform(0.008, 0.014), length) * db(rng.uniform(-14, -9))
    y += mode(rng.uniform(2850, 3100), rng.uniform(0.004, 0.008), length) * db(rng.uniform(-12, -7))
    return finish(y, lp=8000, rt60=0.14, wet=-24)


def clockwork_run(rng, seconds, v):
    """Clockwork running under a belt jog or smoothing pass: escapement-like ticks at
    10-12/s (interval spread like the references), louder while the drive is under load."""
    y = np.zeros(samples(seconds))
    at = rng.uniform(0, 0.03)
    while at < seconds:
        y += mode(rng.uniform(2800, 3100), 0.004, seconds, at) * db(rng.uniform(-8, 0)) * (0.4 + 0.6 * v[min(len(v) - 1, samples(at))])
        y += mode(rng.uniform(410, 450), 0.01, seconds, at) * db(-12)
        at += rng.uniform(0.06, 0.1) * (1 + rng.normal(0, 0.15))
    return y


def run_loop(rng, seconds=1.2):
    """B4 loop: the scroll clockwork running while the stylus writes, a quiet escapement and a
    faint dark room floor. The references keep a bed about 25-30 dB under the strokes instead of
    falling to digital silence between them. Seamless: the ends crossfade into each other."""
    n = samples(seconds)
    fold = samples(0.05)
    y = clockwork_run(rng, seconds + 0.05, np.ones(n + fold)) * db(-6)
    y += lowpass(fractal_noise(rng, n + fold, 1.6), 1500) * 0.02
    head, body_ = y[:fold], y[fold:fold + n].copy()
    body_[-fold:] = body_[-fold:] * np.linspace(1, 0, fold) + head * np.linspace(0, 1, fold)
    x = lowpass(highpass(body_, 80), 7000)
    return x / (np.max(np.abs(x)) + 1e-9) * db(-1)


def wax_noise(rng, n, lo=500, hi=2200, beta=1.0):
    """Stylus-on-wax friction noise between lo and hi. A measured pen on paper centres near
    1.3 kHz, Sailwind's own quill writing near 3-6.7 kHz. Wax strokes sit between the two."""
    x = band(fractal_noise(rng, n, beta), (lo + hi) / 2, (lo + hi) / 2 / (hi - lo))
    return x / (np.max(np.abs(x)) + 1e-9)


def paper(rng):
    """B5: the wax-cloth belt jogged forward over its rollers by the clockwork: a soft cloth
    swish that follows the belt speed, the run ticking underneath, and a light latch."""
    seconds = rng.uniform(0.25, 0.4)
    v = velocity(seconds, rng.uniform(0.35, 0.5))
    y = wax_noise(rng, samples(seconds), 300, 1400) * v * db(-6)
    y += clockwork_run(rng, seconds, v) * db(-10)
    tail = np.zeros(samples(seconds + 0.06))
    tail[:len(y)] = y
    latch = highpass(burst(rng, 0.0006, len(tail) / SR, seconds), 1200)
    tail += latch / (np.max(np.abs(latch)) + 1e-9) * db(-8)
    tail += mode(rng.uniform(380, 460), 0.012, len(tail) / SR, seconds) * db(-16)
    return finish(tail, lp=6000, rt60=0.18, wet=-22)


def eraser(rng):
    """B6: the smoothing pad wiping the wax flat: a slow, dark, even rub (softer than any
    stroke), the clockwork ticking under it, and the pad settling."""
    seconds = rng.uniform(0.5, 0.8)
    v = velocity(seconds, 0.5)
    y = wax_noise(rng, samples(seconds), 300, 1100) * np.sqrt(v)
    t = t_axis(seconds)
    y *= 1 - 0.15 * (0.5 + 0.5 * np.sin(2 * np.pi * rng.uniform(5, 8) * t))
    y = y / (np.max(np.abs(y)) + 1e-9) * db(-4) + clockwork_run(rng, seconds, v) * db(-12)
    end = np.zeros(samples(seconds + 0.05))
    end[:len(y)] = y
    end += mode(rng.uniform(300, 380), 0.01, len(end) / SR, seconds) * db(-16)
    return finish(end, lp=4500, rt60=0.18, wet=-22)


def stroke(rng):
    """B7: one stylus stroke in the wax. Shape follows measured pen strokes: 25-215 ms (median
    about 95), a soft ~20-40 ms onset rather than a tick, 10-14 Hz pressure wobble as the stroke
    turns. Colour sits between a pen on paper and Sailwind's brighter quill writing, with a
    light cabinet knock underneath."""
    seconds = float(np.clip(rng.lognormal(np.log(0.095), 0.45), 0.03, 0.22))
    n = samples(seconds)
    t = np.arange(n) / SR
    attack = min(seconds * 0.4, rng.uniform(0.02, 0.04))
    release = min(seconds * 0.3, rng.uniform(0.01, 0.02))
    rise = 0.5 - 0.5 * np.cos(np.pi * np.clip(t / attack, 0, 1))
    # pressure eases off after the onset peak, as the measured strokes do
    fade = np.exp(-np.maximum(t - attack, 0) / max(0.02, (seconds - attack) * rng.uniform(0.5, 0.9)))
    env = rise * fade * np.clip((seconds - t) / release, 0, 1)
    wobble = 1 - rng.uniform(0.3, 0.55) * (0.5 + 0.5 * np.sin(2 * np.pi * rng.uniform(10, 14) * t + rng.uniform(0, 6.3)))
    centre = rng.uniform(1800, 2600)
    y = wax_noise(rng, n, centre * 0.3, centre * 2.4, beta=0.7) * env * wobble
    y += impulses(rng, 900, seconds, 0.0003, 1500, 6000, 2.0, env) * 0.03
    # the stylus pressure knocks faintly through the cabinet, as Sailwind's writing keeps a low layer
    y += body(rng, y, (rng.uniform(140, 200), rng.uniform(320, 420)), 0.012, db(-10))
    low = lowpass(fractal_noise(rng, n, 1.0), 260) * env
    y += low / (np.max(np.abs(low)) + 1e-9) * np.max(np.abs(y)) * db(rng.uniform(-9, -6))
    return finish(y, lp=9000, rt60=0.18, wet=-22)


# ---------------------------------------------------------------- demo mixes (B2, B4, B7 + C)

def place(mix, take, at, gain):
    i = samples(at)
    seg = take[: max(0, len(mix) - i)]
    mix[i:i + len(seg)] += seg * gain


def splitflap_update(rng, flaps, rows=2, cells=18):
    """B2: wave starts per column, 12-18 flaps/s, steps capped to a virtual alphabet, last landing accent."""
    mix = np.zeros(samples(2.0))
    events = []
    for row in range(rows):
        for c in range(cells):
            if rng.random() < 0.25:
                continue  # unchanged cell
            steps = int(rng.integers(3, 16))
            rate = 16 * rng.uniform(0.95, 1.05)
            start = c * 0.035 + row * 0.012 + rng.uniform(0, 0.012)
            for k in range(1, steps + 1):
                events.append((start + k / rate * rng.uniform(0.93, 1.07), k == steps))
    events.sort()
    last = None
    for when, final in events:
        take = int(rng.integers(len(flaps)))
        if take == last:
            take = (take + 1) % len(flaps)
        last = take
        gain = db(rng.uniform(-9, 0)) * (db(2) if final else 1)
        place(mix, pitched(flaps[take], rng.uniform(-40, 40)), when, gain)
    return soft_clip(mix / np.max(np.abs(mix)) * db(-3))


def drum_update(rng, steps_takes, stops, rows=2, wheels=22):
    """Letter drums on one indexing shaft: every step advances all wheels that still need to
    move, so each step is a single chunky index at 11-14 steps/s. A step is louder while many
    wheels are engaged, and the last wheel lands on a firmer stop."""
    targets = [int(rng.integers(3, 20)) for _ in range(rows * wheels) if rng.random() > 0.3]
    rate = rng.uniform(11, 14)
    mix = np.zeros(samples(0.4 + max(targets) / rate + 0.4))
    at = 0.15
    last = None
    for k in range(1, max(targets) + 1):
        engaged = sum(1 for t in targets if t >= k)
        final = k == max(targets)
        group = stops if final else steps_takes
        take = int(rng.integers(len(group)))
        if not final and take == last:
            take = (take + 1) % len(group)
        last = take
        gain = db(rng.uniform(-4, 0)) * (engaged / len(targets)) ** 0.3
        place(mix, pitched(group[take], rng.uniform(-30, 30)), at, gain * (db(2) if final else 1))
        at += 1 / rate * rng.uniform(0.94, 1.06)
    return soft_clip(mix / np.max(np.abs(mix)) * db(-3))


def scroll_update(rng, erasers, papers, strokes, ticks, run):
    """B4-B7: smoothing pass, belt jog, then the stylus writes both lines: per letter the
    clockwork steps the carriage (one tick) and the stylus cuts one or two strokes."""
    mix = np.zeros(samples(16.0))
    place(mix, erasers[0], 0.0, 1.0)
    place(mix, papers[0], len(erasers[0]) / SR + 0.05, db(-2))
    at = len(erasers[0]) / SR + len(papers[0]) / SR + 0.15
    for line in range(2):
        for _ in range(int(rng.integers(12, 20))):
            place(mix, ticks[int(rng.integers(len(ticks)))], at, db(rng.uniform(-21, -16)))
            at += rng.uniform(0.02, 0.04)
            for _ in range(int(rng.integers(1, 3))):
                s_ = strokes[int(rng.integers(len(strokes)))]
                place(mix, pitched(s_, rng.uniform(-40, 40)), at, db(rng.uniform(-10, 0)))
                # strokes overlap their room tails: about 5-6 letters a second
                at += rng.uniform(0.06, 0.12)
        at += 0.3
        if line == 0:
            place(mix, papers[1], at, db(-6))
            at += len(papers[1]) / SR + 0.1
    # the clockwork bed runs through the whole update, looped like the runtime will
    start = 0.0
    bed = np.tile(run, int((at - start) / (len(run) / SR)) + 2)[: samples(at - start)]
    bed[: samples(0.05)] *= np.linspace(0, 1, samples(0.05))
    bed[-samples(0.15):] *= np.linspace(1, 0, samples(0.15))
    place(mix, bed, start, db(-26))
    mix = mix[: samples(at + 0.2)]
    return soft_clip(mix / np.max(np.abs(mix)) * db(-6))


def main():
    rng = np.random.default_rng(1300)
    flaps = [flap(rng) for _ in range(12)]
    steps = [index(rng) for _ in range(8)]
    stops = [index(rng, heavy=True) for _ in range(4)]
    papers = [paper(rng) for _ in range(6)]
    erasers = [eraser(rng) for _ in range(6)]
    strokes = [stroke(rng) for _ in range(10)]
    ticks = [escapement(rng) for _ in range(6)]
    runs = [run_loop(rng)]
    takes = {"flap": flaps, "index": steps, "stop": stops, "paper": papers, "eraser": erasers, "stroke": strokes, "tick": ticks, "run": runs}
    total = 0.0
    for name, group in takes.items():
        for i, x in enumerate(group):
            write(OUT / "takes" / f"{name}-{i + 1:02d}.wav", x, TAKE_SR)
            total += len(x) / SR
    demo_rng = np.random.default_rng(1301)
    write(OUT / "demo-aestrin-splitflap.wav", splitflap_update(demo_rng, flaps), SR)
    write(OUT / "demo-alankh-drums.wav", drum_update(demo_rng, steps, stops), SR)
    write(OUT / "demo-emerald-scroll.wav", scroll_update(demo_rng, erasers, papers, strokes, ticks, runs[0]), SR)
    print(f"{OUT}: {sum(len(g) for g in takes.values())} takes, {total:.1f} s of take audio "
          f"(~{total * TAKE_SR * 2 / 1024:.0f} KiB at {TAKE_SR} Hz), 3 demos")


if __name__ == "__main__":
    main()
