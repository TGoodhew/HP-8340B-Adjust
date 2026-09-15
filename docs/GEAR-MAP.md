# Gear map — this bench against Table 4-2

What the manual's procedures call for, what is actually here, and what that does to each test's
traceability class.

**Traceability classes:** `Spec` meets the manual's requirement · `Relative` deltas are valid but
it is not an absolute reference · `Typical` indicative only · `NotPossible` cannot be done with
what is here.

## Table 4-2 requirement against what is here

| Manual needs | On hand | Verdict |
|---|---|---|
| Spectrum analyzer 0.01–22 GHz (8566B) + K281C HPF for 22–26.5 | **HP 8563E**, 9 kHz – 26.5 GHz, GPIB 18 | **Better.** Covers every band directly, no filter. Replaces the LO + mixer squegging test: look at the carrier ±300 MHz, RBW 300 kHz / VBW 100 kHz, and use zero span for time-domain relaxation oscillation. Also the swept-power envelope viewer (max hold while the DUT sweeps). Has video out and ext trigger. |
| Frequency counter to DUT max (5343A) | **HP 5351A**, 26.5 GHz, GPIB 14 | Covered. Lock DUT and counter to the same 10 MHz. |
| 10 MHz frequency standard (5061A) | **Z3805A GPSDO** (plus a BG7TBL GPSDO) | Covered. Distributed through a Symmetricom distribution amplifier to the 8340B ext ref, 8563E, 5351A, 8673B, 3325B and E4438C. Only some of these can be *confirmed* over the bus — see below. |
| DVM (3456A) | **HP 3458A** | Covered, overkill. |
| Oscilloscope, 50 Ω input, A-vs-B | **Rigol DS1104Z** (LAN) | XY mode covered. Input is 1 MΩ only, so a 50 Ω feedthrough (HP 10100C, itself in Table 4-2) goes in front of the detector. Trace readout over SCPI drives the live XY view. |
| Crystal detector, negative, to 26.5 GHz (8473C) | **HP 8473C** (3.5 mm, 26.5 GHz), **8470B** (N, 18 GHz), **8472B** (SMA, 18 GHz) | Covered. The 8473C is the manual's own choice. **No positive-polarity detector** — only 5-15 step 26 wants one, and that step is skipped. |
| Power meter + 8481A / 8485A sensors (436A) | **HP 437B + HP 8481A + HP 8485A** — exactly the sensors Table 4-2 asks for; plus **HP 8902A + 11792A** (50 MHz – 18 GHz, HP-IB) and **HP 432A + 478A** thermistor (10 MHz – 10 GHz) | **Fully covered across the entire instrument, and better than the manual's 436A.** The 8481A (10 MHz – 18 GHz) and 8485A (to 26.5 GHz) on the HP-IB 437B span the whole 10 MHz – 26.5 GHz range as `Spec`, automatable. The 11792A is a second automatable path below 18 GHz and the thermistor a third, independent one below 10 GHz. |
| Second sweeper as LO + mixer 0955-0307 | **HP 8673B** (2–26 GHz), **Agilent E4438C** (≤ 6 GHz, very low phase noise) | Not needed for squegging (8563E direct). Needed for the 4-9 quadrature method and 4-16 FM flatness, where the missing part is a broadband mixer. |
| Modulation analyzer (8901A/8902A) | **HP 8902A** | Covered. |
| Function generator (3325A) | **HP 3325B**, GPIB 10 | Covered. |
| Pulse generator ≤ 100 ns width, ≤ 10 ns rise (8012B) | **HP 8116A**; **Rigol DG1032Z** as a second source | Covered: 4-11 to 4-14 and 5-18 are all possible. **Verify the 8116A transition-time and minimum-width figures against its own manual before trusting the rise/fall test.** |
| Attenuators 10/20 dB to 26.5 GHz (8493C) | **8494G + 8496G** programmable, DC–18 GHz, via 11713A | Fine to 18 GHz, and the tool can characterise the pad at each test frequency against the 478A (≤ 10 GHz) or the 8902A. **Gap above 18 GHz.** |
| LF FFT analyzer 20 Hz – 40 MHz (3585A), LNA, mixer, notch | **8563E + 85671A Phase Noise Utility**, and KE5FX **PN.EXE** | 4-9 by the direct (composite) method instead — see below. |
| Power supply 0–50 V (6294A) | 8116A / DG1032Z DC output, monitored by the 3458A | Covered for the 5.00 V and ±0.3 V points. |
| Directional coupler, power splitter 11667B | **HP 11667A** splitter (DC–18 GHz); no coupler | 4-6 and 5-15 steps 21–34 covered to 18 GHz. The coupler is only for 5-19, which is not applicable. |
| Plotter/printer for test records | **HP 7090A**, GPIB 6 | Nice-to-have. HP-GL plots of envelopes and flatness, reusing GpibMcp's forwarding path. |

### Not in Table 4-2, but on this bench

**Agilent 3499A** switch/control mainframe with two **33311C** SPDT coaxial switches (DC–26.5 GHz),
several **33311B** (DC–18 GHz) on 44476A/44476B modules, and a **44472A** dual 4-channel VHF
switch. This collapses setups S1–S4 (and most of S6) into one routed build where switch changes
are zero-cost logged transitions rather than re-cabling. Path loss and match must be characterised
and subtracted (M1-10).

**HP 8903B** audio analyzer. Its 100 kΩ input closes the 5-5 step 48 active-probe gap — see below.

### Which instruments can prove they are on the Z3805A

Everything on the bench is fed from the Z3805A through a Symmetricom distribution amplifier, so
in practice they are all locked. But "in practice" is not a measurement, and a distribution
amplifier output that has come loose looks exactly like one that has not. `probe` therefore
reports what each instrument can actually *prove*, not what the cabinet looks like:

| Instrument | Confirmable over the bus? | How |
|---|---|---|
| **8340B** (DUT) | Yes | Extended status byte #2 bit 3, external frequency reference selected. |
| **8673B** | Yes | Extended status bit 3 (switch in EXT) **and** bit 4 clear (phase locked). Bit 3 alone is set even with no reference connected, so both are required. |
| **8563E** | Yes | `FREF?` (programming guide p. 477) returns INT or EXT. Watch for this one: `IP` presets FREF back to **INT**, so an analyzer preset mid-session silently drops it off the house reference. |
| **5351A** | **No** | The counter switches to an external reference automatically and lights EXT REF, but no command in the manual's list reads that state back. `probe` reports it as unknown and asks for a front-panel check. 4-2 is meaningless if this one is wrong. |
| **3325B**, **E4438C** | Not yet implemented | Their drivers are M0-15. |

One caveat that no status bit covers: the 8673B manual (paragraph 3-11) warns that an external
reference below the specified 0.1–1 V rms "may be sufficient to turn off the ɸ UNLOCKED status
annunciator, giving a false indication of normal operation. In fact, the phase noise of the
Signal Generator may be degraded." Since the 8673B is the down-conversion LO for 4-9, its
reference *level* is worth confirming once at the rear panel, not just its lock state.

## How 4-9 phase noise is done here

The manual's two-source quadrature method needs an LNA, a mixer and a 3585A. None of those are
here. Instead PN.EXE measures the DUT directly at the manual's test frequencies and offsets
(30 Hz to 100 kHz are all within the 8563E narrow-RBW reach) and exports dBc/Hz against offset.

The result is DUT **plus** analyzer noise, so the pass rule is deliberately conservative:

- Composite **below** the spec limit → **pass** (`Spec`). It cannot be anything but a pass, since
  the true DUT noise is lower still.
- Composite **above** the limit → **inconclusive**, unless the 8563E baseline at that frequency is
  ≥ 10 dB lower, in which case subtract it in power and re-judge.

Baseline the 8563E with the E4438C (a far cleaner source) up to its frequency limit, and use PN's
shipped 8560-series baseline plots above that. The analyzer LO noise degrades by 20·log(N) in its
higher harmonic bands, so **bands 3–4 of the DUT at 10 kHz offset will often be inconclusive** —
that is a known and accepted limit, not a bug.

The 85671A card is for quick checks at the bench; PN.EXE produces the records.

## How 5-5 step 48 is done here

Step 48 wants a 100 kΩ-in / 50 Ω-out active probe (1121A class) to probe the output of the 40 kHz
low-pass on A36 at 20 kHz and 50 kHz while nulling A36L7/L8 to ≥ 65 dB down.

The **8903B** closes it: its analyzer input is 100 kΩ like the 1121A, it reads true-RMS level in
dB relative to a stored reference, the 80 kHz low-pass keeps 50 kHz while rejecting HF noise, and
it is on HP-IB. The manual applies the two tones one at a time, so a broadband level meter is
valid, and reading below the −65 dB target proves the null conservatively.

Set the 20 kHz reference to about 1 V rms at the node so −65 dB (0.56 mV) sits 25–30 dB above the
8903B noise floor.

**Caveat:** 300 pF of input capacitance plus cable is far more than the 1121A few pF, on a passive
LC filter node. Use a very short lead and check whether adding about 10 pF at the pickup moves the
null. If it does, put the home-made JFET buffer (1 MΩ ‖ 2 pF in, unity gain, AC-coupled, driving
100 kΩ so no back-termination) between the node and the 8903B. A resistive Z0 probe is the wrong
tool here.

## Gaps, in priority order

| Gap | Blocks | Plan |
|---|---|---|
| **Broadband DBM, 2–26.5 GHz** | 4-16 FM flatness at 50 kHz–10 MHz rates; the manual's exact 4-12/4-13 down-conversion; optionally PN.EXE external-conversion mode | Band 0/1 up to the E4438C limit: any cheap DBM with the E4438C as LO. For 4-12/4-13 the 8473C into a 50 Ω-terminated DS1104Z channel is the alternative envelope method. Low priority. |
| **Positive-polarity detector** | 5-15 step 26 only | Skip the step. |
| **3.5 mm cable and 10/20 dB pads to 26.5 GHz** | Band-4 work in front of anything but the 8563E | On the borrow list. |

**Closed by what is on hand:** **absolute power across the entire 10 MHz – 26.5 GHz range
(437B + 8481A + 8485A)** · pulse generator (8116A, DG1032Z spare) · 300–400 MHz source for 5-5
(E4438C) · low-voltage PSU (8116A/DG1032Z DC + 3458A) · splitter to 18 GHz (11667A) · phase noise
at 30 Hz–100 kHz offsets by the direct method · a full set of 8340B PC-board extenders · 50 Ω
feedthroughs · SMB service cables · 10 MHz distribution · adapters.

The absolute-power gap above 18 GHz was the project's top-priority gap. It is closed: the 437B is
on HP-IB and the two sensors between them span the whole instrument, so 4-5 in band 4, the
18–20 GHz stretch of band 3, and 5-14 step 30 are all `Spec` with nothing borrowed.

## Borrowed kit

Every item is optional and driven from config. When a flag is on, the affected measurements
upgrade their traceability class automatically and the session records the borrowed serial
numbers.

**Power sensors are no longer on this list.** The resident 8481A and 8485A on the 437B already
cover 10 MHz – 26.5 GHz as `Spec`, so nothing needs borrowing to complete the power measurements.
What remains is passive 26.5 GHz plumbing.

| Part | Upgrades | Still needed? |
|---|---|---|
| **11667B** splitter, DC–26.5 GHz | 4-6 external leveling in band 4 — the resident 11667A stops at 18 GHz | **Yes.** The one real remaining gap. |
| **8493C** Opt 010 / 020 pads, DC–26.5 GHz | Pads for band 4 in front of the sensor, counter and 8902A | **Yes**, unless a 26.5 GHz pad is bought. |
| **11500-series** 3.5 mm cable | Band-4 connections | **Yes.** |
| **U8485A** USB thermocouple sensor, DC/10 MHz – 33 GHz | Was the route to band-4 `Spec` | **No longer needed** — the resident 8485A does this. Useful only as an independent cross-check, and it would need a USBTMC driver written from scratch. |

**Not available:** E5056A signal source analyzer, N9030B/N9020B X-series analyzer, any borrowed
signal generator or analyzer. The affected tests keep the classes computed without them — 4-9 is
a conservative pass via PN.EXE/85671A, 4-16 flatness is partial.

The U2040 X-series USB peak sensors are **not** a substitute for 4-12: 5 MHz video bandwidth and
pulse-parameter analysis only for rise times ≥ 100 ns.

## Not needed from Table 4-2

K281C high-pass (the 8563E reaches 26.5 GHz) · a second 8340B as LO (8673B / E4438C) · 8481A
sensor (11792A + 478A cover 10 MHz – 18 GHz) · 5316A universal counter (the DS1104Z measures the
4-3 STOP SWEEP interval) · signature analyzer · HP 236 computer.

## Primary measurement paths

1. **DS1104Z + 8473C** — the real-time swept-power display used while turning pots.
2. **8563E** — spectrum, squegging detection, zero span, and a slower but detector-independent
   swept envelope.
3. **5351A** — frequency.
4. **Absolute power — `Spec` across the whole instrument.** **437B + 8481A** (10 MHz – 18 GHz) and
   **437B + 8485A** (to 26.5 GHz) together cover 10 MHz – 26.5 GHz, automatable over HP-IB. The
   **8902A + 11792A** (50 MHz – 18 GHz) is a second automatable path and the **432A/478A**
   thermistor a third, independent one below 10 GHz on a different measurement principle.
   8902A/11793A tuned RF level is no longer needed for absolute power and drops to a cross-check.
5. **3458A** — DC test points and the 0.5 V/GHz output.
6. **8116A / DG1032Z** — pulse and modulation drive. **E4438C** as a clean VHF/UHF source, low-band
   LO and phase-noise baseline source.
7. **8563E + PN.EXE** (records) or the **85671A** card (bench checks) — phase noise.
