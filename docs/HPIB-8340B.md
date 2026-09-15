# HP 8340B HP-IB command table

**Repo rule 2: never fabricate an HP-IB code.**

This table mirrors `Hp8340BCommands.All` in
`src/HP8340B.Instruments/Instruments/Hp8340BCommands.cs`. The two are kept in step by
`HpibCodeTests`, which asserts the status of the codes the project depends on and proves that an
unknown code is refused.

**Source: Table 3-2, HP 8340B/41B Programming Codes**, Operating Manual Section III,
pp. 3-59 to 3-62 — `8340b User.pdf` in the local manual library.

## How codes work

Codes are the front-panel key mnemonics concatenated. Lower case is upshifted and spaces are
ignored, so `IP CW 2.3 GZ PL -30 DB` and `ipcw2.3gzpl-30db` are the same command.

The manual's own suffix notation, which the table below uses:

| Suffix | Meaning |
|---|---|
| `d` | decimal data — integer, real or exponential |
| `m` | a `1` or `0` must follow the code; `1` turns the function on |
| `n` | a single digit register number (1–9 for `SV`, 0–9 for `RC`) |
| `t` | a terminator is required |
| `(…)` | the code makes the instrument **output** data; the letters give the format |
| `*` | special suffix requirements — read the detailed explanation for that code |

Terminators double as unit scalars: `GZ`, `MZ`, `KZ`, `HZ`, `DB`, `SC` and `MS`. A comma or an
ASCII LF also terminates, scaling to the fundamental units of Hz, seconds or dB(m).

The 8340B predates IEEE 488.2: **there is no `*IDN?` and no `*OPC?`.** `OI` is the identification
query, and settling is handled with the status bytes — see below.

## Reading Table 3-2 without getting it wrong

> This matters. A mis-paired code sends the wrong command to a 26.5 GHz synthesiser.

Extract Table 3-2 with **`pdftotext -raw`**, never `-layout`. In layout mode the Code and
Operation columns drift by one row wherever a cell wraps, and every entry after that point is
silently mis-paired. The layout output makes `SHFB` look like "Restore calibration constant
access" when it is actually the display multiplier, and `SHEF` is the cal-constant code.

In raw mode the two columns come out as equal-length lists — 49 and 49 on page 2 of 4 — that pair
correctly. Three independent checks confirm the raw pairing:

1. **The prose cites codes directly.** "[SHIFT] [ENTRY OFF] (HP-IB: SHEF) recalls the Calibration
   Constant Access Function"; "[SHIFT] [PEAK] (HP-IB: SHRP) … aligns all of the YTM tracking
   calibration constants".
2. **The `OM` parameter list names the same things independently**: item 40 I/O channel
   `[SHIFT][GHz]`, 41 I/O subchannel `[SHIFT][MHz]`, 42 I/O write `[SHIFT][kHz]`, 37 bypassed ALC
   `[SHIFT][METER]`, 35 decoupled ATN/ALC `[SHIFT][PWR SWP]`.
3. **The SH-prefixed codes track their unprefixed forms.** `A1`/`A2`/`A3` are INT/XTAL/METER, so
   `SHA1`/`SHA2`/`SHA3` are SHIFT plus those.

## Frequency

| Code | Key / operation |
|---|---|
| `CW d t` | CW frequency |
| `FA d t` | START FREQ |
| `FB d t` | STOP FREQ |
| `CF d t` | CENTER FREQ |
| `DF d t` | **Delta** frequency — *not* frequency span |
| `SF d t` | Step frequency size |
| `BC` | Change frequency band |

## Power and leveling

| Code | Key / operation |
|---|---|
| `PL d t` | POWER LEVEL |
| `PS1` / `PS0` | Power sweep on / off |
| `AT d` | Attenuator |
| `SL m d t` | Power slope |
| `RF1` / `RF0` | RF output on / off |
| `A1` | Leveling, internal — `[INT]` |
| `A2` | Leveling, external — `[XTAL]` |
| `A3` | Leveling, power meter — `[METER]` |

`A2` with nothing connected to the external-leveling input is effectively unleveled, which is how
5-14's SYTM tracking adjustments are made.

## Sweep and trigger

| Code | Key / operation |
|---|---|
| `S1` | Sweep, continuous — `[CONT]` |
| `S2` | Sweep, single — `[SINGLE]` |
| `S3` | Sweep, manual — `[MANUAL]` |
| `ST d t` | SWEEP TIME |
| `TS` | Take sweep |
| `RS` | Reset sweep |
| `T1` / `T2` / `T3` | Trigger free run / line / external |

## Modulation

| Code | Key / operation |
|---|---|
| `AM1` / `AM0` | Amplitude modulation on / off |
| `FM1` / `FM0` | Frequency modulation on / off |
| `PM1` / `PM0` | Pulse modulation on / off |

## State storage and the learn string

| Code | Key / operation |
|---|---|
| `SV n` | Save instrument state, register 1–9 |
| `RC n` | Recall instrument state, register 0–9 |
| `OL (123b)` | Output learn data — read back 123 binary bytes |
| `IL 123b` | Input learn data — 123 binary bytes follow |

5-14 steps 32–58 save three states per band (slow sweep, AUTO, single sweep) into registers 1, 2
and 3, so `SV` and `RC` carry the delay-compensation comparisons.

## Output and query

| Code | Key / operation |
|---|---|
| `OI (19a)` | Output identification — 19 ASCII characters |
| `OS (2b)` | **Output status bytes — both of them, as 2 binary bytes** |
| `OA (d)` | Output active parameter |
| `OM (8b)` | Output mode data |
| `OP (d)` | Output interrogated parameter |
| `OR (d)` | Output power level |
| `OF (d)` | Output fault values |
| `CS` | Clear both status bytes |
| `RM 1 b` | Status byte mask |
| `RE 1 b` | Extended status byte mask |

## Instrument

| Code | Key / operation |
|---|---|
| `IP` | INSTR PRESET |
| `EF` | Entry display off — `[ENTRY OFF]` |
| `EK` | Enable rotary knob |
| `KR` | Keyboard release |
| `SH` | Shift prefix — `[SHIFT]` |

## Shifted functions the adjustment procedures need

| Code | Key | Operation |
|---|---|---|
| `SHRP` | `[SHIFT][PEAK]` | **Tracking calibration** — aligns all the YTM tracking calibration constants, 5–10 s, displays "AUTO TRACKING". This is 5-14 step 31. |
| `RP1` / `RP0` | `[PEAK]` | RF peaking on / off — peaks the present CW frequency only, a fraction of a second. **Not** the same as `SHRP`. |
| `SHAK` | `[SHIFT][AMTD MKR]` | Immediate YTM peak — fine alignment only, faster than `[PEAK]` but less range. |
| `SHA3` | `[SHIFT][METER]` | Access linear modulator — **the ALC bypass**. 5-16 step 1, and 5-14 step 55's `[SHIFT][METER] −50 dBm`. |
| `SHPS` | `[SHIFT][PWR SWP]` | Decouple ATN, ALC. Used throughout 5-16. |
| `SHCF` | `[SHIFT][CF]` | Set frequency step size. |
| `SHPL` | `[SHIFT][POWER LEVEL]` | Set power level step. |
| `SHS3` | `[SHIFT][MANUAL]` | Fault diagnostic — displays `FAULT: CAL KICK ADC PEAK TRK`. |

`RP` against `SHRP` is worth care: sending `RP` where `SHRP` was meant would peak one frequency
instead of recalibrating the whole tracking table, and would look like it had worked.

## Calibration-constant I/O

> **Danger.** These reach the protected calibration. Repo rule 1: no write without an explicit
> per-action confirmation, never in a loop, and `backup-cal` must have run first.

| Code | Key | Operation |
|---|---|---|
| `SHGZ d t` | `[SHIFT][GHz]` | I/O channel |
| `SHMZ d t` | `[SHIFT][MHz]` | I/O subchannel |
| `SHKZ d t` | `[SHIFT][kHz]` | I/O write |
| `SHHZ` | `[SHIFT][Hz]` | Read from I/O |
| `SHEF` | `[SHIFT][ENTRY OFF]` | Restore calibration constant access function |

These map directly onto the front-panel sequence in service manual 5-14 step 2:

```
[INSTR PRESET]
[SHIFT] [GHz] 2 [Hz]        ->  SHGZ 2 HZ      select cal constant 2 (I/O channel)
[SHIFT] [MHz] 1 2 [Hz]      ->  SHMZ 12 HZ     I/O subchannel
[SHIFT] [kHz] 2 2 [Hz]      ->  SHKZ 22 HZ     I/O write
1 0 0 [Hz]                  ->                 set the value to 100
```

and on the manual's note that subsequent access needs only
`[SHIFT][GHz] n [Hz] [SHIFT][ENTRY OFF]` → `SHGZ n HZ SHEF`.

`OM` parameter 22 is "Calibration constants accessed", so the instrument reports whether it is in
cal-constant mode — worth reading before and after.

**The read path is the one to be careful about.** `SHHZ` ("Read from I/O") appears in Table 3-2
but, unlike its three siblings, has no matching entry in the `OM` parameter list. It is very
likely the mechanism behind HP's own 08340-10009 "display cal data" utility, but the semantics
must be confirmed on hardware before M0-04 relies on them. `Hp8340BCommands` flags this in the
code's own source note, and a test asserts the flag is there.

## Status byte #1

Service manual Table 4-31, p. 4-85. Read by serial poll, or with `OS`. Modelled as `StatusByte1`.

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

Service manual Table 4-31, p. 4-85. `(L)` marks the bits the manual shows as latched. Read with
`OS (2b)`, which returns both bytes. Modelled as `StatusByte2`.

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

- **Bit 6, RF unleveled** is the front-panel UNLEVELED lamp. It is how the leveled squegging scan
  (M1-05) finds "maximum leveled power, just below UNLEVELED" over the bus rather than by eye, and
  how M1-06 tells a genuine power reversal from simply running out of leveled range.
- **Bit 3, external frequency reference selected** is how `probe` confirms the DUT is on the
  Z3805A rather than trusting the rear-panel switch position.

`RM` and `RE` set the SRQ masks for the two bytes, and `CS` clears both.

## Nothing is Unverified

Every code the project knows about is now cited. `Hp8340BCommands.Require()` still throws on an
unknown mnemonic, and `AnyUnverifiedCodeRefusesToBeSent` keeps the guard honest for whatever gets
added next.
