// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts;

/// <summary>
/// Identifies which phase of the shaping pass a <see cref="ShapingBuffer"/> serves.
/// Shapers use the role to run phase-specific work: syllable analysis and reordering
/// belong to substitution and must not run again during positioning.
/// </summary>
internal enum ShapingBufferRole
{
    /// <summary>
    /// The per-font-run workspace buffer glyphs are substituted in.
    /// </summary>
    Substitution,

    /// <summary>
    /// The accumulated result buffer glyphs are seeded and positioned in.
    /// </summary>
    Positioning,
}
