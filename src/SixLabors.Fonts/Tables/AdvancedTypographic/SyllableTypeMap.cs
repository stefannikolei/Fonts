// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Maps state machine grammar rule names to <see cref="SyllableType"/> values. Invoked
/// once per matched syllable at the state machine boundary, so per-glyph storage and
/// comparison never touch the rule name strings.
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
        "number_joiner_terminated_cluster" => SyllableType.NumberJoinerTerminatedCluster,
        "numeral_cluster" => SyllableType.NumeralCluster,
        "standalone_cluster" => SyllableType.StandaloneCluster,
        "standard_cluster" => SyllableType.StandardCluster,
        "symbol_cluster" => SyllableType.SymbolCluster,
        "virama_terminated_cluster" => SyllableType.ViramaTerminatedCluster,
        "vowel_syllable" => SyllableType.VowelSyllable,
        _ => SyllableType.Other,
    };
}
