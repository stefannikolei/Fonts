// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.TrueType.Hinting;

/// <summary>
/// Describes the outcome of instruction based hinting for a glyph outline, used to decide
/// which axes the geometric grid fitter may process afterwards.
/// </summary>
internal enum TrueTypeHintingResult
{
    /// <summary>
    /// No interpreter executed for the glyph.
    /// </summary>
    None,

    /// <summary>
    /// Instructions were present but were not applied.
    /// </summary>
    Failed,

    /// <summary>
    /// The font's control value program inhibited grid fitting at this size. This is a
    /// deliberate instruction from the font that its outlines render better untouched,
    /// not a failure, so no geometric fitting substitutes for it.
    /// </summary>
    Inhibited,

    /// <summary>
    /// Instructions were applied and touched outline points on the Y axis only.
    /// </summary>
    AppliedY,

    /// <summary>
    /// Instructions were applied and touched outline points on the X axis.
    /// </summary>
    AppliedXY,
}
