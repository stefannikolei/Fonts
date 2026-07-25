// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts;

/// <summary>
/// The width-independent result of shaping text before logical line composition:
/// a run table holding run-constant state once, and parallel per-glyph info and
/// position arrays holding identities and pure numbers. The arrays are views over
/// pooled scratch storage and may exceed the live counts; the view is valid only
/// while the renting scope holds its scratch, so consumers copy what they retain.
/// </summary>
internal readonly struct ShapedText
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShapedText"/> struct.
    /// </summary>
    /// <param name="runs">The shaped run table.</param>
    /// <param name="infos">The per-glyph identity records, parallel to <paramref name="positions"/>.</param>
    /// <param name="positions">The per-glyph geometry records, parallel to <paramref name="infos"/>.</param>
    /// <param name="glyphCount">The number of live entries in the per-glyph arrays.</param>
    /// <param name="bidiRuns">The resolved bidi runs covering the shaped text.</param>
    /// <param name="bidiMap">
    /// The code point index to bidi-run index mapping built during shaping. Entries for
    /// code points no shaping pass visited hold -1.
    /// </param>
    /// <param name="layoutMode">The layout mode used while shaping.</param>
    public ShapedText(
        ShapedTextRun[] runs,
        ShapedGlyphInfo[] infos,
        ShapedGlyphPosition[] positions,
        int glyphCount,
        BidiRun[] bidiRuns,
        int[] bidiMap,
        LayoutMode layoutMode)
    {
        this.Runs = runs;
        this.Infos = infos;
        this.Positions = positions;
        this.GlyphCount = glyphCount;
        this.BidiRuns = bidiRuns;
        this.BidiMap = bidiMap;
        this.LayoutMode = layoutMode;
    }

    /// <summary>
    /// Gets the number of live entries in <see cref="Infos"/> and <see cref="Positions"/>.
    /// </summary>
    public int GlyphCount { get; }

    /// <summary>
    /// Gets the shaped run table: run-constant state referenced per glyph by
    /// <see cref="ShapedGlyphInfo.RunIndex"/>.
    /// </summary>
    public ShapedTextRun[] Runs { get; }

    /// <summary>
    /// Gets the per-glyph identity records in logical order.
    /// </summary>
    public ShapedGlyphInfo[] Infos { get; }

    /// <summary>
    /// Gets the per-glyph geometry records, parallel to <see cref="Infos"/>.
    /// </summary>
    public ShapedGlyphPosition[] Positions { get; }

    /// <summary>
    /// Gets the resolved bidi runs covering the shaped text.
    /// </summary>
    public BidiRun[] BidiRuns { get; }

    /// <summary>
    /// Gets the code point index to bidi-run index mapping built during shaping,
    /// indexed by code point position. Unvisited positions hold -1.
    /// </summary>
    public int[] BidiMap { get; }

    /// <summary>
    /// Gets the layout mode used while shaping.
    /// </summary>
    public LayoutMode LayoutMode { get; }

    /// <summary>
    /// Gets the contiguous range of shaped glyphs at the given codepoint offset, along
    /// with the aggregate shaping state of the range's non-placeholder entries.
    /// </summary>
    /// <param name="offset">The zero-based index within the input codepoint collection.</param>
    /// <param name="searchIndex">
    /// The glyph index to start searching from. Updated to the position of the first
    /// match so that subsequent calls with increasing offsets avoid rescanning.
    /// </param>
    /// <param name="start">The index of the first matching glyph.</param>
    /// <param name="count">The number of matching glyphs.</param>
    /// <param name="pointSize">The font size in PT units of the font containing the glyphs.</param>
    /// <param name="isSubstituted">Whether the range is the result of a substitution.</param>
    /// <param name="isVerticalSubstitution">Whether a vertical alternate feature changed any glyph in the range.</param>
    /// <param name="isDecomposed">Whether the range is the result of a decomposition substitution.</param>
    /// <returns><see langword="true"/> when at least one glyph matches the offset.</returns>
    public bool TryGetGlyphsAtOffset(
        int offset,
        ref int searchIndex,
        out int start,
        out int count,
        out float pointSize,
        out bool isSubstituted,
        out bool isVerticalSubstitution,
        out bool isDecomposed)
    {
        ShapedGlyphInfo[] infos = this.Infos;
        start = 0;
        count = 0;
        pointSize = 0;
        isSubstituted = false;
        isVerticalSubstitution = false;
        isDecomposed = false;

        for (int i = searchIndex; i < this.GlyphCount; i++)
        {
            if (infos[i].CodePointIndex == offset)
            {
                if (count == 0)
                {
                    start = i;
                    searchIndex = i;
                }

                ref readonly ShapedGlyphInfo info = ref infos[i];
                if (!info.IsPlaceholder)
                {
                    isSubstituted = info.IsSubstituted;
                    isDecomposed = info.IsDecomposed;
                    isVerticalSubstitution |= info.IsVerticalSubstituted;
                    pointSize = this.Runs[info.RunIndex].PointSize;
                }

                count++;
            }
            else if (count > 0)
            {
                // Codepoint indices, though non-sequential, are sorted, so we can stop searching.
                break;
            }
        }

        return count > 0;
    }
}
