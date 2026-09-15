# HP-8340B-Adjust — conventions

Tooling for adjusting and verifying an HP 8340B Synthesized Sweeper (10 MHz – 26.5 GHz)
against the HP 8340B/41B Operating & Service Manual, Sections IV and V.

## The rules that are not negotiable

1. **Never write to the 8340B without explicit per-action confirmation in the conversation.**
   That covers calibration constants, "store to protected memory", and learn-string input.
   Never in a background loop. Reads are fine. Log every write with timestamp, old and new value.
2. **Never fabricate an HP-IB code.** Every mnemonic lives in `Hp8340BCommands.All` with a
   citation. Unverified codes throw in `Require()` rather than reaching the instrument, and
   `docs/HPIB-8340B.md` carries the same table. Verify against Tables 3-1/3-2 of the
   **operating** manual, not the service manual.
3. **Every measurement records its traceability class** (`Spec`, `Relative`, `Typical`,
   `NotPossible`) and the instrument settings used, in the session JSON.
4. **Simulators are first-class.** `dotnet test` must pass with no hardware. Hardware-only work
   is tagged `needs-dut` and gated on `probe` succeeding.
5. **Power limits are enforced in code, not by memory.** Warn before anything that could put
   more than +10 dBm into the 8563E or 5351A without a pad, and before connecting the DUT
   unleveled at +20 dBm to anything. Refuse outright above +7 dBm with the 478A selected
   (10 mW mount) and above +17 dBm with the 11792A or an 848x sensor, unless a characterised
   pad is declared in config. Pad values are config and are subtracted from results.
6. **Match the other repos:** Spectre.Console CLI, small focused commits, issue references in
   commit messages (`Fixes #n`), close issues with a comment saying how it was tested (sim or
   hardware).
7. **No manual excerpts in the repo.** `docs/manual/` and `sessions/` are git-ignored. This
   repo is public.

## Stack

- **.NET 10** (`net10.0-windows`), nullable enabled, x64. This diverges from HP-Attenuator's
  net472: that target exists because of the Kelary.Ivi.Visa .NET Framework build, and NI
  VISA.NET 26.0 now ships a .NET 6.0 vendor assembly, so modern .NET resolves the installed
  provider. `-windows` is for the ScottPlot WinForms live views.
- **Spectre.Console 0.49.1** for the CLI, as in HP-Attenuator.
- **IviFoundation.Visa 8.0.2** for the vendor-neutral VISA API.
- **xunit** for tests.

## Layout

| Project | What lives there |
|---|---|
| `HP8340B.Instruments` | VISA transport, `IInstrument`, one driver per instrument, simulators, bench config |
| `HP8340B.Measurements` | Band and power-spec models, envelope, squegging scan, monotonicity, spurs |
| `HP8340B.Bench` | `BenchSetup` data, hook-up cards, wiring self-checks, the planner |
| `HP8340B.Procedures` | Guided step runners for 5-14, 5-16 and the Section IV tests |
| `HP8340B.Reports` | JSON/CSV/Markdown/HP-GL output |
| `HP8340B.Live` | Live plot windows. **Blocked on decision issue M2-01** — no package reference until that is answered |
| `HP8340B.Cli` | Spectre.Console entry point |

## Running

```
dotnet run --project src/HP8340B.Cli -- probe --sim      # no hardware needed
dotnet run --project src/HP8340B.Cli -- probe            # live bench
dotnet run --project src/HP8340B.Cli -- setup show S4    # hook-up card
dotnet run --project src/HP8340B.Cli -- codes            # HP-IB code table
dotnet test                                              # must pass with no hardware
```

Addresses live in `bench.json`. A git-ignored `bench.local.json` beside it overlays entries by
role, so real addresses never have to be committed.

## Writing code here

- Match the surrounding comment density: these drivers are read at a bench, months later, with
  a manual open. Say *why* a magic number is what it is and cite the manual page.
- The 8340B predates IEEE 488.2. There is no `*IDN?` and no `*OPC?`. Settling is the status
  byte (bit 3 RF settled, bit 4 end of sweep) or SRQ — see `Hp8340BStatus.cs`.
- Anything `SKELETON` in a doc comment is a stub waiting on its own issue. Do not build on it
  without reading that issue first.
