# ADR-0028: HD texture packs come into scope; ultrawide stays out

Status: accepted (2026-10-24, the owner's decision in the graphics-remaster
grilling). Supersedes the texture-pack part of ADR-0001.

## Context

ADR-0001 kept HD texture packs and ultrawide out of v1.0 so the release would
not slip. Since then the port has shipped, and it has a mod system
(ADR-0026). The graphics-remaster research
(`docs/research/graphics-remaster.md` §5.1, §7) ranks texture replacement as
a high payoff once community packs exist. Native already hashes every guest
texture and can dump textures as DDS. Ultrawide would need RE across the
projection, the HUD and culling (research §3.2).

## Decision

- Native texture replacement by texture key is in scope. The project ships
  the dump mode, the loader and a guide. Packs are game-derived and come from
  the community; the project never ships them (the ADR-0012 stance).
- Ultrawide stays out of scope.
- The work starts in Phase 2 of the remaster plan, not before.

## Consequences

- Texture replacement is an Enhancement: scene runs turn it off (ADR-0002).
- Emulated gets no texture replacement unless a later decision adds an SDK
  patch for it.
