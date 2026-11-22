#!/usr/bin/env python3
"""Offline generator of the 320 bits of 2/pi that SimMath's Payne-Hanek reduction reads.

Spec 015 (specs/015-shared-simulation-core/research.md R3). A clean-room tool, run in a throwaway
virtual environment pinned by requirements.txt and never in CI. emit_cs.py writes the words to
src/MmoEngine.SimCore/Math/TwoOverPi.g.cs.

The words are floor(2/pi * 2^320), split into ten 32-bit words, most significant first: word j holds
the bits of 2/pi from 2^-(32j + 1) to 2^-(32j + 32). They are computed at 120 significant digits
(about 398 bits), and the script checks that the bits past the 320th are not so close to a word
boundary that the extra precision could not settle them.
"""

import sys

import mpmath
from mpmath import mp, mpf

mp.dps = 120

BITS = 320
WORDS = BITS // 32


def two_over_pi_words():
    """The ten 32-bit words of floor(2/pi * 2^320), most significant first."""
    scaled = 2 / mp.pi * mpf(2) ** BITS
    whole = int(mpmath.floor(scaled))
    left = scaled - whole
    # 120 digits leave about 78 bits past bit 320: the remainder must be clear of 0 and 1 by far more
    # than the working precision.
    margin = mpf(2) ** -60
    if left < margin or left > 1 - margin:
        raise ValueError("2/pi is too close to a word boundary at bit 320 for 120 digits")
    words = [(whole >> (32 * (WORDS - 1 - j))) & 0xFFFFFFFF for j in range(WORDS)]
    if len(words) != WORDS or words[0] >> 31 != 1:
        raise ValueError("2/pi must start with a set bit at 2^-1")
    return words


def main():
    for index, word in enumerate(two_over_pi_words()):
        print(f"TwoOverPi.Words[{index}] 0x{word:08X}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
