# Manual

**Everything else in this directory is git-ignored.** The HP 8340B/41B service manual is not
redistributed here (repo rule 7).

To recreate the text extracts this project refers to:

1. Put your own copy of the Operating & Service Manual, Volume 1 in this directory as
   `8340B-Service-Manual-Vol1.pdf`. A text-layer PDF has been available at
   `https://www.abex.co.uk/esales/test/hp/signal_generator/8340b/2624a00110_s02800/Manual.pdf`
   (327 pages, about 13 MB).
2. Run `pdftotext -layout 8340B-Service-Manual-Vol1.pdf full-text.txt`.
3. Run `bash extract.sh`.

That produces the section slices the documentation cites:

| File | Contents |
|---|---|
| `sec-5-14-unleveled.txt` | 5-14 Unleveled RF Output Adjustments, pp. 5-71 to 5-88 |
| `sec-5-15-alc.txt` | 5-15 ALC Adjustments, pp. 5-89 onward |
| `sec-5-16-leveled.txt` | 5-16 Leveled RF Output Adjustments, pp. 5-101 to 5-114 |
| `sec-5-17-flatness.txt` | 5-17 RF Output Power Flatness |
| `table-4-2-equipment.txt` | Table 4-2, recommended test equipment |
| `table-5-3-interdep.txt` | Table 5-3, adjustment interdependence |

## A note on extraction quality

The text layer is good for prose and poor for tables. Two-column tables interleave their rows
under `pdftotext -layout`, and `-raw` reads them column-at-a-time instead. Numerals in the older
scanned tables come through with OCR damage (`2-32` for `2.32`).

Where a table matters — cal-constant presets, power specifications — **read it in both modes and
reconcile them**, or read it off the page image. The corrections log in
[../ADJUSTMENT-BRIEF.md](../ADJUSTMENT-BRIEF.md) records which tables have been checked this way
and which have not.

## Section III

The HP-IB programming codes (Tables 3-1 and 3-2) are in the **operating** manual, not this one.
That document is not yet on this machine, which is why so much of
[../HPIB-8340B.md](../HPIB-8340B.md) is marked Unverified.
