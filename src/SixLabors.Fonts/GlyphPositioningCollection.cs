// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using SixLabors.Fonts.Tables.AdvancedTypographic;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts;

/// <summary>
/// Represents a collection of glyph metrics that are mapped to input codepoints.
/// </summary>
internal sealed class GlyphPositioningCollection : GlyphShapingCollection
{
    /// <summary>
    /// Contains a map the index of a map within the collection, non-sequential codepoint offsets, and their glyph ids, point size, and mtrics.
    /// </summary>
    private readonly List<GlyphPositioningData> glyphs = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="GlyphPositioningCollection"/> class.
    /// </summary>
    /// <param name="textOptions">The text options.</param>
    /// <param name="featureMap">The feature bit assignment shared by the shaping pass.</param>
    public GlyphPositioningCollection(TextOptions textOptions, ShapingFeatureMap featureMap)
        : base(textOptions, featureMap)
    {
    }

    /// <inheritdoc />
    public override int Count => this.glyphs.Count;

    /// <inheritdoc />
    public override GlyphShapingData this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => this.glyphs[index].Data;
    }

    /// <summary>
    /// Gets the full positioning data at the given index.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    /// <returns>The positioning data.</returns>
    public GlyphPositioningData GetPositioningData(int index) => this.glyphs[index];

    /// <summary>
    /// Resets the collection for reuse by a new shaping pass, returning its glyph data
    /// instances to the pool. This collection owns the pass's final instances (the
    /// substitution collection transfers ownership during metrics population), so this
    /// is the single point instances are pooled, keeping each instance pooled at most
    /// once.
    /// </summary>
    /// <param name="textOptions">The text options for the new pass.</param>
    /// <param name="pool">The pool receiving the retired instances.</param>
    internal void ResetForReuse(TextOptions textOptions, List<GlyphShapingData> pool)
    {
        List<GlyphPositioningData> glyphs = this.glyphs;
        for (int i = 0; i < glyphs.Count; i++)
        {
            pool.Add(glyphs[i].Data);
        }

        glyphs.Clear();
        this.ResetCore(textOptions);
    }

    /// <summary>
    /// Updates the collection of glyph ids to the metrics collection to overwrite any glyphs that have been previously
    /// identified as fallbacks.
    /// </summary>
    /// <param name="font">The font face with metrics.</param>
    /// <param name="collection">The glyph substitution collection.</param>
    /// <returns><see langword="true"/> if the metrics collection does not contain any fallbacks; otherwise <see langword="false"/>.</returns>
    public bool TryUpdate(Font font, GlyphSubstitutionCollection collection)
    {
        FontMetrics fontMetrics = font.FontMetrics;
        LayoutMode layoutMode = this.TextOptions.LayoutMode;
        ColorFontSupport colorFontSupport = this.TextOptions.ColorFontSupport;
        bool hasFallBacks = false;
        List<int> orphans = [];

        ulong verticalMask = this.GetVerticalFeatureMask();

        for (int i = 0; i < this.glyphs.Count; i++)
        {
            GlyphPositioningData current = this.glyphs[i];
            if (current.Metrics.GlyphType != GlyphType.Fallback)
            {
                // We've already got the correct glyph.
                continue;
            }

            int offset = current.Offset;
            float pointSize = current.PointSize;
            if (collection.TryGetGlyphShapingDataAtOffset(offset, out IReadOnlyList<GlyphShapingData>? data))
            {
                int replacementCount = 0;
                for (int j = 0; j < data.Count; j++)
                {
                    GlyphShapingData shape = data[j];
                    ushort id = shape.GlyphId;
                    CodePoint codePoint = shape.CodePoint;

                    TextAttributes textAttributes = shape.TextRun.TextAttributes;
                    TextDecorations textDecorations = shape.TextRun.TextDecorations;

                    bool isVertical = AdvancedTypographicUtils.IsVerticalGlyph(codePoint, layoutMode)
                        || (shape.AppliedFeatureMask & verticalMask) != 0;

                    FontGlyphMetrics metrics = fontMetrics.GetGlyphMetrics(codePoint, id, textAttributes, textDecorations, layoutMode, colorFontSupport);
                    {
                        // If the glyphs are fallbacks we don't want them as
                        // we've already captured them on the first run.
                        if (metrics.GlyphType == GlyphType.Fallback && !CodePoint.IsControl(codePoint))
                        {
                            hasFallBacks = true;
                        }
                    }

                    if (metrics.GlyphType != GlyphType.Fallback)
                    {
                        if (replacementCount == 0)
                        {
                            // There should only be a single fallback glyph at this position from the previous collection.
                            this.glyphs.RemoveAt(i);
                        }

                        // We only want a single dimensional advance for positioning.

                        // Track the number of inserted glyphs at the offset so we can correctly increment our position.
                        // The substituted data is reused rather than copied: the
                        // substitution collection releases its instances at the end of
                        // each run, so positioning takes ownership.
                        shape.ClearFeatures();
                        if (isVertical)
                        {
                            shape.Bounds = new(0, 0, 0, metrics.AdvanceHeight);
                        }
                        else
                        {
                            shape.Bounds = new(0, 0, metrics.AdvanceWidth, 0);
                        }

                        this.RecordGlyphId(metrics.GlyphId);
                        this.glyphs.Insert(i += replacementCount, new(offset, shape, font, pointSize, metrics));
                        replacementCount++;
                    }
                }
            }
            else
            {
                // If a font had glyphs but a follow up font also has them and can substitute. e.g ligatures
                // then we end up with orphaned fallbacks. We need to remove them.
                orphans.Add(i);
            }
        }

        // Remove any orphans.
        for (int i = orphans.Count - 1; i >= 0; i--)
        {
            this.glyphs.RemoveAt(orphans[i]);
        }

        return !hasFallBacks;
    }

    /// <summary>
    /// Adds the collection of glyph ids to the metrics collection.
    /// identified as fallbacks.
    /// </summary>
    /// <param name="font">The font face with metrics.</param>
    /// <param name="collection">The glyph substitution collection.</param>
    /// <returns><see langword="true"/> if the metrics collection does not contain any fallbacks; otherwise <see langword="false"/>.</returns>
    public bool TryAdd(Font font, GlyphSubstitutionCollection collection)
    {
        bool hasFallBacks = false;
        FontMetrics fontMetrics = font.FontMetrics;
        LayoutMode layoutMode = this.TextOptions.LayoutMode;
        ColorFontSupport colorFontSupport = this.TextOptions.ColorFontSupport;

        ulong verticalMask = this.GetVerticalFeatureMask();

        for (int i = 0; i < collection.Count; i++)
        {
            GlyphShapingData data = collection.GetGlyphShapingData(i, out int offset);
            CodePoint codePoint = data.CodePoint;
            ushort id = data.GlyphId;

            if (data.IsPlaceholder)
            {
                // Placeholders are synthetic glyphs: they need layout metrics but must not
                // go through font glyph lookup, fallback resolution, or GPOS positioning.
                FontGlyphMetrics placeholderMetrics = PlaceholderGlyphMetrics.Create(font, data.TextRun, this.TextOptions.Dpi);

                GlyphShapingData placeholderData = data;
                placeholderData.ClearFeatures();
                if (layoutMode.IsVertical())
                {
                    placeholderData.Bounds = new(0, 0, 0, placeholderMetrics.AdvanceHeight);
                }
                else
                {
                    placeholderData.Bounds = new(0, 0, placeholderMetrics.AdvanceWidth, 0);
                }

                placeholderData.IsPositioned = true;

                this.RecordGlyphId(placeholderMetrics.GlyphId);
                this.glyphs.Add(new(offset, placeholderData, font, font.Size, placeholderMetrics));
                continue;
            }

            TextAttributes textAttributes = data.TextRun.TextAttributes;
            TextDecorations textDecorations = data.TextRun.TextDecorations;

            bool isVertical = AdvancedTypographicUtils.IsVerticalGlyph(codePoint, layoutMode)
                || (data.AppliedFeatureMask & verticalMask) != 0;

            FontGlyphMetrics metrics = fontMetrics.GetGlyphMetrics(codePoint, id, textAttributes, textDecorations, layoutMode, colorFontSupport);

            if (metrics.GlyphType == GlyphType.Fallback && !CodePoint.IsControl(codePoint))
            {
                hasFallBacks = true;
            }

            // We only want a single dimensional advance for positioning; assigning a
            // fresh bounds value starts dirty tracking clean for GPOS.
            // The substituted data is reused rather than copied: the substitution
            // collection releases its instances at the end of each run, so positioning
            // takes ownership.
            data.ClearFeatures();
            if (isVertical)
            {
                data.Bounds = new(0, 0, 0, metrics.AdvanceHeight);
            }
            else
            {
                data.Bounds = new(0, 0, metrics.AdvanceWidth, 0);
            }

            this.RecordGlyphId(metrics.GlyphId);
            this.glyphs.Add(new(offset, data, font, font.Size, metrics));
        }

        return !hasFallBacks;
    }

    /// <summary>
    /// Marks the glyph at the specified index as positioned. Positions accumulate in the
    /// glyph's shaping bounds and are read from there by consumers, so the shared metrics
    /// instance is never mutated.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    public void UpdatePosition(int index) => this[index].IsPositioned = true;

    /// <summary>
    /// Adds dx and dy to the positioned advance of the glyph at the given index and id.
    /// Advances accumulate in the glyph's shaping bounds so the shared metrics instance
    /// is never mutated.
    /// </summary>
    /// <param name="fontMetrics">The font face with metrics.</param>
    /// <param name="index">The zero-based index of the element.</param>
    /// <param name="glyphId">The id of the glyph to offset.</param>
    /// <param name="dx">The delta x-advance.</param>
    /// <param name="dy">The delta y-advance.</param>
    public void Advance(FontMetrics fontMetrics, int index, ushort glyphId, short dx, short dy)
    {
        LayoutMode layoutMode = this.TextOptions.LayoutMode;
        GlyphPositioningData glyph = this.glyphs[index];
        FontGlyphMetrics m = glyph.Metrics;

        if (m.GlyphId == glyphId && fontMetrics == m.FontMetrics)
        {
            bool isVertical = AdvancedTypographicUtils.IsVerticalGlyph(m.CodePoint, layoutMode)
                || (glyph.Data.AppliedFeatureMask & this.GetVerticalFeatureMask()) != 0;

            // Advance heights grow downward but font-space grows upward, hence the negation.
            glyph.Data.Bounds.Width += dx;
            if (isVertical)
            {
                glyph.Data.Bounds.Height -= dy;
            }
        }
    }

    /// <summary>
    /// Returns a value indicating whether the element at the given index should be processed.
    /// </summary>
    /// <param name="fontMetrics">The font face with metrics.</param>
    /// <param name="index">The zero-based index of the elements to position.</param>
    /// <returns><see langword="true"/> if the element should be processed; otherwise, <see langword="false"/>.</returns>
    public bool ShouldProcess(FontMetrics fontMetrics, int index)
    {
        GlyphPositioningData data = this.glyphs[index];
        if (data.Data.IsPositioned)
        {
            return false;
        }

        return data.Metrics.FontMetrics == fontMetrics;
    }

    /// <summary>
    /// Gets the combined mask of the three vertical alternate features. Computed from
    /// the shared feature map so it stays valid for applied bits written during
    /// substitution and read here after the copy into this collection.
    /// </summary>
    /// <returns>The combined mask, or zero when no vertical feature was registered.</returns>
    internal ulong GetVerticalFeatureMask()
        => this.FeatureMap.GetMask(KnownFeatureTags.VerticalAlternates)
        | this.FeatureMap.GetMask(KnownFeatureTags.VerticalAlternatesAndRotation)
        | this.FeatureMap.GetMask(KnownFeatureTags.VerticalAlternatesForRotation);

    [DebuggerDisplay("{DebuggerDisplay,nq}")]
    public class GlyphPositioningData
    {
        public GlyphPositioningData(int offset, GlyphShapingData data, Font font, float pointSize, FontGlyphMetrics metrics)
        {
            this.Offset = offset;
            this.Data = data;
            this.Font = font;
            this.PointSize = pointSize;
            this.Metrics = metrics;
        }

        public int Offset { get; set; }

        public GlyphShapingData Data { get; set; }

        public Font Font { get; set; }

        public float PointSize { get; set; }

        public FontGlyphMetrics Metrics { get; set; }

        /// <summary>
        /// Gets the positioned horizontal advance in font design units: the shaping bounds
        /// value once positioning has written one, otherwise the metrics advance.
        /// </summary>
        public ushort AdvanceWidth => this.Data.Bounds.IsDirtyWH ? (ushort)this.Data.Bounds.Width : this.Metrics.AdvanceWidth;

        /// <summary>
        /// Gets the positioned vertical advance in font design units: the shaping bounds
        /// value once positioning has written one, otherwise the metrics advance.
        /// </summary>
        public ushort AdvanceHeight => this.Data.Bounds.IsDirtyWH ? (ushort)this.Data.Bounds.Height : this.Metrics.AdvanceHeight;

        /// <summary>
        /// Gets the placement offset written by positioning, in font design units. Geometry
        /// consumers compose it with the metrics offset.
        /// </summary>
        public Vector2 PositionOffset => new(this.Data.Bounds.X, this.Data.Bounds.Y);

        private string DebuggerDisplay => FormattableString.Invariant($"Offset: {this.Offset}, Data: {this.Data.ToDebuggerDisplay()}");
    }
}
