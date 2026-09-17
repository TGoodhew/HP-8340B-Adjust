# Where this project is

Last updated 17 Sep 2026. **947 tests, all passing, sim only — nothing here has met an
instrument.**

## The one-line summary

M1-05 landed, and then a scope inventory turned into a milestone of its own: the bench gained five
oscilloscopes, so M0 grew by eight issues and most of them are now done. Everything built remains
developable and testable offline. The next hardware session's list is longer than it was, and more
specific.

## State

| Milestone | Closed | Open |
|---|---|---|
| M0 — Foundation | 21 | 11 |
| M1 — Protect and baseline | 4 | 6 |
| M2 — Live tuning aids | 0 | 11 |
| M3 – M5 | 0 | 23 |

`main` is green and pushed. Run `dotnet test`, then `hp8340b probe --sim`, `route status`,
`setup show S4`, `codes`.

## What happened in the session of 17 Sep

Seven commits, oldest first:

- **M1-05 (#39)** — the leveled squegging scanner of 5-16 steps 37-50. Finds maximum leveled power
  over the bus from extended status byte #2 bit 6 rather than by watching the lamp, and draws the
  5-16 steps 32-34 line: at or below maximum *specified* leveled power a response is a fault, above
  it the output is going unleveled and no pot fixes it.
- **M0-31 (#87)** — DS1104Z waveform capture moved to BYTE with the preamble cached. One round trip
  a frame instead of five, 1.2 kB instead of ~17 kB.
- **M0-30 (#86), part** — the measurement core for instrument speeds. Distributions, not averages.
- **M0-25 (#81)** — `IOscilloscope`, `ScopeCapability`, and three scope roles.
- **M0-26/27 (#82, #83)** — TDS3014B and DPO3034 drivers.
- **M0-28 (#84)** — 54845A Infiniium driver.
- **M0-29 (#85)** — detector calibration bound to the scope and termination it was taken on.

### The new `needs-bench` label

`needs-dut` means the 8340B. `needs-hardware` means kit that is **not** on the bench. Neither
covered "needs an instrument that is here" — the 54845A is present, is not the DUT, and is not
missing — so work that could not be closed was filtering as if it could. `needs-bench` fills that
gap and has been applied across every open issue, including the decision issues that cannot be
answered from a desk (#2, #5, #6, #7, #11).

### Three findings worth not rediscovering

1. **The TDS3014B has no usable external trigger.** Its programmer manual states neither EXT nor
   EXT10 is available on **4-channel** TDS3000 Series instruments, and the 3014B is four-channel.
   Every hook-up card says "DUT rear sweep trigger to EXT TRIG" and quietly assumed otherwise. It
   can still run S5 — but only by spending a channel on the trigger, and three test points plus a
   trigger is *exactly* four. `ScopeRole` now models signal channels and trigger separately for
   this reason.
2. **The fast-scope bandwidth bar is arithmetic, not a guess, and it is low.** Table 4-25 specifies
   rise and fall under 25 ns; 0.35/B with a root-sum-square puts a 2% contribution at ~70 MHz. A
   100 MHz scope contributes about 1%. **Bandwidth is not what rules scopes out of 4-12 — the
   50 Ω input is.** The 54845A's value is that at 1.5 GHz the scope leaves the error budget
   entirely, putting the limit on the 8473C's video bandwidth where it belongs.
3. **Three sample-scaling conventions now live in this codebase**, and each produces a plausible
   trace when applied to the wrong instrument:
   - Rigol: `(raw − yorigin − yreference) × yincrement`
   - Tektronix: `YZEro + YMUlt × (raw − YOFf)`
   - Agilent: `(raw − YREFerence) × YINCrement + YORigin`

   All three are pinned by tests. Do not write a fourth driver by analogy with a third.

## Pick up here

**Software-only work, no hardware needed:**

- **#89, M0-32** — the driver substitutability audit. The scope work is its worked example, and
  doing it before more drivers get written is the point. Note the audit's own finding: concrete
  driver types barely leak upward (only `Bench/Model/SelfChecks.cs` names any), because the
  measurement layer takes delegates — but a delegate carries no contract about units, sign or
  failure behaviour, which is the real problem.
- **#86 remainder** — the `bench-rate` command, end-to-end frame rate and turn-to-see latency with
  the DUT sweeping, DUT settle and sweep timing, session integration.
- **M1: #37 baseline capture, #41 zero-span relaxation, #43 planner.** All three sim-buildable.
  #37 is a hard precondition for the 5-14/5-16 runners: a pot turn is not undoable, so the baseline
  is the only audit trail there will ever be.

**#44 (M1-10 matrix calibration) is blocked on D-11**, the 3499A cable trace — not on missing kit,
despite its `needs-hardware` label. Worth relabelling.

## What needs the bench

The original four, unchanged:

1. **#16 — cal constants.** `SHHZ`, the *read* code, is inferred from Table 3-2 with no
   corroborating source. Everything downstream of a cal-constant read is provisional until
   confirmed.
2. **#29 — borrowed kit.** The U8485A is not here, and is now optional anyway: the resident
   8481A + 8485A cover 10 MHz – 26.5 GHz.
3. **#31 — connector names.** Every one is `[verify-connector]`.
4. **#28 — PN hand-off.**

Added this session:

5. **Byte order and signedness on every new scope driver.** The Rigol is unsigned with a mid-scale
   reference; Tektronix is signed; the Infiniium's is *inferred* from Agilent's convention rather
   than read from a page, because the quick reference gives syntax and not semantics. A wrong guess
   inverts or offsets the trace without making it look broken — and the detector output is
   negative-going, so a wrong trace still looks like a detector trace.
6. **Which remote interface is fitted to the 54845A** (LAN, GPIB or both).
7. **Whether the TDS3014B has a communication module fitted.** It does not need one — B-series have
   built-in Ethernet — but if a spare **TDS3EM** ever turns up, do not fit it: the manual states it
   stops *both* the built-in port and the module's own port working.

### The short list for the next bench session

Unchanged in priority: trace the 3499A cables (D-11); read the frequency range off the 33311B/C
switch bodies; enter the 478A's six calibration points and the 8481A/8485A cal-factor charts;
confirm the 5351A's EXT REF annunciator; confirm the 432A's MOUNT RESISTANCE is on 200 Ω; fill in
the TBD addresses.

## Cross-project: ManualForge

A parallel Claude session (`manualforge-72`) built a detection-and-repair feature for PDFs whose
text layer is **present but incomplete**. That came out of this project: searching for Infiniium
commands returned "a real absence", and the manual was in the library the whole time — 110 pages,
no images, a real text layer holding only front matter, with the entire command reference drawn as
vector syntax diagrams.

**The technique that recovered it is worth keeping:** when `pdftotext` returns little or nothing
from a manual that plainly has content, render the pages instead —
`pdftoppm -png -r 110 -f <page> -l <page> file.pdf out` — and read the images. It works because
these are vector drawings, not scans.

**Waiting on Tony:** whether to deploy the ManualForge build over the installed copy and re-run the
repair and re-index of the real library. Until then the live MCP index is unchanged and still
reports absences it cannot substantiate.

**Owed by this session:** an independent run of the 33-query ground-truth table in ManualForge's
`docs/GROUND-TRUTH-54845A.md` once that lands. The agreement was that the caller who hit the
original miss should verify it rather than the author of the detector.

## Things a reader should know before trusting anything

- **`docs/PROVISIONAL.md`** lists every threshold never checked against an instrument, and
  separately every figure stated *about* hardware with no manual to cite. A drift test fails if that
  list and the source fall out of step.
- **`docs/ADJUSTMENT-BRIEF.md`** has a changelog of corrections found while building this.
- **The simulated DUT deliberately misbehaves**, and every scanner behaviour is asserted twice —
  absent when the model is adjusted, present when it is not.
- **M2-02 (#46) asks for a 0.5 s refresh, and that target is probably wrong.** 2 Hz is in the range
  where an operator can no longer connect what their hand did to what the trace did. The DUT's own
  sweep time is 50 ms (5-16 steps 24 and 90), so the ceiling is 20 Hz. Commented on the issue; the
  acceptance criteria are deliberately left for Tony to change.

## Bugs found by tests, not by hardware

All of these produce plausible wrong answers rather than errors:

- A settle-wait deadline that only bounded the gaps between polls.
- Failed bus writes never reaching the audit log, including the learn string.
- Two catches that answered a question on behalf of an instrument that never answered.
- `ScanPoints` drifting so the last point of band 1 landed on 7.000 GHz.
- A `record`'s `with` sharing the cal-factor list between two sensors.
- A justification comment claiming session JSON stability that was false twice over — enums
  serialise as strings, and nothing serialised that type at all.
- A test asserting a 50 Ω feedthrough across 1 MΩ is exactly 50 Ω. It is 49.9975.
