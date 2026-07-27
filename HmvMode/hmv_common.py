"""Shared constants/math for HMV mode, used by both main.py (offline, librosa-based beat
detection) and live.py (real-time, envelope-follower based - see live.py's own docstring for why
that one exists separately). Deliberately has NO heavy dependencies (just colorsys/struct from the
stdlib) - main.py can't be frozen into an .exe because of what IT imports (librosa -> numba), but
this module itself was never the problem, and live.py needs these exact already-tuned/verified
formulas without pulling librosa in transitively (a plain `import main` would do exactly that,
since main.py imports librosa at module level).

Wire format (matches GamePatcher.cs's HMV_MODE section, unchanged by which tool sends it):
    float32 thrust_speed, float32 thrust_strength, float32 thrust_middle, uint32 bgr_color
"""
from __future__ import annotations

import colorsys

UPDATE_HZ = 30
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 45736

# See main.py's v3 design note (HMVMODE.txt has the full story): hue rotation targets constant
# PERCEIVED brightness, not just a fixed HSV "value" - pure yellow and pure blue can share an
# HSV value yet look wildly different in brightness, which read as flickering before this fix.
HUE_SATURATION = 0.6
TARGET_LUMA = 0.47
HUE_STEP_DEGREES = 137.5

# How long a hit's "pulse" visibly lingers before settling back to baseline - short enough that
# each hit reads as a distinct pulse rather than a slow swell (also from the v2 fix).
PULSE_DECAY_SEC = 0.18

THRUST_MIDDLE = 0.5
THRUST_STRENGTH_BASE = 2.0
THRUST_STRENGTH_PEAK = 7.0
THRUST_SPEED_PULSE_BOOST = 0.5  # up to +50% speed right on a hit, settling back between hits


def hue_to_bgr_int(hue_deg: float) -> int:
    """HSV hue -> packed GameMaker BGR color, at a constant PERCEIVED brightness regardless of
    hue. See the module docstring / HMVMODE.txt for why this isn't just a fixed HSV value."""
    h = (hue_deg % 360) / 360.0
    r1, g1, b1 = colorsys.hsv_to_rgb(h, HUE_SATURATION, 1.0)
    luma_at_full_value = 0.299 * r1 + 0.587 * g1 + 0.114 * b1
    value = min(1.0, TARGET_LUMA / max(0.05, luma_at_full_value))
    r, g, b = colorsys.hsv_to_rgb(h, HUE_SATURATION, value)
    ri, gi, bi = int(r * 255), int(g * 255), int(b * 255)
    return (bi << 16) | (gi << 8) | ri  # GameMaker colors are packed 0x00BBGGRR


def pulse_value(time_since_hit: float) -> float:
    """1.0 right on a hit, decaying linearly to 0.0 over PULSE_DECAY_SEC - the shared "discrete
    decaying pulse, not smooth fade" shape both main.py and live.py rely on."""
    return max(0.0, 1.0 - time_since_hit / PULSE_DECAY_SEC)
