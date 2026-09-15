#!/bin/bash
# Slices named sections out of full-text.txt for reference in later passes.
# Page headings are preceded by a form feed, hence the \f? in every anchor.
# Everything here is git-ignored (repo rule 7: no manual excerpts in the repo).
F=full-text.txt
slice() {  # slice <outfile> <start-regex> <end-regex>
  local s e
  s=$(grep -nP "$2" "$F" | head -1 | cut -d: -f1)
  if [ -z "$s" ]; then echo "MISS start: $1 ($2)"; return; fi
  e=$(awk -v s="$s" 'NR>s' "$F" | grep -nP "$3" | head -1 | cut -d: -f1)
  if [ -z "$e" ]; then e=$(wc -l < "$F"); else e=$((s+e)); fi
  sed -n "${s},${e}p" "$F" > "$1"
  echo "$1: lines $s-$e ($((e-s)) lines)"
}
slice sec-5-14-unleveled.txt  '^\f?5-14\. UNLEVELED RF OUTPUT ADJUSTMENTS'  '^\f?5-15\. ALC ADJUSTMENTS'
slice sec-5-15-alc.txt        '^\f?5-15\. ALC ADJUSTMENTS'                  '^\f?5-16\. LEVELED RF'
slice sec-5-16-leveled.txt    '^\f?5-16\. LEVELED RF'                       '^\f?5-17\. RF OUTPUT'
slice sec-5-17-flatness.txt   '^\f?5-17\. RF OUTPUT'                        '^\f?5-18\.'
slice sec-4-performance.txt   '^\f?4-1\. INTRODUCTION'                      '^\f?SECTION V'
slice table-4-2-equipment.txt 'Table 4-2\.'                                 '^\f?4-[0-9]+\. '
slice table-5-3-interdep.txt  'Table 5-3\.'                                 '^\f?5-1\. '
