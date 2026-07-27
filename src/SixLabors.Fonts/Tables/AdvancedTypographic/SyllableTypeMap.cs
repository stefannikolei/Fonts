// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Maps state machine grammar rule names to <see cref="SyllableType"/> values. Invoked
/// when a shaper translates its machine's tag rows into a per-state table, so match
/// handling and per-glyph storage never touch the rule name strings.
/// </summary>
internal static class SyllableTypeMap
{
    /// <summary>
    /// Maps a grammar rule name to its <see cref="SyllableType"/>.
    /// </summary>
    /// <param name="tag">The rule name reported by the state machine match.</param>
    /// <returns>
    /// The corresponding type, or <see cref="SyllableType.Other"/> for a rule name with
    /// no dedicated member. Unknown names must not throw: an unrecognized rule name
    /// string previously compared false against every known literal, and the mapping
    /// preserves that behavior.
    /// </returns>
    public static SyllableType FromTag(string tag) => tag switch
    {
        "broken_cluster" => SyllableType.BrokenCluster,
        "consonant_syllable" => SyllableType.ConsonantSyllable,
        "independent_cluster" => SyllableType.IndependentCluster,
        "non_khmer_cluster" => SyllableType.NonIndicCluster,
        "number_joiner_terminated_cluster" => SyllableType.NumberJoinerTerminatedCluster,
        "numeral_cluster" => SyllableType.NumeralCluster,
        "standalone_cluster" => SyllableType.StandaloneCluster,
        "standard_cluster" => SyllableType.StandardCluster,
        "symbol_cluster" => SyllableType.SymbolCluster,
        "virama_terminated_cluster" => SyllableType.ViramaTerminatedCluster,
        "vowel_syllable" => SyllableType.VowelSyllable,
        _ => SyllableType.Other,
    };

    /// <summary>
    /// Builds the per-state syllable type table for a machine's tag rows, translated
    /// once at machine construction so match handling reads an array element instead
    /// of mapping a rule name string per match. States without a tag map to
    /// <see cref="SyllableType.None"/>.
    /// </summary>
    /// <param name="tags">The machine's per-state tag rows.</param>
    /// <returns>The per-state <see cref="SyllableType"/> table.</returns>
    public static SyllableType[] FromMachineTags(string[][] tags)
    {
        SyllableType[] types = new SyllableType[tags.Length];
        for (int i = 0; i < tags.Length; i++)
        {
            string[] row = tags[i];
            types[i] = row.Length > 0 ? FromTag(row[0]) : SyllableType.None;
        }

        return types;
    }
}
