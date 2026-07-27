// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;

namespace SixLabors.Fonts;

/// <summary>
/// Represents a single shaped glyph: the result of substitution and positioning,
/// before line breaking or scaling.
/// </summary>
/// <remarks>
/// Advances and offsets are expressed in font design units for the font the run was
/// shaped against; multiply by the font size over
/// <see cref="FontMetrics.UnitsPerEm"/> to convert to pixel units.
/// </remarks>
public readonly struct ShapedGlyph
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShapedGlyph"/> struct.
    /// </summary>
    /// <param name="glyphId">The glyph identifier within the font.</param>
    /// <param name="codePointIndex">The index of the character the glyph came from.</param>
    /// <param name="advanceWidth">The horizontal advance in font design units.</param>
    /// <param name="advanceHeight">The vertical advance in font design units.</param>
    /// <param name="offset">The placement offset in font design units.</param>
    internal ShapedGlyph(
        ushort glyphId,
        int codePointIndex,
        ushort advanceWidth,
        ushort advanceHeight,
        Vector2 offset)
    {
        this.GlyphId = glyphId;
        this.CodePointIndex = codePointIndex;
        this.AdvanceWidth = advanceWidth;
        this.AdvanceHeight = advanceHeight;
        this.Offset = offset;
    }

    /// <summary>
    /// Gets the glyph identifier within the font the run was shaped against. Glyph
    /// id 0 is the font's missing glyph, produced when the font cannot map the
    /// character.
    /// </summary>
    public ushort GlyphId { get; }

    /// <summary>
    /// Gets the index into the run's text of the first character this glyph came
    /// from.
    /// </summary>
    /// <remarks>
    /// Glyphs that came from the same characters carry the same value: several
    /// characters that merged into one glyph, and several glyphs that came from one
    /// character, are both read from the values repeating.
    /// </remarks>
    public int CodePointIndex { get; }

    /// <summary>
    /// Gets the horizontal advance in font design units, after positioning features
    /// have been applied.
    /// </summary>
    public ushort AdvanceWidth { get; }

    /// <summary>
    /// Gets the vertical advance in font design units, after positioning features
    /// have been applied.
    /// </summary>
    public ushort AdvanceHeight { get; }

    /// <summary>
    /// Gets the placement offset in font design units, in Y-up font space. The
    /// offset positions the glyph outline relative to its pen position and does not
    /// contribute to the advance.
    /// </summary>
    public Vector2 Offset { get; }
}
