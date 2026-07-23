// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts;

/// <summary>
/// Represents a single shaped glyph: the result of substitution and positioning, before
/// line breaking, visual reordering, or scaling.
/// </summary>
/// <remarks>
/// Advances and offsets are expressed in font design units for the glyph's
/// <see cref="Font"/>; multiply by the font size over
/// <see cref="FontMetrics.UnitsPerEm"/> to convert to pixel units.
/// </remarks>
public readonly struct ShapedGlyph
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShapedGlyph"/> struct.
    /// </summary>
    /// <param name="font">The font face that resolved the glyph.</param>
    /// <param name="glyphId">The glyph identifier within the font face.</param>
    /// <param name="codePoint">The leading codepoint the glyph represents.</param>
    /// <param name="codePointIndex">The codepoint index into the source text.</param>
    /// <param name="codePointCount">The number of codepoints the glyph represents.</param>
    /// <param name="advanceWidth">The horizontal advance in font design units.</param>
    /// <param name="advanceHeight">The vertical advance in font design units.</param>
    /// <param name="offset">The placement offset in font design units.</param>
    internal ShapedGlyph(
        Font font,
        ushort glyphId,
        CodePoint codePoint,
        int codePointIndex,
        int codePointCount,
        ushort advanceWidth,
        ushort advanceHeight,
        Vector2 offset)
    {
        this.Font = font;
        this.GlyphId = glyphId;
        this.CodePoint = codePoint;
        this.CodePointIndex = codePointIndex;
        this.CodePointCount = codePointCount;
        this.AdvanceWidth = advanceWidth;
        this.AdvanceHeight = advanceHeight;
        this.Offset = offset;
    }

    /// <summary>
    /// Gets the font face that resolved the glyph: the primary font, a text run override,
    /// or a fallback font.
    /// </summary>
    public Font Font { get; }

    /// <summary>
    /// Gets the glyph identifier within <see cref="Font"/>. Glyph id 0 is the font's
    /// missing glyph, produced when the font cannot map the codepoint.
    /// </summary>
    public ushort GlyphId { get; }

    /// <summary>
    /// Gets the leading codepoint the glyph represents: the first codepoint for
    /// ligatures.
    /// </summary>
    public CodePoint CodePoint { get; }

    /// <summary>
    /// Gets the codepoint index into the source text of the first codepoint this glyph
    /// represents, following the <see cref="TextRun.Start"/> indexing convention.
    /// </summary>
    /// <remarks>
    /// Ligatures emit one glyph indexed at the first codepoint with
    /// <see cref="CodePointCount"/> covering the rest; marks and decompositions emit
    /// multiple glyphs sharing one index.
    /// </remarks>
    public int CodePointIndex { get; }

    /// <summary>
    /// Gets the number of codepoints the glyph represents.
    /// </summary>
    public int CodePointCount { get; }

    /// <summary>
    /// Gets the horizontal advance in font design units, after positioning features have
    /// been applied.
    /// </summary>
    public ushort AdvanceWidth { get; }

    /// <summary>
    /// Gets the vertical advance in font design units, after positioning features have
    /// been applied.
    /// </summary>
    public ushort AdvanceHeight { get; }

    /// <summary>
    /// Gets the placement offset in font design units, in Y-up font space. The offset
    /// positions the glyph outline relative to its pen position and does not contribute
    /// to the advance.
    /// </summary>
    public Vector2 Offset { get; }
}
