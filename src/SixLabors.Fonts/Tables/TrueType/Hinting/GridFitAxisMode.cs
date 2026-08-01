// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.TrueType.Hinting;

/// <summary>
/// Selects how the geometric grid fitter treats one axis of a glyph outline.
/// </summary>
internal enum GridFitAxisMode
{
    /// <summary>
    /// The axis is left untouched.
    /// </summary>
    None,

    /// <summary>
    /// Stems are detected, normalized to whole pixel widths and snapped to the grid, and
    /// remaining points are interpolated. Used for axes the font's instructions left
    /// unfitted.
    /// </summary>
    Full,

    /// <summary>
    /// Only strokes thinner than a pixel are widened to one pixel on grid boundaries so
    /// they cannot vanish under coverage sampling. Everything the instructions already
    /// fitted is preserved. This substitutes for the dropout control that classic bi-level
    /// rasterizers apply during scan conversion.
    /// </summary>
    Rescue,
}
