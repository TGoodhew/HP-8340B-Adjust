import json, io, sys

# Table 4-8, Swept Frequency Accuracy Test Frequencies, manual p. 4-24.
# Transcribed from the rendered page image (pdftoppm -r 200). The text layer damages the
# numerals in this table (2-32 for 2.32, 24.55' for 24.55), so it cannot be used.
#
# Every row starts at 2.3 GHz. Columns: stop GHz, sweep time ms, RBW kHz, test limit kHz.
# The three centre-frequency columns in the manual are exactly start + {0.2, 0.5, 0.8} * span,
# verified against every printed row, so they are computed rather than transcribed - which also
# makes them a check on the stop frequencies.
ROWS = [
    # stop,        sweep_ms, rbw_khz, limit_khz
    (2.300099,     3000,  0.3,     0.99),
    (2.300101,     3000,  0.3,     1.01),
    (2.300499,     1000,  1.0,     4.99),
    (2.300501,     1000,  1.0,     5.01),
    (2.30499,       300,  3.0,    49.9),
    (2.30501,       300,  3.0,   100.02),
    (2.31,          300,  3.0,   200.0),
    (2.32,          100, 10.0,   400.0),
    (2.33,          100, 10.0,   600.0),
    (2.34,          100, 30.0,   800.0),
    (2.349,         100, 30.0,   998.0),
    (2.3501,        100, 30.0,  1020.0),
    (2.36,          100, 30.0,  1200.0),
    (2.37,          100, 30.0,  1400.0),
    (2.38,          100, 30.0,  1600.0),
    (2.39,          100, 30.0,  1800.0),
    (2.3999,        100, 30.0,  1980.0),
    (2.4001,        100, 100.0, 2002.0),
    (2.799,         100, 1000.0, 4990.0),
    (2.801,         100, 1000.0, 5010.0),
    (7.29,          100, 3000.0, 49900.0),
    (7.31,          100, 3000.0, 50000.0),
    (8.3,           100, 3000.0, 50000.0),
    (16.452,        100, 3000.0, 50000.0),
    (20.0,          100, 3000.0, 50000.0),
    (24.55,         100, 3000.0, 50000.0),   # 8340B only
    (26.5,          100, 3000.0, 50000.0),   # 8340B only
]

START = 2.3

rows = []
for stop, sweep_ms, rbw, limit in ROWS:
    span = stop - START
    rows.append({
        "startGHz": START,
        "stopGHz": stop,
        "sweepTimeMs": sweep_ms,
        "resolutionBandwidthKHz": rbw,
        "testLimitKHz": limit,
        # start + fraction * span, verified against the printed centre-frequency columns.
        "centreFrequencyGHz": {
            "20": round(START + 0.2 * span, 9),
            "50": round(START + 0.5 * span, 9),
            "80": round(START + 0.8 * span, 9),
        },
        "spanMHz": round(span * 1000.0, 6),
        # Stop frequencies above 20.0 GHz apply to the 8340B only (manual step 7 and note 1).
        "band4Only": stop > 20.0,
    })

data = {
    "source": "HP 8340B/41B service manual, Table 4-8, Swept Frequency Accuracy Test "
              "Frequencies, p. 4-24. Transcribed from the rendered page image, not the text "
              "layer, which damages the numerals in this table.",
    "note": "Centre frequencies are computed as start + {0.2, 0.5, 0.8} * span and were verified "
            "against every printed row. The manual misprints the 80%-of-band value on row 1 as "
            "2.300792; start + 0.8 * span gives 2.3000792, which is what the other 26 rows' "
            "pattern requires.",
    "limits": "The printed test limits ARE the specification. They do not follow one percentage "
              "across the table, so do not compute them: below a 5 MHz span they are 1% of span, "
              "from 5 MHz to about 100 MHz they are 2%, from about 500 MHz they return to 1%, and "
              "for the widest spans they are capped at 50 MHz absolute. Within the 2% group, rows "
              "11 and 12 read about 2.04% and row 17 about 1.98% - all three are transcribed as "
              "printed rather than rounded to the pattern.",
    "design": "The stop frequencies come in pairs that bracket span boundaries at 100 kHz, "
              "500 kHz, 5 MHz, 50 MHz, 100 MHz, 500 MHz and 5 GHz, so the test exercises the "
              "instrument either side of each point where its behaviour changes. That is why "
              "consecutive rows look like near-duplicates.",
    "rows": rows,
}

out = sys.argv[1]
with io.open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump(data, f, indent=2)
    f.write("\n")
print("wrote", out, len(rows), "rows")
