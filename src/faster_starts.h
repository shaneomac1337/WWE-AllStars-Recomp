// Faster starts (.scratch/faster-starts/spec.md): Shorter loading screens, the Kismet wait
// cuts of the pre-match load.

#pragma once

#include <string>

namespace wwe::faster_starts {

// A Kismet op as the cut rules see it: the package it was loaded from, the name of the
// sequence that holds it, its own name (SeqAct_Delay_3) and its class name.
struct KismetOp {
  std::string package, sequence, name, cls;
};

// Shorter loading screens: the two Game_Settings delays (5.0 s, 3.0 s) of Spwn_Sys...
bool IsCutDelay(const KismetOp& op, float duration);
// ...and Streaming_Assets' 2.0 s output link to the intro movie (`delay` as queued).
bool IsCutLink(const KismetOp& activator, const KismetOp& target, float delay);

}  // namespace wwe::faster_starts
