// nickname_validator.h — strict UTF-8 nickname validator.
//
// Mirrors SelfNicknameHandler.IsValidNickname:
//   - Hangul syllables (가-힣 = U+AC00..U+D7A3)
//   - ASCII alphanumerics
//   - 1..30 codepoints (any other char or length out of range → reject)
//
// Why strict matters here: OtherNicknameHandler probes 5 candidate offsets
// for a length-prefixed UTF-8 nickname. A loose validator that permitted
// symbols would accept misaligned slices that decoded as garbage like
// "!까불", producing phantom party rows. The strict gate forces the probe
// to advance until it lands on the real name boundary.

#ifndef AION2FUN_ENGINE_NICKNAME_VALIDATOR_H
#define AION2FUN_ENGINE_NICKNAME_VALIDATOR_H

#include <cstddef>
#include <cstdint>

namespace aion2fun {

// Validates `len` bytes of UTF-8 against the Hangul + ASCII alnum rule.
// Decodes UTF-8 inline (no allocations); rejects 4-byte sequences (we
// only allow ASCII + 3-byte Hangul). Returns true on full pass.
bool is_valid_nickname(const uint8_t* data, size_t len) noexcept;

}  // namespace aion2fun

#endif
