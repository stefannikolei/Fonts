// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// The feature classification and bit assignment owned by one shape plan. Features
/// that apply to every glyph of the plan's segments share a single reserved global
/// bit, so any number of them fits one mask; only features whose per-glyph state
/// varies receive distinct bits, assigned in registration order and append-only, so
/// plans of the same identity assign identical layouts. Classification moves in one
/// direction only: a global feature may be demoted to varying or disabled outright,
/// and either move is terminal, which keeps repeated per-segment planning
/// convergent. The vertical trio occupies reserved bits in the applied-mask space,
/// which is independent of the registration space managed here.
/// </summary>
internal sealed class ShapePlanFeatures
{
    /// <summary>
    /// The fixed mask bit for the vertical alternates feature. The vertical trio
    /// keeps reserved bits identical across every plan so applied-mask consumers
    /// that span plans, such as the copy-out's vertical detection, never depend on
    /// any single plan's layout.
    /// </summary>
    public const uint VerticalAlternatesMask = 1U << 0;

    /// <summary>
    /// The fixed mask bit for the vertical alternates for rotation feature.
    /// </summary>
    public const uint VerticalAlternatesForRotationMask = 1U << 1;

    /// <summary>
    /// The fixed mask bit for the vertical kerning feature.
    /// </summary>
    public const uint VerticalKerningMask = 1U << 2;

    /// <summary>
    /// The combined mask of the three vertical alternate features, constant across
    /// plans by the reserved-bit contract above.
    /// </summary>
    public const uint VerticalFeatureMask = VerticalAlternatesMask | VerticalAlternatesForRotationMask | VerticalKerningMask;

    /// <summary>
    /// The single mask bit shared by every global feature: one that applies to all
    /// glyphs of the plan's segments. Every planned glyph carries this bit, so the
    /// lookups of any number of global features match with one bit between them and
    /// the distinct bits are kept for features whose per-glyph state varies.
    /// </summary>
    public const uint GlobalFeatureMask = 1U << 31;

    /// <summary>
    /// The first bit available to varying features; lower bits stay clear of the
    /// applied-mask space's reserved trio so a mask value's space is identifiable
    /// at a glance when debugging.
    /// </summary>
    private const int FirstAssignableBit = 3;

    /// <summary>
    /// The number of assignable varying-feature bits: everything between the first
    /// assignable bit and the reserved global bit.
    /// </summary>
    private const int AssignableBitCount = 31 - FirstAssignableBit;

    /// <summary>
    /// The varying feature tag values, indexed by bit position above the reserved
    /// bits. Stored as raw <see cref="uint"/> values so lookups take the runtime's
    /// vectorized primitive search path.
    /// </summary>
    private readonly List<uint> featureTags = new(16);

    /// <summary>
    /// The global feature tag values; each resolves to the shared
    /// <see cref="GlobalFeatureMask"/> bit.
    /// </summary>
    private readonly List<uint> globalTags = new(32);

    /// <summary>
    /// The disabled feature tag values. A disabled feature resolves to a zero mask
    /// and never re-registers, so its lookups are never collected and its state
    /// survives repeated per-segment planning.
    /// </summary>
    private readonly List<uint> disabledTags = new(1);

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
    private uint lastMask;

    /// <summary>
    /// Gets the fixed vertical-trio mask bit for a feature, or zero for any other
    /// feature. Applied-mask consumers only ever read the vertical bits, which are
    /// reserved and constant across plans, so applied recording needs no plan state.
    /// </summary>
    /// <param name="tag">The feature tag whose lookups applied.</param>
    /// <returns>The fixed vertical mask bit, or zero.</returns>
    public static uint GetVerticalMask(Tag tag)
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
    /// Gets the mask for the given feature tag: a varying feature's distinct bit,
    /// the shared global bit for a global feature, or zero when the tag is disabled
    /// or unknown. A zero result is safe at every consumption site: testing it
    /// enables or matches nothing and clearing it clears nothing.
    /// </summary>
    /// <param name="tag">The feature tag.</param>
    /// <returns>The feature's mask, or zero.</returns>
    public uint GetMask(Tag tag)
    {
        if (tag.Value == this.lastTagValue)
        {
            return this.lastMask;
        }

        if (this.disabledTags.Count > 0 && this.disabledTags.Contains(tag.Value))
        {
            return 0;
        }

        int index = this.featureTags.IndexOf(tag.Value);
        if (index >= 0)
        {
            uint mask = 1U << (FirstAssignableBit + index);
            this.lastTagValue = tag.Value;
            this.lastMask = mask;
            return mask;
        }

        if (this.globalTags.Contains(tag.Value))
        {
            this.lastTagValue = tag.Value;
            this.lastMask = GlobalFeatureMask;
            return GlobalFeatureMask;
        }

        // A zero mask is never memoized: the tag may gain an assignment later and
        // the memo must not serve a stale zero after that.
        return 0;
    }

    /// <summary>
    /// Gets the distinct bit for a varying feature, assigning the next free bit
    /// when the tag has none. A feature currently classified global is demoted to
    /// varying, keeping the terminal classification: repeated per-segment planning
    /// re-registers the same features, and a demoted feature must stay demoted. A
    /// disabled feature stays disabled, and a plan that has exhausted its bits
    /// assigns nothing; both cases return zero, which registers nothing, enables
    /// nothing, and matches nothing at every consumption site.
    /// </summary>
    /// <param name="tag">The feature tag.</param>
    /// <returns>The distinct bit, or zero when disabled or exhausted.</returns>
    public uint GetOrAddMask(Tag tag)
    {
        if (this.disabledTags.Count > 0 && this.disabledTags.Contains(tag.Value))
        {
            return 0;
        }

        int index = this.featureTags.IndexOf(tag.Value);
        if (index >= 0)
        {
            uint mask = 1U << (FirstAssignableBit + index);
            this.lastTagValue = tag.Value;
            this.lastMask = mask;
            return mask;
        }

        // Demote a global registration: removal is a no-op when the tag was never
        // global. The allocation below then gives the feature its distinct bit.
        this.globalTags.Remove(tag.Value);

        if (this.featureTags.Count == AssignableBitCount)
        {
            return 0;
        }

        this.featureTags.Add(tag.Value);
        uint added = 1U << (FirstAssignableBit + this.featureTags.Count - 1);
        this.lastTagValue = tag.Value;
        this.lastMask = added;
        return added;
    }

    /// <summary>
    /// Gets the shared global bit for a feature that applies to every glyph,
    /// recording the tag as global when it is new to this plan. A feature already
    /// demoted to varying keeps its distinct bit and a disabled feature stays
    /// disabled: both classifications are terminal so repeated per-segment planning
    /// converges instead of oscillating.
    /// </summary>
    /// <param name="tag">The feature tag.</param>
    /// <returns>The feature's mask, or zero when the feature is disabled.</returns>
    public uint GetOrAddGlobalMask(Tag tag)
    {
        if (this.disabledTags.Count > 0 && this.disabledTags.Contains(tag.Value))
        {
            return 0;
        }

        int index = this.featureTags.IndexOf(tag.Value);
        if (index >= 0)
        {
            uint mask = 1U << (FirstAssignableBit + index);
            this.lastTagValue = tag.Value;
            this.lastMask = mask;
            return mask;
        }

        if (!this.globalTags.Contains(tag.Value))
        {
            this.globalTags.Add(tag.Value);
        }

        this.lastTagValue = tag.Value;
        this.lastMask = GlobalFeatureMask;
        return GlobalFeatureMask;
    }

    /// <summary>
    /// Disables a feature for the whole plan: its mask resolves to zero from now
    /// on, so no lookups are collected for it and no per-glyph state can enable it.
    /// Disabling is terminal; later registrations of the tag do not resurrect it.
    /// </summary>
    /// <param name="tag">The feature tag.</param>
    public void DisableFeature(Tag tag)
    {
        this.globalTags.Remove(tag.Value);
        if (!this.disabledTags.Contains(tag.Value))
        {
            this.disabledTags.Add(tag.Value);
        }

        if (this.lastTagValue == tag.Value)
        {
            this.lastTagValue = 0;
            this.lastMask = 0;
        }
    }
}
