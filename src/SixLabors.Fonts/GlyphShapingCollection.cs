// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;
using SixLabors.Fonts.Tables.AdvancedTypographic;

namespace SixLabors.Fonts;

/// <summary>
/// The base for the glyph shaping collections, owning the state and operations shared
/// by substitution and positioning: the pass-wide feature bit assignment, the resolved
/// language candidates, the glyph id digest, and per-glyph feature mask manipulation.
/// Derived collections own only their storage and the shape-specific mutation APIs.
/// </summary>
internal abstract class GlyphShapingCollection
{
    /// <summary>
    /// The approximate membership filter over every glyph id the collection has ever
    /// contained. See <see cref="GlyphDigest"/> for the growth contract.
    /// </summary>
    private GlyphSetDigest glyphDigest;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlyphShapingCollection"/> class.
    /// </summary>
    /// <param name="textOptions">The text options.</param>
    /// <param name="featureMap">The feature bit assignment shared by the shaping pass.</param>
    protected GlyphShapingCollection(TextOptions textOptions, ShapingFeatureMap featureMap)
    {
        this.TextOptions = textOptions;
        this.FeatureMap = featureMap;

        // A null culture takes the ambient current culture, mirroring the reference
        // shaping engine model where an unset buffer language is guessed from the
        // locale. CultureInfo.InvariantCulture expresses no language preference.
        CultureInfo culture = textOptions.Culture ?? CultureInfo.CurrentCulture;
        this.LanguageTags = OpenTypeLanguageTagMap.TryGetTags(culture, out Tag[] tags) ? tags : [];
    }

    /// <summary>
    /// Gets the collection count.
    /// </summary>
    public abstract int Count { get; }

    /// <summary>
    /// Gets the text options used by this collection.
    /// </summary>
    public TextOptions TextOptions { get; }

    /// <summary>
    /// Gets the candidate OpenType language system tags resolved from
    /// <see cref="TextOptions.Culture"/>, most specific first, or an empty array when the
    /// culture expresses no language preference. Resolved once per shaping pass.
    /// </summary>
    public Tag[] LanguageTags { get; }

    /// <summary>
    /// Gets the feature bit assignment shared by every collection of the shaping pass.
    /// See <see cref="ShapingFeatureMap"/> for the mask model and why the instance must
    /// be shared across the substitution and positioning collections.
    /// </summary>
    public ShapingFeatureMap FeatureMap { get; }

    /// <summary>
    /// Gets the approximate membership filter over every glyph id the collection has
    /// ever contained. The digest only grows: substituted-away ids remain, keeping a
    /// definitive negative from <see cref="GlyphSetDigest.MightIntersect"/> sound while
    /// lookups mutate the collection mid-application. Every glyph id write must funnel
    /// through the collection so the digest observes it; see <see cref="SetGlyphId"/>
    /// and <see cref="RecordGlyphId"/>.
    /// </summary>
    public GlyphSetDigest GlyphDigest => this.glyphDigest;

    /// <summary>
    /// Gets the glyph shaping data at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the elements to get.</param>
    /// <returns>The <see cref="GlyphShapingData"/>.</returns>
    public abstract GlyphShapingData this[int index] { get; }

    /// <summary>
    /// Sets the glyph id at the specified index, recording the id in
    /// <see cref="GlyphDigest"/>. Callers outside the collections must use this rather
    /// than writing <see cref="GlyphShapingData.GlyphId"/> directly, which would leave
    /// the digest unaware of the new id.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    /// <param name="glyphId">The glyph id to set.</param>
    public void SetGlyphId(int index, ushort glyphId)
    {
        this.glyphDigest.Add(glyphId);
        this[index].GlyphId = glyphId;
    }

    /// <summary>
    /// Adds the shaping feature to the collection which should be applied to the glyph at a specified index.
    /// </summary>
    /// <remarks>
    /// Registration only ever accumulates: adding a disabled entry for an already
    /// enabled feature must not clear the enabled bit, matching the list model this
    /// replaced where a disabled duplicate left earlier enabled entries in force.
    /// </remarks>
    /// <param name="index">The zero-based index of the element.</param>
    /// <param name="feature">The feature to apply.</param>
    public void AddShapingFeature(int index, TagEntry feature)
    {
        GlyphShapingData data = this[index];
        ulong mask = this.FeatureMap.GetOrAddMask(feature.Tag);
        data.RegisteredFeatureMask |= mask;
        if (feature.Enabled)
        {
            data.FeatureMask |= mask;
        }
    }

    /// <summary>
    /// Adds the shaping feature to every glyph in the given range, resolving the
    /// feature's mask bit once for the whole range. Shaper plans register each stage
    /// feature across the full run, so the per-glyph work must be a single bitwise OR.
    /// </summary>
    /// <param name="index">The zero-based index of the first element.</param>
    /// <param name="count">The number of elements in the range.</param>
    /// <param name="feature">The feature to apply.</param>
    public void AddShapingFeatureRange(int index, int count, TagEntry feature)
    {
        ulong mask = this.FeatureMap.GetOrAddMask(feature.Tag);
        int end = index + count;
        for (int i = index; i < end; i++)
        {
            GlyphShapingData data = this[i];
            data.RegisteredFeatureMask |= mask;
            if (feature.Enabled)
            {
                data.FeatureMask |= mask;
            }
        }
    }

    /// <summary>
    /// Enables a previously added shaping feature.
    /// </summary>
    /// <remarks>
    /// Intersecting with the registered mask preserves the contract that enabling a
    /// feature a shaper never added for this glyph is a no-op.
    /// </remarks>
    /// <param name="index">The zero-based index of the element.</param>
    /// <param name="feature">The feature to enable.</param>
    public void EnableShapingFeature(int index, Tag feature)
    {
        GlyphShapingData data = this[index];
        data.FeatureMask |= data.RegisteredFeatureMask & this.FeatureMap.GetMask(feature);
    }

    /// <summary>
    /// Disables a previously added shaping feature.
    /// </summary>
    /// <remarks>
    /// An unregistered tag yields a zero mask whose complement clears nothing.
    /// </remarks>
    /// <param name="index">The zero-based index of the element.</param>
    /// <param name="feature">The feature to disable.</param>
    public void DisableShapingFeature(int index, Tag feature)
    {
        GlyphShapingData data = this[index];
        data.FeatureMask &= ~this.FeatureMap.GetMask(feature);
    }

    /// <summary>
    /// Records a glyph id in <see cref="GlyphDigest"/>. Derived collections must call
    /// this from every code path that stores or overwrites a glyph id.
    /// </summary>
    /// <param name="glyphId">The glyph id.</param>
    protected void RecordGlyphId(ushort glyphId) => this.glyphDigest.Add(glyphId);
}
