// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// Identifies one drawing command in a buffered CFF outline.
/// </summary>
internal enum CffOutlineVerb : byte
{
    /// <summary>
    /// Starts a new contour at one point.
    /// </summary>
    Move,

    /// <summary>
    /// Draws a line to one point.
    /// </summary>
    Line,

    /// <summary>
    /// Draws a cubic curve through two control points to an end point.
    /// </summary>
    Cubic,
}
