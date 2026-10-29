#!/bin/sh
# Skin maker regression run: builds every skin in cases.txt with an old and a new skintool and says, for each,
# whether it came out the same. A skin that changed gets a picture (rows: old, new, the fighter) and how much of
# the picture moved, with the stretch, thin and floor checks before and after.
#   regress.sh <old skintool> <new skintool> <fighters dir> <models dir> <work dir> [case name pattern]
# A skintool here is a command, e.g. "dotnet /path/skintool.dll". The fighters dir is Top-8er's
# assets/skins/fighters; the models dir holds the model files cases.txt names (Alex's project files:
# skins/tests/models). Builds are repeatable, so "same" is byte for byte; anything else is a real change.
# The summary goes to <work dir>/summary.txt, the pictures to <work dir>/<case>.png.
set -u
OLD=$1; NEW=$2; A=$(cd "$3" && pwd); M=$(cd "$4" && pwd); mkdir -p "$5"; W=$(cd "$5" && pwd); ONLY=${6:-}
CASES=$(dirname "$0")/cases.txt
JOBS=${JOBS:-2}

one() {
  name=$1; c=$2; model=$3; shift 3
  d=$(ls "$A/$c" | grep -m1 'Nr.dat')
  [ -f "$M/$model" ] || { echo "$name: skipped (no $model in the models folder)"; return; }
  for v in old new; do
    if [ $v = old ]; then bin=$OLD; else bin=$NEW; fi
    rm -rf "$W/$name-$v"
    (cd "$M" && $bin build --model "$M/$model" "$@" --fighter "$A/$c/Pl$c.dat" --dat "$A/$c/$d" --anims "$A/$c/Pl${c}AJ.dat" \
      --out "$W/$name-$v" > "$W/$name-$v.log" 2>&1) || { echo "$name: $v build FAILED (see $name-$v.log)"; return; }
  done
  if cmp -s "$W/$name-old/$d" "$W/$name-new/$d"; then
    echo "$name: same"
  else
    echo "$name: CHANGED; $($NEW compare "$W/$name.png" "$W/$name-old/$d" "$W/$name-new/$d" "$A/$c/Pl$c.dat" "$A/$c/Pl${c}AJ.dat" --original "$A/$c/$d" | tr '\n' ';' | sed 's/;$//; s/;/; /g')"
  fi
}

grep -v '^#' "$CASES" | grep -v '^ *$' | while read -r name rest; do
  case "$name" in *$ONLY*) echo "$name $rest";; esac
done > "$W/cases.run"

# each line is run in its own shell so the quoted options in cases.txt are split as written
: > "$W/summary.txt"
i=0
while read -r line; do
  ( eval "one $line" >> "$W/summary.txt" ) &
  i=$((i + 1))
  [ $((i % JOBS)) -eq 0 ] && wait
done < "$W/cases.run"
wait
sort -o "$W/summary.txt" "$W/summary.txt"
cat "$W/summary.txt"
