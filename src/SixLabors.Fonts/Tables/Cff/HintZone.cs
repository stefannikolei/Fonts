// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// One alignment zone from a font's declarative hinting values, in design units. A bottom
/// zone aligns the bottoms of features such as the baseline and descenders and carries its
/// flat edge on top, where round overshoots meet flat bottoms; a top zone aligns feature
/// tops and carries its flat edge on the bottom.
/// </summary>
internal readonly struct HintZone
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HintZone"/> struct.
    /// </summary>
    /// <param name="bottom">The lower edge of the zone band.</param>
    /// <param name="top">The upper edge of the zone band.</param>
    /// <param name="flat">The flat edge shared by round and flat features once overshoots are suppressed.</param>
    /// <param name="isBottom">Whether the zone aligns feature bottoms; otherwise it aligns feature tops.</param>
    public HintZone(float bottom, float top, float flat, bool isBottom)
    {
        this.Bottom = bottom;
        this.Top = top;
        this.Flat = flat;
        this.IsBottom = isBottom;
    }

    /// <summary>
    /// Gets the lower edge of the zone band.
    /// </summary>
    public float Bottom { get; }

    /// <summary>
    /// Gets the upper edge of the zone band.
    /// </summary>
    public float Top { get; }

    /// <summary>
    /// Gets the flat edge shared by round and flat features once overshoots are suppressed.
    /// </summary>
    public float Flat { get; }

    /// <summary>
    /// Gets a value indicating whether the zone aligns feature bottoms.
    /// </summary>
    public bool IsBottom { get; }
}
