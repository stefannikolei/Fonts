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
    /// <param name="bidiGlyphRanges">
    /// The contiguous visual glyph range belonging to each entry in <paramref name="bidiRuns"/>.
    /// </param>
    /// <param name="bidiRunCount">The number of live bidi runs and glyph ranges.</param>
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
        ShapedGlyphRange[] bidiGlyphRanges,
        int bidiRunCount,
        int[] bidiMap,
        LayoutMode layoutMode)
    {
        this.Runs = runs;
        this.Infos = infos;
        this.Positions = positions;
        this.GlyphCount = glyphCount;
        this.BidiRuns = bidiRuns;
        this.BidiGlyphRanges = bidiGlyphRanges;
        this.BidiRunCount = bidiRunCount;
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
    /// Gets the per-glyph identity records in logical directional-run order and
    /// visual order within each run.
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
    /// Gets the contiguous visual glyph range belonging to each resolved bidi run.
    /// </summary>
    public ShapedGlyphRange[] BidiGlyphRanges { get; }

    /// <summary>
    /// Gets the number of live entries in <see cref="BidiRuns"/> and
    /// <see cref="BidiGlyphRanges"/>.
    /// </summary>
    public int BidiRunCount { get; }

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
    /// <param name="searchBidiRunIndex">
    /// The bidi-run index used by the previous lookup. Updated to the run containing
    /// <paramref name="offset"/>.
    /// </param>
    /// <param name="searchIndex">
    /// The glyph index at which the next logical-source lookup starts. It advances
    /// through left-to-right visual storage and retreats through right-to-left visual
    /// storage.
    /// </param>
    /// <param name="start">The index of the first matching glyph.</param>
    /// <param name="count">The number of matching glyphs.</param>
    /// <param name="pointSize">The font size in PT units of the font containing the glyphs.</param>
    /// <param name="isSubstituted">Whether the range is the result of a substitution.</param>
    /// <param name="isVerticalSubstitution">Whether a vertical alternate feature changed any glyph in the range.</param>
    /// <param name="isDecomposed">Whether the range is the result of a decomposition substitution.</param>
    /// <param name="nextCodePointIndex">
    /// The next shaped source codepoint index in logical order, or
    /// <see cref="int.MaxValue"/> when the run contains no later shaped source.
    /// </param>
    /// <returns><see langword="true"/> when at least one glyph matches the offset.</returns>
    public bool TryGetGlyphsAtOffset(
        int offset,
        ref int searchBidiRunIndex,
        ref int searchIndex,
        out int start,
        out int count,
        out float pointSize,
        out bool isSubstituted,
        out bool isVerticalSubstitution,
        out bool isDecomposed,
        out int nextCodePointIndex)
    {
        ShapedGlyphInfo[] infos = this.Infos;
        start = 0;
        count = 0;
        pointSize = 0;
        isSubstituted = false;
        isVerticalSubstitution = false;
        isDecomposed = false;
        nextCodePointIndex = int.MaxValue;

        int bidiRunIndex = Math.Max(searchBidiRunIndex, 0);
        while (bidiRunIndex < this.BidiRunCount && offset >= this.BidiRuns[bidiRunIndex].End)
        {
            bidiRunIndex++;
        }

        int rangeStart;
        int rangeEnd;
        bool readsRightToLeft;
        if (bidiRunIndex < this.BidiRunCount
            && offset >= this.BidiRuns[bidiRunIndex].Start)
        {
            ShapedGlyphRange range = this.BidiGlyphRanges[bidiRunIndex];
            rangeStart = range.Start;
            rangeEnd = range.End;
            readsRightToLeft = (this.BidiRuns[bidiRunIndex].Level & 1) != 0;
        }
        else
        {
            // Placeholders can sit at the exclusive end of all source text. They
            // are not owned by a source bidi run, so they occupy the small tail
            // after the final recorded run range and retain insertion order.
            rangeStart = this.BidiRunCount > 0
                ? this.BidiGlyphRanges[this.BidiRunCount - 1].End
                : 0;

            rangeEnd = this.GlyphCount;
            readsRightToLeft = false;
            bidiRunIndex = this.BidiRunCount;
        }

        if (searchBidiRunIndex != bidiRunIndex)
        {
            // Browsers perform the same direction-aware source lookup over an
            // already-visual glyph array. RTL source indices therefore run
            // opposite the stored glyph order, while the returned glyph span
            // itself remains visual.
            searchBidiRunIndex = bidiRunIndex;
            searchIndex = readsRightToLeft ? rangeEnd - 1 : rangeStart;
        }

        if (readsRightToLeft)
        {
            int i = Math.Min(searchIndex, rangeEnd - 1);
            while (i >= rangeStart)
            {
                int codePointIndex = infos[i].CodePointIndex;
                if (codePointIndex < offset)
                {
                    i--;
                    continue;
                }

                if (codePointIndex > offset)
                {
                    searchIndex = i;
                    return false;
                }

                int end = i + 1;
                while (i > rangeStart && infos[i - 1].CodePointIndex == offset)
                {
                    i--;
                }

                start = i;
                count = end - i;
                searchIndex = i - 1;
                break;
            }
        }
        else
        {
            int i = Math.Max(searchIndex, rangeStart);
            while (i < rangeEnd)
            {
                int codePointIndex = infos[i].CodePointIndex;
                if (codePointIndex < offset)
                {
                    i++;
                    continue;
                }

                if (codePointIndex > offset)
                {
                    searchIndex = i;
                    return false;
                }

                start = i;
                int end = i + 1;
                while (end < rangeEnd && infos[end].CodePointIndex == offset)
                {
                    end++;
                }

                count = end - i;
                searchIndex = end;
                break;
            }
        }

        if (count == 0)
        {
            return false;
        }

        int matchEnd = start + count;
        for (int i = start; i < matchEnd; i++)
        {
            ref readonly ShapedGlyphInfo info = ref infos[i];
            if (!info.IsPlaceholder)
            {
                isSubstituted = info.IsSubstituted;
                isDecomposed = info.IsDecomposed;
                isVerticalSubstitution |= info.IsVerticalSubstituted;
                pointSize = this.Runs[info.RunIndex].PointSize;
            }
        }

        if (searchIndex >= rangeStart && searchIndex < rangeEnd)
        {
            nextCodePointIndex = infos[searchIndex].CodePointIndex;
        }

        return true;
    }
}

/// <summary>
/// A half-open contiguous glyph range over the projected shaping arrays.
/// </summary>
internal readonly struct ShapedGlyphRange
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShapedGlyphRange"/> struct.
    /// </summary>
    /// <param name="start">The zero-based index of the first glyph.</param>
    /// <param name="count">The number of glyphs in the range.</param>
    public ShapedGlyphRange(int start, int count)
    {
        this.Start = start;
        this.Count = count;
    }

    /// <summary>
    /// Gets the zero-based index of the first glyph.
    /// </summary>
    public int Start { get; }

    /// <summary>
    /// Gets the number of glyphs in the range.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Gets the index immediately after the final glyph.
    /// </summary>
    public int End => this.Start + this.Count;
}
