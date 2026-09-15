# Adjustment brief — HP 8340B/41B Sections IV and V

Distilled from the HP 8340B/41B Operating & Service Manual, Volume 1, plus the appended
*Attenuator Calibration and Operation Verification Test Software* manual (HP P/N 08340-10009).

Page references are to the service manual. Section III (HP-IB programming codes, Tables 3-1 and
3-2) is in the **operating** manual, which is not yet on this machine — see
[HPIB-8340B.md](HPIB-8340B.md).

No manual text is reproduced here beyond what is needed to act on it; `docs/manual/` is
git-ignored (repo rule 7).

## Changelog of corrections

Everything below was checked against the manual's text layer on 15 Sep 2026. Where the original
project brief and the manual disagree, the manual wins and the difference is recorded here.

| # | Date | Correction |
|---|---|---|
| 1 | 2026-09-15 | **Output power accuracy, band 0.** The brief gave ±0.9 dB for +10 to −9.95 dBm. Table 4-9 (2 of 2) gives **±1.2 dB** for that row; ±0.9 dB is the row above it, +18 to +10 dBm. Bands 1–3 (±1.5 dB) and band 4 (±2.0 dB) at +10 to −9.95 dBm were correct. |
| 2 | 2026-09-15 | **Extended status byte added.** The brief carried status byte #1 only. Table 4-31 also defines extended status byte #2, and two of its bits change the design: **bit 6 RF unleveled** lets the tool find maximum leveled power over the bus instead of by watching the front-panel lamp, and **bit 3 external frequency reference selected** lets `probe` confirm the DUT is on the Z3805A rather than trusting the rear-panel switch. |
| 3 | 2026-09-15 | **Cal-constant presets confirmed, not corrected.** The two-column table in 5-14 step 3 scrambles badly under layout-preserving extraction. Re-read in raw order it is unambiguous and matches the brief exactly: CC3–CC8 = 100, CC9–CC12 = 1024, CC50–CC53 = 0, CC71–CC74 = 1024, CC75 = 25, CC76 = 1000, CC77 = −25, CC78 = +25, CC80 = 0, with CC2 = 100 set separately in step 2. |
| 4 | 2026-09-15 | **A24 pot table confirmed.** 5-14 steps 4–6 list exactly the twelve pots and names the brief gives. |
| 5 | 2026-09-15 | **Max leveled power table confirmed** for all four options against Table 4-9 (1 of 2), p. 4-24. |

### Still to verify

- **Table 4-9 output power accuracy and flatness, rows below +10 dBm.** The row labels drift
  against the value columns under both extraction modes; the lower-level rows and the
  Option 001/004/005 blocks are not safely transcribable from the text layer. Read them off the
  page image before putting them in code. Tracked on the M4-02 issue.
- **Table 4-8 swept frequency accuracy test frequencies** and **Table 4-2 equipment** both
  extract with OCR damage to the numerals (`2-32` for `2.32`, `24.55'` for `24.55`). Transcribe
  from the page image, not the text layer.

## B.1 Architecture

### Frequency bands

Bandswitch points are approximate; the exact crossovers are themselves adjustable via CC77 and
CC78 (5-14 steps 59–67).

| Band | Range | How it is generated |
|---|---|---|
| 0 | 0.01 – 2.3 GHz | Heterodyne — YO mixed with the A8 3.7 GHz oscillator |
| 1 | 2.3 – 7.0 GHz | YIG oscillator (YO) fundamental |
| 2 | 7.0 – 13.5 GHz | SYTM ×2 |
| 3 | 13.5 – 20.0 GHz | SYTM ×3 |
| 4 | 20.0 – 26.5 GHz | SYTM ×4 (8340B only) |

### Assemblies that matter

A13 SYTM (YIG-tuned multiplier, contains the SRD) · A28 SYTM Driver (tracking) · A24 Attenuator
Driver / SRD Bias · A26 Linear Modulator (ALC modulator, loop gain) · A25 ALC Detector (logger) ·
A27 Level Control · A44 YIG Oscillator · A54 YO Pretune DAC / Delay Compensation · A55 YO Driver ·
A11 Bands 1–4 detector · A12 Band 0 splitter/detector.

### Maximum leveled output power (Table 4-9, p. 4-24, 0 °C to +35 °C)

| Option | B0 | B1 | B2 | B3 | B4 (20–23) | B4 (23–26.5) |
|---|---|---|---|---|---|---|
| Standard (F.P. out w/ atten) | +10.0 | +12.0 | +10.0 | +9.0 | +3.0 | +1.0 |
| 001 (F.P. out w/o atten) | +10.0 | +13.0 | +12.0 | +11.0 | +6.0 | +4.0 |
| 004 (R.P. out w/ atten) | +10.0 | +11.0 | +9.0 | +7.0 | +1.0 | −1.0 |
| 005 (R.P. out w/o atten) | +10.0 | +12.0 | +11.0 | +9.0 | +4.0 | +2.0 |

All figures dBm. Implemented as `MaxLeveledPower` and covered by `MaxLeveledPowerTests`.

### Output power accuracy, standard instrument (Table 4-9, 2 of 2)

| Level | Band 0 | Bands 1–3 | Band 4 |
|---|---|---|---|
| +18 to +10 dBm | ±0.9 dB | ±1.8 dB | ±2.3 dB |
| +10 to −9.95 dBm | ±1.2 dB | ±1.5 dB | ±2.0 dB |

Lower-level rows and the other options: see "Still to verify" above. Note +18 to +10 dBm is
above the specified maximum leveled power — the ALC typically operates up to +20 dB to make that
range usable where the extra power exists (Table 4-9 note 3).

### Spurious, at 0 dBm (dBc)

| | B0 | B1 | B2 | B3 | B4 |
|---|---|---|---|---|---|
| Harmonics | < −35 | < −35 | < −35 | < −35 | < −35 |
| Subharmonics | — | — | < −25 | < −25 | < −20 |
| Non-harmonic | < −50 | < −70 | < −64 | < −60 | < −58 |
| Line-related, offset < 300 Hz | < −50 | < −50 | < −44 | < −40 | < −38 |
| Line-related, 300 Hz – 1 kHz | < −60 | < −60 | < −54 | < −50 | < −48 |
| Line-related, > 1 kHz | < −65 | < −65 | < −59 | < −55 | < −53 |

Carried over from the project brief; not yet re-checked against the specification pages.

## B.2 Squegging

The manual's definition (5-14, footnote): the squegging region is an area where undesired
oscillations of the YIG sphere or the step recovery diode — both inside the SYTM — are produced.

Symptoms:

- **Power reversal** — actual output *falls* as requested output rises.
- **Power holes** in the output.
- **Spurious responses** on the RF output.

In the unleveled adjustments, turning an SRD bias pot **clockwise increases bias and moves toward
the squegging region**. If squegging occurs, turn the pot counter-clockwise until the power is
about **0.5 dB below** the squegging region.

**Band 1 squegging cannot be adjusted out.** It is a function of SYTM input power and only occurs
when maximum *unleveled* power is requested. The scanner must treat it as expected and report
band 1 only at or below maximum specified power (M1-08).

### A24 pots — SRD bias

| Pot | Name | Owns |
|---|---|---|
| A24R1 | OFF A | SRD bias offset, low end of band 2 (leveled) |
| A24R2 | OFF B | SRD bias offset, high end of band 2 (leveled) |
| A24R3 | X2A | Unleveled SRD bias, band 2 first half (~7–10 GHz) |
| A24R4 | X2B | Unleveled SRD bias, band 2 second half (~10–13.5 GHz) |
| A24R5 | X2C | Leveled SRD bias, band 2 |
| A24R6 | X3A | Unleveled, band 3 first half (~13.5–15 GHz) |
| A24R7 | X3B | Unleveled, band 3 second half (~15–20 GHz) |
| A24R8 | X3C | Leveled, band 3 |
| A24R9 | X4A | Unleveled, band 4 first half (~20–23 GHz) |
| A24R10 | X4B | Unleveled, band 4 second half (~23–26.5 GHz) |
| A24R11 | X4C | Leveled, band 4 |
| A24R12 | MIN | Minimum modulator level (most-negative trace at A26TP3) |

Confirmed against 5-14 step 4. Implemented as `Bands.UnleveledPotFor` / `Bands.LeveledPotFor`.

**Test points:** A24TP12 = SRD BIAS · A26TP3 = MOD LVL · A25TP2 = DET.

### A28 SYTM Driver pots

R6 OFST · R13 DRP · R19 DYS · R22 DYO · R24 BP2 FREQ · R25 BP3 FREQ · R27 BP2 · R28 BP3 ·
R82 GAIN · R88 KICK · R99 BP1 · R67 V/GHz (the 0.5 V/GHz rear-panel output).

**Jumpers A28W1 and A28W2 must be removed** to set the V/GHz circuit to 0.5 V/GHz (5-14 step 5).

### A26 Linear Modulator pots

R88 MO (modulator offset) · R7 BAL · R43 HET (band 0 ALC loop gain) · R45 X1 · R47 X2 · R49 X3 ·
R51 X4 (loop gain per band) · R91 GAIN.

## B.3 Adjustment 5-14 — Unleveled RF Output (pp. 5-71 to 5-88)

**Purpose:** make the SYTM passband track the YO for maximum power out, and set unleveled SRD
bias in the multiplying bands.

**Prerequisites the manual lists:** power supplies, 20/30 loop, M/N loop, pretune and sweep time
must be checked or adjusted first.

**Setup:** scope in A-vs-B (X = DUT rear-panel SWEEP OUTPUT 0–10 V, Y = crystal detector on the
RF output), DVM on the 0.5 V/GHz output. This is setup **S4** here, with the DS1104Z in XY mode.

Step numbers matter — Table 5-3 interdependence refers to them.

| Steps | What happens |
|---|---|
| 2–3 | Preset cal constants (below). |
| 4–6 | Pot presets — **only** if A13, A24, A28 or A26 were replaced. Also remove A28W1/W2. |
| 7 | 0.5 V/GHz: CW 10 MHz, adjust A28R67 for 5.0 ± 0.5 mV on the DVM. |
| 8–15 | **Band 2** (6.9–13.5 GHz, 200 ms sweep, `[XTAL]` selected with nothing connected = effectively unleveled): peak A28R82 GAIN across the band; X2A (first half) and X2B (second half) for maximum power, backing off 0.5 dB from squegging; A28R6 OFST and R82 GAIN for optimum; check A24TP12 shows a slight upward slope (adjust X2A/X2B by ≤ ⅛ turn); minimum power in the band must be **> 1 dB above max leveled power** — confirm with the power meter if marginal. |
| 16–22 | **Band 3** (13.35–20 GHz): A28R99 BP1 peaks the second half; X3A/X3B as above; CC73 (around 1024) peaks the whole band; min-power check. |
| 23–30 | **Band 4** (19.8–26.5 GHz): A28R24 BP2 FREQ, R25 BP3 FREQ, R27 BP2, R28 BP3 breakpoint pots, with the "if no effect, return to full CW" rules; X4A/X4B; CC74 around 1024; min-power check. |
| 31 | **SYTM auto tracking:** `[SHIFT][PEAK]` → "AUTO TRACKING CALIBRATION", wait, then INSTR PRESET. |
| 32–58 | **Delay compensation per band.** For each band save three states: slow sweep 200 ms (register 1), AUTO sweep time (register 2), single sweep (register 3). Fast and single traces must be within **1 dB** of the slow trace (**2 dB** for band 1), with minimum power still > spec + 1 dB. Band 4 uses A28R22 DYO (first half), A28R19 DYS (second half) and CC75 (0–500, fixes the small dropout at the start of the sweep). Band 3 uses CC7, falling back to DYO/DYS + CC8 if CC7 cannot peak > 60 % of the band. Band 2 uses CC6. Band 1 uses CC5, run with `[SHIFT][METER] −50 dBm` so band-1 squegging is absent. |
| 59–67 | **Multiband** (2.3–26.5 GHz, 1 s vs AUTO vs single): CC4 (band 4), CC3 (band 3), CC2 (band 2), then CC77 and CC78 (−25…+25) for the band-switch points — reset to −25 / +25 if no effect. Same limits, ignoring band-1 squegging. |
| 68–69 | CC1 = 500, adjust A28R13 DRP for optimum power over the full range, CC1 back to 50, then **store to protected memory**: `[SHIFT][MHz] 1 4 [Hz]`, `[SHIFT][kHz] 5 3 4 9 [Hz]`, wait for "CALIBRATION STORED", INSTR PRESET. |
| 70–80 | **Unleveled squegging test** — see below. |

### Cal-constant presets (steps 2–3)

CC2 = 100 (step 2). Then CC3–CC8 = 100 · CC9–CC12 = 1024 · CC50–CC53 = 0 · CC71–CC74 = 1024 ·
CC75 = 25 · CC76 = 1000 · CC77 = −25 · CC78 = +25 · CC80 = 0.

Delay-compensation constants have range 0–131. Access key sequence, after INSTR PRESET:
`[SHIFT][GHz] n [Hz]`, `[SHIFT][MHz] 1 2 [Hz]`, `[SHIFT][kHz] 2 2 [Hz]`. Subsequently just
`[SHIFT][GHz] n [Hz] [SHIFT][ENTRY OFF]`. Values are entered on the keypad or knob and terminated
with `[Hz]`.

### Unleveled squegging test (steps 70–80)

The manual mixes the DUT against a second 8340B as LO 600 MHz below and views the IF on an 8566B
(590–800 MHz, RBW 300 kHz, VBW 100 kHz, ref −10 dBm, 0 dB attenuation), with the DUT at requested
+20 dBm (UNLEVELED on), stepped in 100 MHz steps through band 2 (7–13.5), band 3 (13.5–20) and
band 4 (20–26.5). Mixing products appear as low-level signals; **squegging shows as a
higher-amplitude spurious response**.

**This bench does it directly on the 8563E** (9 kHz – 26.5 GHz), looking at the carrier ±300 MHz
with the same RBW and VBW. No LO, no mixer, no K281C high-pass. That removes the mixing products
the manual has to warn about, which is a simplification rather than a compromise.

Fixes: below 10 GHz adjust X2A slightly CCW, above 10 GHz X2B; band 3 splits at 15 GHz
(X3A/X3B); band 4 splits at 23 GHz (X4A/X4B). The manual's caveat — "if the control is adjusted
and there is no effect on the response, the response is probably a mixing product" — applies to
its own setup, not to the direct one.

## B.4 Adjustment 5-16 — Leveled RF Output (pp. 5-101 to 5-114)

| Steps | What happens |
|---|---|
| 1–3 | **Modulator offset.** DVM on A26TP3, A26R88 fully CW, CW 8 GHz, `[SHIFT][METER]` to bypass the ALC, knob for 0.00 V on the DVM, then A26R88 CCW to peak POWER dBm — or −0.2 dB if there is no peak. |
| 4–22 | **Leveled bias.** Scope CH A on A26TP3 MOD LVL, CH B on A24TP12 SRD BIAS, external trigger from the DUT, 5 ms/div. State: 7–13.5 GHz sweep, `[SHIFT][PWR SWP] −20 dBm`, 50 ms sweep → A24R12 MIN for the most-negative trace. Then power-sweep mode −20 to +20 dB with AM on at CW 8 GHz: OFF A (7 GHz end) and OFF B (13 GHz end) for the "square corner" SRD bias waveform; X2C at 10 GHz, X3C at 17.5 GHz, X4C at 23.3 GHz for a straight MOD LVL power-sweep trace with no step or bow. Then step every 200 MHz through band 2 (7–13.4), band 3 (13.5–19.9) and band 4 (20.1–26.5) re-checking — a compromise across each band is expected. |
| 23–29 | **Linear modulator ALC loop gain.** CH A A26TP3, CH B A25TP2 inverted, A+B; power sweep −20 → +20 dB with AM on. Adjust A26R43 HET (band 0, 10 MHz–2.21 GHz), R45 X1, R47 X2, R49 X3, R51 X4 for the flattest line, stepping CW through each band in 200 MHz steps. **Usually not needed unless A26 was replaced.** |
| 30–36 | **Leveled bias bands 2–4.** Detector + scope A-vs-B, AUTO sweep; vary the ALC level −20 → +20 dBm with the knob watching for squegging (spikes, oscillations). If it appears **at or below** maximum specified leveled power, turn X2C / X3C / X4C CCW; **above** maximum leveled power it is simply going unleveled and is not a fault. Then at ALC 0, −5, −10 and −15 dBm, sweep the start frequency up from 14 GHz watching for squegging (X3C in band 3, X4C in band 4). |
| 37–50 | **Leveled squegging test.** Same setup as 5-14 steps 70–80 — on this bench, the 8563E directly. DUT at maximum leveled power (just below UNLEVELED lighting), 100 MHz steps through each band, then repeated at every 5 dB down to −20 dBm. Fix with X2C/X3C/X4C slightly CCW. |

Extended status byte #2 bit 6 is the UNLEVELED lamp, so "just below UNLEVELED lighting" is
findable over the bus rather than by eye.

## B.5 The other adjustments, and interdependence

| | Adjustment | Note for this bench |
|---|---|---|
| 5-1 | +22 V supply (A35) | |
| 5-2 | 10 MHz standard (A51) | |
| 5-3 | 100 MHz VCXO (A30) | |
| 5-4 | M/N loop | |
| 5-5 | 20/30 loop PLL1/2/3 (A36/38/39/40/43) | Step 48 needs a 100 kΩ active probe. Closed by the 8903B — see [GEAR-MAP.md](GEAR-MAP.md). |
| 5-6 | YO pretune DAC (A54) | |
| 5-7 | YO main driver (A55) | |
| 5-8 | YO loop (A48/A49) | |
| 5-9 | FM accuracy / overmod | |
| 5-10 | YO delay compensation (A54) | |
| 5-11 | 3.7 GHz oscillator (A8) | |
| 5-12 | Marker / bandcross (A57) | |
| 5-13 | Sweep generator (A58) | |
| 5-14 | Unleveled RF output | The main event. |
| 5-15 | ALC adjustments | Logger temperature compensation only if A11/A12 replaced. ALC accuracy at 4.5 GHz and 1.5 GHz — the 432A/478A covers both. External leveling with a negative detector and CC48, A25R80 EX−. **Step 26 wants a positive detector, which is not on hand: that polarity check is skipped.** |
| 5-16 | Leveled RF output | |
| 5-17 | RF output power flatness | CC13, 39, 42, 43, 44. All steps (1.5, 2.4, 4.5 and 14 GHz) are within the 8902A + 11792A range. |
| 5-18 | Pulse adjustments (A21) | |
| 5-19 | External module leveling (A23) | **Not applicable** — no 8355x module. |

### Table 5-3 interdependence, the rows that matter

| Assembly replaced | Adjustments required |
|---|---|
| A13 SYTM | 5-14 and 5-16 |
| A24 Attenuator Driver / SRD Bias | 5-14 and 5-16 (A24 adjustments only) |
| A26 Linear Modulator | 5-16, 5-18 |
| A28 SYTM Driver | 5-14 and 5-16 |
| A44 YIG Oscillator | 5-7, 5-8, 5-9, 5-10, 5-14 steps 32–69 |
| A54 YO Pretune DAC | 5-6, 5-7, 5-10, 5-14, 5-16 |
| A63 90 dB attenuator | 5-17 and the automated attenuator calibration software |

These are hard constraints for the planner (M1-09).

## B.6 Section IV performance tests

| Test | Subject |
|---|---|
| 4-1 | Internal time base aging rate (24 h against the house standard) |
| 4-2 | Frequency range and CW accuracy |
| 4-3 | Sweep time accuracy |
| 4-4 | Swept frequency accuracy |
| 4-5 | Maximum leveled output power and power accuracy |
| 4-6 | External leveling |
| 4-7 | Spurious signals, 10 MHz – 22 GHz |
| 4-8 | Spurious signals, 22 – 26.5 GHz (the 8566B needs a K281C high-pass; the 8563E does not) |
| 4-9 | SSB phase noise (manual: two-source quadrature, LNA, HP 3585A) |
| 4-10 | Power sweep |
| 4-11 | Pulse on/off ratio |
| 4-12 | Pulse rise and fall time |
| 4-13 | Pulse modulation accuracy |
| 4-14 | Video feedthrough |
| 4-15 | AM |
| 4-16 | FM accuracy and flatness |
| 4-17 | HP-IB operation verification |

4-5 method: continuous sweep AUTO, single sweep AUTO, and single 2 s sweep; take the worst case,
put a marker at the minimum-power point, set the DUT to CW there and measure with a power meter.
Flatness is measured at 0 dBm and is primarily a function of the RF path, so it is essentially the
same at all ALC levels — but it does change when the step attenuator changes range.

HP's own 08340-10009 software automated CW frequency accuracy, power accuracy and flatness,
maximum leveled power, a phase-lock diagnostic, ALC accuracy, a cal-constant display utility and
attenuator calibration. **So reading cal constants over HP-IB is possible** — finding the
mechanism is issue M0-04.

## B.7 HP-IB

See [HPIB-8340B.md](HPIB-8340B.md) for the code table, both status bytes and what still needs
verifying against Section III.
