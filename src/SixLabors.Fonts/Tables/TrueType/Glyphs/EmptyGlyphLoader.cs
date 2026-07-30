// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.TrueType.Glyphs;

/// <summary>
/// A <see cref="GlyphLoader"/> that produces an empty glyph outline.
/// Used for glyphs that have no outline data (e.g. space characters).
/// </summary>
internal class EmptyGlyphLoader : GlyphLoader
{
    private readonly Bounds fallbackEmptyBounds;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmptyGlyphLoader"/> class.
    /// </summary>
    /// <param name="fallbackEmptyBounds">The fallback bounds to use if glyph 0 cannot be resolved.</param>
    public EmptyGlyphLoader(Bounds fallbackEmptyBounds)
        => this.fallbackEmptyBounds = fallbackEmptyBounds;

    /// <inheritdoc/>
    public override GlyphVector CreateGlyph(GlyphTable table)
    {
        // A zero-length glyf entry has no ink. Reusing glyph zero's bounds would
        // incorrectly turn every empty glyph, including spaces, into .notdef.
        // Measurement derives fallback advance bounds from this empty vector later.
        return GlyphVector.Empty(this.fallbackEmptyBounds);
    }
}
