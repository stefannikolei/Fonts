// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Tables.AdvancedTypographic;

namespace SixLabors.Fonts;

/// <summary>
/// Defines the contract for glyph shaping collections.
/// </summary>
internal interface IGlyphShapingCollection
{
    /// <summary>
    /// Gets the collection count.
    /// </summary>
    public int Count { get; }

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
    /// through the collection so the digest observes it; see <see cref="SetGlyphId"/>.
    /// </summary>
    public GlyphSetDigest GlyphDigest { get; }

    /// <summary>
    /// Gets the glyph shaping data at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the elements to get.</param>
    /// <returns>The <see cref="GlyphShapingData"/>.</returns>
    public GlyphShapingData this[int index] { get; }

    /// <summary>
    /// Sets the glyph id at the specified index, recording the id in
    /// <see cref="GlyphDigest"/>. Callers outside the collection must use this rather
    /// than writing <see cref="GlyphShapingData.GlyphId"/> directly, which would leave
    /// the digest unaware of the new id.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    /// <param name="glyphId">The glyph id to set.</param>
    public void SetGlyphId(int index, ushort glyphId);

    /// <summary>
    /// Adds the shaping feature to the collection which should be applied to the glyph at a specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    /// <param name="feature">The feature to apply.</param>
    public void AddShapingFeature(int index, TagEntry feature);

    /// <summary>
    /// Enables a previously added shaping feature.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    /// <param name="feature">The feature to enable.</param>
    public void EnableShapingFeature(int index, Tag feature);

    /// <summary>
    /// Disables a previously added shaping feature.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    /// <param name="feature">The feature to disable.</param>
    public void DisableShapingFeature(int index, Tag feature);
}
