"""
Property-based media-degradation suite for the IgnisDisc framing, checksum and parity code.

The suite damages encoded data the way failing optical media does (bit flips, bursts, zero-filled sectors,
dropped or shifted blocks, truncation) and checks two invariants against the shipped C# code:

  1. Exact recovery          - damage inside the parity bound gives back the bit-identical original.
  2. Zero silent corruption  - damage outside the bound gives an explicit error, never different bytes
                               reported as good.

The C# code is reached through CodecBridge.cs (same folder), so the properties run against the real
implementation. Expected values come from independent models written from the standards:
ECMA-130 (CD-ROM sync, header, EDC, P/Q parity), IEC 60908 Annex J (CD-Text CRC), RFC 9639 (FLAC layout)
and the PAR 2.0 specification (packet layout).

A test marked xfail(strict=True) records a confirmed defect: the property is the behaviour we want, and the
test fails today. When the defect is fixed the strict marker turns the suite red until the marker is removed.

Run:
    pip install hypothesis pytest
    pytest tests/PropertyVerification -q

Environment:
    IGNIS_PROPERTY_EXAMPLES   examples per fast property (default 200)
"""

from __future__ import annotations

import atexit
import base64
import hashlib
import json
import os
import random
import shutil
import struct
import subprocess
import tempfile
from pathlib import Path

import pytest
from hypothesis import assume, example, given, settings
from hypothesis import strategies as st

HERE = Path(__file__).resolve().parent
EXAMPLES = int(os.environ.get("IGNIS_PROPERTY_EXAMPLES", "200"))

settings.register_profile("media", deadline=None, max_examples=EXAMPLES, database=None, print_blob=False)
settings.load_profile("media")

# Properties that touch the file system or start external processes run fewer examples.
SLOW = settings(deadline=None, max_examples=max(10, EXAMPLES // 4), database=None)
VERY_SLOW = settings(deadline=None, max_examples=max(6, EXAMPLES // 8), database=None)


# ---------------------------------------------------------------------------------------------------------
# Bridge to the shipped C# code
# ---------------------------------------------------------------------------------------------------------

class Rejected(Exception):
    """The C# code refused the input with an exception: an explicit, visible failure."""

    def __init__(self, kind: str, message: str):
        super().__init__(f"{kind}: {message}")
        self.kind = kind
        self.message = message


class _Bridge:
    def __init__(self) -> None:
        self.proc = subprocess.Popen(
            ["dotnet", "run", "CodecBridge.cs"],
            cwd=HERE,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            bufsize=1,
        )
        startup = []
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("CodecBridge.cs did not start:\n" + "".join(startup))
            if line.strip() == "READY":
                return
            startup.append(line)

    def call(self, op: str, **args):
        request = {"op": op}
        for key, value in args.items():
            request[key] = base64.b64encode(bytes(value)).decode() if isinstance(value, (bytes, bytearray)) else value
        self.proc.stdin.write(json.dumps(request) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        if not line:
            raise RuntimeError("CodecBridge.cs exited unexpectedly")
        reply = json.loads(line)
        if not reply.pop("ok"):
            raise Rejected(reply["error"], reply["message"])
        return reply

    def close(self) -> None:
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()


_bridge: _Bridge | None = None


def call(op: str, **args):
    global _bridge
    if _bridge is None:
        _bridge = _Bridge()
        atexit.register(_bridge.close)
    return _bridge.call(op, **args)


def unb64(text: str) -> bytes:
    return base64.b64decode(text)


# ---------------------------------------------------------------------------------------------------------
# Shared strategies
# ---------------------------------------------------------------------------------------------------------

def bulk_bytes(size: int, kind: str, seed: int) -> bytes:
    """Deterministic bulk data. Drawing a seed keeps large buffers out of Hypothesis's shrink budget."""
    if kind == "zeros":
        return bytes(size)
    if kind == "ones":
        return b"\xff" * size
    return random.Random(seed).randbytes(size)


data_kinds = st.sampled_from(["random", "random", "random", "zeros", "ones"])
seeds = st.integers(0, 2**32 - 1)
sector_payloads = st.builds(lambda kind, seed: bulk_bytes(2048, kind, seed), data_kinds, seeds)
random_payloads = seeds.map(lambda seed: bulk_bytes(2048, "random", seed))

# 0 .. 89:59:74. ECMA-130 addresses are three BCD bytes, so nothing beyond 99:59:74 can be written.
LAST_LBA_90_MIN = 90 * 60 * 75 - 150 - 1
lbas = st.one_of(st.integers(0, LAST_LBA_90_MIN), st.sampled_from([0, 1, 74, 75, 4499, 4500, LAST_LBA_90_MIN]))


def flip_bits(buf: bytes, bit_positions) -> bytes:
    out = bytearray(buf)
    for bit in bit_positions:
        out[bit // 8] ^= 1 << (bit % 8)
    return bytes(out)


@st.composite
def bit_bursts(draw, first_byte: int, end_byte: int, max_bits: int):
    """An error burst: first and last bit of the span are flipped, the bits between are arbitrary."""
    length = draw(st.integers(1, max_bits))
    start = draw(st.integers(first_byte * 8, end_byte * 8 - length))
    inner = draw(st.integers(0, (1 << max(0, length - 2)) - 1)) if length > 2 else 0
    bits = {start, start + length - 1}
    bits.update(start + 1 + i for i in range(max(0, length - 2)) if (inner >> i) & 1)
    return sorted(bits)


# ---------------------------------------------------------------------------------------------------------
# Independent models written from the standards
# ---------------------------------------------------------------------------------------------------------

SYNC = bytes([0x00] + [0xFF] * 10 + [0x00])

# GF(2^8) with the ECMA-130 Annex A field polynomial x^8 + x^4 + x^3 + x^2 + 1 and primitive element 2.
GF_EXP = [0] * 510
GF_LOG = [0] * 256
_x = 1
for _i in range(255):
    GF_EXP[_i] = _x
    GF_LOG[_x] = _i
    _x <<= 1
    if _x & 0x100:
        _x ^= 0x11D
for _i in range(255, 510):
    GF_EXP[_i] = GF_EXP[_i - 255]


def gf_mul(a: int, b: int) -> int:
    return 0 if a == 0 or b == 0 else GF_EXP[GF_LOG[a] + GF_LOG[b]]


def gf_div(a: int, b: int) -> int:
    return 0 if a == 0 else GF_EXP[(GF_LOG[a] - GF_LOG[b]) % 255]


def _carryless_multiply(a: int, b: int) -> int:
    result = 0
    while b:
        if b & 1:
            result ^= a
        a <<= 1
        b >>= 1
    return result


# ECMA-130 14.3: P(x) = (x^16 + x^15 + x^2 + 1) * (x^16 + x^2 + x + 1)
EDC_GENERATOR = _carryless_multiply(0x18005, 0x10007)


def edc_spec(data: bytes) -> int:
    """EDC by polynomial long division. The least significant bit of each byte is the highest power of x."""
    remainder = 0
    for byte in data:
        for bit in range(8):
            remainder ^= ((byte >> bit) & 1) << 31
            remainder = ((remainder << 1) ^ EDC_GENERATOR) & 0xFFFFFFFF if remainder & 0x80000000 else remainder << 1
    return int(f"{remainder:032b}"[::-1], 2)


def bcd(value: int) -> int:
    return (value // 10) << 4 | (value % 10)


def header_for(lba: int, mode: int) -> bytes:
    minutes, rest = divmod(lba + 150, 60 * 75)
    seconds, frames = divmod(rest, 75)
    return bytes([bcd(minutes), bcd(seconds), bcd(frames), mode])


def _build_codewords():
    """Byte offsets of every P and Q codeword in a 2352-byte sector (ECMA-130 Annex A, one per byte plane)."""
    p_words, q_words = [], []
    for plane in (0, 1):
        for n in range(43):
            p_words.append([12 + 2 * (43 * m + n) + plane for m in range(26)])
        for n in range(26):
            word = [12 + 2 * ((44 * m + 43 * n) % 1118) + plane for m in range(43)]
            word.append(12 + 2 * (43 * 26 + n) + plane)
            word.append(12 + 2 * (44 * 26 + n) + plane)
            q_words.append(word)
    return p_words, q_words


P_CODEWORDS, Q_CODEWORDS = _build_codewords()


def syndromes(values) -> tuple[int, int]:
    """H * V for the Annex A parity-check matrix [[1, 1, ..., 1], [a^(n-1), ..., a^1, a^0]]."""
    n = len(values)
    s0 = s1 = 0
    for i, value in enumerate(values):
        if value:
            s0 ^= value
            s1 ^= GF_EXP[GF_LOG[value] + n - 1 - i]
    return s0, s1


def parity_violations(raw: bytes, zero_header: bool) -> int:
    view = bytearray(raw)
    if zero_header:  # Mode 2 Form 1 computes parity with the four header bytes taken as zero
        view[12:16] = b"\x00\x00\x00\x00"
    return sum(1 for word in P_CODEWORDS + Q_CODEWORDS if syndromes([view[i] for i in word]) != (0, 0))


class Unrecoverable(Exception):
    """The reference decoder could not restore a sector that passes the EDC."""


def _repair_codeword(buf: bytearray, word, erased: set) -> bool:
    n = len(word)
    s0, s1 = syndromes([buf[i] for i in word])
    known = [k for k, position in enumerate(word) if position in erased]

    if len(known) > 2:
        return False

    if len(known) == 2:  # two erasures: solve e1 + e2 = S0, e1*X1 + e2*X2 = S1
        x1, x2 = GF_EXP[n - 1 - known[0]], GF_EXP[n - 1 - known[1]]
        e1 = gf_div(s1 ^ gf_mul(s0, x2), x1 ^ x2)
        buf[word[known[0]]] ^= e1
        buf[word[known[1]]] ^= s0 ^ e1
        erased.difference_update(word[k] for k in known)
        return True

    if len(known) == 1:  # one erasure; the second syndrome exposes any additional unknown error
        if s1 != gf_mul(s0, GF_EXP[n - 1 - known[0]]):
            return False
        buf[word[known[0]]] ^= s0
        erased.discard(word[known[0]])
        return True

    if s0 == 0 and s1 == 0:
        return False
    if s0 == 0 or s1 == 0:  # two or more errors
        return False
    index = n - 1 - (GF_LOG[s1] - GF_LOG[s0]) % 255
    if not 0 <= index < n:
        return False
    buf[word[index]] ^= s0
    return True


def reference_decode_mode1(raw: bytes, erasures=()) -> bytes:
    """
    Reference Mode 1 decoder: iterative Q-then-P correction (one unknown error or two flagged erasures per
    codeword), with the EDC as the final judge. It defines what "inside the parity bound" means for the
    sector format and is the oracle a production decoder would be measured against.
    """
    buf = bytearray(raw)
    erased = set(erasures)
    for _ in range(16):
        progress = False
        for word in Q_CODEWORDS:
            progress |= _repair_codeword(buf, word, erased)
        for word in P_CODEWORDS:
            progress |= _repair_codeword(buf, word, erased)
        if not progress:
            break
    if bytes(buf[:12]) != SYNC or buf[15] != 1:
        raise Unrecoverable("sync or mode byte damaged")
    if struct.unpack_from("<I", buf, 2064)[0] != edc_spec(bytes(buf[:2064])):
        raise Unrecoverable("EDC mismatch after P/Q correction")
    return bytes(buf[16:2064])


def cdtext_crc_spec(pack16: bytes) -> int:
    """IEC 60908 Annex J: CRC-16, x^16 + x^12 + x^5 + 1, most significant bit first, stored inverted."""
    remainder = 0
    for byte in pack16:
        for bit in range(7, -1, -1):
            remainder ^= ((byte >> bit) & 1) << 15
            remainder = ((remainder << 1) ^ 0x11021) & 0xFFFF if remainder & 0x8000 else remainder << 1
    return remainder ^ 0xFFFF


# ---------------------------------------------------------------------------------------------------------
# 1. CD-ROM raw sectors: bit-exact layout against ECMA-130
# ---------------------------------------------------------------------------------------------------------

@given(sector_payloads, lbas)
def test_mode1_sector_layout_matches_ecma130(payload, lba):
    raw = unb64(call("mode1_encode", data=payload, lba=lba)["raw"])
    assert len(raw) == 2352
    assert raw[:12] == SYNC
    assert raw[12:16] == header_for(lba, 1)
    assert raw[16:2064] == payload
    assert struct.unpack_from("<I", raw, 2064)[0] == edc_spec(raw[:2064])
    assert raw[2068:2076] == bytes(8)
    assert parity_violations(raw, zero_header=False) == 0


@given(sector_payloads, lbas)
def test_mode2_form1_sector_layout_matches_spec(payload, lba):
    raw = unb64(call("mode2_encode", data=payload, lba=lba)["raw"])
    assert len(raw) == 2352
    assert raw[:12] == SYNC
    assert raw[12:16] == header_for(lba, 2)
    assert raw[16:20] == raw[20:24] == bytes([0, 0, 0x08, 0])
    assert raw[24:2072] == payload
    assert struct.unpack_from("<I", raw, 2072)[0] == edc_spec(raw[16:2072])
    assert parity_violations(raw, zero_header=True) == 0


@given(sector_payloads, lbas, st.sampled_from([1, 2]))
def test_undamaged_sector_round_trips(payload, lba, mode):
    raw = unb64(call(f"mode{mode}_encode", data=payload, lba=lba)["raw"])
    reply = call("sector_extract", raw=raw)
    assert reply["edcValid"] is True
    assert unb64(reply["data"]) == payload


def test_edc_agrees_with_spec_division_on_empty_and_short_inputs():
    for data in (b"", b"\x00", b"\x01", b"\x80", b"\xff" * 5, bytes(range(256))):
        assert call("edc", data=data)["edc"] == edc_spec(data)


# ---------------------------------------------------------------------------------------------------------
# 2. CD-ROM raw sectors: damage is never reported as good data
# ---------------------------------------------------------------------------------------------------------

def _edc_span(mode: int) -> tuple[int, int]:
    """Bytes protected by the EDC, including the stored EDC itself."""
    return (0, 2068) if mode == 1 else (16, 2076)


def _assert_rejected_or_identical(raw: bytes, payload: bytes):
    try:
        reply = call("sector_extract", raw=raw)
    except Rejected:
        return
    if reply["edcValid"]:
        assert unb64(reply["data"]) == payload, "damaged sector was reported EDC-valid with different user data"


@given(st.data(), sector_payloads, lbas, st.sampled_from([1, 2]))
def test_bit_flips_and_short_bursts_are_always_detected(data, payload, lba, mode):
    """
    Guaranteed by the EDC polynomial: every odd number of bit errors (factor x + 1), every double bit error
    (the polynomial's period exceeds the sector) and every burst of 32 bits or fewer.
    """
    raw = unb64(call(f"mode{mode}_encode", data=payload, lba=lba)["raw"])
    first, end = _edc_span(mode)
    flips = data.draw(st.one_of(
        st.lists(st.integers(first * 8, end * 8 - 1), min_size=1, max_size=3, unique=True),
        bit_bursts(first, end, 32),
    ))
    damaged = flip_bits(raw, flips)
    try:
        reply = call("sector_extract", raw=damaged)
    except Rejected:
        return
    assert reply["edcValid"] is False


@given(st.data(), random_payloads, lbas, st.sampled_from([1, 2]))
def test_header_damage_combined_with_payload_bursts_is_never_accepted(data, payload, lba, mode):
    """Framing damage (mode byte, address, sync) at the same moment as a random-filled payload burst."""
    raw = bytearray(unb64(call(f"mode{mode}_encode", data=payload, lba=lba)["raw"]))
    first, end = _edc_span(mode)

    start = data.draw(st.integers(first, end - 1))
    length = data.draw(st.integers(1, min(2048, end - start)))
    fill = bulk_bytes(length, "random", data.draw(seeds))
    assume(fill != bytes(raw[start:start + length]))
    raw[start:start + length] = fill

    for position in data.draw(st.lists(st.integers(0, 15), max_size=3, unique=True)):
        raw[position] ^= data.draw(st.integers(1, 255))

    _assert_rejected_or_identical(bytes(raw), payload)


@given(random_payloads, lbas)
def test_mode1_zero_filled_body_is_rejected(payload, lba):
    """A rescue copy that keeps sync and header but zero-fills an unreadable Mode 1 body must not validate."""
    raw = bytearray(unb64(call("mode1_encode", data=payload, lba=lba)["raw"]))
    raw[16:] = bytes(2352 - 16)
    _assert_rejected_or_identical(bytes(raw), payload)


@pytest.mark.xfail(strict=True, reason=(
    "F2: a Mode 2 sector whose body (bytes 16..2351) is zero-filled is a valid codeword - EDC and P/Q parity "
    "of all-zero data are zero - so Extract2048From2352 returns 2048 zero bytes with edcValid = true."))
@given(random_payloads, lbas)
@example(bulk_bytes(2048, "random", 1), 0)
def test_mode2_zero_filled_body_is_rejected(payload, lba):
    raw = bytearray(unb64(call("mode2_encode", data=payload, lba=lba)["raw"]))
    raw[16:] = bytes(2352 - 16)
    _assert_rejected_or_identical(bytes(raw), payload)


@given(st.lists(random_payloads, min_size=3, max_size=3), st.integers(0, 300000), st.integers(1, 2351), st.sampled_from([1, 2]))
def test_shifted_sector_boundaries_are_rejected(payloads, lba, shift, mode):
    """A reader that lost byte alignment must get an error for every misaligned 2352-byte window."""
    stream = b"".join(unb64(call(f"mode{mode}_encode", data=p, lba=lba + i)["raw"]) for i, p in enumerate(payloads))
    for index in range(2):
        window = stream[shift + index * 2352: shift + (index + 1) * 2352]
        with pytest.raises(Rejected) as rejection:
            call("sector_extract", raw=window)
        assert rejection.value.kind == "InvalidDataException"


@given(random_payloads, lbas, st.integers(0, 2351), st.sampled_from([1, 2]))
def test_truncated_sector_is_rejected(payload, lba, kept, mode):
    raw = unb64(call(f"mode{mode}_encode", data=payload, lba=lba)["raw"])
    with pytest.raises(Rejected) as rejection:
        call("sector_extract", raw=raw[:kept])
    assert rejection.value.kind == "ArgumentException"


@pytest.mark.xfail(strict=True, reason=(
    "F1: RawSectorCodec writes P/Q parity but has no decoder for it. Extract2048From2352 only compares the "
    "EDC, so a single wrong byte - the smallest error the sector format can correct - is not recovered."))
@given(random_payloads, lbas, st.integers(16, 2063), st.integers(1, 255))
@example(bulk_bytes(2048, "random", 7), 16, 100, 0x01)
def test_production_decoder_corrects_a_single_byte_error(payload, lba, position, error):
    raw = bytearray(unb64(call("mode1_encode", data=payload, lba=lba)["raw"]))
    raw[position] ^= error
    reply = call("sector_extract", raw=bytes(raw))
    assert reply["edcValid"] is True
    assert unb64(reply["data"]) == payload


# ---------------------------------------------------------------------------------------------------------
# 3. CD-ROM raw sectors: what the written parity can recover (reference decoder)
# ---------------------------------------------------------------------------------------------------------

ECC_FIRST, ECC_END = 12, 2352  # P/Q parity protects everything after the sync pattern


@given(random_payloads, lbas, st.lists(st.tuples(st.integers(ECC_FIRST, ECC_END - 1), st.integers(1, 255)),
                                       min_size=1, max_size=2, unique_by=lambda e: e[0]))
@example(bulk_bytes(2048, "random", 3), 0, [(2248, 0x5A), (2300, 0xC3)])   # both parity bytes of one Q codeword
@example(bulk_bytes(2048, "random", 3), 0, [(12, 0x01), (98, 0x01)])       # two bytes of one P codeword
@example(bulk_bytes(2048, "random", 3), 0, [(2076, 0xFF), (2162, 0xFF)])   # both parity bytes of one P codeword
def test_reference_decoder_recovers_any_two_byte_errors(payload, lba, errors):
    raw = bytearray(unb64(call("mode1_encode", data=payload, lba=lba)["raw"]))
    for position, error in errors:
        raw[position] ^= error
    assert reference_decode_mode1(bytes(raw)) == payload


@given(random_payloads, lbas, st.data())
def test_reference_decoder_recovers_flagged_bursts_up_to_172_bytes(payload, lba, data):
    """
    172 consecutive bytes are two rows of the 86-byte-wide P/Q matrix, so each P codeword loses at most two
    bytes - exactly what a distance-3 code restores when the positions are known (C2 pointers).
    """
    raw = bytearray(unb64(call("mode1_encode", data=payload, lba=lba)["raw"]))
    start = data.draw(st.integers(ECC_FIRST, ECC_END - 1))
    length = data.draw(st.integers(1, min(172, ECC_END - start)))
    raw[start:start + length] = bulk_bytes(length, data.draw(st.sampled_from(["zeros", "ones", "random"])), data.draw(seeds))
    assert reference_decode_mode1(bytes(raw), erasures=range(start, start + length)) == payload


@given(random_payloads, lbas, st.data())
def test_reference_decoder_never_returns_wrong_data(payload, lba, data):
    """Past the bound the inner code miscorrects often; the EDC must turn every such case into an error."""
    raw = bytearray(unb64(call("mode1_encode", data=payload, lba=lba)["raw"]))
    flagged = []
    if data.draw(st.booleans()):
        start = data.draw(st.integers(ECC_FIRST, ECC_END - 1))
        length = data.draw(st.integers(1, min(900, ECC_END - start)))
        raw[start:start + length] = bulk_bytes(length, "random", data.draw(seeds))
        if data.draw(st.booleans()):
            flagged = range(start, start + length)
    for position in data.draw(st.lists(st.integers(ECC_FIRST, ECC_END - 1), max_size=40, unique=True)):
        raw[position] ^= data.draw(st.integers(1, 255))
    try:
        decoded = reference_decode_mode1(bytes(raw), erasures=flagged)
    except Unrecoverable:
        return
    assert decoded == payload


def _two_error_miscorrection_fraction(n: int) -> float:
    """
    Exact share of two-symbol error patterns in one (n, n-2) codeword that a single-error decoder "corrects"
    into a third position. For errors x at a and y = t*x at b the computed locator is
    (X_a + t*X_b) / (1 + t), which depends only on the ratio t.
    """
    miscorrected = total = 0
    for a in range(n):
        for b in range(a + 1, n):
            xa, xb = GF_EXP[n - 1 - a], GF_EXP[n - 1 - b]
            for t in range(1, 256):
                total += 1
                if t == 1:
                    continue  # S0 = 0: detected as uncorrectable
                locator = gf_div(xa ^ gf_mul(t, xb), 1 ^ t)
                if locator and GF_LOG[locator] < n:
                    miscorrected += 1
    return miscorrected / total


def test_inner_parity_alone_miscorrects_double_errors():
    """
    Past the Singleton bound (2v + e > n - k = 2) the P and Q codes decode a fixed share of double errors
    into a different valid codeword. This is why the EDC, not the parity, is the corruption gate.
    """
    p_share = _two_error_miscorrection_fraction(26)
    q_share = _two_error_miscorrection_fraction(45)
    assert p_share == pytest.approx(24 / 255)
    assert q_share == pytest.approx(43 / 255)


# ---------------------------------------------------------------------------------------------------------
# 4. CD-Text packs
# ---------------------------------------------------------------------------------------------------------

_title_alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
titles = st.text(alphabet=_title_alphabet, min_size=1, max_size=28)
track_lists = st.lists(titles, min_size=1, max_size=8)


def _packs(blob: bytes):
    return [blob[i:i + 18] for i in range(0, len(blob), 18)]


@given(titles, track_lists)
def test_cdtext_round_trip_and_crc_match_annex_j(album, tracks):
    blob = unb64(call("cdtext_build", album=album, titles=tracks)["packs"])
    assert len(blob) % 18 == 0 and blob
    for pack in _packs(blob):
        assert struct.unpack(">H", pack[16:18])[0] == cdtext_crc_spec(pack[:16])
    parsed = call("cdtext_parse", packs=blob)
    assert parsed["album"] == album
    assert parsed["titles"] == tracks


@given(titles, track_lists, st.data())
def test_cdtext_bursts_up_to_16_bits_are_rejected(album, tracks, data):
    blob = unb64(call("cdtext_build", album=album, titles=tracks)["packs"])
    pack_index = data.draw(st.integers(0, len(blob) // 18 - 1))
    flips = data.draw(bit_bursts(pack_index * 18, pack_index * 18 + 18, 16))
    with pytest.raises(Rejected) as rejection:
        call("cdtext_parse", packs=flip_bits(blob, flips))
    assert rejection.value.kind == "InvalidDataException"


@given(titles, track_lists, st.data())
def test_cdtext_truncation_inside_a_pack_is_rejected(album, tracks, data):
    blob = unb64(call("cdtext_build", album=album, titles=tracks)["packs"])
    cut = data.draw(st.integers(1, len(blob) - 1))
    # A length of 18n + 4 is read as "4-byte header + packs"; that heuristic is covered by the audit notes.
    assume(cut % 18 not in (0, 4))
    with pytest.raises(Rejected):
        call("cdtext_parse", packs=blob[:cut])


@given(titles, track_lists, st.data())
def test_cdtext_reordered_packs_are_restored(album, tracks, data):
    """Exact recovery: packs carry a sequence number, so two swapped packs must not change the text."""
    packs = _packs(unb64(call("cdtext_build", album=album, titles=tracks)["packs"]))
    assume(len(packs) >= 2)
    i = data.draw(st.integers(0, len(packs) - 1))
    j = data.draw(st.integers(0, len(packs) - 1))
    packs[i], packs[j] = packs[j], packs[i]
    parsed = call("cdtext_parse", packs=b"".join(packs))
    assert parsed["album"] == album
    assert parsed["titles"] == tracks


@given(titles, track_lists, st.data())
@example("AlbumTitleNumberOneIsLong", ["FirstTrackTitleIsLong", "SecondTrackTitleIsLong"], None)
def test_cdtext_dropped_pack_is_rejected(album, tracks, data):
    packs = _packs(unb64(call("cdtext_build", album=album, titles=tracks)["packs"]))
    assume(len(packs) >= 2)
    dropped = 1 if data is None else data.draw(st.integers(0, len(packs) - 1))
    del packs[dropped]
    try:
        parsed = call("cdtext_parse", packs=b"".join(packs))
    except Rejected:
        return
    assert (parsed["album"], parsed["titles"]) == (album, tracks), "a missing pack changed the text silently"


# ---------------------------------------------------------------------------------------------------------
# 5. FLAC frame sync, CRC-8 / CRC-16 and resynchronisation
# ---------------------------------------------------------------------------------------------------------

FLAC_AUDIO_START = 4 + 4 + 34          # "fLaC" + STREAMINFO block header + STREAMINFO (no other metadata)
FLAC_TOTAL_SAMPLES_BYTES = range(21, 26)  # 36-bit total-samples field: low nibble of byte 21, bytes 22..25
FLAC_BLOCK = 4096

pcm_streams = st.builds(lambda frames, seed: bulk_bytes(frames * 4, "random", seed), st.integers(1, 3 * FLAC_BLOCK + 500), seeds)


def _decode_or_reject(flac: bytes):
    try:
        return unb64(call("flac_decode", flac=flac)["pcm"])
    except Rejected:
        return None


@given(pcm_streams)
def test_flac_round_trip_is_bit_exact(pcm):
    flac = unb64(call("flac_encode", pcm=pcm)["flac"])
    assert flac[:4] == b"fLaC"
    assert unb64(call("flac_decode", flac=flac)["pcm"]) == pcm


@given(pcm_streams, st.data())
def test_flac_garbage_between_frames_is_skipped(pcm, data):
    """Exact recovery from lost framing: junk inserted at a frame boundary damages no frame."""
    flac = unb64(call("flac_encode", pcm=pcm)["flac"])
    whole_blocks = len(pcm) // (FLAC_BLOCK * 4)
    block = data.draw(st.integers(0, whole_blocks))
    boundary = FLAC_AUDIO_START if block == 0 else len(unb64(call("flac_encode", pcm=pcm[:block * FLAC_BLOCK * 4])["flac"]))
    junk = bulk_bytes(data.draw(st.integers(1, 64)), data.draw(st.sampled_from(["random", "zeros", "ones"])), data.draw(seeds))
    assert unb64(call("flac_decode", flac=flac[:boundary] + junk + flac[boundary:])["pcm"]) == pcm


_FLAC_SILENT = (
    "F3: the FLAC decoder drops frames that fail CRC-8/CRC-16 and substitutes silence (or nothing) without "
    "telling the caller, and never checks the STREAMINFO MD5 or sample count. ")


@pytest.mark.xfail(strict=True, reason=_FLAC_SILENT + "A corrupt frame comes back as altered audio.")
@given(pcm_streams, st.data())
@example(bulk_bytes(3 * FLAC_BLOCK * 4, "random", 11), None)
def test_flac_corrupt_frame_is_reported(pcm, data):
    flac = bytearray(unb64(call("flac_encode", pcm=pcm)["flac"]))
    position = FLAC_AUDIO_START + 6000 if data is None else data.draw(st.integers(FLAC_AUDIO_START, len(flac) - 1))
    flac[position] ^= 0x40 if data is None else data.draw(st.integers(1, 255))
    decoded = _decode_or_reject(bytes(flac))
    assert decoded is None or decoded == pcm, "audio from a corrupt FLAC frame was returned as if intact"


@pytest.mark.xfail(strict=True, reason=_FLAC_SILENT + "A truncated file comes back as shorter audio.")
@given(pcm_streams, st.data())
@example(bulk_bytes(3 * FLAC_BLOCK * 4, "random", 11), None)
def test_flac_truncated_stream_is_reported(pcm, data):
    flac = unb64(call("flac_encode", pcm=pcm)["flac"])
    cut = len(flac) // 2 if data is None else data.draw(st.integers(FLAC_AUDIO_START, len(flac) - 1))
    decoded = _decode_or_reject(flac[:cut])
    assert decoded is None or decoded == pcm, "a truncated FLAC stream was decoded without an error"


@pytest.mark.xfail(strict=True, reason=_FLAC_SILENT + "A bit flip in the STREAMINFO length field cuts the audio short.")
@given(pcm_streams, st.data())
@example(bulk_bytes(2 * FLAC_BLOCK * 4, "random", 11), None)
def test_flac_length_field_bit_flip_is_reported(pcm, data):
    flac = bytearray(unb64(call("flac_encode", pcm=pcm)["flac"]))
    if data is None:
        flac[24] ^= 0x30  # 8192 samples -> 4096
    else:
        position = data.draw(st.sampled_from(list(FLAC_TOTAL_SAMPLES_BYTES)))
        flac[position] ^= 1 << data.draw(st.integers(0, 3 if position == 21 else 7))
    decoded = _decode_or_reject(bytes(flac))
    assert decoded is None or decoded == pcm, "a damaged STREAMINFO sample count changed the decoded audio silently"


# ---------------------------------------------------------------------------------------------------------
# 6. Archive SHA-256 manifest
# ---------------------------------------------------------------------------------------------------------

MANIFEST = "IGNISDISC-MANIFEST.sha256"

_name_part = st.text(alphabet="abcdefghijklmnopqrstuvwxyz0123456789", min_size=1, max_size=8)
_file_names = st.builds(lambda folder, stem, ext: f"{folder}f_{stem}.{ext}",
                        st.sampled_from(["", "", "d1/", "d1/d2/"]), _name_part, st.sampled_from(["bin", "txt", "dat"]))
file_trees = st.dictionaries(_file_names, st.binary(max_size=700), min_size=1, max_size=5)


class StagedArchive:
    """A source tree staged by the real ArchiveStager (no PAR2), removed again on exit."""

    def __init__(self, tree: dict[str, bytes]):
        self.tree = tree
        self._tmp = tempfile.TemporaryDirectory(prefix="ignis-prop-")
        root = Path(self._tmp.name)
        source = root / "source"
        for name, content in tree.items():
            path = source / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        reply = call("archive_stage", source=str(source), stagingRoot=str(root / "staging"))
        assert reply["fileCount"] == len(tree) and len(reply["discs"]) == 1
        self.disc = Path(reply["discs"][0])

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self._tmp.cleanup()

    def verify(self):
        return call("archive_verify", path=str(self.disc))


@SLOW
@given(file_trees)
def test_archive_manifest_round_trip(tree):
    with StagedArchive(tree) as archive:
        manifest = (archive.disc / MANIFEST).read_text(encoding="utf-8")
        listed = dict(reversed(line.split("  ", 1)) for line in manifest.splitlines())
        assert listed == {name: hashlib.sha256(content).hexdigest() for name, content in tree.items()}
        result = archive.verify()
        assert result["intact"] is True
        assert result["filesChecked"] == len(tree)


@SLOW
@given(file_trees, st.data())
def test_archive_payload_damage_is_always_reported(tree, data):
    with StagedArchive(tree) as archive:
        name = data.draw(st.sampled_from(sorted(tree)))
        path = archive.disc / name
        content = tree[name]
        kinds = ["append", "delete"] + (["flip", "truncate", "zero_fill"] if content.strip(b"\x00") else [])
        kind = data.draw(st.sampled_from(kinds))
        if kind == "append":
            path.write_bytes(content + b"\x00")
        elif kind == "delete":
            path.unlink()
        elif kind == "flip":
            position = data.draw(st.integers(0, len(content) - 1))
            path.write_bytes(content[:position] + bytes([content[position] ^ data.draw(st.integers(1, 255))]) + content[position + 1:])
        elif kind == "truncate":
            path.write_bytes(content[:data.draw(st.integers(0, len(content) - 1))])
        else:
            path.write_bytes(bytes(len(content)))

        result = archive.verify()
        assert result["intact"] is False
        assert name in result["missing"] + result["corrupt"]


def _damage_manifest(path: Path, data) -> None:
    original = path.read_bytes()
    lines = original.splitlines(keepends=True)
    kind = "empty" if data is None else data.draw(st.sampled_from(["empty", "zero_fill", "drop_lines", "cut", "zero_range"]))
    if kind == "empty":
        damaged = b""
    elif kind == "zero_fill":
        damaged = bytes(len(original))
    elif kind == "drop_lines":
        damaged = b"".join(lines[:data.draw(st.integers(0, len(lines) - 1))])
    elif kind == "cut":
        damaged = original[:data.draw(st.integers(0, len(original) - 1))]
    else:
        start = data.draw(st.integers(0, len(original) - 1))
        length = data.draw(st.integers(1, len(original) - start))
        damaged = original[:start] + bytes(length) + original[start + length:]
    path.write_bytes(damaged)


@SLOW
@given(file_trees, st.data())
@example({"f_a.bin": b"payload"}, None)
def test_archive_manifest_damage_never_passes_with_fewer_files(tree, data):
    with StagedArchive(tree) as archive:
        _damage_manifest(archive.disc / MANIFEST, data)
        try:
            result = archive.verify()
        except Rejected:
            return
        assert not (result["intact"] and result["filesChecked"] < len(tree)), (
            f"damaged manifest verified as intact after checking {result['filesChecked']} of {len(tree)} files")


def test_standalone_verify_script_fails_on_zero_filled_manifest():
    with StagedArchive({"f_a.bin": b"payload", "f_b.bin": b"more payload"}) as archive:
        manifest = archive.disc / MANIFEST
        manifest.write_bytes(bytes(manifest.stat().st_size))
        run = subprocess.run(
            ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(archive.disc / "VERIFY.PS1")],
            capture_output=True, text=True, timeout=120)
        assert run.returncode != 0, run.stdout


def test_archive_with_leading_space_file_name_verifies_intact():
    with StagedArchive({" leading.txt": b"one", "f_plain.txt": b"two"}) as archive:
        result = archive.verify()
        assert result["intact"] is True, result


# ---------------------------------------------------------------------------------------------------------
# 7. Archive span planner: capacity boundaries
# ---------------------------------------------------------------------------------------------------------

MEDIA = ["CdR", "DvdMinusR", "DvdPlusR", "DvdPlusRDualLayer"]
_usable_cache: dict[tuple[str, int], int] = {}


def usable_sectors(media: str, redundancy: int) -> int:
    key = (media, redundancy)
    if key not in _usable_cache:
        _usable_cache[key] = call("plan", sizes=[], media=media, redundancy=redundancy)["usable"]
    return _usable_cache[key]


def sectors_for(size: int) -> int:
    return (size + 2047) // 2048


@given(st.sampled_from(MEDIA), st.integers(0, 100), st.data())
def test_span_plan_places_every_file_once_within_capacity(media, redundancy, data):
    usable = usable_sectors(media, redundancy)
    edge = usable * 2048
    size = st.one_of(st.integers(0, edge), st.integers(0, 4096), st.sampled_from([0, 1, 2047, 2048, 2049, edge - 1, edge]))
    sizes = data.draw(st.lists(size, max_size=40))

    plan = call("plan", sizes=sizes, media=media, redundancy=redundancy)

    names = [name for disc in plan["discs"] for name in disc["files"]]
    assert sorted(names) == [f"f{i:06d}" for i in range(len(sizes))]
    assert [disc["number"] for disc in plan["discs"]] == list(range(1, len(plan["discs"]) + 1))
    assert plan["totalBytes"] == sum(sizes)
    for disc in plan["discs"]:
        on_disc = [sizes[int(name[1:])] for name in disc["files"]]
        assert disc["files"], "empty disc in plan"
        assert disc["bytes"] == sum(on_disc)
        assert disc["sectors"] == sum(sectors_for(s) for s in on_disc)
        assert 0 <= disc["sectors"] <= usable


@given(st.sampled_from(MEDIA), st.integers(0, 100), st.data())
@example("CdR", 10, None)
def test_span_plan_rejects_any_file_larger_than_one_disc(media, redundancy, data):
    usable = usable_sectors(media, redundancy)
    size = 2**42 if data is None else data.draw(st.one_of(
        st.integers(usable * 2048 + 1, 2**62), st.sampled_from([2**41, 2**42, 2**43, 2**44 + 1, 2**62])))
    with pytest.raises(Rejected) as rejection:
        call("plan", sizes=[size], media=media, redundancy=redundancy)
    assert rejection.value.kind == "InvalidOperationException"


# ---------------------------------------------------------------------------------------------------------
# 8. PAR2 recovery data, created with the application's arguments and repaired by Par2Runner.RepairAsync
# ---------------------------------------------------------------------------------------------------------

_PAR2_MAGIC = b"PAR2\x00PKT"
_PAR2_MAIN = b"PAR 2.0\x00Main\x00\x00\x00\x00"
_PAR2_FILE_DESCRIPTION = b"PAR 2.0\x00FileDesc"
_PAR2_RECOVERY_SLICE = b"PAR 2.0\x00RecvSlic"


def read_par2_layout(recovery_dir: Path):
    """Slice size, {file name: length} and the distinct intact recovery slices, read per the PAR 2.0 packet layout."""
    slice_size = None
    files: dict[str, int] = {}
    exponents: set[int] = set()
    for par2_file in recovery_dir.glob("*.par2"):
        blob = par2_file.read_bytes()
        offset = 0
        while True:
            offset = blob.find(_PAR2_MAGIC, offset)
            if offset < 0 or offset + 64 > len(blob):
                break
            (length,) = struct.unpack_from("<Q", blob, offset + 8)
            if length < 64 or offset + length > len(blob):
                offset += 8
                continue
            packet_hash, kind = blob[offset + 16:offset + 32], blob[offset + 48:offset + 64]
            body = blob[offset + 64:offset + length]
            if hashlib.md5(blob[offset + 32:offset + length]).digest() == packet_hash:
                if kind == _PAR2_MAIN:
                    (slice_size,) = struct.unpack_from("<Q", body, 0)
                elif kind == _PAR2_FILE_DESCRIPTION:
                    (file_length,) = struct.unpack_from("<Q", body, 48)
                    files[body[56:].rstrip(b"\x00").decode("utf-8")] = file_length
                elif kind == _PAR2_RECOVERY_SLICE:
                    exponents.add(struct.unpack_from("<I", body, 0)[0])
            offset += length
    return slice_size, files, exponents


class Par2Set:
    """Payload files plus RECOVERY\\*.par2, built the way Par2Runner.CreateAsync builds them."""

    def __init__(self, par2: str, tree: dict[str, bytes], redundancy: int):
        self.par2 = par2
        self.tree = tree
        self._tmp = tempfile.TemporaryDirectory(prefix="ignis-par2-")
        root = Path(self._tmp.name)
        self.disc = root / "disc01"
        scratch = root / "scratch"
        scratch.mkdir()
        for name, content in tree.items():
            path = self.disc / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)

        arguments = call("par2_args", output=str(scratch / "recovery.par2"), base=str(self.disc), redundancy=redundancy)["args"]
        created = subprocess.run([par2, *arguments], cwd=self.disc, capture_output=True, text=True, timeout=300)
        assert created.returncode == 0, created.stdout + created.stderr

        self.recovery = self.disc / "RECOVERY"
        self.recovery.mkdir()
        for produced in scratch.glob("*.par2"):
            shutil.move(str(produced), str(self.recovery / produced.name))

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self._tmp.cleanup()

    def repair(self) -> tuple[int, str]:
        """Runs the application's Par2Runner.RepairAsync. Returns (exit code, par2 error output)."""
        reply = call("par2_repair", par2=self.par2, folder=str(self.disc))
        self.attempts = reply["attempts"]
        return reply["exitCode"], reply["errorOutput"]

    def every_payload_file_exists(self) -> bool:
        return all((self.disc / name).is_file() for name in self.tree)

    def keep_only_volumes(self, *name_parts: str) -> None:
        for volume in self.recovery.glob("*.vol*.par2"):
            if not any(part in volume.name for part in name_parts):
                volume.unlink()

    def payload_is_original(self) -> bool:
        return all((self.disc / name).is_file() and (self.disc / name).read_bytes() == content
                   for name, content in self.tree.items())


@pytest.fixture(scope="module")
def par2_executable():
    path = call("par2_find")["path"]
    if not path:
        pytest.skip("par2 is not installed; PAR2 properties not run")
    return path


par2_trees = st.dictionaries(
    st.builds(lambda folder, stem: f"{folder}f_{stem}.bin", st.sampled_from(["", "d1/"]), _name_part),
    st.builds(lambda size, seed: bulk_bytes(size, "random", seed), st.integers(300, 6000), seeds),
    min_size=1, max_size=3)


def _damage_par2_set(archive: Par2Set, data, beyond_bound: bool, lose_volumes: bool):
    """Optionally deletes recovery volumes, then flips one byte in each of a chosen number of distinct source blocks."""
    if lose_volumes:
        volumes = sorted(archive.recovery.glob("*.vol*.par2"))
        assume(len(volumes) >= 2)
        for volume in data.draw(st.lists(st.sampled_from(volumes), min_size=1, max_size=len(volumes) - 1, unique=True)):
            volume.unlink()

    slice_size, files, exponents = read_par2_layout(archive.recovery)
    assert slice_size and set(files) == set(archive.tree)
    blocks = [(name, index) for name, length in sorted(files.items()) for index in range(-(-length // slice_size))]
    available = len(exponents)
    assume(0 < available < len(blocks))

    count = data.draw(st.integers(available + 1, min(len(blocks), available + 8)) if beyond_bound else st.integers(1, available))
    chosen = data.draw(st.lists(st.sampled_from(blocks), min_size=count, max_size=count, unique=True))
    for name, index in chosen:
        path = archive.disc / name
        content = bytearray(path.read_bytes())
        position = min(index * slice_size + data.draw(st.integers(0, slice_size - 1)), len(content) - 1)
        assume(position // slice_size == index)
        content[position] ^= data.draw(st.integers(1, 255))
        path.write_bytes(content)


@VERY_SLOW
@given(par2_trees, st.sampled_from([5, 10, 30]), st.data())
def test_par2_repairs_any_damage_within_the_bound_when_all_volumes_survive(par2_executable, tree, redundancy, data):
    """
    Exact recovery. With every recovery volume present par2 uses exponents 0..k-1, which makes the decoding
    matrix a Vandermonde matrix over distinct constants - always invertible.
    """
    with Par2Set(par2_executable, tree, redundancy) as archive:
        _damage_par2_set(archive, data, beyond_bound=False, lose_volumes=False)
        assert not archive.payload_is_original()
        exit_code, stderr = archive.repair()
        assert exit_code == 0, stderr
        assert archive.payload_is_original()


@VERY_SLOW
@given(par2_trees, st.sampled_from([5, 10, 30]), st.data())
def test_par2_with_lost_volumes_repairs_exactly_or_fails_explicitly(par2_executable, tree, redundancy, data):
    """
    Once recovery volumes are missing the surviving exponents have gaps and the matrix can be singular (see
    the constructed case below). Par2Runner retries with one volume set aside at a time; if every attempt
    fails the result is par2's explicit error - never exit code 0 with different bytes - and every file
    still exists under its own name.
    """
    with Par2Set(par2_executable, tree, redundancy) as archive:
        _damage_par2_set(archive, data, beyond_bound=False, lose_volumes=True)
        exit_code, stderr = archive.repair()
        if exit_code == 0:
            assert archive.payload_is_original()
        else:
            assert "RS computation error" in stderr, f"unexpected par2 failure {exit_code}: {stderr}"
            assert not archive.payload_is_original()
            assert archive.every_payload_file_exists(), "a failed repair left a file only under a backup name"


@VERY_SLOW
@given(par2_trees, st.sampled_from([5, 10, 30]), st.booleans(), st.data())
def test_par2_reports_failure_one_block_past_the_bound(par2_executable, tree, redundancy, lose_volumes, data):
    """
    The bound counts damaged blocks, not damaged bytes: one wrong byte in each of (recovery blocks + 1)
    blocks is unrecoverable however small the total damage is. par2 must say so and leave no file changed
    into something that merely looks repaired.
    """
    with Par2Set(par2_executable, tree, redundancy) as archive:
        _damage_par2_set(archive, data, beyond_bound=True, lose_volumes=lose_volumes)
        exit_code, _ = archive.repair()
        assert exit_code != 0
        assert not archive.payload_is_original()
        assert archive.every_payload_file_exists(), "a failed repair left a file only under a backup name"


# PAR 2.0 multiplies input slice i by (2^n_i)^exponent in GF(2^16), where n_i is the i-th positive integer not
# divisible by 3, 5, 17 or 257. Two slices whose n differ by a multiple of 257 have constants whose 255th
# powers are equal, so with only exponents 0 and 255 on hand their two equations are linearly dependent.
_PAR2_LOG_CONSTANTS = [n for n in range(1, 4000) if n % 3 and n % 5 and n % 17 and n % 257]
_SINGULAR_PAIR = (_PAR2_LOG_CONSTANTS.index(2), _PAR2_LOG_CONSTANTS.index(259))


def _singular_case(par2: str) -> Par2Set:
    """One 8,000-byte file at 30 % redundancy (600 recovery blocks), one byte wrong in each of two chosen blocks."""
    archive = Par2Set(par2, {"f_one.bin": bulk_bytes(8000, "random", 42)}, 30)
    slice_size, _, _ = read_par2_layout(archive.recovery)
    path = archive.disc / "f_one.bin"
    content = bytearray(path.read_bytes())
    for block in _SINGULAR_PAIR:
        content[block * slice_size] ^= 0xFF
    path.write_bytes(content)
    return archive


def test_par2_repairs_two_damaged_blocks_with_257_recovery_blocks_left(par2_executable):
    """
    PAR2 is not an any-k-of-n code once recovery volumes are missing. With the volumes holding exponents 0 and
    255..510 left (257 recovery blocks), these two damaged blocks make par2's matrix singular and a single run
    exits 6 with 'RS computation error'. Par2Runner.RepairAsync has to get past that by retrying.
    """
    with _singular_case(par2_executable) as archive:
        archive.keep_only_volumes("vol000+", "vol255+")
        _, _, exponents = read_par2_layout(archive.recovery)
        assert len(exponents) == 257

        single_run = subprocess.run([par2_executable, "repair", "-q", "-B.", str(Path("RECOVERY") / "recovery.par2")],
                                    cwd=archive.disc, capture_output=True, text=True, timeout=300)
        assert single_run.returncode == 6 and "RS computation error" in single_run.stderr
        (archive.disc / "f_one.bin.1").replace(archive.disc / "f_one.bin")  # undo what the failed run renamed

        exit_code, stderr = archive.repair()
        assert exit_code == 0, stderr
        assert archive.attempts > 1
        assert archive.payload_is_original()
        assert sorted(p.name for p in archive.recovery.glob("*.vol*.par2")) == ["recovery.vol000+001.par2", "recovery.vol255+256.par2"]


def test_par2_singular_case_repairs_in_one_run_with_a_different_volume_subset(par2_executable):
    """The same damage needs no retry when par2 is offered a different set of recovery volumes."""
    with _singular_case(par2_executable) as archive:
        archive.keep_only_volumes("vol255+")
        exit_code, stderr = archive.repair()
        assert exit_code == 0, stderr
        assert archive.attempts == 1
        assert archive.payload_is_original()
