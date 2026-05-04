// varint.h — Aion 2 KR LEB128-style VarInt parser.
//
// Direct port of Aion2FunDps.Protocol.FrameAssembler.TryReadVarInt with the
// same overflow-rejection rule. Pure logic, no allocations, no globals —
// safe to call from any thread.
//
// Format (LEB128):
//   Each byte's low 7 bits are payload, high bit is "more bytes follow".
//   Maximum 5 bytes encodes a 35-bit value, but the protocol's effective
//   range is 31 bits (Int32 positive); the 5th byte's top 4 bits MUST be
//   zero or we treat the entire varint as malformed.
//
// Why we don't use a 64-bit accumulator: a corrupt prefix that overflows
// into the sign bit produces a hugely-negative value that downstream
// length math interprets as "skip absurd bytes", which silently desyncs
// the frame stream. Catching it here as an explicit rejection keeps the
// frame assembler's invariants intact.

#ifndef AION2FUN_ENGINE_VARINT_H
#define AION2FUN_ENGINE_VARINT_H

#include <cstdint>
#include <cstddef>

namespace aion2fun {

// Result struct keeps the C# (out value, out bytesRead) shape ergonomic
// in C++. ok=false on any of: empty input, more-bits set on byte 5, or
// overflow check failed.
struct VarIntResult {
    int32_t value;
    int     bytes_read;
    bool    ok;
};

// Parses a LEB128 varint from `data` (length `len`). Reads at most 5
// bytes. On success returns {value, bytes_read, true}. On any failure
// returns {0, 0, false} — caller must treat as a parse failure and
// either wait for more bytes (incomplete) or skip the frame (corrupt).
VarIntResult try_read_varint(const uint8_t* data, size_t len) noexcept;

}  // namespace aion2fun

#endif  // AION2FUN_ENGINE_VARINT_H
