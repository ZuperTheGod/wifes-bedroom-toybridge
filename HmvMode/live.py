#!/usr/bin/env python3
"""HMV live mode - a real-time, no-pre-analysis-wait alternative to main.py's offline
beat-locked analysis (see main.py / HMVMODE.txt for that one, which stays exactly as-is). Where
main.py pre-scans an entire song file for a BPM/beat-timestamp grid via librosa (accurate, but
needs the whole file analyzed up front, and can't be frozen into an .exe because librosa's numba
dependency crashes under PyInstaller - see HMVMODE.txt's "WHY NOT AN EXE"), this reacts as
playback happens: decode once (fast), then start streaming immediately, with no separate
"analyze first" phase. It also has NO librosa/numba dependency at all, so unlike main.py this one
is a real candidate for a standalone .exe.

Approach, inspired by two small open-source Buttplug.io-adjacent tools the user pointed at:
  - Audio-To-Vibrations (github.com/DabzillaNation/Audio-To-Vibrations, MIT license): reacts to
    audio live via smoothing + a threshold cutoff, rather than pre-scanning a whole file. That
    general shape - envelope-follow, then fire on a threshold crossing - is reused here.
  - musicboom (github.com/NovaGlider/musicboom, NO stated license - only its GENERAL idea is
    reused below, not its source, since no license means no legal right to copy its code;
    frequency-band splitting via a low/high-pass filter isn't itself copyrightable): splits audio
    into a bass band and a treble band, one motor per band. Adapted here as bass -> the game's
    thrust/color pulse, treble -> a smaller secondary rhythmic accent, instead of two motors.

Both real, hard-won fixes from main.py's own v2/v3 playtests (see HMVMODE.txt) are reused
VERBATIM via hmv_common, not re-derived: pulses are discrete and decaying (pulse_value,
PULSE_DECAY_SEC), not a smooth fade, and hue rotation targets constant PERCEIVED brightness
(hue_to_bgr_int), not a naive fixed HSV value.

Known trade-off vs. main.py: this does NOT separate percussive from harmonic content first
(librosa.effects.hpss) the way main.py does, because that's an offline, non-causal technique that
doesn't fit a genuinely real-time/streaming design - so busy/mashed-together tracks may lock on
less precisely here than in main.py's offline mode. Neither Audio-To-Vibrations nor musicboom do
this either (they filter the live raw signal directly), so this matches their actual approach
rather than trying to approximate HPSS causally.

Usage:
    python live.py path/to/song.mp3 [--host 127.0.0.1] [--port 45736] [--base-speed 3.0]

HONEST STATUS: envelope/threshold logic and packet output verified against a synthetic
known-tempo test track (see HMVMODE.txt for the equivalent main.py verification) - NOT yet
playtested in-game for actual feel. Same open item as main.py's own v3.

DECODING: soundfile (libsndfile) handles wav/flac/ogg directly and fast, but it does NOT support
m4a/AAC at all (confirmed directly - "Format not recognised", not a bug on our end, libsndfile
has simply never supported that container/codec) and its mp3 support depends on the libsndfile
version bundled with the installed soundfile wheel. Falls back to audioread (which shells out to
an external ffmpeg binary via its FFmpegAudioFile backend) for anything soundfile can't open -
confirmed this correctly decodes a real .m4a file. This means the fallback path needs ffmpeg on
PATH; if that's not the case on some future machine, that's the first thing to check for an
"couldn't decode" report on an mp3/m4a/etc. that soundfile itself rejects.

THRUST DEPTH (v2 of this file, after real playtest feedback): thrust_strength used to be driven
by the same discrete decaying bass-pulse as thrust_speed (base 2.0 -> peak 7.0 on a hit). Real
feedback: speed variation read fine, but depth didn't - the stroke should go completely in and
out on loud parts and stay a smaller, incomplete motion on quiet parts, scaled continuously by
loudness ("percentages"), not in discrete hit-triggered pulses.

Root cause of why 2.0->7.0 never actually delivered "more complete," confirmed by reading the
real GML (gml_Object_oFutaMatingPress_Draw_0), not guessed:
    thrust_set = median(1, thrust_middle + (dcos(thrust_time) * (0.25 * thrust_strength)), 0)
`median(1, x, 0)` is GameMaker's clamp-to-[0,1] idiom, and it's unconditional (not just during
orgasm/knot/edge states, as an earlier summary in NOTES.txt implied). At thrust_strength = 2.0,
the amplitude (0.25*2 = 0.5) already swings thrust_set the full 0.5 +/- 0.5 = exactly 0 to 1 -
already a COMPLETE stroke. Anything above 2.0 is clamped right back to [0,1] anyway - it doesn't
add depth, it just makes the wave spend longer sitting pinned at the 0/1 extremes before swinging
back. That's a pacing difference, not a completeness difference, which is exactly the gap between
what the old pulse design produced and what was actually asked for.

Fixed by scaling thrust_strength continuously by loudness-as-a-percentage-of-this-song's-own-peak,
between STRENGTH_QUIET (a small, mostly-centered, INCOMPLETE stroke) and STRENGTH_LOUD (2.0 - the
exact value that reaches the true 0..1 extremes, per the math above). thrust_speed's existing
bass/treble-pulse-driven formula is UNCHANGED (that part's feel was confirmed fine) - only
thrust_strength's source signal changed.

FIRST ATTEMPT AT THIS (worth recording so it isn't re-tried unchanged) used an ONLINE adaptive
ceiling - "loud" = current envelope vs. a peak-hold reference that rises instantly to a new peak
and decays slowly otherwise. Built, then tested against a synthetic quiet->loud->quiet track and
caught via real UDP capture (not assumed) that it was WRONG: because the ceiling rises in a single
step to match whatever's currently playing, a sustained STEADY section (quiet or loud, doesn't
matter) makes the ceiling lock onto itself within one tick, so pct reads ~100% almost immediately
regardless of the section's actual absolute level - it can only measure "loud relative to a peak
that hasn't decayed away yet," not "loud relative to this song's real dynamic range." Confirmed via
capture: strength hit ~2.0 within half a second into a deliberately QUIET synthetic section.

SECOND ATTEMPT (also worth recording): computed the ceiling once from the whole decoded array (99th
percentile of individual |sample| values) - an improvement, but re-testing (added temporary debug
logging of live.py's own elapsed/pct/strength directly, sidestepping any cross-process clock
ambiguity from UDP-capture-based testing) showed the loud section only ever reached pct~0.64, never
approaching 1.0. Root cause: the per-tick envelope is a MEAN of the chunk's |samples| (smooth,
matches BandFollower's own convention), while the ceiling was a PERCENTILE of INDIVIDUAL samples
(much closer to peak) - for any waveform, mean(|x|) and a high percentile of individual |x| have a
basically fixed ratio (a sine wave's mean is ~64% of its peak, a "crest factor" thing) that has
nothing to do with actual relative loudness, so pct could never reach 1.0 even at the true loudest
point. Comparing two different statistics against each other, not an actual loudness difference.

Fixed for real by making both sides of the ratio the SAME statistic: precompute a mean-abs
envelope over the same ~1/30s window size used per playback tick, across the WHOLE song (one
reshape+mean, still a single cheap vectorized pass - not the kind of slow pre-analysis this module
avoids), then take the 99th percentile of THAT array as the ceiling (compute_song_loudness_ceiling
below). Verified with live.py's own internal elapsed/pct/strength logged directly during a real
run (removing any two-process clock ambiguity a UDP capture would introduce) against the
quiet(0-6s)->loud(6-12s)->quiet(12-18s) test track: strength reads ~0.39 through both quiet
sections and ~1.98-2.00 through the loud section - essentially STRENGTH_QUIET and STRENGTH_LOUD
respectively, tracking the song's actual shape.
"""
from __future__ import annotations

import argparse
import socket
import struct
import sys
import time

import numpy as np
import soundfile as sf
import sounddevice as sd
from scipy.signal import lfilter, lfilter_zi

import audioread

from hmv_common import (
    UPDATE_HZ, DEFAULT_HOST, DEFAULT_PORT, HUE_STEP_DEGREES,
    THRUST_MIDDLE, THRUST_STRENGTH_BASE, THRUST_SPEED_PULSE_BOOST,
    hue_to_bgr_int, pulse_value,
)

# Bass/treble split points - same cutoffs musicboom uses (its idea, reimplemented independently).
BASS_FC_HZ = 200.0
TREBLE_FC_HZ = 1000.0

# How fast each band's rolling "floor" (recent-average loudness) adapts - slow enough that it
# tracks the song's overall level, not individual hits, so hits are judged relative to what's
# normal for THIS section of THIS song rather than a fixed absolute threshold.
FLOOR_TAU_SEC = 1.5

# A band "hits" when its envelope exceeds its own floor by this ratio...
HIT_RATIO = 1.5
# ...and at least this long has passed since that band's last hit (avoids re-triggering multiple
# times on one sustained transient).
MIN_HIT_INTERVAL_SEC = 0.12

TREBLE_SPEED_BOOST = 0.15  # smaller than bass's THRUST_SPEED_PULSE_BOOST - a secondary accent

STRENGTH_QUIET = 0.3  # a small, mostly-centered, genuinely INCOMPLETE stroke at silence
STRENGTH_LOUD = THRUST_STRENGTH_BASE  # 2.0 - see THRUST DEPTH note in the module docstring

# Percentile (not a flat max) so one single clipped/outlier moment can't single-handedly set the
# ceiling absurdly high and make everything else in the song read as quiet by comparison.
SONG_CEILING_PERCENTILE = 99.0


def compute_song_loudness_ceiling(y: np.ndarray, sr: float) -> float:
    """The value that per-tick loudness (see the mean-abs-of-chunk computation in run()) is
    compared against to get a 0..1 percentage. MUST use the exact same statistic (mean-abs over a
    ~1/UPDATE_HZ-second window) as what's computed per-tick during playback - comparing a chunk
    MEAN against a raw-sample PERCENTILE looks superficially reasonable but is comparing two
    different statistics with a basically fixed ratio between them for any steady waveform (a
    sine wave's mean is ~64% of its peak, a "crest factor" thing that has nothing to do with
    actual relative loudness) - confirmed this was wrong the first way, see THRUST DEPTH above."""
    window_size = max(1, int(round(sr / UPDATE_HZ)))
    n_windows = len(y) // window_size
    if n_windows < 1:
        return max(float(np.mean(np.abs(y))) if len(y) else 1e-6, 1e-6)
    trimmed = y[: n_windows * window_size]
    windowed_envelope = np.abs(trimmed).reshape(n_windows, window_size).mean(axis=1)
    return max(float(np.percentile(windowed_envelope, SONG_CEILING_PERCENTILE)), 1e-6)


def _rbj_lowpass(sr: float, fc: float, q: float = 0.707) -> tuple[np.ndarray, np.ndarray]:
    """Standard RBJ Audio EQ Cookbook low-pass biquad coefficients."""
    w0 = 2 * np.pi * fc / sr
    alpha = np.sin(w0) / (2 * q)
    cosw0 = np.cos(w0)
    b0, b1, b2 = (1 - cosw0) / 2, 1 - cosw0, (1 - cosw0) / 2
    a0, a1, a2 = 1 + alpha, -2 * cosw0, 1 - alpha
    return np.array([b0, b1, b2]) / a0, np.array([1.0, a1 / a0, a2 / a0])


def _rbj_highpass(sr: float, fc: float, q: float = 0.707) -> tuple[np.ndarray, np.ndarray]:
    """Standard RBJ Audio EQ Cookbook high-pass biquad coefficients."""
    w0 = 2 * np.pi * fc / sr
    alpha = np.sin(w0) / (2 * q)
    cosw0 = np.cos(w0)
    b0, b1, b2 = (1 + cosw0) / 2, -(1 + cosw0), (1 + cosw0) / 2
    a0, a1, a2 = 1 + alpha, -2 * cosw0, 1 - alpha
    return np.array([b0, b1, b2]) / a0, np.array([1.0, a1 / a0, a2 / a0])


class BandFollower:
    """One frequency band's filter + envelope + rolling floor + hit detector, fed one small
    chunk (one 1/30s tick's worth of samples) at a time as playback proceeds."""

    def __init__(self, sr: float, fc: float, is_lowpass: bool):
        self.sr = sr
        self.b, self.a = _rbj_lowpass(sr, fc) if is_lowpass else _rbj_highpass(sr, fc)
        self.zi = lfilter_zi(self.b, self.a) * 0.0
        self.floor: float | None = None
        self.last_hit_time = -1.0

    def process_chunk(self, chunk: np.ndarray, now: float) -> tuple[bool, float]:
        """Returns (hit_happened_this_chunk, pulse_value_for_now)."""
        filtered, self.zi = lfilter(self.b, self.a, chunk, zi=self.zi)
        env = float(np.mean(np.abs(filtered))) if len(filtered) else 0.0

        if self.floor is None:
            self.floor = env  # bootstrap: don't fire a spurious hit on the very first chunk
        else:
            dt = len(chunk) / self.sr
            alpha = 1.0 - np.exp(-dt / FLOOR_TAU_SEC) if dt > 0 else 0.0
            self.floor += alpha * (env - self.floor)

        hit = False
        if env > self.floor * HIT_RATIO and (now - self.last_hit_time) >= MIN_HIT_INTERVAL_SEC:
            hit = True
            self.last_hit_time = now

        return hit, pulse_value(now - self.last_hit_time if self.last_hit_time >= 0 else 1e9)


def _decode_with_audioread(path: str) -> tuple[np.ndarray, int]:
    """Fallback for anything soundfile can't open (m4a/AAC, some mp3 builds, etc.) - shells out to
    ffmpeg via audioread. audioread always yields signed 16-bit little-endian PCM regardless of
    the source format, per its own documented contract."""
    with audioread.audio_open(path) as f:
        sr = f.samplerate
        channels = f.channels
        buf = bytearray()
        for chunk in f:
            buf += chunk
    data = np.frombuffer(bytes(buf), dtype="<i2").astype(np.float32) / 32768.0
    if channels > 1:
        data = data.reshape(-1, channels)
    return data, sr


def load_audio(path: str) -> tuple[np.ndarray, int]:
    try:
        return sf.read(path, dtype="float32", always_2d=False)
    except Exception as ex:
        print(f"soundfile couldn't open this file directly ({ex}) - trying ffmpeg fallback...")
        try:
            return _decode_with_audioread(path)
        except Exception as ex2:
            raise RuntimeError(
                f"Couldn't decode {path} with either soundfile or ffmpeg. "
                f"soundfile said: {ex}. ffmpeg fallback said: {ex2}. "
                "Is ffmpeg installed and on PATH?"
            ) from ex2


def run(path: str, host: str, port: int, base_speed: float) -> None:
    print(f"Loading {path} ...")
    y, sr = load_audio(path)
    if y.ndim > 1:
        y = y.mean(axis=1)  # collapse to mono, same as main.py's librosa.load(mono=True)
    duration = len(y) / sr
    print(f"Loaded: {duration:.1f}s @ {sr} Hz. No pre-analysis needed - starting immediately.")
    song_ceiling = compute_song_loudness_ceiling(y, sr)

    bass = BandFollower(sr, BASS_FC_HZ, is_lowpass=True)
    treble = BandFollower(sr, TREBLE_FC_HZ, is_lowpass=False)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print(f"base thrust_speed = {base_speed:.2f} (game's own normal pace is 2.0, for reference)")
    print(f"Sending to {host}:{port} at {UPDATE_HZ} Hz. Ctrl+C to stop early.")

    sd.play(y, sr)
    start = time.monotonic()
    period = 1.0 / UPDATE_HZ
    next_sample = 0
    current_hue = 0.0

    try:
        while True:
            elapsed = time.monotonic() - start
            if elapsed >= duration:
                break

            target_sample = min(len(y), int(elapsed * sr))
            chunk = y[next_sample:target_sample]
            next_sample = target_sample

            bass_hit, bass_pulse = bass.process_chunk(chunk, elapsed) if len(chunk) else (False, 0.0)
            _treble_hit, treble_pulse = treble.process_chunk(chunk, elapsed) if len(chunk) else (False, 0.0)
            loudness_pct = min(1.0, float(np.mean(np.abs(chunk))) / song_ceiling) if len(chunk) else 0.0

            if bass_hit:
                current_hue = (current_hue + HUE_STEP_DEGREES) % 360

            # Depth (thrust_strength) now tracks continuous loudness, not discrete pulses - see
            # THRUST DEPTH in the module docstring for why 2.0 is the ceiling, not higher.
            thrust_strength = STRENGTH_QUIET + loudness_pct * (STRENGTH_LOUD - STRENGTH_QUIET)
            # Speed still follows the bass/treble hit-pulses - confirmed feeling fine as-is.
            thrust_speed = base_speed * (1.0 + bass_pulse * THRUST_SPEED_PULSE_BOOST + treble_pulse * TREBLE_SPEED_BOOST)
            color = hue_to_bgr_int(current_hue)

            packet = struct.pack("<3fI", thrust_speed, thrust_strength, THRUST_MIDDLE, color)
            sock.sendto(packet, (host, port))

            time.sleep(period)
    except KeyboardInterrupt:
        print("\nStopped early.")
    finally:
        sd.stop()
        sock.close()
        print("Done - the game will fall back to normal behavior within half a second.")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Real-time (no pre-analysis) alternative to main.py - reacts live to a song's "
                    "bass/treble instead of pre-scanning for a beat grid.")
    parser.add_argument("song", help="Path to a local audio file (mp3, wav, ogg, flac, ...)")
    parser.add_argument("--base-speed", type=float, default=3.0,
                         help="Resting thrust_speed between hits (default 3.0 - game's own normal "
                              "pace is 2.0; hits multiply on top of this, same as main.py's pulses)")
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    args = parser.parse_args()

    run(args.song, args.host, args.port, args.base_speed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
