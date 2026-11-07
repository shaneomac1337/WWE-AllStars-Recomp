// The vibration mode (CONTEXT.md): Off, Original or Enhanced, one setting for
// every pad, and the rumble mixer that
// Enhanced's effects play through.

#pragma once

#include <chrono>
#include <cstdint>
#include <functional>

#include "vibration/game_state.h"
#include "vibration/shapes.h"

namespace rex::input {
class InputSystem;
}

namespace wwe::vibration {

// The values of --wwe_vibration_mode.
enum class Mode : int32_t { kOff = 0, kOriginal = 1, kEnhanced = 2 };

Mode CurrentMode();

using Clock = std::chrono::steady_clock;

// Each pad request gets the strongest of the game's request and the mixer's
// effects for that pad, per motor; then Off silences it and the vibration
// strength scales it, while the vibration ledger still counts what the game
// asked for. `now` is the mixer's clock and `game` the game's state (tests
// inject both). Leaving Enhanced, pausing or a new world drops every effect,
// and while paused Enhanced sends zero.
void Configure(rex::input::InputSystem* input,
               std::function<Clock::time_point()> now = &Clock::now,
               std::function<GameState()> game = [] { return GameState{}; });

// Adds an effect for pad index `pad` (0-3) to the rumble mixer; it starts at
// that pad's next request.
void AddEffect(uint32_t pad, const Shape& shape);

// Drops every effect on every pad.
void ClearEffects();

}  // namespace wwe::vibration
