// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using SixLabors.Fonts.Tables.AdvancedTypographic;

namespace SixLabors.Fonts;

/// <summary>
/// Encapsulates logic for shaping text into a positioned glyph stream.
/// </summary>
/// <remarks>
/// <para>
/// The text and its properties are set on a <see cref="TextShapingBuffer"/>, the
/// buffer is shaped against a font, and the glyphs are read back from it. Shaping
/// applies the font's substitution and positioning features but does not wrap the
/// text.
/// </para>
/// <para>
/// <see cref="Shape(Font, TextShapingBuffer)"/> treats the text as unwrapped
/// logical lines separated by hard breaks and resolves mixed-direction text within
/// each line. <see cref="ShapeRun(Font, TextShapingBuffer)"/> shapes one directional
/// run that has already been selected by the caller.
/// </para>
/// <para>
/// Advances and offsets are scaled to the supplied font's size.
/// </para>
/// </remarks>
public static partial class TextShaper
{
    /// <summary>
    /// Shapes the buffer's text as unwrapped logical lines separated by hard
    /// breaks, replacing the buffer's glyphs with each line's visually ordered
    /// glyphs.
    /// </summary>
    /// <param name="font">The font to shape against.</param>
    /// <param name="buffer">The buffer holding the line, which receives the glyphs.</param>
    public static void Shape(Font font, TextShapingBuffer buffer)
    {
        Guard.NotNull(font, nameof(font));
        Guard.NotNull(buffer, nameof(buffer));

        ShapeIntoBuffer(font, buffer, [], TextBidiMode.Normal);
    }

    /// <summary>
    /// Shapes the buffer's text as unwrapped logical lines separated by hard breaks
    /// with the given features turned on, replacing the buffer's glyphs with each
    /// line's visually ordered glyphs.
    /// </summary>
    /// <param name="font">The font to shape against.</param>
    /// <param name="buffer">The buffer holding the line, which receives the glyphs.</param>
    /// <param name="features">The feature tags to turn on for the line.</param>
    public static void Shape(Font font, TextShapingBuffer buffer, Tag[] features)
    {
        Guard.NotNull(font, nameof(font));
        Guard.NotNull(buffer, nameof(buffer));
        Guard.NotNull(features, nameof(features));

        ShapeIntoBuffer(font, buffer, features, TextBidiMode.Normal);
    }

    /// <summary>
    /// Shapes the buffer's text as one directional run, replacing the buffer's
    /// glyphs with the glyphs in reading order.
    /// </summary>
    /// <param name="font">The font to shape against.</param>
    /// <param name="buffer">The buffer holding the run, which receives the glyphs.</param>
    public static void ShapeRun(Font font, TextShapingBuffer buffer)
    {
        Guard.NotNull(font, nameof(font));
        Guard.NotNull(buffer, nameof(buffer));

        ShapeIntoBuffer(font, buffer, [], TextBidiMode.Override);
    }

    /// <summary>
    /// Shapes the buffer's text as one directional run with the given features
    /// turned on, replacing the buffer's glyphs with the glyphs in reading order.
    /// </summary>
    /// <param name="font">The font to shape against.</param>
    /// <param name="buffer">The buffer holding the run, which receives the glyphs.</param>
    /// <param name="features">The feature tags to turn on for the run.</param>
    public static void ShapeRun(Font font, TextShapingBuffer buffer, Tag[] features)
    {
        Guard.NotNull(font, nameof(font));
        Guard.NotNull(buffer, nameof(buffer));
        Guard.NotNull(features, nameof(features));

        ShapeIntoBuffer(font, buffer, features, TextBidiMode.Override);
    }

    /// <summary>
    /// Shapes the buffer under the selected bidirectional contract and publishes
    /// the resulting glyphs.
    /// </summary>
    /// <param name="font">The font to shape against.</param>
    /// <param name="buffer">The buffer holding the text and receiving the glyphs.</param>
    /// <param name="features">The feature tags to turn on.</param>
    /// <param name="bidiMode">Whether the text is a logical line or one directional run.</param>
    private static void ShapeIntoBuffer(Font font, TextShapingBuffer buffer, Tag[] features, TextBidiMode bidiMode)
    {
        if (buffer.Text.IsEmpty)
        {
            _ = buffer.Reserve(0);
            buffer.Commit(0);
            return;
        }

        ShapingScratch scratch = ScratchPool.Get();
        try
        {
            TextOptions options = scratch.GetShapingOptions(font, buffer.Direction, buffer.Language, buffer.Script, features, bidiMode);
            ShapingBuffer shaped = ShapeCore(buffer.Text, options, scratch, null);
            Span<int> lineEnds = default;

            if (bidiMode == TextBidiMode.Normal)
            {
                // ShapeCore deliberately leaves positioned records in logical order
                // because layout cannot choose visual order until line breaking. This
                // API has no soft wrapping, so each newline function fixes a complete
                // line on which the shared L2 transformation can run immediately.
                ReadOnlySpan<int> paragraphEnds = scratch.BidiData.ParagraphEnds;
                lineEnds = buffer.ReserveLineEnds(paragraphEnds.Length);
                int glyphStart = 0;
                for (int paragraph = 0; paragraph <= paragraphEnds.Length; paragraph++)
                {
                    int codePointEnd = paragraph < paragraphEnds.Length ? paragraphEnds[paragraph] : scratch.BidiData.Length;
                    int glyphEnd = glyphStart;
                    while (glyphEnd < shaped.Count && shaped[glyphEnd].CodePointIndex < codePointEnd)
                    {
                        glyphEnd++;
                    }

                    BidiReordering.Reorder(shaped, scratch.BidiRuns, scratch.BidiMap, glyphStart, glyphEnd);
                    if (paragraph < paragraphEnds.Length)
                    {
                        lineEnds[paragraph] = glyphEnd;
                    }

                    glyphStart = glyphEnd;
                }
            }
            else if (scratch.RunReadsRightToLeft)
            {
                // A directional run has no internal bidi segmentation: its stated
                // direction applies to every ordinary character. Turning an RTL run
                // around whole after positioning keeps joiners and other neutral
                // records attached to the same neighbours as the logical input.
                shaped.ReverseRange(0, shaped.Count);
            }

            int count = shaped.Count;
            Span<ShapedGlyph> destination = buffer.Reserve(count);
            for (int i = 0; i < count; i++)
            {
                ref GlyphShapingData shaping = ref shaped[i];
                ref ShapingBuffer.GlyphMetricsEntry entry = ref shaped.MetricsAt(i);
                ref GlyphShapingPosition position = ref shaped.PositionAt(i);
                float scale = font.Size / entry.Metrics.UnitsPerEm;
                Vector2 offset = (new Vector2(position.Bounds.X, position.Bounds.Y) + entry.Metrics.Offset) * scale;
                destination[i] = new ShapedGlyph(entry.Metrics.GlyphId, shaping.CodePointIndex, entry.GetAdvanceWidth(in position) * scale, entry.GetAdvanceHeight(in position) * scale, offset);
            }

            // Public shaping has no placeholder text runs, so the paragraph loop's
            // shaped-buffer indices are also the published glyph indices.
            buffer.Commit(count);
            buffer.CommitLineEnds(lineEnds.Length);
        }
        finally
        {
            ScratchPool.Return(scratch);
        }
    }
}
