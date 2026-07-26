// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;

namespace SixLabors.Fonts;

/// <summary>
/// Encapsulates logic for shaping text into a positioned glyph stream.
/// </summary>
/// <remarks>
/// <para>
/// Shaping runs the pipeline text layout uses: bidi analysis, font and text run
/// itemization, fallback font resolution, and the font's substitution and positioning
/// features. The result is the glyph stream in logical (source) order, before line
/// breaking, visual reordering, or scaling. Advances and offsets are expressed in font
/// design units; see <see cref="ShapedGlyph"/> for the conversion to pixel units.
/// </para>
/// <para>
/// External text stacks that itemize runs themselves shape one run per call: set a
/// single <see cref="TextOptions.Font"/> with no
/// <see cref="TextOptions.FallbackFontFamilies"/> so unmapped codepoints produce the
/// font's missing glyph, and pre-resolve the direction with
/// <see cref="TextOptions.TextDirection"/> and <see cref="TextBidiMode.Override"/>.
/// Shaping is context sensitive, so a caller shaping a slice of a larger paragraph
/// passes the containing text and keeps the glyphs whose
/// <see cref="ShapedGlyph.CodePointIndex"/> falls inside the slice.
/// </para>
/// </remarks>
public static partial class TextShaper
{
    /// <inheritdoc cref="Shape(ReadOnlySpan{char}, TextOptions)"/>
    public static IReadOnlyList<ShapedGlyph> Shape(string text, TextOptions options)
    {
        Guard.NotNull(text, nameof(text));

        return Shape(text.AsSpan(), options);
    }

    /// <summary>
    /// Shapes the text into a positioned glyph stream using the supplied options.
    /// </summary>
    /// <param name="text">The text to shape.</param>
    /// <param name="options">
    /// The text options. Shaping honors the font selection members
    /// (<see cref="TextOptions.Font"/>, <see cref="TextOptions.FallbackFontFamilies"/>,
    /// <see cref="TextOptions.TextRuns"/>), <see cref="TextOptions.TextDirection"/> and
    /// <see cref="TextOptions.TextBidiMode"/>, <see cref="TextOptions.LayoutMode"/>,
    /// <see cref="TextOptions.KerningMode"/>, <see cref="TextOptions.FeatureTags"/>, and
    /// <see cref="TextOptions.Culture"/>. Layout members such as
    /// <see cref="TextOptions.Dpi"/>, <see cref="TextOptions.Origin"/>, wrapping, and
    /// alignment do not affect shaping.
    /// </param>
    /// <returns>The shaped glyphs in logical order.</returns>
    public static IReadOnlyList<ShapedGlyph> Shape(ReadOnlySpan<char> text, TextOptions options)
    {
        Guard.NotNull(options, nameof(options));

        if (text.IsEmpty)
        {
            return [];
        }

        TextShapingBuffer buffer = new();
        Shape(text, options, buffer);
        return buffer.Glyphs.ToArray();
    }

    /// <inheritdoc cref="Shape(ReadOnlySpan{char}, TextOptions, TextShapingBuffer)"/>
    public static void Shape(string text, TextOptions options, TextShapingBuffer buffer)
    {
        Guard.NotNull(text, nameof(text));

        Shape(text.AsSpan(), options, buffer);
    }

    /// <summary>
    /// Shapes the text into a positioned glyph stream, replacing the contents of the
    /// supplied buffer. Reusing one buffer across calls keeps steady-state shaping
    /// free of allocation; see <see cref="Shape(ReadOnlySpan{char}, TextOptions)"/>
    /// for the shaping semantics and honored options.
    /// </summary>
    /// <param name="text">The text to shape.</param>
    /// <param name="options">The text options.</param>
    /// <param name="buffer">The buffer receiving the shaped glyphs in logical order.</param>
    public static void Shape(ReadOnlySpan<char> text, TextOptions options, TextShapingBuffer buffer)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNull(buffer, nameof(buffer));

        if (text.IsEmpty)
        {
            buffer.Clear();
            return;
        }

        ShapingScratch scratch = ScratchPool.Get();
        try
        {
            ShapingBuffer shaped = ShapeCore(text, options, scratch, null);

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
                    entry.Font,
                    entry.Metrics.GlyphId,
                    shaping.CodePoint,
                    shaping.CodePointIndex,
                    shaping.CodePointCount,
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
