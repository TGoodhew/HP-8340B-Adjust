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
