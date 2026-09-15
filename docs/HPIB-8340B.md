# HP 8340B HP-IB command table

**Repo rule 2: never fabricate an HP-IB code.**

This table mirrors `Hp8340BCommands.All` in `src/HP8340B.Instruments/Instruments/Hp8340BCommands.cs`.
The two are kept in step by `HpibCodeTests`, which asserts the status of every entry and proves
that `Require()` throws on anything Unverified.

## How codes work

Codes are the front-panel key mnemonics concatenated. Lower case is upshifted and spaces are
ignored, so `IP CW 2.3 GZ PL -30 DB` and `ipcw2.3gzpl-30db` are the same command.

The 8340B predates IEEE 488.2: **there is no `*IDN?` and no `*OPC?`.** Settling is handled with
the status byte or SRQ — see the status byte tables below.

## Where the full table lives

The complete code set is in **Tables 3-1 and 3-2 of the OPERATING manual**, which is not on this
machine. The *service* manual carries only what Section IV's test programs happen to use. That
is why so much below is Unverified: it is not guesswork that needs tidying, it is a document that
needs obtaining. See the "Source Section III of the operating manual" decision issue.

## Verified

| Code | Front-panel key | Source |
|---|---|---|
| `IP` | INSTR PRESET | Service manual 4-17, p. 4-83: "the program outputs an IP (INSTR PRESET)". Also the Section III example. Proven on this bench in HP-Attenuator. |
| `S2` | SINGLE sweep | Service manual 4-17, p. 4-83: "S2 (Single sweep)". Program listing, Table 4-32. |
| `TS` | TAKE SWEEP | Service manual 4-17, p. 4-83: "two TS (Take Sweep) commands". Program listing, Table 4-32. |
| `CW` | CW | Section III example `IP CW 2.3 GZ PL -30 DB`. Proven on this bench in HP-Attenuator. |
| `PL` | POWER LEVEL | Section III example `PL -30 DB`. Proven on this bench in HP-Attenuator. |
| `RF1` | RF ON | Proven on this bench in HP-Attenuator (`Hp8340B.cs`). |
| `RF0` | RF OFF | Proven on this bench in HP-Attenuator (`Hp8340B.cs`). |
| `GZ` | GHz (unit) | Section III example `CW 2.3 GZ`. |
| `MZ` | MHz (unit) | Proven on this bench in HP-Attenuator (`Hp8340B.cs`). |
| `DB` | dB(m) (unit) | Section III example `PL -30 DB`. |

## Unverified — these throw if a driver tries to send them

| Code | Believed to be | What to check |
|---|---|---|
| `FA` | START FREQ | Table 3-2 |
| `FB` | STOP FREQ | Table 3-2 |
| `CF` | CENTER FREQ | Table 3-2 |
| `DF` | DELTA FREQ / FREQ SPAN | Table 3-2 — the mnemonic *and* whether it is span or delta |
| `ST` | SWEEP TIME | Table 3-2, including the time-unit suffixes |
| `S1` | CONTINUOUS sweep | Table 3-2. `S2` is confirmed as single sweep, so this is very likely continuous |
| `S3` | MANUAL sweep | Table 3-2 |
| `KZ` | kHz (unit) | Table 3-1 unit terminators |
| `HZ` | Hz (unit) | Table 3-1 unit terminators |

## Not yet in the table at all

Everything the guided procedures need beyond the above, none of which has a candidate mnemonic
worth writing down until Section III is to hand:

`[SHIFT]` prefix handling, `[PEAK]` (SYTM auto tracking), `[XTAL]`, `[METER]`, `[PWR SWP]`,
`[AM]`, `[SAVE]`/`[RECALL]`, step size `[SHIFT][CF]`, learn string in and out, reading the
extended status byte, "output active parameter", and the calibration-constant access sequence.

The cal-constant access sequence is known as a **key sequence** from service manual 5-14 step 2 —
`[INSTR PRESET]`, `[SHIFT][GHz] n [Hz]`, `[SHIFT][MHz] 1 2 [Hz]`, `[SHIFT][kHz] 2 2 [Hz]` — but
its HP-IB equivalent is not. HP's own 08340-10009 software had a "display cal data" utility, so a
mechanism exists; finding it is issue M0-04.

## Status byte #1

Manual Table 4-31, p. 4-85. Read by serial poll. Modelled as `StatusByte1`.

| Bit | Value | Function |
|---|---|---|
| 7 | 128 | SRQ on new frequencies or sweep time in effect |
| 6 | 64 | Request service (RQS) |
| 5 | 32 | SRQ on HP-IB syntax error |
| 4 | 16 | SRQ on end of sweep |
| 3 | 8 | SRQ on RF settled |
| 2 | 4 | SRQ on change in extended status byte |
| 1 | 2 | SRQ on numeric entry completed (HP-IB or front panel) |
| 0 | 1 | SRQ on any front-panel key pressed |

## Extended status byte #2

Manual Table 4-31, p. 4-85. `(L)` marks the bits the manual shows as latched.
Modelled as `StatusByte2`.

| Bit | Value | Function |
|---|---|---|
| 7 | 128 | Fault indicator on |
| 6 | 64 (L) | **RF unleveled** |
| 5 | 32 (L) | Power failure |
| 4 | 16 (L) | RF unlocked |
| 3 | 8 | **External frequency reference selected** |
| 2 | 4 | Oven cold |
| 1 | 2 | Over modulation |
| 0 | 1 (L) | Self test failed |

Two of these change how this project works:

- **Bit 6, RF unleveled** is the front-panel UNLEVELED lamp. It is how the leveled squegging
  scan (M1-05) finds "maximum leveled power, just below UNLEVELED" over the bus rather than by
  eye, and how M1-06 tells a genuine power reversal from simply running out of leveled range.
- **Bit 3, external frequency reference selected** is how `probe` confirms the DUT is on the
  Z3805A rather than its internal standard, instead of trusting the rear-panel switch position.

Reading byte #2 needs a code from Table 3-2 that is not yet verified, so neither is wired up yet
(M0-03).
