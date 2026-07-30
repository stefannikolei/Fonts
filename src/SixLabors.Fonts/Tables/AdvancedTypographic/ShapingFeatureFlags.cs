// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Per-feature registration flags controlling how the feature's lookups treat
/// joiners and syllable boundaries during sequence matching.
/// </summary>
[Flags]
internal enum ShapingFeatureFlags : byte
{
    /// <summary>
    /// No flags: the feature's lookups skip the zero width joiner automatically,
    /// match the zero width non-joiner, and match across syllables.
    /// </summary>
    None = 0,

    /// <summary>
    /// The feature's lookups match the zero width non-joiner themselves instead of
    /// the matcher treating it as transparent during context matching.
    /// </summary>
    ManualZwnj = 1 << 0,

    /// <summary>
    /// The feature's lookups match the zero width joiner themselves instead of the
    /// matcher treating it as transparent.
    /// </summary>
    ManualZwj = 1 << 1,

    /// <summary>
    /// The feature's lookups match both joiners themselves.
    /// </summary>
    ManualJoiners = ManualZwnj | ManualZwj,

    /// <summary>
    /// The feature's lookups never match across syllable boundaries: matching
    /// latches the syllable at the cursor and refuses records of any other.
    /// </summary>
    PerSyllable = 1 << 2,

    /// <summary>
    /// Alternate substitutions select from their set using the shaping buffer's
    /// deterministic random sequence.
    /// </summary>
    Random = 1 << 3,
}
