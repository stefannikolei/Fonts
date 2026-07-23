// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

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

        ShapedText shaped = ShapeText(text, options);

        var probe = ShapingProbe.Enter();
        ShapedGlyphInfo[] infos = shaped.Infos;
        ShapedGlyphPosition[] positions = shaped.Positions;
        ShapedTextRun[] runs = shaped.Runs;
        List<ShapedGlyph> glyphs = new(infos.Length);
        for (int i = 0; i < infos.Length; i++)
        {
            ref readonly ShapedGlyphInfo info = ref infos[i];
            if (info.IsPlaceholder)
            {
                // Placeholder runs reserve layout space for inline objects; they carry
                // no glyph.
                continue;
            }

            ref readonly ShapedGlyphPosition position = ref positions[i];
            glyphs.Add(new ShapedGlyph(
                runs[info.RunIndex].Font,
                info.GlyphId,
                info.CodePoint,
                info.CodePointIndex,
                info.CodePointCount,
                position.AdvanceWidth,
                position.AdvanceHeight,
                position.Bearing + position.Offset));
        }

        ShapingProbe.Exit(ShapingProbe.Projection, probe);
        return glyphs;
    }
}
