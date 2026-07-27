#!/usr/bin/env python3
"""HMV mode - drives a patched game's thrust rhythm and background tint from a local song's
beat, over the UDP channel the game's HMV patch listens on (see GamePatcher.cs's HMV MODE
section for the game side of this). Doesn't touch the game or Buttplug.io at all directly -
it only ever sends small UDP packets; the game (if patched with --hmv) is what actually acts
on them, and fails safe on its own if this tool stops sending (falls back to normal behavior
within half a second).

Usage:
    python main.py path/to/song.mp3 [--cycles-per-beat 0.3] [--host 127.0.0.1] [--port 45736]

Wire format per packet (16 bytes, little-endian, matches GamePatcher.cs's HMV_MODE section):
    float32 thrust_speed, float32 thrust_strength, float32 thrust_middle, uint32 bgr_color

DESIGN NOTES:
  v2: switched from a time-smoothed loudness curve (read as slow unsynced fading) to discrete
  pulses locked to detected beat timestamps, and slowed the default base pace way down (the v1
  default was ~7x faster than the game's own default pace at a typical song's tempo).

  v3 (this version), after a second real playtest: two more real issues.
    1. "Hue going light to dark" turned out to be a real color-math bug, not a game-side issue -
       HSV's "value" (brightness) is NOT how human eyes perceive brightness. Pure yellow and
       pure blue can have the identical HSV value yet look dramatically different in brightness
       (yellow's perceptual luma is roughly 8x blue's). Since hue was rotating through all colors
       at a genuinely fixed V, it was still swinging between perceptually-bright and
       perceptually-dark hues, which reads exactly as flickering brightness even though V never
       moved. Fixed by targeting constant PERCEPTUAL luma instead of constant HSV value -
       hue_to_bgr_int now solves for whatever V makes each hue's actual apparent brightness equal.
    2. "Thrusting doesn't match the audio" - beat_track was running against the full mix (vocals,
       melody, everything blended), which is unreliable for busy/mashed-together tracks. Now
       isolates the percussive component first (librosa.effects.hpss splits harmonic vs.
       percussive content) and, per direct feedback, further restricts onset detection to
       low/bass frequencies specifically - both beat tracking AND the per-beat pulse trigger are
       now based on that bass-focused percussive signal, so pulses land on actual felt bass hits
       rather than a generic whole-mix estimate.
"""
from __future__ import annotations

import argparse
import socket
import struct
import sys
import time

import numpy as np
import librosa
import sounddevice as sd

from hmv_common import (
    UPDATE_HZ, DEFAULT_HOST, DEFAULT_PORT, HUE_STEP_DEGREES, PULSE_DECAY_SEC,
    THRUST_MIDDLE, THRUST_STRENGTH_BASE, THRUST_STRENGTH_PEAK, THRUST_SPEED_PULSE_BOOST,
    hue_to_bgr_int, pulse_value,
)

# Bass/kick range for isolating "the beat" from the rest of the mix.
BASS_FMAX_HZ = 200

# NOTE: HUE_SATURATION/TARGET_LUMA/hue_to_bgr_int's perceptual-luma-correction math, and the
# discrete-decaying-pulse shape (pulse_value), now live in hmv_common.py so live.py (the new
# real-time engine - see HMVMODE.txt) can reuse these exact already-tuned/verified formulas
# without pulling in librosa (which live.py deliberately avoids). Behavior here is unchanged.


def analyze(path: str) -> tuple[np.ndarray, int, float, np.ndarray]:
    print(f"Loading and analyzing {path} ...")
    y, sr = librosa.load(path, sr=None, mono=True)

    # Isolate rhythm content from melody/vocals before tracking anything - much more reliable
    # for busy mixes/mashups than analyzing the full signal together.
    _y_harmonic, y_percussive = librosa.effects.hpss(y)

    # Restrict onset detection to bass/kick frequencies specifically, per direct feedback that
    # syncing to "something that repeats like a beat/bass" would feel more accurate than a
    # generic whole-spectrum onset signal.
    # n_mels kept low deliberately - the default (128) crams far more mel filters into this
    # narrow a band than the underlying FFT frequency resolution can actually distinguish,
    # leaving most of them empty (librosa warns about exactly this otherwise).
    S_bass = librosa.feature.melspectrogram(y=y_percussive, sr=sr, fmax=BASS_FMAX_HZ, n_mels=20)
    onset_env = librosa.onset.onset_strength(sr=sr, S=librosa.power_to_db(S_bass, ref=np.max))

    tempo, _ = librosa.beat.beat_track(onset_envelope=onset_env, sr=sr)
    tempo = float(np.asarray(tempo).item())

    beat_times = librosa.onset.onset_detect(onset_envelope=onset_env, sr=sr, units="time")
    beat_times = np.asarray(beat_times).reshape(-1)

    print(f"Detected tempo: {tempo:.1f} BPM, {len(beat_times)} bass hits, duration: {len(y) / sr:.1f}s")
    return y, sr, tempo, beat_times


def run(path: str, host: str, port: int, cycles_per_beat: float) -> None:
    y, sr, tempo, beat_times = analyze(path)

    base_thrust_speed = (tempo / 10.0) * cycles_per_beat

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    print(f"base thrust_speed = {base_thrust_speed:.2f} (game's own normal pace is 2.0, for reference)")
    print(f"Sending to {host}:{port} at {UPDATE_HZ} Hz. Ctrl+C to stop early.")

    sd.play(y, sr)
    start = time.monotonic()
    duration = len(y) / sr
    period = 1.0 / UPDATE_HZ

    beat_idx = 0
    current_hue = 0.0

    try:
        while True:
            elapsed = time.monotonic() - start
            if elapsed >= duration:
                break

            # Step to a new hue for every bass hit that's happened since the last update - this
            # is what makes the color change land exactly ON each hit rather than drifting.
            while beat_idx < len(beat_times) and beat_times[beat_idx] <= elapsed:
                current_hue = (current_hue + HUE_STEP_DEGREES) % 360
                beat_idx += 1

            last_beat_time = beat_times[beat_idx - 1] if beat_idx > 0 else 0.0
            time_since_beat = elapsed - last_beat_time
            pulse = pulse_value(time_since_beat)

            thrust_strength = THRUST_STRENGTH_BASE + pulse * (THRUST_STRENGTH_PEAK - THRUST_STRENGTH_BASE)
            thrust_speed = base_thrust_speed * (1.0 + pulse * THRUST_SPEED_PULSE_BOOST)
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
    parser = argparse.ArgumentParser(description="Sync a patched game's thrust and background tint to a song's beat.")
    parser.add_argument("song", help="Path to a local audio file (mp3, wav, ogg, flac, ...)")
    parser.add_argument("--cycles-per-beat", type=float, default=0.3,
                         help="Base thrust cycles per detected beat before the per-beat pulse boost (default 0.3 - "
                              "raise this if it still feels too slow, lower it if still too fast)")
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    args = parser.parse_args()

    run(args.song, args.host, args.port, args.cycles_per_beat)
    return 0


if __name__ == "__main__":
    sys.exit(main())
