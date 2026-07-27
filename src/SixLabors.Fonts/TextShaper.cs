// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using SixLabors.Fonts.Tables.AdvancedTypographic;

namespace SixLabors.Fonts;

/// <summary>
/// Encapsulates logic for shaping one run of text into a positioned glyph stream.
/// </summary>
/// <remarks>
/// <para>
/// The text of a run and the properties it is shaped under are set on a
/// <see cref="TextShapingBuffer"/>, the buffer is shaped against a font, and the
/// glyphs are read back from it. Shaping applies the font's substitution and
/// positioning features; it does not divide the text, break lines, or scale.
/// </para>
/// <para>
/// A run reads one way throughout. A caller holding text of mixed direction
/// divides it into runs itself, shapes each of them, and places them against one
/// another once it knows where its lines break.
/// </para>
/// <para>
/// Advances and offsets are expressed in font design units; see
/// <see cref="ShapedGlyph"/> for the conversion to pixel units.
/// </para>
/// </remarks>
public static partial class TextShaper
{
    /// <summary>
    /// Shapes the buffer's text against the font, replacing the buffer's glyphs.
    /// </summary>
    /// <param name="font">The font to shape against.</param>
    /// <param name="buffer">The buffer holding the run, which receives the glyphs.</param>
    public static void Shape(Font font, TextShapingBuffer buffer)
    {
        Guard.NotNull(font, nameof(font));
        Guard.NotNull(buffer, nameof(buffer));

        Shape(font, buffer, []);
    }

    /// <summary>
    /// Shapes the buffer's text against the font with the given features turned on,
    /// replacing the buffer's glyphs.
    /// </summary>
    /// <param name="font">The font to shape against.</param>
    /// <param name="buffer">The buffer holding the run, which receives the glyphs.</param>
    /// <param name="features">The feature tags to turn on for the run.</param>
    public static void Shape(Font font, TextShapingBuffer buffer, Tag[] features)
    {
        Guard.NotNull(font, nameof(font));
        Guard.NotNull(buffer, nameof(buffer));
        Guard.NotNull(features, nameof(features));

        if (buffer.Text.IsEmpty)
        {
            buffer.Reserve(0);
            buffer.Commit(0);
            return;
        }

        ShapingScratch scratch = ScratchPool.Get();
        try
        {
            TextOptions options = scratch.GetShapingOptions(font, buffer.Direction, buffer.Language, features);
            ShapingBuffer shaped = ShapeCore(buffer.Text, options, scratch, null);

            // Shaping hands the run back in the order it is read, as the callers of
            // a shaping API expect. A run that reads backwards is turned around whole,
            // once, after positioning: every record moves, including the characters
            // carrying no direction of their own such as the joiners. Turning the
            // run's parts around separately would strand those where they were
            // written, because they belong to no directional run.
            if (scratch.RunReadsRightToLeft)
            {
                shaped.ReverseRange(0, shaped.Count);
            }

            int count = shaped.Count;
            Span<ShapedGlyph> destination = buffer.Reserve(count);
            int written = 0;
            for (int i = 0; i < count; i++)
            {
                ref GlyphShapingData shaping = ref shaped[i];
                if (shaping.IsPlaceholder)
                {
                    // Placeholder runs reserve layout space for inline objects; they
                    // carry no glyph.
                    continue;
                }

                ref ShapingBuffer.GlyphMetricsEntry entry = ref shaped.MetricsAt(i);
                ref GlyphShapingPosition position = ref shaped.PositionAt(i);
                destination[written++] = new ShapedGlyph(
                    entry.Metrics.GlyphId,
                    shaping.CodePointIndex,
                    entry.GetAdvanceWidth(in position),
                    entry.GetAdvanceHeight(in position),
                    new Vector2(position.Bounds.X, position.Bounds.Y) + entry.Metrics.Offset);
            }

            buffer.Commit(written);
        }
        finally
        {
            ScratchPool.Return(scratch);
        }
    }
}
