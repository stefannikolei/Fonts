// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Unicode;

/// <summary>
/// The direction a script is written in when it is set horizontally.
/// </summary>
internal enum ScriptHorizontalDirection
{
    /// <summary>
    /// The script is written from left to right.
    /// </summary>
    LeftToRight,

    /// <summary>
    /// The script is written from right to left.
    /// </summary>
    RightToLeft,

    /// <summary>
    /// The script is written either way, so a run of it is left in the order it
    /// arrived rather than being turned around.
    /// </summary>
    Either
}
