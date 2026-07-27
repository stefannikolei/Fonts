// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts;

/// <summary>
/// Reorders positioned glyph records from logical order to visual order for one
/// resolved line.
/// </summary>
/// <remarks>
/// Implements rule L2 of the
/// <see href="https://www.unicode.org/reports/tr9/#L2">Unicode Bidirectional Algorithm</see>.
/// </remarks>
internal static class BidiReordering
{
    /// <summary>
    /// Supplies level lookup and range reversal for a glyph storage representation.
    /// Static operations let the JIT specialize the shared loop without delegates,
    /// closures, or interface instances on the shaping hot path.
    /// </summary>
    /// <typeparam name="TState">The reordered storage.</typeparam>
    private interface IBidiReorderingOperations<TState>
    {
        /// <summary>
        /// Gets the number of live glyph records.
        /// </summary>
        /// <param name="state">The reordered storage.</param>
        /// <returns>The live glyph count.</returns>
        static abstract int GetCount(TState state);

        /// <summary>
        /// Gets the resolved embedding level of one glyph.
        /// </summary>
        /// <param name="state">The reordered storage.</param>
        /// <param name="index">The glyph index.</param>
        /// <returns>The resolved embedding level.</returns>
        static abstract int GetLevel(TState state, int index);

        /// <summary>
        /// Reverses one half-open range while keeping each glyph record intact.
        /// </summary>
        /// <param name="state">The reordered storage.</param>
        /// <param name="start">The first glyph index.</param>
        /// <param name="end">The index after the final glyph.</param>
        static abstract void Reverse(TState state, int start, int end);
    }

    /// <summary>
    /// Reorders layout entries for one line.
    /// </summary>
    /// <param name="glyphs">The logically ordered layout entries.</param>
    public static void Reorder(List<GlyphLayoutData> glyphs)
        => Reorder<List<GlyphLayoutData>, LayoutGlyphOperations>(glyphs);

    /// <summary>
    /// Reorders positioned shaping records for one line.
    /// </summary>
    /// <param name="glyphs">The logically ordered shaping records.</param>
    /// <param name="bidiRuns">The resolved bidirectional runs covering the source text.</param>
    /// <param name="bidiMap">The source codepoint to bidirectional-run mapping.</param>
    public static void Reorder(ShapingBuffer glyphs, BidiRun[] bidiRuns, int[] bidiMap)
        => Reorder<ShapingGlyphState, ShapingGlyphOperations>(new(glyphs, bidiRuns, bidiMap));

    /// <summary>
    /// Applies rule L2 of the Unicode Bidirectional Algorithm to one line.
    /// </summary>
    /// <typeparam name="TState">The reordered storage.</typeparam>
    /// <typeparam name="TOperations">The specialized operations for that storage.</typeparam>
    /// <param name="state">The reordered storage and any level-mapping state it needs.</param>
    private static void Reorder<TState, TOperations>(TState state)
        where TOperations : struct, IBidiReorderingOperations<TState>
    {
        int count = TOperations.GetCount(state);
        int maximumLevel = 0;
        int minimumOddLevel = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int level = TOperations.GetLevel(state, i);
            maximumLevel = Math.Max(maximumLevel, level);
            if ((level & 1) != 0)
            {
                minimumOddLevel = Math.Min(minimumOddLevel, level);
            }
        }

        if (minimumOddLevel == int.MaxValue)
        {
            return;
        }

        // UAX #9 rule L2 reverses each maximal contiguous sequence whose level is
        // at least the current level, walking down from the highest resolved level
        // to the lowest odd one. Operating directly on the destination storage
        // keeps every glyph's identity, positioning, and source index together and
        // requires no temporary permutation or per-run allocation.
        for (int level = maximumLevel; level >= minimumOddLevel; level--)
        {
            int start = 0;
            while (start < count)
            {
                while (start < count && TOperations.GetLevel(state, start) < level)
                {
                    start++;
                }

                int end = start;
                while (end < count && TOperations.GetLevel(state, end) >= level)
                {
                    end++;
                }

                if (end - start > 1)
                {
                    TOperations.Reverse(state, start, end);
                }

                start = end + 1;
            }
        }
    }

    /// <summary>
    /// Adapts layout entries to the shared reordering loop.
    /// </summary>
    private readonly struct LayoutGlyphOperations : IBidiReorderingOperations<List<GlyphLayoutData>>
    {
        /// <inheritdoc/>
        public static int GetCount(List<GlyphLayoutData> state) => state.Count;

        /// <inheritdoc/>
        public static int GetLevel(List<GlyphLayoutData> state, int index) => state[index].BidiRun.Level;

        /// <inheritdoc/>
        public static void Reverse(List<GlyphLayoutData> state, int start, int end) => state.Reverse(start, end - start);
    }

    /// <summary>
    /// Holds a shaping buffer together with the source mapping needed to recover
    /// each glyph's resolved embedding level.
    /// </summary>
    private readonly struct ShapingGlyphState
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ShapingGlyphState"/> struct.
        /// </summary>
        /// <param name="glyphs">The positioned shaping records.</param>
        /// <param name="bidiRuns">The resolved bidirectional runs.</param>
        /// <param name="bidiMap">The source codepoint to bidirectional-run mapping.</param>
        public ShapingGlyphState(ShapingBuffer glyphs, BidiRun[] bidiRuns, int[] bidiMap)
        {
            this.Glyphs = glyphs;
            this.BidiRuns = bidiRuns;
            this.BidiMap = bidiMap;
        }

        /// <summary>
        /// Gets the positioned shaping records.
        /// </summary>
        public ShapingBuffer Glyphs { get; }

        /// <summary>
        /// Gets the resolved bidirectional runs.
        /// </summary>
        public BidiRun[] BidiRuns { get; }

        /// <summary>
        /// Gets the source codepoint to bidirectional-run mapping.
        /// </summary>
        public int[] BidiMap { get; }
    }

    /// <summary>
    /// Adapts positioned shaping records to the shared reordering loop.
    /// </summary>
    private readonly struct ShapingGlyphOperations : IBidiReorderingOperations<ShapingGlyphState>
    {
        /// <inheritdoc/>
        public static int GetCount(ShapingGlyphState state) => state.Glyphs.Count;

        /// <inheritdoc/>
        public static int GetLevel(ShapingGlyphState state, int index)
        {
            int codePointIndex = state.Glyphs[index].CodePointIndex;
            return state.BidiRuns[state.BidiMap[codePointIndex]].Level;
        }

        /// <inheritdoc/>
        public static void Reverse(ShapingGlyphState state, int start, int end) => state.Glyphs.ReverseRange(start, end);
    }
}
