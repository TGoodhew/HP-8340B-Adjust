# Where this project is

Last updated 15 Sep 2026. **838 tests, all passing, sim only — nothing here has met an
instrument.**

## The one-line summary

M0 is done apart from four issues that genuinely need the bench. M1 is half done. Everything
built so far is developable and testable offline; the next hardware session has a short, specific
list of things only it can settle.

## State

| Milestone | Done | Open |
|---|---|---|
| M0 — Foundation | 21 | 3 |
| M1 — Protect and baseline | 4 | 6 |
| M2 – M5 | 0 | 33 |

`main` is green and pushed. Run `dotnet test`, then `hp8340b probe --sim`, `route status`,
`route card --campaign Adjustment`, `setup show S4`, `codes`.

## Pick up here

**M1-05, the leveled squegging scanner (#39)** is the next issue in order and is fully
sim-buildable. It is the same scan as the unleveled one but at maximum *leveled* power, which it
finds over the bus by raising the level until extended status byte #2 bit 6 (UNLEVELED) sets and
backing off — no watching the front panel. It maps to X2C/X3C/X4C via `Bands.LeveledPotFor`, and
must distinguish squegging at or below maximum specified leveled power (a fault, pot CCW) from
behaviour above it (simply going unleveled, not a fault).

Then #41 zero-span relaxation check, #37 baseline capture, #43 planner, #44 matrix wizard.

## What needs the bench, and nothing else will do

These four are open for that reason alone.

1. **#16 — cal constants (`needs-dut`).** Implemented and sim-tested. `SHHZ`, the *read* code, is
   inferred from Table 3-2 with no corroborating source. The write path is cited; the read path is
   not. Everything downstream of a cal-constant read is provisional until this is confirmed.
2. **#29 — borrowed kit.** The U8485A is not here and its manual is not in the library. The
   traceability machinery it feeds is built and tested.
3. **#31 — connector names.** Every one is flagged `[verify-connector]`. They came from the
   service manual's figures and need eyes on the instruments.
4. **#28 — PN hand-off.** Import and pass rule are done. Handing the bus over needs the bus.

### The short list for the next bench session

In rough order of value per minute:

- **Trace the 3499A cables and fill in `Data/routing.json`.** Decision D-11. This *cannot* be read
  off the bus — the mainframe reports `GP RELAY 44471` for every 44471/44476/44477 and its own
  guide says to check the modules physically. `route status` shows 8 legs still on PLACEHOLDER
  numbers.
- **Read the frequency range off the 33311B/C switch bodies.** Thirty seconds, and it removes the
  biggest unsourced dependency in the project: that figure decides whether a band-4 result may be
  reported as `Spec`. See `docs/PROVISIONAL.md`.
- **Enter the 478A's six calibration points and the 8481A/8485A cal-factor charts.** Per-sensor,
  printed on the hardware, and the code refuses to guess them.
- **Confirm the 5351A's EXT REF annunciator.** It is the only instrument on the bench that cannot
  report its reference over the bus, and 4-2 depends on it.
- **Confirm the 432A's MOUNT RESISTANCE switch is on 200 Ω.** The 478A manual warns in capitals
  that the 100 Ω setting damages the thermistor.
- **Fill in the TBD addresses in `bench.json`.**

## Things a reader should know before trusting anything

- **`docs/PROVISIONAL.md`** lists every threshold that has never been checked against an
  instrument, and separately every figure stated *about* hardware with no manual to cite. A drift
  test fails if that list and the source fall out of step. Read it before believing a number.
- **`docs/ADJUSTMENT-BRIEF.md`** has a changelog of sixteen corrections found while building this,
  several of which contradict what the project brief originally said. Entries 14, 15 and 16 are
  the ones most likely to catch somebody out.
- **The simulated DUT deliberately misbehaves.** `SimulatedSweeper` reproduces power reversal,
  power holes and spurious responses, so the scanners can be tested against something that fails.
  Every scanner behaviour is asserted twice — absent when the model is adjusted, present when it
  is not.

## Bugs found by tests, not by hardware

Worth knowing the class of thing this catches, because all of them produce plausible wrong
answers rather than errors:

- A settle-wait deadline that only bounded the gaps between polls, so a 5 s wait could run to 25 s
  on a wedged bus and report nothing unusual.
- Failed bus writes never reaching the audit log, including the learn string — where a partial
  write leaves the DUT in a configuration nobody chose, silently and persistently.
- Two catches that answered a question on behalf of an instrument that never answered.
- `ScanPoints` drifting so the last point of band 1 landed on 7.000 GHz and would have been handed
  band 2's pot.
- A `record`'s `with` sharing the cal-factor list between two sensors, so entering one chart
  rewrote the other.
