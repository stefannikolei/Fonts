// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts;

/// <summary>
/// Defines modes to determine how to apply hinting. The use of mathematical instructions
/// to adjust the display of an outline font so that it lines up with a rasterized grid.
/// </summary>
public enum HintingMode
{
    /// <summary>
    /// Do not hint the glyphs.
    /// </summary>
    None,

    /// <summary>
    /// Hint the glyph using standard configuration.
    /// </summary>
    Standard,

    /// <summary>
    /// Hint the glyph using the complete horizontal and vertical instruction set.
    /// Glyph outlines and origins are aligned to the pixel grid on both axes, producing
    /// the sharpest results at small sizes at the cost of shape fidelity and up to half a
    /// pixel of inter-glyph spacing variation. Measured layout is unaffected.
    /// </summary>
    Full
}
