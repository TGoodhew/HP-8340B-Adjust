# HP-8340B-Adjust

Bench tooling for adjusting and verifying an **HP 8340B Synthesized Sweeper** (10 MHz – 26.5 GHz)
against Sections IV and V of the HP 8340B/41B Operating & Service Manual.

The manual's adjustment procedures were written around an HP 1741A analogue oscilloscope, a
crystal detector, an HP 436A power meter and an HP 8566B spectrum analyzer, with a technician
eyeballing traces while turning pots. The hard part is SYTM (Switched YIG-Tuned Multiplier)
tracking and step-recovery-diode bias, where the failure mode is **squegging** — undesired
oscillation of the YIG sphere or the SRD, showing up as power reversal, power holes and spurious
responses.

This project replaces the eyeball with measurement.

## What it does

1. **Protects the instrument.** Backs up every calibration constant and the instrument state over
   HP-IB before anything is touched; diffs; restores.
2. **Baselines** the sweeper before adjustment — power against frequency per band, leveled and
   unleveled, squegging map, harmonics and spurs, frequency accuracy — so before and after is
   objective rather than remembered.
3. **Replaces scope-and-eyeball feedback** during pot adjustment with live quantitative displays:
   a swept power envelope per band showing the minimum-power point and its margin against spec, a
   point tuning meter for finding the squegging edge and backing off 0.5 dB, and a
   spectrum-based squegging alarm.
4. **Automates the software-only adjustments.** Several adjustments are calibration constants
   entered from the front panel — delay compensation, tracking offsets, band-switch points. Those
   are set over HP-IB and optimised by measurement.
5. **Verifies** the instrument afterwards by re-implementing the Section IV performance tests with
   the equipment actually on this bench, producing a test record in the manual's format, and being
   explicit about which tests are fully to spec, which are relative, and which are not possible.
6. **Says how to wire the bench.** Every procedure declares the physical setup it needs, and every
   setup change shows a hook-up card: each connection from DUT port to instrument port with
   adapters, pads and cable labels, the switches and dials that cannot be set over the bus, the
   safety limits, and a wiring self-check that proves the connections before any measurement runs.
7. **Minimises reconfiguration.** The planner groups everything sharing a physical setup and runs
   it before the setup changes, subject to the manual's precedence rules.

## Status

Skeleton. The solution builds, the test suite passes with no hardware, and `--sim` runs end to
end against simulated instruments. Measurement and procedure code is tracked in the M0–M5
milestones.

## Running

```
dotnet run --project src/HP8340B.Cli -- probe --sim      # no hardware needed
dotnet run --project src/HP8340B.Cli -- probe            # live bench
dotnet run --project src/HP8340B.Cli -- setup list       # the bench setups
dotnet run --project src/HP8340B.Cli -- setup show S4    # one hook-up card
dotnet run --project src/HP8340B.Cli -- codes            # HP-IB code table
```

Requires the .NET 10 SDK. Live hardware runs additionally need NI-VISA; `--sim` does not.

Instrument addresses live in [bench.json](bench.json). Put real addresses in a git-ignored
`bench.local.json` beside it if you would rather not commit them.

## Documentation

| Document | What it covers |
|---|---|
| [docs/ADJUSTMENT-BRIEF.md](docs/ADJUSTMENT-BRIEF.md) | The 5-14 and 5-16 procedures distilled, with page references and a changelog of corrections |
| [docs/GEAR-MAP.md](docs/GEAR-MAP.md) | This bench against the manual's Table 4-2, and what each test's traceability class works out to |
| [docs/SAFETY.md](docs/SAFETY.md) | The manual's warnings and this project's own hard limits |
| [docs/HPIB-8340B.md](docs/HPIB-8340B.md) | The HP-IB command table, with a Verified/Unverified column |
| [docs/SETUPS.md](docs/SETUPS.md) | The bench setups S0–S10 with a diagram per setup |
| [CLAUDE.md](CLAUDE.md) | Conventions, and the rules that are not negotiable |

## A note on the manual

`docs/manual/` is git-ignored. The service manual is not redistributed here. To regenerate the
text extracts this project refers to, put your own copy of the Volume 1 PDF in `docs/manual/` as
`8340B-Service-Manual-Vol1.pdf` and run `docs/manual/extract.sh`.

## Licence

MIT. See [LICENSE](LICENSE).
