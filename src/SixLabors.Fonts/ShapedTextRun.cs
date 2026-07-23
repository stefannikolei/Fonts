// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts;

/// <summary>
/// Run-constant shaping state shared by a consecutive range of shaped glyphs: the
/// resolved font, its point size, the source text run, and, for placeholder entries,
/// the bidi run at the insertion point. Run-level state lives once here rather than
/// being repeated per glyph.
/// </summary>
internal readonly struct ShapedTextRun
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShapedTextRun"/> struct.
    /// </summary>
    /// <param name="font">The font that resolved the run's glyphs.</param>
    /// <param name="pointSize">The font size in PT units.</param>
    /// <param name="textRun">The source text run.</param>
    /// <param name="bidiRun">The bidi run assigned to a placeholder insertion point.</param>
    public ShapedTextRun(Font font, float pointSize, TextRun textRun, BidiRun bidiRun)
    {
        this.Font = font;
        this.PointSize = pointSize;
        this.TextRun = textRun;
        this.BidiRun = bidiRun;
    }

    /// <summary>
    /// Gets the font that resolved the run's glyphs.
    /// </summary>
    public Font Font { get; }

    /// <summary>
    /// Gets the font size in PT units.
    /// </summary>
    public float PointSize { get; }

    /// <summary>
    /// Gets the source text run.
    /// </summary>
    public TextRun TextRun { get; }

    /// <summary>
    /// Gets the bidi run assigned to a placeholder insertion point.
    /// </summary>
    public BidiRun BidiRun { get; }
}
