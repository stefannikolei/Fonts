// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// The feature bit assignment owned by one shape plan: each OpenType feature the
/// plan touches receives a bit within a 64 bit mask, so per-glyph feature state is
/// stored and tested as plain bitwise operations. Bits are assigned in stage-list
/// order when the plan's groups are built and append-only afterwards, so two plans
/// of the same identity assign identical bits and applied masks stay portable
/// between the passes' buffers. The vertical trio occupies reserved bits identical
/// across every plan.
/// </summary>
internal sealed class ShapePlanFeatures
{
    /// <summary>
    /// The fixed mask bit for the vertical alternates feature. The vertical trio
    /// keeps reserved bits identical across every plan so applied-mask consumers
    /// that span plans, such as the copy-out's vertical detection, never depend on
    /// any single plan's layout.
    /// </summary>
    public const ulong VerticalAlternatesMask = 1UL << 0;

    /// <summary>
    /// The fixed mask bit for the vertical alternates for rotation feature.
    /// </summary>
    public const ulong VerticalAlternatesForRotationMask = 1UL << 1;

    /// <summary>
    /// The fixed mask bit for the vertical kerning feature.
    /// </summary>
    public const ulong VerticalKerningMask = 1UL << 2;

    /// <summary>
    /// The combined mask of the three vertical alternate features, constant across
    /// plans by the reserved-bit contract above.
    /// </summary>
    public const ulong VerticalFeatureMask = VerticalAlternatesMask | VerticalAlternatesForRotationMask | VerticalKerningMask;

    /// <summary>
    /// The first bit available to assigned features; lower bits are reserved for
    /// the vertical trio.
    /// </summary>
    private const int FirstAssignableBit = 3;

    /// <summary>
    /// The number of assignable feature bits after the reserved bits.
    /// </summary>
    private const int AssignableBitCount = 64 - FirstAssignableBit;

    /// <summary>
    /// The assigned feature tag values, indexed by bit position above the reserved
    /// bits. Stored as raw <see cref="uint"/> values so lookups take the runtime's
    /// vectorized primitive search path.
    /// </summary>
    private readonly List<uint> featureTags = new(32);

    /// <summary>
    /// The most recently resolved tag value. Queries strongly repeat the same
    /// feature during one feature's application, so a single-entry memo answers
    /// almost every query without a list search. Zero means the memo is empty; the
    /// zero tag is never a valid feature.
    /// </summary>
    private uint lastTagValue;

    /// <summary>
    /// The mask paired with <see cref="lastTagValue"/>.
    /// </summary>
    private ulong lastMask;

    /// <summary>
    /// Gets the fixed vertical-trio mask bit for a feature, or zero for any other
    /// feature. Applied-mask consumers only ever read the vertical bits, which are
    /// reserved and constant across plans, so applied recording needs no plan state.
    /// </summary>
    /// <param name="tag">The feature tag whose lookups applied.</param>
    /// <returns>The fixed vertical mask bit, or zero.</returns>
    public static ulong GetVerticalMask(Tag tag)
    {
        if (tag == KnownFeatureTags.VerticalAlternates)
        {
            return VerticalAlternatesMask;
        }

        if (tag == KnownFeatureTags.VerticalAlternatesForRotation)
        {
            return VerticalAlternatesForRotationMask;
        }

        if (tag == KnownFeatureTags.VerticalKerning)
        {
            return VerticalKerningMask;
        }

        return 0;
    }

    /// <summary>
    /// Gets the mask bit for the given feature tag, or zero when the tag has no
    /// assignment. The vertical trio answers from the reserved bits; a zero result
    /// is safe at every consumption site: testing it enables or matches nothing and
    /// clearing it clears nothing.
    /// </summary>
    /// <param name="tag">The feature tag.</param>
    /// <returns>The single-bit mask, or zero.</returns>
    public ulong GetMask(Tag tag)
    {
        if (tag.Value == this.lastTagValue)
        {
            return this.lastMask;
        }

        if (tag == KnownFeatureTags.VerticalAlternates)
        {
            return VerticalAlternatesMask;
        }

        if (tag == KnownFeatureTags.VerticalAlternatesForRotation)
        {
            return VerticalAlternatesForRotationMask;
        }

        if (tag == KnownFeatureTags.VerticalKerning)
        {
            return VerticalKerningMask;
        }

        int index = this.featureTags.IndexOf(tag.Value);
        ulong mask = index < 0 ? 0 : 1UL << (FirstAssignableBit + index);

        // A zero mask is never memoized: the tag may gain an assignment later and
        // the memo must not serve a stale zero after that.
        if (mask != 0)
        {
            this.lastTagValue = tag.Value;
            this.lastMask = mask;
        }

        return mask;
    }

    /// <summary>
    /// Gets the mask bit for the given feature tag, assigning the next free bit
    /// when the tag is new to this plan. A plan that has exhausted its bits assigns
    /// nothing and returns zero, which disables the feature: a zero mask registers
    /// nothing, enables nothing, and matches nothing at every consumption site.
    /// </summary>
    /// <param name="tag">The feature tag.</param>
    /// <returns>The single-bit mask, or zero when the bits are exhausted.</returns>
    public ulong GetOrAddMask(Tag tag)
    {
        ulong mask = this.GetMask(tag);
        if (mask != 0)
        {
            return mask;
        }

        if (this.featureTags.Count == AssignableBitCount)
        {
            return 0;
        }

        this.featureTags.Add(tag.Value);
        mask = 1UL << (FirstAssignableBit + this.featureTags.Count - 1);
        this.lastTagValue = tag.Value;
        this.lastMask = mask;
        return mask;
    }
}
