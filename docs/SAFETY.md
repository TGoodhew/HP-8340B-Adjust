# Safety

Two kinds of hazard here: the instrument can hurt you, and you can hurt the instrument. Both
sections matter.

## The manual's warnings

These come from Section V of the HP 8340B/41B Operating & Service Manual and apply to every
adjustment in this project.

- **Adjustments are made with the covers off and power on.** The chassis is live. Treat every
  exposed surface as energised until proven otherwise.
- **The LINE switch has no OFF position.** Standby still applies power to much of the
  instrument. Pulling the plug is the only way to be sure.
- **Allow one hour of warm-up** before any adjustment or performance test. (5-14 step 1 allows
  half an hour for that procedure alone; the general instruction is one hour.)
- **Use non-metallic adjustment tools.** A metal trimmer tool detunes what it touches and can
  short adjacent pins.
- **Do not force slug-tuned components.** They break, and the replacement parts are not
  available.
- **ESD.** The RF-section assemblies carry static-sensitive devices. Wrist strap, grounded mat.

## Calibration constants

The "store to protected memory" sequence overwrites the protected copy of the calibration
constants. The printed cal-constant card under the top cover is the last resort if that copy is
lost and the working copy is wrong.

**Therefore: back up over HP-IB before touching anything.** `backup-cal` (M1-01) runs in setup
S10 and is the first block of every campaign.

Repo rule 1 makes this structural: nothing in this project writes to the 8340B — cal constants,
store-to-protected, or learn-string input — without an explicit confirmation for that specific
action, and never in a background loop. Every write is logged with a timestamp and the old and
new value.

## Power limits

These are enforced in code, not left to memory (repo rule 5).

| Rule | Limit | Why |
|---|---|---|
| **Refuse** | DUT above **+7 dBm** while the 478A thermistor mount is the selected sensor | The 478A is rated 10 mW. Exceeding it destroys the mount. |
| **Refuse** | DUT above **+17 dBm** while the 11792A or an 848x sensor is selected | Sensor damage level. |
| **Warn** | Anything that could put more than **+10 dBm** into the 8563E or the 5351A without a pad | Analyzer and counter input damage. |
| **Warn** | Connecting the DUT **unleveled at +20 dBm** to anything | 5-14 steps 70–80 request +20 dBm unleveled. Nothing on this bench except a pad and the detector is happy with that. |

Each refusal is lifted only by declaring a **characterised** pad in config — characterised
meaning measured at the test frequency, not assumed from the label. The pad value is then
subtracted from results automatically.

Two more, from the setups:

- **The 8563E input DC limit is 0 V.** Use a DC block when probing any node carrying bias.
- **Never route a biased test point through the 3499A RF tree.** The microwave switches have a
  DC limit far below the internal nodes, and a 1 W average rating.

## Bus etiquette

- **Never drive the 8563E from this tool while PN.EXE or the 85671A Phase Noise Utility is
  measuring.** Two controllers on one analyzer corrupts both. The `pn-handoff` command (M0-16)
  closes this tool's VISA session before handing over, and re-probes afterwards.
- **Never hot-switch the 3499A.** The tool sets `RF0` on the DUT, changes the switch state,
  confirms the state readback, then `RF1`.

## What this project will not do

It will not tell you which way to turn a pot and then claim the instrument is adjusted. Every
pot adjustment is yours; the tool measures, shows you the number, and records what changed.
