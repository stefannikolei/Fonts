// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// An iterator over a glyph shaping collection that respects OpenType lookup flags,
/// skipping glyphs that should be ignored (marks, base glyphs, ligatures) based on the flags.
/// </summary>
internal struct SkippingGlyphIterator
{
    private readonly FontMetrics fontMetrics;

    /// <summary>
    /// The three ignore lookup flags collapsed into a mask over the packed glyph class
    /// bits, so the common skip decision is a single bitwise test.
    /// </summary>
    private ushort ignoreClassMask;
    private ushort markAttachmentType;
    private bool useMarkFilteringSet;
    private ushort markFilteringSet;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkippingGlyphIterator"/> struct.
    /// </summary>
    /// <param name="fontMetrics">The font metrics for glyph class lookups.</param>
    /// <param name="collection">The glyph shaping collection to iterate over.</param>
    /// <param name="index">The starting index in the collection.</param>
    /// <param name="lookupFlags">The lookup flags that control which glyphs to skip.</param>
    /// <param name="markFilteringSet">The mark filtering set index, used when <see cref="LookupFlags.UseMarkFilteringSet"/> is set.</param>
    public SkippingGlyphIterator(
        FontMetrics fontMetrics,
        GlyphShapingCollection collection,
        int index,
        LookupFlags lookupFlags,
        ushort markFilteringSet)
    {
        this.fontMetrics = fontMetrics;
        this.Collection = collection;
        this.Index = index;
        this.ignoreClassMask = (ushort)(((lookupFlags & LookupFlags.IgnoreBaseGlyphs) != 0 ? GlyphShapingClass.BaseProp : 0)
            | ((lookupFlags & LookupFlags.IgnoreLigatures) != 0 ? GlyphShapingClass.LigatureProp : 0)
            | ((lookupFlags & LookupFlags.IgnoreMarks) != 0 ? GlyphShapingClass.MarkProp : 0));
        this.markAttachmentType = (ushort)((int)(lookupFlags & LookupFlags.MarkAttachmentTypeMask) >> 8);
        this.useMarkFilteringSet = (lookupFlags & LookupFlags.UseMarkFilteringSet) != 0;
        this.markFilteringSet = markFilteringSet;
    }

    /// <summary>
    /// Gets the glyph shaping collection being iterated.
    /// </summary>
    public GlyphShapingCollection Collection { get; }

    /// <summary>
    /// Gets or sets the current index in the collection.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Advances to the next non-skipped glyph in the forward direction.
    /// </summary>
    /// <returns>The new index after advancing.</returns>
    public int Next()
    {
        this.Move(1);
        return this.Index;
    }

    /// <summary>
    /// Advances to the next non-skipped glyph in the backward direction.
    /// </summary>
    /// <returns>The new index after moving backward.</returns>
    public int Prev()
    {
        this.Move(-1);
        return this.Index;
    }

    /// <summary>
    /// Moves the iterator by the specified number of non-skipped glyphs. A negative count moves backward.
    /// </summary>
    /// <param name="count">The number of positions to move. Negative values move backward.</param>
    /// <returns>The new index after incrementing.</returns>
    public int Increment(int count = 1)
    {
        int direction = count < 0 ? -1 : 1;
        count = Math.Abs(count);
        while (count-- > 0)
        {
            this.Move(direction);
        }

        return this.Index;
    }

    /// <summary>
    /// Resets the iterator to a new index and lookup flags.
    /// </summary>
    /// <param name="index">The new starting index.</param>
    /// <param name="lookupFlags">The new lookup flags.</param>
    /// <param name="markFilteringSet">The new mark filtering set index.</param>
    public void Reset(int index, LookupFlags lookupFlags, ushort markFilteringSet)
    {
        this.Index = index;
        this.ignoreClassMask = (ushort)(((lookupFlags & LookupFlags.IgnoreBaseGlyphs) != 0 ? GlyphShapingClass.BaseProp : 0)
            | ((lookupFlags & LookupFlags.IgnoreLigatures) != 0 ? GlyphShapingClass.LigatureProp : 0)
            | ((lookupFlags & LookupFlags.IgnoreMarks) != 0 ? GlyphShapingClass.MarkProp : 0));
        this.markAttachmentType = (ushort)((int)(lookupFlags & LookupFlags.MarkAttachmentTypeMask) >> 8);
        this.useMarkFilteringSet = (lookupFlags & LookupFlags.UseMarkFilteringSet) != 0;
        this.markFilteringSet = markFilteringSet;
    }

    /// <summary>
    /// Moves the iterator one step in the given direction, skipping glyphs that should be ignored.
    /// </summary>
    /// <param name="direction">The direction to move: 1 for forward, -1 for backward.</param>
    private void Move(int direction)
    {
        this.Index += direction;
        while (this.Index >= 0 && this.Index < this.Collection.Count)
        {
            if (!this.ShouldIgnore(this.Index))
            {
                break;
            }

            this.Index += direction;
        }
    }

    /// <summary>
    /// Determines whether the glyph at the given index should be ignored based on the current lookup flags.
    /// </summary>
    /// <param name="index">The index of the glyph to check.</param>
    /// <returns><see langword="true"/> if the glyph should be skipped; otherwise, <see langword="false"/>.</returns>
    private readonly bool ShouldIgnore(int index)
    {
        GlyphShapingData data = this.Collection[index];
        ushort props = AdvancedTypographicUtils.GetGlyphShapingClass(this.fontMetrics, data.GlyphId, data).Props;

        if ((props & this.ignoreClassMask) != 0)
        {
            return true;
        }

        if ((props & GlyphShapingClass.MarkProp) != 0)
        {
            // Skip marks not in the lookup's MarkFilteringSet.
            // This requires GDEF MarkGlyphSetsDef support.
            if (this.useMarkFilteringSet && !AdvancedTypographicUtils.IsInMarkFilteringSet(this.fontMetrics, this.markFilteringSet, data.GlyphId))
            {
                return true;
            }

            // The high byte carries the mark attachment class; a lookup restricted to
            // one attachment class skips marks of any other.
            if (this.markAttachmentType > 0 && (props >> 8) != this.markAttachmentType)
            {
                return true;
            }
        }

        return false;
    }
}
