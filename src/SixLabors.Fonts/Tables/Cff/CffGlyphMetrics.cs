// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Collections.Concurrent;
using System.Numerics;
using SixLabors.Fonts.Rendering;
using SixLabors.Fonts.Tables.TrueType.Hinting;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// Represents a glyph metric from a particular Compact Font Face.
/// </summary>
internal class CffGlyphMetrics : FontGlyphMetrics
{
    private CffGlyphData glyphData;

    /// <summary>
    /// Buffered upright outline copies keyed by pixel size and hinting mode, mirroring the
    /// TrueType scaled outline cache: placement, synthetic oblique, layout rotation and
    /// origin apply per point at replay time so one buffered copy serves every run and
    /// positioned offset. Allocated on first render because shaping and measurement clone
    /// metrics without ever rendering them.
    /// </summary>
    private ConcurrentDictionary<ScaledOutlineKey, CffOutline>? scaledOutlineCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="CffGlyphMetrics"/> class with text attribute parameters.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="glyphId">The glyph identifier.</param>
    /// <param name="codePoint">The Unicode code point.</param>
    /// <param name="glyphData">The CFF glyph data containing the charstring program.</param>
    /// <param name="bounds">The glyph bounding box.</param>
    /// <param name="advanceWidth">The advance width.</param>
    /// <param name="advanceHeight">The advance height.</param>
    /// <param name="leftSideBearing">The left side bearing.</param>
    /// <param name="topSideBearing">The top side bearing.</param>
    /// <param name="unitsPerEM">The units per em.</param>
    /// <param name="textAttributes">The text attributes.</param>
    /// <param name="textDecorations">The text decorations.</param>
    /// <param name="glyphType">The glyph type.</param>
    public CffGlyphMetrics(
        StreamFontMetrics fontMetrics,
        ushort glyphId,
        CodePoint codePoint,
        CffGlyphData glyphData,
        Bounds bounds,
        ushort advanceWidth,
        ushort advanceHeight,
        short leftSideBearing,
        short topSideBearing,
        ushort unitsPerEM,
        TextAttributes textAttributes,
        TextDecorations textDecorations,
        GlyphType glyphType)
        : base(
              fontMetrics,
              glyphId,
              codePoint,
              bounds,
              advanceWidth,
              advanceHeight,
              leftSideBearing,
              topSideBearing,
              unitsPerEM,
              textAttributes,
              textDecorations,
              glyphType)
        => this.glyphData = glyphData;

    /// <inheritdoc/>
    internal override void RenderOutlineTo(
        IGlyphRenderer renderer,
        Vector2 glyphOrigin,
        GlyphLayoutMode mode,
        TextRun? textRun,
        Vector2 positionOffset,
        Vector2 positionedAdvance,
        float scaledPPEM,
        HintingMode hintingMode)
    {
        Matrix3x2 transform = this.GetOutlineTransform(mode, textRun);
        Vector2 scale = this.GetOutlineScale(scaledPPEM);

        // Charstrings evaluate once per size into a buffered outline; every render replays
        // it through a unit scale transforming renderer, which reproduces the streaming
        // arithmetic exactly. Each mode shapes the outline differently: unhinted geometry,
        // vertical only fitting, or full grid fitting.
        ConcurrentDictionary<ScaledOutlineKey, CffOutline> cache =
            LazyInitializer.EnsureInitialized(ref this.scaledOutlineCache, static () => new());
        CffOutline outline = cache.GetOrAdd(new ScaledOutlineKey(scaledPPEM, hintingMode), static (key, self) => self.CreateScaledOutline(key), this);

        Vector2 scaledOffset = (this.Offset + positionOffset) * scale;

        // Full hinting aligns the outline to the pixel grid in glyph space; adjusting the
        // origin so the composed translation lands on whole pixels preserves that grid in
        // device space. The adjustment mirrors the exact per point arithmetic of the
        // transforming renderer, so replay stays sign exact. Snapping only applies to
        // upright, untransformed renders where the grid survives.
        if (hintingMode == HintingMode.Full && outline.IsFitted && transform.IsIdentity)
        {
            Vector2 composed = (scaledOffset * new Vector2(1F, -1F)) + glyphOrigin;
            Vector2 snapped = new(MathF.Floor(composed.X + 0.5F), MathF.Floor(composed.Y + 0.5F));
            glyphOrigin += snapped - composed;
        }

        float boldStrength = this.GetSyntheticBoldStrength(scaledPPEM, textRun);
        if (boldStrength > 0F)
        {
            // Flush through the supplied renderer before glyph completion so skip-ink observes
            // the same synthesized outline as the drawing renderer.
            EmboldeningGlyphRenderer target = EmboldeningGlyphRenderer.Rent(renderer, boldStrength);
            try
            {
                TransformingGlyphRenderer transforming = new(target, glyphOrigin, Vector2.One, scaledOffset, transform);
                outline.ReplayTo(ref transforming);
                target.CompleteOutline();
            }
            finally
            {
                target.Release();
            }
        }
        else
        {
            TransformingGlyphRenderer transforming = new(renderer, glyphOrigin, Vector2.One, scaledOffset, transform);
            outline.ReplayTo(ref transforming);
        }
    }

    /// <summary>
    /// Computes the pixels per design unit scale for the glyph, folding in the CFF font
    /// matrix. The normalized font matrix is identity for the default
    /// [0.001, 0, 0, 0.001, 0, 0] with one thousand units per em.
    /// </summary>
    /// <param name="scaledPPEM">The scaled size to render/measure the glyph at.</param>
    /// <returns>The per axis scale.</returns>
    private Vector2 GetOutlineScale(float scaledPPEM)
    {
        Vector2 scale = new Vector2(scaledPPEM) / this.ScaleFactor;
        if (this.glyphData.FontMatrix is double[] fm)
        {
            float upm = this.UnitsPerEm;
            scale *= new Vector2((float)(fm[0] * upm), (float)(fm[3] * upm));
        }

        return scale;
    }

    /// <summary>
    /// Returns the size to render/measure the glyph at. Under full hinting the em square is
    /// constrained to whole pixels, matching the classic rasterizers the declared hinting
    /// values were authored for.
    /// </summary>
    /// <param name="pointSize">The font size in pt units.</param>
    /// <param name="dpi">The DPI (Dots Per Inch) to render/measure the glyph at</param>
    /// <param name="hintingMode">The hinting mode, which may constrain the size to whole pixels.</param>
    /// <returns>The <see cref="float"/>.</returns>
    internal override float GetScaledSize(float pointSize, float dpi, HintingMode hintingMode)
    {
        float scaledPPEM = base.GetScaledSize(pointSize, dpi, hintingMode);
        if (hintingMode == HintingMode.Full)
        {
            scaledPPEM = MathF.Max(1F, MathF.Floor((scaledPPEM / 72F) + 0.5F)) * 72F;
        }

        return scaledPPEM;
    }

    /// <summary>
    /// Builds the buffered outline cached per pixel size and hinting mode, aligning the
    /// mode semantics with the TrueType interpreter: unhinted geometry stays untouched,
    /// standard hinting fits the vertical axis only from the declared horizontal stem
    /// zones and blue zone flats, and full hinting fits both axes. The anchor array is
    /// font level state precomputed at parse time, so fitting allocates nothing here.
    /// </summary>
    /// <param name="key">The cache key carrying the pixel size and hinting mode.</param>
    /// <returns>The buffered <see cref="CffOutline"/>.</returns>
    private CffOutline CreateScaledOutline(ScaledOutlineKey key)
    {
        Vector2 scale = this.GetOutlineScale(key.ScaledPPEM);
        CffOutline outline = this.glyphData.BuildOutline(scale);

        float pixelSize = key.ScaledPPEM / 72F;
        if (key.HintingMode != HintingMode.None && pixelSize <= GlyphGridFitter.MaxFitPixelsPerEm)
        {
            GridFitAxisMode fitX = key.HintingMode == HintingMode.Full ? GridFitAxisMode.Full : GridFitAxisMode.None;
            float[] topAnchors = this.glyphData.HintingValues?.BlueFlats ?? [];
            GridFitOptions options = new(pixelSize, fitX, GridFitAxisMode.Full, topAnchors, scale.Y);
            if (GlyphGridFitter.FitInPlace(outline.Points, outline.ContourEnds, outline.VerticalStems, outline.HorizontalStems, in options))
            {
                outline.IsFitted = true;
            }
        }

        return outline;
    }

    /// <summary>
    /// Identifies a cached buffered outline by pixel size and the hinting mode that shaped
    /// it. Modes producing identical geometry are normalized onto one key before lookup.
    /// </summary>
    private readonly struct ScaledOutlineKey : IEquatable<ScaledOutlineKey>
    {
        public ScaledOutlineKey(float scaledPPEM, HintingMode hintingMode)
        {
            this.ScaledPPEM = scaledPPEM;
            this.HintingMode = hintingMode;
        }

        public float ScaledPPEM { get; }

        public HintingMode HintingMode { get; }

        public bool Equals(ScaledOutlineKey other) => this.ScaledPPEM == other.ScaledPPEM && this.HintingMode == other.HintingMode;

        public override bool Equals(object? obj) => obj is ScaledOutlineKey other && this.Equals(other);

        public override int GetHashCode() => HashCode.Combine(this.ScaledPPEM, this.HintingMode);
    }
}
