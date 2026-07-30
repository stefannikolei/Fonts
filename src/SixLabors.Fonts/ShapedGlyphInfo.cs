// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts;

/// <summary>
/// Flags describing how shaping produced a glyph.
/// </summary>
[Flags]
internal enum ShapedGlyphFlags : byte
{
    /// <summary>
    /// No flags.
    /// </summary>
    None = 0,

    /// <summary>
    /// The entry is an inline placeholder.
    /// </summary>
    Placeholder = 1,

    /// <summary>
    /// The glyph is the result of a substitution.
    /// </summary>
    Substituted = 2,

    /// <summary>
    /// The glyph is the result of a decomposition substitution.
    /// </summary>
    Decomposed = 4,

    /// <summary>
    /// A vertical alternate feature changed the glyph.
    /// </summary>
    VerticalSubstituted = 8,

    /// <summary>
    /// Tracking must preserve the glyph's cursive shaping run.
    /// </summary>
    CursiveScript = 16,
}

/// <summary>
/// The identity half of a shaped glyph: which glyph, which source codepoints it
/// covers, and which run it belongs to. Positioning values live in the parallel
/// position array.
/// </summary>
internal readonly struct ShapedGlyphInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShapedGlyphInfo"/> struct.
    /// </summary>
    /// <param name="codePointIndex">The zero-based index within the input codepoint collection.</param>
    /// <param name="codePoint">The leading codepoint the glyph represents.</param>
    /// <param name="codePointCount">The codepoint count represented by the glyph.</param>
    /// <param name="glyphId">The glyph id.</param>
    /// <param name="runIndex">The index of the owning entry in the shaped run table.</param>
    /// <param name="flags">The shaping flags.</param>
    public ShapedGlyphInfo(
        int codePointIndex,
        CodePoint codePoint,
        int codePointCount,
        ushort glyphId,
        ushort runIndex,
        ShapedGlyphFlags flags)
    {
        this.CodePointIndex = codePointIndex;
        this.CodePoint = codePoint;
        this.CodePointCount = codePointCount;
        this.GlyphId = glyphId;
        this.RunIndex = runIndex;
        this.Flags = flags;
    }

    /// <summary>
    /// Gets the zero-based index within the input codepoint collection.
    /// </summary>
    public int CodePointIndex { get; }

    /// <summary>
    /// Gets the leading codepoint the glyph represents.
    /// </summary>
    public CodePoint CodePoint { get; }

    /// <summary>
    /// Gets the codepoint count represented by the glyph.
    /// </summary>
    public int CodePointCount { get; }

    /// <summary>
    /// Gets the glyph id.
    /// </summary>
    public ushort GlyphId { get; }

    /// <summary>
    /// Gets the index of the owning entry in the shaped run table.
    /// </summary>
    public ushort RunIndex { get; }

    /// <summary>
    /// Gets the shaping flags.
    /// </summary>
    public ShapedGlyphFlags Flags { get; }

    /// <summary>
    /// Gets a value indicating whether the entry is an inline placeholder.
    /// </summary>
    public bool IsPlaceholder => (this.Flags & ShapedGlyphFlags.Placeholder) != 0;

    /// <summary>
    /// Gets a value indicating whether the glyph is the result of a substitution.
    /// </summary>
    public bool IsSubstituted => (this.Flags & ShapedGlyphFlags.Substituted) != 0;

    /// <summary>
    /// Gets a value indicating whether the glyph is the result of a decomposition substitution.
    /// </summary>
    public bool IsDecomposed => (this.Flags & ShapedGlyphFlags.Decomposed) != 0;

    /// <summary>
    /// Gets a value indicating whether a vertical alternate feature changed the glyph.
    /// </summary>
    public bool IsVerticalSubstituted => (this.Flags & ShapedGlyphFlags.VerticalSubstituted) != 0;

    /// <summary>
    /// Gets a value indicating whether tracking must preserve the glyph's cursive shaping run.
    /// </summary>
    public bool IsCursiveScript => (this.Flags & ShapedGlyphFlags.CursiveScript) != 0;
}
