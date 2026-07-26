// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// Default shaper, which will be applied to all glyphs.
/// Based on fontkit: <see href="https://github.com/foliojs/fontkit/blob/master/src/opentype/shapers/DefaultShaper.js"/>
/// </summary>
internal class DefaultShaper : BaseShaper
{
    /// <summary>
    /// The 'rvrn' (required variation alternates) feature tag.
    /// </summary>
    protected static readonly Tag RvnrTag = Tag.Parse("rvrn");

    /// <summary>
    /// The 'ltra' (left-to-right alternates) feature tag.
    /// </summary>
    protected static readonly Tag LtraTag = Tag.Parse("ltra");

    /// <summary>
    /// The 'ltrm' (left-to-right mirrored forms) feature tag.
    /// </summary>
    protected static readonly Tag LtrmTag = Tag.Parse("ltrm");

    /// <summary>
    /// The 'rtla' (right-to-left alternates) feature tag.
    /// </summary>
    protected static readonly Tag RtlaTag = Tag.Parse("rtla");

    /// <summary>
    /// The 'rtlm' (right-to-left mirrored forms) feature tag.
    /// </summary>
    protected static readonly Tag RtlmTag = Tag.Parse("rtlm");

    /// <summary>
    /// The 'frac' (fractions) feature tag.
    /// </summary>
    protected static readonly Tag FracTag = Tag.Parse("frac");

    /// <summary>
    /// The 'numr' (numerators) feature tag.
    /// </summary>
    protected static readonly Tag NumrTag = Tag.Parse("numr");

    /// <summary>
    /// The 'dnom' (denominators) feature tag.
    /// </summary>
    protected static readonly Tag DnomTag = Tag.Parse("dnom");

    /// <summary>
    /// The 'ccmp' (glyph composition/decomposition) feature tag.
    /// </summary>
    protected static readonly Tag CcmpTag = Tag.Parse("ccmp");

    /// <summary>
    /// The 'locl' (localized forms) feature tag.
    /// </summary>
    protected static readonly Tag LoclTag = Tag.Parse("locl");

    /// <summary>
    /// The 'rlig' (required ligatures) feature tag.
    /// </summary>
    protected static readonly Tag RligTag = Tag.Parse("rlig");

    /// <summary>
    /// The 'mark' (mark positioning) feature tag.
    /// </summary>
    protected static readonly Tag MarkTag = Tag.Parse("mark");

    /// <summary>
    /// The 'mkmk' (mark-to-mark positioning) feature tag.
    /// </summary>
    protected static readonly Tag MkmkTag = Tag.Parse("mkmk");

    /// <summary>
    /// The 'calt' (contextual alternates) feature tag.
    /// </summary>
    protected static readonly Tag CaltTag = Tag.Parse("calt");

    /// <summary>
    /// The 'clig' (contextual ligatures) feature tag.
    /// </summary>
    protected static readonly Tag CligTag = Tag.Parse("clig");

    /// <summary>
    /// The 'liga' (standard ligatures) feature tag.
    /// </summary>
    protected static readonly Tag LigaTag = Tag.Parse("liga");

    /// <summary>
    /// The 'rclt' (required contextual alternates) feature tag.
    /// </summary>
    protected static readonly Tag RcltTag = Tag.Parse("rclt");

    /// <summary>
    /// The 'curs' (cursive positioning) feature tag.
    /// </summary>
    protected static readonly Tag CursTag = Tag.Parse("curs");

    /// <summary>
    /// The 'kern' (kerning) feature tag.
    /// </summary>
    protected static readonly Tag KernTag = Tag.Parse("kern");

    /// <summary>
    /// The 'vert' (vertical alternates) feature tag.
    /// </summary>
    protected static readonly Tag VertTag = Tag.Parse("vert");

    /// <summary>
    /// The 'vkrn' (vertical kerning) feature tag.
    /// </summary>
    protected static readonly Tag VKernTag = Tag.Parse("vkrn");

    /// <summary>
    /// The fraction slash code point (U+2044).
    /// </summary>
    private static readonly CodePoint FractionSlash = new(0x2044);

    /// <summary>
    /// The solidus (slash) code point (U+002F).
    /// </summary>
    private static readonly CodePoint Slash = new(0x002F);

    /// <summary>
    /// The shaping stages accumulated during feature planning, in registration order.
    /// Stage counts are small (≤ ~16), so duplicate suppression scans the list by tag;
    /// this keeps application order deterministic and avoids per-call hashing.
    /// </summary>
    private readonly List<ShapingStage> shapingStages = new(16);

    /// <summary>
    /// The kerning mode from the text options.
    /// </summary>
    private readonly KerningMode kerningMode;

    /// <summary>
    /// The user-specified feature tags from the text options.
    /// </summary>
    private readonly IReadOnlyList<Tag> featureTags;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultShaper"/> class with PostGpos mark zeroing.
    /// </summary>
    /// <param name="script">The script classification.</param>
    /// <param name="textOptions">The text options.</param>
    public DefaultShaper(ScriptClass script, TextOptions textOptions)
        : this(script, MarkZeroingMode.PostGpos, textOptions)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultShaper"/> class.
    /// </summary>
    /// <param name="script">The script classification.</param>
    /// <param name="markZeroingMode">The mark zeroing mode.</param>
    /// <param name="textOptions">The text options.</param>
    protected DefaultShaper(ScriptClass script, MarkZeroingMode markZeroingMode, TextOptions textOptions)
    {
        this.ScriptClass = script;
        this.MarkZeroingMode = markZeroingMode;
        this.kerningMode = textOptions.KerningMode;
        this.featureTags = textOptions.FeatureTags;
    }

    /// <inheritdoc />
    protected override void PlanFeatures(ShapingBuffer buffer, int index, int count)
    {
    }

    /// <inheritdoc />
    protected override void PlanPreprocessingFeatures(ShapingBuffer buffer, int index, int count)
    {
        // Add variation Features.
        this.EnableFeature(buffer, index, count, RvnrTag);

        // Add directional features once per direction span. A segment may span
        // direction runs, so these stay varying features whose bits cover only
        // their own span; a global classification would let one direction's
        // lookups match glyphs of the other.
        int end = index + count;
        int spanStart = index;
        while (spanStart < end)
        {
            TextDirection direction = buffer[spanStart].Direction;
            int spanEnd = spanStart + 1;
            while (spanEnd < end && buffer[spanEnd].Direction == direction)
            {
                spanEnd++;
            }

            int spanCount = spanEnd - spanStart;
            if (direction == TextDirection.LeftToRight)
            {
                this.AddFeature(buffer, spanStart, spanCount, LtraTag);
                this.AddFeature(buffer, spanStart, spanCount, LtrmTag);
            }
            else
            {
                this.AddFeature(buffer, spanStart, spanCount, RtlaTag);
                this.AddFeature(buffer, spanStart, spanCount, RtlmTag);
            }

            spanStart = spanEnd;
        }

        // TODO: Fractional feature should be assigned here but disabled.
        // They should then be enabled in AssignFeatures.
    }

    /// <inheritdoc />
    protected override void PlanPostprocessingFeatures(ShapingBuffer buffer, int index, int count)
    {
        // Add common features.
        this.EnableFeature(buffer, index, count, CcmpTag);
        this.EnableFeature(buffer, index, count, LoclTag);
        this.EnableFeature(buffer, index, count, RligTag);
        this.EnableFeature(buffer, index, count, MarkTag);
        this.EnableFeature(buffer, index, count, MkmkTag);

        LayoutMode layoutMode = buffer.TextOptions.LayoutMode;
        bool isVerticalLayout = false;
        for (int i = index; i < index + count; i++)
        {
            ref GlyphShapingData shapingData = ref buffer[i];
            isVerticalLayout |= AdvancedTypographicUtils.IsVerticalGlyph(shapingData.CodePoint, layoutMode);
        }

        // Add horizontal or vertical features.
        if (!isVerticalLayout)
        {
            // Add horizontal features.
            this.EnableFeature(buffer, index, count, CaltTag);
            this.EnableFeature(buffer, index, count, CligTag);
            this.EnableFeature(buffer, index, count, LigaTag);
            this.EnableFeature(buffer, index, count, RcltTag);
            this.EnableFeature(buffer, index, count, CursTag);
            this.EnableFeature(buffer, index, count, KernTag);
        }
        else
        {
            // We only apply `vert` feature.See:
            // https://github.com/harfbuzz/harfbuzz/commit/d71c0df2d17f4590d5611239577a6cb532c26528
            // https://lists.freedesktop.org/archives/harfbuzz/2013-August/003490.html

            // We really want to find a 'vert' feature if there's any in the font, no
            // matter which script/langsys it is listed (or not) under.
            // See various bugs referenced from:
            // https://github.com/harfbuzz/harfbuzz/issues/63
            this.EnableFeature(buffer, index, count, VertTag);
        }

        // Add user defined features.
        foreach (Tag feature in this.featureTags)
        {
            // We've already dealt with fractional features.
            if (feature != FracTag && feature != NumrTag && feature != DnomTag)
            {
                this.EnableFeature(buffer, index, count, feature);
            }
        }
    }

    /// <inheritdoc />
    protected override void AssignFeatures(ShapingBuffer buffer, int index, int count)
    {
        // TODO: We shouldn't be relying on the feature list
        // User defined fractional features require special treatment.
        // https://docs.microsoft.com/en-us/typography/opentype/spec/features_fj#tag-frac
        if (this.HasFractions())
        {
            this.AssignFractionalFeatures(buffer, index, count);
        }
    }

    /// <summary>
    /// Adds a shaping feature to the specified range of glyphs in the buffer and registers the corresponding shaping stage.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the first element.</param>
    /// <param name="count">The number of elements.</param>
    /// <param name="feature">The feature tag to add.</param>
    /// <param name="enabled">Whether the feature is initially enabled.</param>
    /// <param name="preAction">An optional action to invoke before the feature is applied.</param>
    /// <param name="postAction">An optional action to invoke after the feature is applied.</param>
    protected void AddFeature(
        ShapingBuffer buffer,
        int index,
        int count,
        Tag feature,
        bool enabled = true,
        Action<ShapePlan, ShapingBuffer, int, int>? preAction = null,
        Action<ShapePlan, ShapingBuffer, int, int>? postAction = null)
    {
        if (this.kerningMode == KerningMode.None)
        {
            if (feature == KernTag || feature == VKernTag)
            {
                return;
            }
        }

        buffer.AddShapingFeatureRange(index, count, new TagEntry(feature, enabled), this.Features.GetOrAddMask(feature));
        this.AddStage(feature, preAction, postAction);
    }

    /// <summary>
    /// Adds a varying shaping feature with registration flags controlling its
    /// lookups' joiner handling and syllable scope.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the first element.</param>
    /// <param name="count">The number of elements.</param>
    /// <param name="feature">The feature tag to add.</param>
    /// <param name="flags">The registration flags.</param>
    /// <param name="enabled">Whether the feature is initially enabled.</param>
    /// <param name="preAction">The action to invoke before the feature is applied, or <see langword="null"/>.</param>
    /// <param name="postAction">The action to invoke after the feature is applied, or <see langword="null"/>.</param>
    protected void AddFeature(
        ShapingBuffer buffer,
        int index,
        int count,
        Tag feature,
        ShapingFeatureFlags flags,
        bool enabled,
        Action<ShapePlan, ShapingBuffer, int, int>? preAction,
        Action<ShapePlan, ShapingBuffer, int, int>? postAction)
    {
        this.Features.AddFlags(feature, flags);
        this.AddFeature(buffer, index, count, feature, enabled, preAction, postAction);
    }

    /// <summary>
    /// Registers a global feature over the given range: one that applies to every
    /// glyph of the plan's segments and therefore shares the plan's single global
    /// mask bit instead of consuming a distinct bit. Features whose per-glyph
    /// state varies register through the add-feature overloads instead.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the first element.</param>
    /// <param name="count">The number of elements.</param>
    /// <param name="feature">The feature tag to enable.</param>
    protected void EnableFeature(ShapingBuffer buffer, int index, int count, Tag feature)
        => this.EnableFeature(buffer, index, count, feature, null, null);

    /// <summary>
    /// Registers a global feature with registration flags controlling its lookups'
    /// joiner handling and syllable scope.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the first element.</param>
    /// <param name="count">The number of elements.</param>
    /// <param name="feature">The feature tag to enable.</param>
    /// <param name="flags">The registration flags.</param>
    protected void EnableFeature(ShapingBuffer buffer, int index, int count, Tag feature, ShapingFeatureFlags flags)
    {
        this.Features.AddFlags(feature, flags);
        this.EnableFeature(buffer, index, count, feature, null, null);
    }

    /// <summary>
    /// Registers a global feature with registration flags and stage actions.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the first element.</param>
    /// <param name="count">The number of elements.</param>
    /// <param name="feature">The feature tag to enable.</param>
    /// <param name="flags">The registration flags.</param>
    /// <param name="preAction">The action to invoke before the feature is applied, or <see langword="null"/>.</param>
    /// <param name="postAction">The action to invoke after the feature is applied, or <see langword="null"/>.</param>
    protected void EnableFeature(
        ShapingBuffer buffer,
        int index,
        int count,
        Tag feature,
        ShapingFeatureFlags flags,
        Action<ShapePlan, ShapingBuffer, int, int>? preAction,
        Action<ShapePlan, ShapingBuffer, int, int>? postAction)
    {
        this.Features.AddFlags(feature, flags);
        this.EnableFeature(buffer, index, count, feature, preAction, postAction);
    }

    /// <summary>
    /// Registers a global feature and attaches the supplied stage actions. The
    /// feature applies to every glyph of the plan's segments and therefore shares
    /// the plan's single global mask bit, which every glyph record is born
    /// carrying: registration records the classification and the stage without
    /// touching any glyph.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the first element.</param>
    /// <param name="count">The number of elements.</param>
    /// <param name="feature">The feature tag to enable.</param>
    /// <param name="preAction">The action to invoke before the feature is applied, or <see langword="null"/>.</param>
    /// <param name="postAction">The action to invoke after the feature is applied, or <see langword="null"/>.</param>
    protected void EnableFeature(
        ShapingBuffer buffer,
        int index,
        int count,
        Tag feature,
        Action<ShapePlan, ShapingBuffer, int, int>? preAction,
        Action<ShapePlan, ShapingBuffer, int, int>? postAction)
    {
        if (this.kerningMode == KerningMode.None)
        {
            if (feature == KernTag || feature == VKernTag)
            {
                return;
            }
        }

        _ = this.Features.GetOrAddGlobalMask(feature);
        this.AddStage(feature, preAction, postAction);
    }

    /// <summary>
    /// Appends a shaping stage for the feature unless one already exists. The
    /// first registration wins, matching the previous set semantics: a duplicate
    /// tag keeps the originally supplied pre and post actions.
    /// </summary>
    /// <param name="feature">The feature tag the stage applies.</param>
    /// <param name="preAction">The action to invoke before the feature is applied, or <see langword="null"/>.</param>
    /// <param name="postAction">The action to invoke after the feature is applied, or <see langword="null"/>.</param>
    private void AddStage(
        Tag feature,
        Action<ShapePlan, ShapingBuffer, int, int>? preAction,
        Action<ShapePlan, ShapingBuffer, int, int>? postAction)
    {
        List<ShapingStage> stages = this.shapingStages;
        for (int i = 0; i < stages.Count; i++)
        {
            if (stages[i].FeatureTag == feature)
            {
                return;
            }
        }

        stages.Add(new ShapingStage(feature, preAction, postAction));
    }

    /// <inheritdoc />
    public override List<ShapingStage> GetShapingStages() => this.shapingStages;

    /// <summary>
    /// Assigns fractional feature tags (numerator, denominator, fraction) to glyphs forming fraction sequences.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the first element.</param>
    /// <param name="count">The number of elements.</param>
    private void AssignFractionalFeatures(ShapingBuffer buffer, int index, int count)
    {
        // Enable contextual fractions.
        for (int i = index; i < index + count; i++)
        {
            ref GlyphShapingData shapingData = ref buffer[i];
            if (shapingData.CodePoint == FractionSlash || shapingData.CodePoint == Slash)
            {
                int start = i;
                int end = i + 1;

                // Apply numerator.
                if (start > 0)
                {
                    CodePoint numeratorCodePoint = buffer[start - 1].CodePoint;
                    while (start > 0 && CodePoint.IsDigit(numeratorCodePoint))
                    {
                        this.AddFeature(buffer, start - 1, 1, NumrTag);
                        this.AddFeature(buffer, start - 1, 1, FracTag);
                        start--;
                    }
                }

                // Apply denominator.
                if (end < buffer.Count)
                {
                    CodePoint denominatorCodePoint = buffer[end].CodePoint;
                    while (end < buffer.Count && CodePoint.IsDigit(denominatorCodePoint))
                    {
                        this.AddFeature(buffer, end, 1, DnomTag);
                        this.AddFeature(buffer, end, 1, FracTag);
                        end++;
                    }
                }

                // Apply fraction slash.
                this.AddFeature(buffer, i, 1, FracTag);
                i = end - 1;
            }
        }
    }

    /// <summary>
    /// Determines whether the user-specified feature tags include fractional features.
    /// </summary>
    /// <returns><see langword="true"/> if fractional features are present; otherwise, <see langword="false"/>.</returns>
    private bool HasFractions()
    {
        bool hasNmr = false;
        bool hasDnom = false;

        // My kingdom for a binary search on IReadOnlyList
        for (int i = 0; i < this.featureTags.Count; i++)
        {
            Tag feature = this.featureTags[i];
            if (feature == FracTag)
            {
                return true;
            }

            if (feature == DnomTag)
            {
                hasDnom = true;
            }

            if (feature == NumrTag)
            {
                hasNmr = true;
            }

            if (hasDnom && hasNmr)
            {
                return true;
            }
        }

        return false;
    }
}
