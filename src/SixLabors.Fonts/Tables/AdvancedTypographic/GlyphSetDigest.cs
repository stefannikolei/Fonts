// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// An approximate glyph membership filter for lookup coverage.
/// </summary>
/// <remarks>
/// <para>
/// Conceptually a tiny Bloom filter tuned for glyph coverage queries: three 64 bit
/// words, each indexing the glyph id at a different shift so ids cluster into different
/// buckets per word. A glyph "may" be in the set only when all three words contain its
/// bucket bit. False positives fall through to the lookup's exact coverage test, so they
/// cost only what the pre-digest code always paid; false negatives cannot occur.
/// </para>
/// <para>
/// The filter is highly accurate when a lookup covers a local cluster of glyph ids,
/// which is the common case for real fonts, and degrades to always-maybe when coverage
/// is spread across the id space. The three shifts bucket runs of 16 ids, single ids,
/// and runs of 64 ids respectively.
/// </para>
/// </remarks>
internal struct GlyphSetDigest
{
    private const int BitsMinusOne = 63;
    private ulong mask0;
    private ulong mask1;
    private ulong mask2;

    /// <summary>
    /// Adds a single glyph id to the digest.
    /// </summary>
    /// <param name="glyphId">The glyph id.</param>
    public void Add(ushort glyphId)
    {
        this.mask0 |= 1UL << ((glyphId >> 4) & BitsMinusOne);
        this.mask1 |= 1UL << (glyphId & BitsMinusOne);
        this.mask2 |= 1UL << ((glyphId >> 6) & BitsMinusOne);
    }

    /// <summary>
    /// Adds an inclusive range of glyph ids to the digest.
    /// </summary>
    /// <param name="start">The first glyph id in the range.</param>
    /// <param name="end">The last glyph id in the range.</param>
    public void AddRange(ushort start, ushort end)
    {
        this.mask0 = AddRange(this.mask0, start, end, 4);
        this.mask1 = AddRange(this.mask1, start, end, 0);
        this.mask2 = AddRange(this.mask2, start, end, 6);
    }

    /// <summary>
    /// Marks the digest as containing every glyph, used when a subtable's gating
    /// coverage is unknown so the lookup is always attempted, matching the behavior
    /// before digests existed.
    /// </summary>
    public void AddAll()
    {
        this.mask0 = ulong.MaxValue;
        this.mask1 = ulong.MaxValue;
        this.mask2 = ulong.MaxValue;
    }

    /// <summary>
    /// Gets a value indicating whether the glyph may be a member of the digested set.
    /// A false result is definitive; a true result must be confirmed by the lookup's
    /// exact coverage test.
    /// </summary>
    /// <param name="glyphId">The glyph id.</param>
    /// <returns><see langword="false"/> when the glyph is definitely absent.</returns>
    public readonly bool MightContain(ushort glyphId)
        => (this.mask0 & (1UL << ((glyphId >> 4) & BitsMinusOne))) != 0
        && (this.mask1 & (1UL << (glyphId & BitsMinusOne))) != 0
        && (this.mask2 & (1UL << ((glyphId >> 6) & BitsMinusOne))) != 0;

    /// <summary>
    /// Gets a value indicating whether this digest and <paramref name="other"/> may
    /// share a member: every word pair must share at least one bucket bit. A false
    /// result proves the underlying sets are disjoint; a true result is approximate.
    /// </summary>
    /// <param name="other">The other digest.</param>
    /// <returns><see langword="false"/> when the sets are definitely disjoint.</returns>
    public readonly bool MightIntersect(in GlyphSetDigest other)
        => (this.mask0 & other.mask0) != 0
        && (this.mask1 & other.mask1) != 0
        && (this.mask2 & other.mask2) != 0;

    /// <summary>
    /// Sets every bucket bit from <paramref name="start"/> through <paramref name="end"/>
    /// at the given shift, saturating the word when the range spans all buckets.
    /// </summary>
    /// <param name="mask">The current word.</param>
    /// <param name="start">The first glyph id in the range.</param>
    /// <param name="end">The last glyph id in the range.</param>
    /// <param name="shift">The bucket shift for this word.</param>
    /// <returns>The updated word.</returns>
    private static ulong AddRange(ulong mask, ushort start, ushort end, int shift)
    {
        if ((end >> shift) - (start >> shift) >= BitsMinusOne)
        {
            return ulong.MaxValue;
        }

        // Sets the contiguous bucket bits from start through end inclusive, wrapping
        // within the word: with mb >= ma the expression is (mb << 1) - ma, the bits
        // ma..mb; with mb < ma the unsigned wrap of (mb - ma) plus the borrowed 1
        // produces the two runs ma..63 and 0..mb.
        ulong ma = 1UL << ((start >> shift) & BitsMinusOne);
        ulong mb = 1UL << ((end >> shift) & BitsMinusOne);
        return mask | unchecked(mb + (mb - ma) - (mb < ma ? 1UL : 0UL));
    }
}
