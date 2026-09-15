# Manual

**Everything else in this directory is git-ignored.** No manual is redistributed here
(repo rule 7).

## The local library

Vendor manuals live at `C:\Users\Tony\OneDrive\Documents\Manuals`. Check there before searching
the web. Most files are PDFs named by model number. Coverage for this project:

| Instrument | File | Text layer? |
|---|---|---|
| **8340B operating** (Section III, Tables 3-1/3-2) | `8340b User.pdf` | **Yes** — this is the one that matters |
| 8340B service, assembly level | `HP8340B/08340-90243.pdf` | Yes, 530 pp. |
| 8340B service (Sections IV & V) | `HP8340B/08340-90243CH16.pdf` | Yes — byte-identical to the copy this project downloaded |
| 8340B operating, scanned | `HP8340B/HP 8340B, 41B Operating.pdf` | No — image only, 401 pp. |
| 8340B calibration | `HP8340B/08340-90336-cal-v2.pdf` | — |
| 3499A switch/control | `3499A User & Programming.pdf` | Yes — covers the 44472A module |
| 3458A | `3458A/3458A Multimeter User's Guide .pdf` | — |
| 8902A | `8902A Operation & Calibration.pdf` and several service volumes | — |
| 11792A sensor module | `11792A--Operations_Guide.pdf`, `HP 11792A Operating & Service.pdf` | — |
| 11793A converter | `11793A.pdf` | — |
| 432A power meter | `432A-OSM.pdf` | — |
| 478A thermistor mount | `478A-ON.pdf` | — |
| 437B power meter | `437B-UM.pdf`, `437B-SM.pdf` | — |
| 8481A sensor | `8481A.pdf`, `8481A_SM.pdf` | — |
| 8673B | `8673B User.pdf`, `8673B Service.pdf` | — |
| 8116A pulse generator | `8116A-OSM-002.pdf` and option variants | — |
| 8903B audio analyzer | `8903b.pdf`, `8903B Service Manual.pdf` | — |
| 5351A counter | `5351A.pdf` | — |
| 3325B | `3325B-OM.pdf`, `3325B-SM.pdf` | — |
| E4438C | `E4438C/E4438C SCPI Command Reference.pdf` and others | — |
| 11667A splitter | `11667A.pdf` | — |
| 11713A switch driver | `11713A-OSM.pdf` | — |
| 8470B detector | `8470B.pdf` | — |
| 7090A plotter | `7090A-OM.pdf`, `7090A-SM.pdf` | — |

**Not in the library** — find these elsewhere when their issues come up: **8563E**,
**DS1104Z**, **8473C** and **8472B** detectors, **8485A**/**U8485A**, **DG1032Z**, **K486A**.
The 44472A is covered inside the 3499A manual rather than separately.

## Recreating the text extracts

1. Copy the service manual Volume 1 here as `8340B-Service-Manual-Vol1.pdf` (or symlink
   `HP8340B/08340-90243CH16.pdf` from the library — it is the same file).
2. `pdftotext -layout 8340B-Service-Manual-Vol1.pdf full-text.txt`
3. `bash extract.sh`

That produces the section slices the documentation cites:

| File | Contents |
|---|---|
| `sec-5-14-unleveled.txt` | 5-14 Unleveled RF Output Adjustments, pp. 5-71 to 5-88 |
| `sec-5-15-alc.txt` | 5-15 ALC Adjustments, pp. 5-89 onward |
| `sec-5-16-leveled.txt` | 5-16 Leveled RF Output Adjustments, pp. 5-101 to 5-114 |
| `sec-5-17-flatness.txt` | 5-17 RF Output Power Flatness |
| `table-4-2-equipment.txt` | Table 4-2, recommended test equipment |
| `table-5-3-interdep.txt` | Table 5-3, adjustment interdependence |

For the operating manual: `pdftotext -layout "8340b User.pdf" op-full-text.txt`.

## Extraction quality — read this before transcribing any table

The text layer is good for prose and treacherous for tables.

- **`-layout` interleaves multi-column tables.** Wherever a cell wraps, every row after it drifts
  against its neighbours. This is silent and the result looks perfectly plausible.
- **`-raw` reads column-at-a-time**, which fixes side-by-side columns but splits a table into
  separate code and operation lists that have to be paired by index.
- **Numerals in the older scanned tables are OCR-damaged** — `2-32` for `2.32`, `24.55'` for
  `24.55`.

So: **read every table in both modes and reconcile them**, or read it off the page image. Where
prose elsewhere in the manual cites the same fact, use that as the tiebreak — it is how Table 3-2
was verified (see [../HPIB-8340B.md](../HPIB-8340B.md)).

The corrections log in [../ADJUSTMENT-BRIEF.md](../ADJUSTMENT-BRIEF.md) records which tables have
been checked this way and which have not.
