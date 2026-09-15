import json, io, sys, re

src, out = sys.argv[1], sys.argv[2]
data = json.load(io.open(src, encoding="utf-8"))

def node_id(name, seen):
    key = "n" + (re.sub(r"[^A-Za-z0-9]", "", name)[:24] or "X")
    n = key
    i = 1
    while n in seen and seen[n] != name:
        i += 1
        n = f"{key}{i}"
    seen[n] = name
    return n

L = []
w = L.append

w("# Bench setups")
w("")
w("Every measurement, test and guided procedure declares the `BenchSetup` it needs. When the")
w("planner changes setup it shows the hook-up card for the new one; `hp8340b setup show <id>`")
w("prints it on demand.")
w("")
w("**This file is generated from `src/HP8340B.Bench/Data/setups.json`** so the documentation and")
w("the app cannot drift apart. Edit the JSON, not this file. (Generation moves into the app in")
w("issue M5-06; for now it is a script.)")
w("")
w("Connector names marked **[verify-connector]** are placeholders taken from the service manual's")
w("figures. They have not yet been checked against Section III of the operating manual or against")
w("the instruments themselves.")
w("")

w("## Index")
w("")
w("| ID | Setup | Used by |")
w("|---|---|---|")
for s in data["setups"]:
    used = s["usedBy"]
    if len(used) > 90:
        used = used[:89] + "…"
    w(f"| [{s['id']}](#{s['id'].lower()}) | {s['name']} | {used} |")
w("")

for s in data["setups"]:
    w(f"## {s['id']}")
    w("")
    w(f"**{s['name']}**")
    w("")
    if s.get("manualFigure"):
        w(f"Manual: {s['manualFigure']}")
        w("")
    w(f"Used by: {s['usedBy']}")
    w("")

    # Mermaid diagram
    w("```mermaid")
    w("flowchart LR")
    seen = {}
    for i, c in enumerate(s["connections"]):
        a = node_id(c["from"], seen)
        b = node_id(c["to"], seen)
        w(f'  {a}["{c["from"]}"]')
        w(f'  {b}["{c["to"]}"]')
        label = " + ".join(c["via"]) if c["via"] else ""
        if label:
            w(f'  {a} -- "{label}" --> {b}')
        else:
            w(f"  {a} --> {b}")
    w("```")
    w("")

    w("### Connections")
    w("")
    for c in s["connections"]:
        parts = [c["from"]] + c["via"] + [c["to"]]
        flag = "  **[verify-connector]**" if c.get("verifyConnector") else ""
        w(f"- {' → '.join(parts)}{flag}")
    w("")

    if s.get("physicalSettings"):
        w("### Physical settings (cannot be set over the bus)")
        w("")
        for p in s["physicalSettings"]:
            w(f"- {p}")
        w("")

    if s.get("safetyLimits"):
        w("### Safety limits")
        w("")
        for p in s["safetyLimits"]:
            w(f"- {p}")
        w("")

    if s.get("selfCheck"):
        w("### Wiring self-check")
        w("")
        w(f"{s['selfCheck']['description']}")
        w("")
        w(f"**Expect:** {s['selfCheck']['expected']}")
        w("")

w("## Routed setups SR and SV")
w("")
w("The 3499A lets one physical build serve S1, S2, S3 and the detector leg of S4, so the planner")
w("treats those as one setup with switch changes as zero-cost, logged transitions.")
w("")
w("The RF tree (SR) and the LF multiplexers (SV) are **not yet in this data file** — defining them")
w("is issue M0-22, and the slot/channel map has to be confirmed against the actual module")
w("inventory first (decision issue M5-07). The rules they must follow:")
w("")
w("1. Legs through a 33311B are usable to 18 GHz; the two 33311C legs reach 26.5 GHz. In band 4")
w("   and 18–20 GHz the planner schedules direct-connection excursions for the 18 GHz legs. The")
w("   tool must refuse to report a band-4 result taken through an 18 GHz leg as `Spec`.")
w("2. Matrix calibration at the start of a campaign, and whenever a cable is moved: per leg,")
w("   measure the DUT direct against through-the-tree across a frequency grid with the sensor as")
w("   reference. Store loss against frequency with a date and an uncertainty. Stale calibration")
w("   blocks `Spec`-class results until redone.")
w("3. **Never hot-switch.** Set `RF0`, change the switch, confirm the state readback, then `RF1`.")
w("4. The hook-up cards list every leg with its cable label, module slot and channel numbers,")
w("   which legs are 18 GHz-limited, the DC limit of the microwave switches (no biased nodes")
w("   through the RF tree) and their 1 W average rating.")
w("5. Final 4-5 power-accuracy results may be taken direct as well as through the tree; when both")
w("   exist, the direct result is the one reported.")
w("6. Matrix calibration covers the RF legs; the SV legs need only a continuity and offset check")
w("   at campaign start.")

io.open(out, "w", encoding="utf-8", newline="\n").write("\n".join(L) + "\n")
print("wrote", out, len(L), "lines")
