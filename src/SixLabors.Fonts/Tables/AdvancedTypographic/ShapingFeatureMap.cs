// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Assigns each OpenType feature tag touched during a single shaping pass a bit within a
/// 64 bit mask, so per-glyph feature state can be stored and tested as plain bitwise
/// operations instead of per-glyph collections.
/// </summary>
/// <remarks>
/// <para>
/// Every glyph carries mask words instead of per-glyph collections, and lookup
/// application gates a glyph with one bitwise AND. The glyph side lives in the three
/// mask fields on <see cref="GlyphShapingData"/>:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="GlyphShapingData.RegisteredFeatureMask"/>: features a
/// shaper added for the glyph, enabled or not. Enabling is only ever an unhide of a
/// registered feature, never an introduction.</description></item>
/// <item><description><see cref="GlyphShapingData.FeatureMask"/>: the enabled subset,
/// the analog of hb_glyph_info_t.mask. This is the word the per-glyph application gate
/// tests.</description></item>
/// <item><description><see cref="GlyphShapingData.AppliedFeatureMask"/>: features whose
/// lookups actually changed the glyph, read after shaping, for example to detect that a
/// vertical alternate was substituted.</description></item>
/// </list>
/// <para>
/// One instance is shared by the substitution and positioning collections of a shaping
/// pass. The sharing is load bearing: applied bits are written while substituting, the
/// glyph data is then copied into the positioning collection, and the positioning stages
/// and the layout walk read those bits later. A per-collection map would renumber the
/// bits across that copy and silently corrupt the applied state.
/// </para>
/// <para>
/// Bits are assigned first come, in registration order. A pass touches the shaper's
/// stage features, the font's required features, and the caller's
/// <see cref="TextOptions.FeatureTags"/>; the largest shaper plans sit far below the 64
/// available bits, so exhaustion indicates a defect rather than a workload and throws
/// loudly instead of shaping incorrectly.
/// </para>
/// </remarks>
internal sealed class ShapingFeatureMap
{
    /// <summary>
    /// The registered tag values, indexed by assigned bit position. Stored as raw
    /// <see cref="uint"/> values rather than <see cref="Tag"/> so lookups take the
    /// runtime's vectorized primitive search path, which custom structs never qualify
    /// for.
    /// </summary>
    private readonly List<uint> tags = new(16);

    /// <summary>
    /// The most recently resolved tag value. Queries within a pass strongly repeat the
    /// same feature (every per-glyph apply during one feature's application resolves that
    /// feature's tag), so a single-entry memo answers almost every query without a list
    /// search. Zero means the memo is empty; the zero tag is never a valid feature.
    /// </summary>
    private uint lastTagValue;

    /// <summary>
    /// The mask paired with <see cref="lastTagValue"/>.
    /// </summary>
    private ulong lastMask;

    /// <summary>
    /// Resets the map for reuse by a new shaping pass, emptying the tag registry and
    /// the single-entry memo.
    /// </summary>
    internal void Reset()
    {
        this.tags.Clear();
        this.lastTagValue = 0;
        this.lastMask = 0;
    }

    /// <summary>
    /// Gets the mask bit for the given feature tag, or zero when the tag has not been
    /// registered. A zero result is safe at every consumption site: testing it enables
    /// or matches nothing and clearing it clears nothing.
    /// </summary>
    /// <param name="tag">The feature tag.</param>
    /// <returns>The single-bit mask, or zero.</returns>
    public ulong GetMask(Tag tag)
    {
        if (tag.Value == this.lastTagValue)
        {
            return this.lastMask;
        }

        int index = this.tags.IndexOf(tag.Value);
        ulong mask = index < 0 ? 0 : 1UL << index;

        // A zero mask is never memoized: the tag may be registered later in the pass
        // and the memo must not serve a stale zero after that registration.
        if (mask != 0)
        {
            this.lastTagValue = tag.Value;
            this.lastMask = mask;
        }

        return mask;
    }

    /// <summary>
    /// Gets the mask bit for the given feature tag, assigning the next free bit when the
    /// tag is new to this pass.
    /// </summary>
    /// <param name="tag">The feature tag.</param>
    /// <returns>The single-bit mask.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a single shaping pass registers more than 64 distinct features.
    /// </exception>
    public ulong GetOrAddMask(Tag tag)
    {
        ulong mask = this.GetMask(tag);
        if (mask != 0)
        {
            return mask;
        }

        if (this.tags.Count == 64)
        {
            throw new InvalidOperationException(
                "A single shaping pass registered more than 64 distinct OpenType features.");
        }

        this.tags.Add(tag.Value);
        mask = 1UL << (this.tags.Count - 1);
        this.lastTagValue = tag.Value;
        this.lastMask = mask;
        return mask;
    }
}
