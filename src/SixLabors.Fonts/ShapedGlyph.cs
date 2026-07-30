// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;

namespace SixLabors.Fonts;

/// <summary>
/// Represents a single shaped glyph: the result of substitution and positioning,
/// before line breaking.
/// </summary>
/// <remarks>
/// Advances and offsets are scaled to the size of the font the run was shaped
/// against and preserve the shaper's Y-up coordinate system.
/// </remarks>
public readonly struct ShapedGlyph
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShapedGlyph"/> struct.
    /// </summary>
    /// <param name="glyphId">The glyph identifier within the font.</param>
    /// <param name="stringIndex">The index of the first input <see cref="char"/> represented by the glyph.</param>
    /// <param name="graphemeIndex">The grapheme index represented by the glyph.</param>
    /// <param name="advanceWidth">The scaled horizontal advance.</param>
    /// <param name="advanceHeight">The scaled vertical advance.</param>
    /// <param name="offset">The scaled placement offset.</param>
    internal ShapedGlyph(ushort glyphId, int stringIndex, int graphemeIndex, float advanceWidth, float advanceHeight, Vector2 offset)
    {
        this.GlyphId = glyphId;
        this.StringIndex = stringIndex;
        this.GraphemeIndex = graphemeIndex;
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
    /// Gets the zero-based index of the first input <see cref="char"/> represented
    /// by this glyph.
    /// </summary>
    /// <remarks>
    /// Glyphs produced from the same input chars share this value.
    /// </remarks>
    public int StringIndex { get; }

    /// <summary>
    /// Gets the zero-based grapheme index in the input text.
    /// </summary>
    public int GraphemeIndex { get; }

    /// <summary>
    /// Gets the scaled horizontal advance after positioning features have been
    /// applied.
    /// </summary>
    public float AdvanceWidth { get; }

    /// <summary>
    /// Gets the scaled vertical advance after positioning features have been
    /// applied.
    /// </summary>
    public float AdvanceHeight { get; }

    /// <summary>
    /// Gets the scaled placement offset in Y-up shaping space. The offset positions
    /// the glyph outline relative to its pen position and does not contribute to the
    /// advance.
    /// </summary>
    public Vector2 Offset { get; }
}
