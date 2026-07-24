// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// The syllable cluster types produced by the Indic, Myanmar, and Universal Shaping
/// Engine state machines. Stored per glyph as a value so syllable classification is
/// integer comparison; the values mirror the top-level grammar rule names.
/// </summary>
internal enum SyllableType
{
    /// <summary>
    /// No syllable information has been assigned.
    /// </summary>
    None = 0,

    /// <summary>
    /// A grammar rule name with no dedicated member. Comparisons against known types
    /// are false, matching the behavior of an unrecognized rule name string.
    /// </summary>
    Other,

    /// <summary>
    /// The broken_cluster rule: a cluster missing its base, repaired with a dotted circle.
    /// </summary>
    BrokenCluster,

    /// <summary>
    /// The consonant_syllable rule.
    /// </summary>
    ConsonantSyllable,

    /// <summary>
    /// The independent_cluster rule.
    /// </summary>
    IndependentCluster,

    /// <summary>
    /// The synthetic type for codepoints outside every syllable match. Assigned by the
    /// shapers, not by a grammar rule.
    /// </summary>
    NonIndicCluster,

    /// <summary>
    /// The number_joiner_terminated_cluster rule.
    /// </summary>
    NumberJoinerTerminatedCluster,

    /// <summary>
    /// The numeral_cluster rule.
    /// </summary>
    NumeralCluster,

    /// <summary>
    /// The standalone_cluster rule.
    /// </summary>
    StandaloneCluster,

    /// <summary>
    /// The standard_cluster rule.
    /// </summary>
    StandardCluster,

    /// <summary>
    /// The symbol_cluster rule.
    /// </summary>
    SymbolCluster,

    /// <summary>
    /// The virama_terminated_cluster rule.
    /// </summary>
    ViramaTerminatedCluster,

    /// <summary>
    /// The vowel_syllable rule.
    /// </summary>
    VowelSyllable,
}
