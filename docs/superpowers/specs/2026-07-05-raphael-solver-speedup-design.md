# Raphael Solver Speedup — Design

**Date:** 2026-07-05
**Status:** Approved (pending final validation-suite results)

## Problem

The Raphael solver (vendored `raphael-rs` submodule, shipped as `Artisan/raphael-cli.bin`)
times out on high-tier stellar missions. Reproduced case: **"EX: Superior Transport
Supplies"** (WVR mission 1530, recipe 38132 — rlvl 728 expert, 9700 progress / 17300
quality / 70 durability) with buffed stats 5811 craftsmanship / 5576 control / 776 CP
(HQ All i Pebre + HQ Cunning Craftsman's Tisane) and the user's config
(`EnsureReliability` → `--adversarial`, `BackloadProgress`, `MaxStellarHand 2`,
`TimeOutMins 1`).

Measured offline (12-core machine, idle — in-game is slower because the game competes
for cores):

| Solver | Steady Hand 1 | Steady Hand 2 | Peak RAM |
|---|---|---|---|
| Stock v0.28.4 | 41.4 s | 79.4 s | ~2 GB |
| This design | 24.6 s | 25.1 s | roughly ¼ |

Both timeouts recorded in `dalamud.log` on 2026-07-05 correspond to the stock ssh=2
case, which cannot finish within 1 minute in-game.

Cost drivers, in order: the Stellar Steady Hand charge dimension (~4.5× state-space
multiplier), adversarial mode (~1.5×, and it disables an upstream pruning LUT), and the
three solver phases sharing the cost roughly equally (quality-upper-bound precompute,
step-lower-bound precompute, main search).

## Approach

Two groups of algorithmic changes inside `raphael-solver` (≈55 lines total), chosen
over alternatives (integration-only config tuning: no real speedup; exact-only
memoization work: insufficient) and validated on the reproduced instance.

### Group 1 — admissible relaxations (cannot change the solver's result)

These coarsen the *bound solvers'* state spaces. Bounds stay admissible (states are
only ever relaxed upward), so the main search still returns the same provably optimal
macros; the only cost is slightly looser pruning.

1. **CP granularity 8** (`quality_upper_bound_solver`): `ReducedState` CP rounds up to
   a multiple of `CP_GRANULARITY = 8` (was 2), and the precompute loop instantiates
   only those CP values (anchored at `(2 * durability_cost).next_multiple_of(8)` so
   lookups always hit). ~4× fewer memoized states/Pareto fronts. Granularity 4 was
   benchmarked and loses on the worst case (35.3 s vs 31.5 s at ssh=2).
2. **Innovation/Veneration merge** (`quality_upper_bound_solver`): when both effects
   are active, extend both to the max of the two remaining durations (from the
   pre-existing untracked `raphael-solver-perf.patch`).
3. **Durability round-up** (`step_lower_bound_solver`): usable durability rounds up to
   a multiple of 10 (also from the patch).
4. **Adversarial IQ quality LUT** (`utils::compute_iq_quality_lut`): upstream returns
   all zeros in adversarial mode (a `TODO`), disabling the maximal-template cutoff.
   Implemented by running the same DP with the adversarial guard *inactive*, so each
   quality action contributes its worst-case (Poor condition) amount. Sound because an
   unguarded quality action always grants at least
   `quality_increase(…, Condition::Poor)` (verified in `raphael-sim/src/state.rs`), and
   guarded/combo continuations are deterministic and larger.

Combined effect: ssh=2 79.4 s → 31.5 s.

### Group 2 — bounded-loss dominance quantization (approved trade-off)

5. **Quantized Pareto dominance** (`macro_solver/pareto_front.rs`): the main search's
   dominance values bucket CP by 8 (`CP_SHIFT = 3`), quality and quality+unreliable by
   16 (`QUALITY_SHIFT = 4`, ~0.09 % of this recipe's 17300), and durability by 5
   (lossless — all durability values are multiples of 5). More states get pruned as
   "equal". A surviving state can be up to one bucket worse per comparison than a
   pruned one, so final macros may be marginally suboptimal. On the reproduced
   instance the solutions are bit-identical; the repo's exhaustive suites are the
   broader check (see Validation). The SIMD guard-bit dominance trick is preserved
   (field boundaries unchanged).

Combined effect: ssh=2 31.5 s → 25.1 s.

### Explicitly rejected

- **Step-slack early termination** (prune as if solutions had one fewer step):
  prototyped, interleaved A/B benchmark showed only noise-level gains (~5 %); not
  worth the suboptimality. The prototype is removed.
- **CP granularity 4**: strictly worse than 8 on the pathological case.
- **Target-quality clamping for cosmic missions**: based on a wrong hypothesis — the
  17300 target *is* reachable (17439 achieved); no Artisan-side quality logic changes.

## Artisan integration

No functional C# changes required. The deliverable is a rebuilt `raphael-cli.bin`.

- `MaxStellarHand = 2` stays sensible: with the fixes, ssh=2 costs the same as ssh=1
  and yields a 1-step-shorter macro.
- Recommend (not require) bumping the default/user `TimeOutMins` from 1 to 2 as
  headroom on busy machines.

## Shipping & maintenance (approved)

Fork `KonaeAkira/raphael-rs` under the user's GitHub (`n4n0byte/raphael-rs`), commit
the changes on a branch off the `v0.28.4` tag, repoint the Artisan submodule to the
fork, and commit the rebuilt `raphael-cli.bin`. Upstream syncs happen by rebasing the
fork branch onto new upstream tags. The now-superseded `raphael-solver-perf.patch`
file is deleted (its two hunks are part of the fork branch).

Build note (this machine): user-local rustup with `nightly-2026-05-10-x86_64-pc-windows-gnu`
(pinned by `rust-toolchain.toml`); `windows-sys` needs GNU `dlltool`/`as` on `PATH`
(MSYS2 mingw-w64 binutils — the rustup self-contained `dlltool` lacks `as`). Build with
`cargo build --release --package raphael-cli` in `raphael-rs/`, then copy
`target/release/raphael-cli.exe` over `Artisan/raphael-cli.bin`.

## Validation

1. **Reproduced instance**: identical solutions (17439 quality; 25 steps ssh=1,
   24 steps ssh=2) at every step of the change stack. ✔ (measured)
2. **`00_edge_cases` suite**: all 11 solution-score expectations pass against the
   prototype. ✔ (verified 2026-07-05 — the 10 reported failures were all in the
   `MacroSolverStats` snapshot blocks, which legitimately change as state counts
   shrink; they are regenerated with `UPDATE_EXPECT=1` on the fork branch.)
3. **Exhaustive suites** (`01_progress_backload_exhaustive`, `02_exhaustive`,
   `03_adversarial_exhaustive`): these sweep many configurations and assert solution
   scores against a reference simulator/exact expectations. Group 1 changes cannot
   alter scores; any score deltas are attributable to change #5 and must be reviewed —
   acceptance bar: no quality regressions beyond 16 quality units (one bucket) and no
   step-count regressions beyond +1 on any swept case, else `QUALITY_SHIFT`/`CP_SHIFT`
   are reduced until the bar is met.
4. **In-game smoke test**: user regenerates the macro for the mission and confirms
   solve time and a valid macro.

## Risks

- **Fork drift**: upstream raphael-rs moves fast; rebasing 5 small, file-local hunks
  is low-effort but manual. Mitigated by keeping the diff minimal and documented here.
- **Change #5 quality loss on untested recipes**: bounded per comparison (16 quality /
  8 CP buckets) and empirically zero on all validated cases, but not provably zero
  end-to-end. It is trivially revertable (two shift constants set to 0 restore exact
  dominance).
- **GNU vs MSVC build**: the shipped `.bin` was previously MSVC-built; the new one is
  windows-gnu. Both are standalone; if any runtime issue appears, building on a
  machine with MSVC Build Tools is a drop-in alternative.
