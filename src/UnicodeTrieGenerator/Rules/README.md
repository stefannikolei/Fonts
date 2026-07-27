Files sourced from:

https://www.unicode.org/Public/17.0.0/ucd/
https://www.unicode.org/Public/17.0.0/ucd/auxiliary/
https://www.unicode.org/Public/17.0.0/ucd/emoji/
https://www.unicode.org/Public/emoji/latest/
https://github.com/microsoft/font-tools

## Universal Shaping Engine override data

Three of the files are not part of the Unicode Character Database. They are the
Universal Shaping Engine override data published by Microsoft under the MIT
licence at https://github.com/microsoft/font-tools, and they carry values the
UCD does not derive:

| File | Contents |
| ---- | -------- |
| `IndicSyllabicCategory-Additional.txt` | Overrides for `Indic_Syllabic_Category` |
| `IndicPositionalCategory-Additional.txt` | Overrides for `Indic_Positional_Category` |
| `IndicShapingInvalidCluster.txt` | Character sequences that spell one vowel but read as another |

The two override files are versioned by the Unicode release they were revised
for, stated in their own headers, and currently stand at Unicode 16.0. They are
applied on top of the Unicode Character Database files above, so both sets are
updated together: raising the UCD version without taking the matching override
revision silently changes the categories the shapers see.

## Shaping state machine rules

The `.machine` files translate HarfBuzz's Ragel shaping state machines into the
syntax consumed by this repository's state-machine generator. Their categories,
expressions, and accepting-rule order come from the corresponding sources in the
pinned HarfBuzz test submodule; only the grammar syntax is different.

| File | Source |
| ---- | ------ |
| `indic.machine` | [`tests/harfbuzz/src/hb-ot-shaper-indic-machine.rl`](../../../tests/harfbuzz/src/hb-ot-shaper-indic-machine.rl) |
| `khmer.machine` | [`tests/harfbuzz/src/hb-ot-shaper-khmer-machine.rl`](../../../tests/harfbuzz/src/hb-ot-shaper-khmer-machine.rl) |
| `myanmar.machine` | [`tests/harfbuzz/src/hb-ot-shaper-myanmar-machine.rl`](../../../tests/harfbuzz/src/hb-ot-shaper-myanmar-machine.rl) |
| `use.machine` | [`tests/harfbuzz/src/hb-ot-shaper-use-machine.rl`](../../../tests/harfbuzz/src/hb-ot-shaper-use-machine.rl) |
