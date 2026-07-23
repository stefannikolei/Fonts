// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;

namespace SixLabors.Fonts;

/// <summary>
/// Pairs a glyph's metrics with its positioned state for layout: the post-positioning
/// advances and placement offset. Layout and rendering read positions from here so the
/// metrics instance itself can remain immutable and shared.
/// </summary>
internal readonly struct PositionedGlyphMetrics
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PositionedGlyphMetrics"/> struct.
    /// </summary>
    /// <param name="metrics">The glyph metrics.</param>
    /// <param name="advanceWidth">The horizontal advance after positioning.</param>
    /// <param name="advanceHeight">The vertical advance after positioning.</param>
    /// <param name="offset">The placement offset after positioning.</param>
    /// <param name="textRun">The text run the glyph belongs to.</param>
    public PositionedGlyphMetrics(FontGlyphMetrics metrics, ushort advanceWidth, ushort advanceHeight, Vector2 offset, TextRun textRun)
    {
        this.Metrics = metrics;
        this.AdvanceWidth = advanceWidth;
        this.AdvanceHeight = advanceHeight;
        this.Offset = offset;
        this.TextRun = textRun;
    }

    /// <summary>
    /// Gets the glyph metrics.
    /// </summary>
    public FontGlyphMetrics Metrics { get; }

    /// <summary>
    /// Gets the horizontal advance in font design units after positioning.
    /// </summary>
    public ushort AdvanceWidth { get; }

    /// <summary>
    /// Gets the vertical advance in font design units after positioning.
    /// </summary>
    public ushort AdvanceHeight { get; }

    /// <summary>
    /// Gets the placement offset in font design units after positioning.
    /// </summary>
    public Vector2 Offset { get; }

    /// <summary>
    /// Gets the text run the glyph belongs to, carried here so rendering reads it from
    /// the positioned state rather than from per-glyph metric clones.
    /// </summary>
    public TextRun TextRun { get; }
}
