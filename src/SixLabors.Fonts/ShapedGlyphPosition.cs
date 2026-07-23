// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;

namespace SixLabors.Fonts;

/// <summary>
/// The geometry half of a shaped glyph: pure numbers in font design units with no
/// object references. Identity lives in the parallel info array.
/// </summary>
internal readonly struct ShapedGlyphPosition
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShapedGlyphPosition"/> struct.
    /// </summary>
    /// <param name="advanceWidth">The positioned horizontal advance.</param>
    /// <param name="advanceHeight">The positioned vertical advance.</param>
    /// <param name="offset">The placement offset written by positioning.</param>
    /// <param name="bearing">The glyph-origin bearing offset copied from the glyph metrics.</param>
    public ShapedGlyphPosition(ushort advanceWidth, ushort advanceHeight, Vector2 offset, Vector2 bearing)
    {
        this.AdvanceWidth = advanceWidth;
        this.AdvanceHeight = advanceHeight;
        this.Offset = offset;
        this.Bearing = bearing;
    }

    /// <summary>
    /// Gets the positioned horizontal advance in font design units.
    /// </summary>
    public ushort AdvanceWidth { get; }

    /// <summary>
    /// Gets the positioned vertical advance in font design units.
    /// </summary>
    public ushort AdvanceHeight { get; }

    /// <summary>
    /// Gets the placement offset written by positioning, in font design units.
    /// </summary>
    public Vector2 Offset { get; }

    /// <summary>
    /// Gets the glyph-origin bearing offset in font design units, copied out of the glyph metrics so projection needs no metrics lookup.
    /// </summary>
    public Vector2 Bearing { get; }
}
