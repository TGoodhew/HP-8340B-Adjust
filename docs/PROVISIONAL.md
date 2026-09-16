# Provisional numbers

Every threshold, tolerance and settle time in this project that **has never been checked against
an instrument**. They are marked in the source with the exact phrase

```
PROVISIONAL: never checked against an instrument.
```

and a test fails if this list and the source fall out of step, so neither can quietly drift from
the other.

## Why this file exists

A number that survives a few readings starts to look measured. Every figure below was chosen by
reasoning about what ought to work — from a manual, from arithmetic, or from judgement — and not
one of them has been put in front of an 8340B. That is a different kind of fact from "the 8470B's
output impedance is 1.3 kΩ", which came out of its manual, or "a 10 dB-down contribution is worth
0.46 dB", which is arithmetic.

The distinction matters most when something disagrees at the bench. If a self-check fails by
2.5 dB, the first question is whether the bench is wrong or whether `LevelToleranceDb` was always
too tight — and that question is only askable if it is written down that nobody knows.

## The list

| Where | Value | What would settle it |
|---|---|---|
| `SelfChecks.LevelToleranceDb` | 2.0 dB | Measure what the bench's own cables and adapters actually lose at 1 GHz, then set this to comfortably clear that but not a mis-declared pad. |
| `SelfChecks.FrequencyTolerance` | 1 ppm | Read the 5351A against the DUT with both on the Z3805A and see what the agreement really is. |
| `DetectorCalibrator.AcceptableRmsResidualDb` | 0.3 dB | Run a real detector calibration and look at the residual a good one gives. The simulated detector fits to 0.006 dB; a real one will be worse, and by how much is the number. |
| `BlockGate.AcknowledgementValidFor` | 2 hours | Whether this interrupts real work. If a campaign routinely runs longer between cable changes, lengthen it deliberately. |
| `Hp432A.ZeroValidFor` | 10 minutes | Directly measurable: zero the 432A, wait, and watch the drift. The right figure is how long it stays inside the reading's uncertainty. |
| `Hp437B.ZeroSettle` | 10 s | Time the 437B's zero routine. The manual gives no figure. |
| `SquegScanner.WarnDbc` | −40 dBc | The manual describes squegging as "a higher-amplitude spurious response" and gives **no number** — it is an eyeball test against a display. Scan a known-good 8340B and see what its worst non-harmonic response actually is; the threshold belongs a few dB above that. |
| `SquegScanner.StrongDbc` | −25 dBc | Same run. This one should sit where a response is unambiguously worth stopping to adjust rather than noting. |
| `MonotonicityTest.ReversalThresholdDb` | 0.2 dB | Ramp a known-good instrument and look at the scatter between adjacent 1 dB steps. The threshold has to clear that, or noise reads as power reversal. |

## Unsourced hardware claims

Different from the table above: these are not numbers I chose, they are figures I stated **about
instruments** without a manual to cite. They need confirming against a data sheet or the hardware
itself, not against a measurement.

| Where | Claim | Why it matters | What would settle it |
|---|---|---|---|
| `SwitchModule.RatingForSwitch` | 33311C reaches 26.5 GHz | **Decides whether a band-4 result may be reported as `Spec`.** | A 33311C data sheet, or the rating printed on the switch body. |
| `SwitchModule.RatingForSwitch` | 33311B reaches 18 GHz | Sets which legs are band-3/4 limited. | As above. |
| `SwitchModule.RatingForSwitch` | 44472A is good to ~100 MHz | Low impact — only DC and audio legs use it, nowhere near the limit. | The 44472A's own specification. The guide gives the 44478A/B as 1.3 GHz but says nothing about the 44472A. |
| `routing.json` `powerRatingNote` | 33311B/C are 1 W average | A leg carrying more than its rating degrades quietly. The 1 W figure in the 3499A guide is for the **44476A's own** switches, not for a 33311 mounted on a carrier. | A 33311 data sheet. |
| `BenchCapability.MaxHzFor` | U8485A reaches 33 GHz | Would let band 4 claim `Spec` on borrowed kit. | The U8485A's data sheet — not in the local library, and the sensor is not here either. |

These surface as `[unconfirmed rating]` on the hook-up card and a trailing `?` in
`route status`, so nothing prints them as bare fact.

**What the manual did settle**, and is therefore cited rather than listed here: the 44476A/B are
carrier modules whose "characteristics are determined by the switches and attenuators installed in
it" — which is why the frequency limit is declared per leg rather than inferred from the module,
and why `44476A` itself carries `Confirmed: true` from the guide's own DC-18 GHz figure.

## Deliberately not in this list

Some numbers look like guesses and are not:

- **`PnPassRule.BaselineMarginDb` = 10 dB** is a consequence of the arithmetic. A contribution
  10 dB down is 9% of the power, so removing it moves the answer by 0.46 dB and leaves the
  baseline's own uncertainty contributing a tenth of what it would at parity.
- **`Hp8902A.ZeroSettle` = 5 s** came from HP-Attenuator, where it is hardware-proven. It has met
  an instrument — just not through this code.
- **Everything from a manual** — the 478A's 200 Ω, the 8470B's 1.3 kΩ output impedance, the
  8116A's 6 ns transition time, every rule-5 refusal threshold. These are cited, not chosen.
- **`SimulatedSweeper`'s model parameters.** They describe a model, not the instrument. The model
  is pinned to published figures where any exist, and it is not pretending to be a measurement.

## After a bench session

Confirm a figure, and it moves out of the table above into the section below it, with the
measurement that settled it. Delete the `PROVISIONAL` marker in the source at the same time — the
test will fail until both sides agree.
