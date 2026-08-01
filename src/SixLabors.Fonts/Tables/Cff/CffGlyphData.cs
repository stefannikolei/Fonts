// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using SixLabors.Fonts.Rendering;
using SixLabors.Fonts.Tables.AdvancedTypographic.Variations;

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// Represents the data for a single CFF glyph, including the raw charstring program
/// and subroutine references needed for evaluation and rendering.
/// </summary>
internal struct CffGlyphData
{
    private readonly byte[][] globalSubrBuffers;
    private readonly byte[][] localSubrBuffers;
    private readonly byte[] charStrings;
    private readonly int nominalWidthX;
    private readonly int version;
    private readonly ItemVariationStore? itemVariationStore;
    private readonly int vsIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="CffGlyphData"/> struct.
    /// </summary>
    /// <param name="glyphIndex">The glyph index (GID).</param>
    /// <param name="globalSubrBuffers">The global subroutine buffers.</param>
    /// <param name="localSubrBuffers">The local subroutine buffers.</param>
    /// <param name="nominalWidthX">The nominal width bias for charstring width values.</param>
    /// <param name="charStrings">The raw charstring byte data for this glyph.</param>
    /// <param name="version">The CFF version (1 or 2).</param>
    /// <param name="itemVariationStore">The optional item variation store for CFF2 blend operations.</param>
    /// <param name="vsIndex">The variation store index for blend operations.</param>
    public CffGlyphData(
        ushort glyphIndex,
        byte[][] globalSubrBuffers,
        byte[][] localSubrBuffers,
        int nominalWidthX,
        byte[] charStrings,
        int version,
        ItemVariationStore? itemVariationStore = null,
        int vsIndex = 0)
    {
        this.GlyphIndex = glyphIndex;
        this.globalSubrBuffers = globalSubrBuffers;
        this.localSubrBuffers = localSubrBuffers;
        this.nominalWidthX = nominalWidthX;
        this.charStrings = charStrings;
        this.version = version;
        this.itemVariationStore = itemVariationStore;
        this.vsIndex = vsIndex;

        this.GlyphName = null;

        // Variations tables are only present for CFF2 format.
        this.FVar = null;
        this.AVar = null;
        this.GVar = null;
    }

    /// <summary>
    /// Gets the glyph index (GID) within the font.
    /// </summary>
    public ushort GlyphIndex { get; }

    /// <summary>
    /// Gets or sets the glyph name from the charset data.
    /// </summary>
    public string? GlyphName { get; set; }

    /// <summary>
    /// Gets or sets the font variations table for CFF2 variable fonts.
    /// </summary>
    public FVarTable? FVar { get; set; }

    /// <summary>
    /// Gets or sets the axis variations table for CFF2 variable fonts.
    /// </summary>
    public AVarTable? AVar { get; set; }

    /// <summary>
    /// Gets or sets the glyph variations table for TrueType-style glyph variations.
    /// </summary>
    public GVarTable? GVar { get; set; }

    /// <summary>
    /// Gets or sets the FontMatrix that transforms charstring coordinates to design units.
    /// </summary>
    public double[]? FontMatrix { get; set; }

    /// <summary>
    /// Gets or sets the declarative hinting values from the owning Private DICT, or
    /// <see langword="null"/> when the font carries none.
    /// </summary>
    public CffHintingValues? HintingValues { get; set; }

    /// <summary>
    /// Computes the bounding box of this glyph by evaluating the charstring program.
    /// </summary>
    /// <returns>The <see cref="Bounds"/> of the glyph.</returns>
    public readonly Bounds GetBounds()
    {
        using CffEvaluationEngine engine = new(
            this.charStrings,
            this.globalSubrBuffers,
            this.localSubrBuffers,
            this.nominalWidthX,
            this.version,
            this.itemVariationStore,
            this.FVar,
            this.AVar,
            this.vsIndex);

        return engine.GetBounds();
    }

    /// <summary>
    /// Renders this glyph to the specified renderer by evaluating the charstring program.
    /// </summary>
    /// <param name="renderer">The glyph renderer to output path operations to.</param>
    /// <param name="origin">The origin point for rendering.</param>
    /// <param name="scale">The scale factor to apply.</param>
    /// <param name="offset">The offset to apply.</param>
    /// <param name="transform">The transformation matrix to apply.</param>
    public readonly void RenderTo(IGlyphRenderer renderer, Vector2 origin, Vector2 scale, Vector2 offset, Matrix3x2 transform)
    {
        using CffEvaluationEngine engine = new(
             this.charStrings,
             this.globalSubrBuffers,
             this.localSubrBuffers,
             this.nominalWidthX,
             this.version,
             this.itemVariationStore,
             this.FVar,
             this.AVar,
             this.vsIndex);

        engine.RenderTo(renderer, origin, scale, offset, transform);
    }

    /// <summary>
    /// Evaluates the charstring once into a buffered outline in upright pixel space, Y up
    /// with the baseline at zero, collecting the declared stem zones along the way.
    /// Placement, synthetic oblique, rotation and origin apply at replay time, so one
    /// buffered outline serves every render at the size.
    /// </summary>
    /// <param name="scale">The pixels per design unit scale, including the font matrix.</param>
    /// <returns>The buffered <see cref="CffOutline"/>.</returns>
    public readonly CffOutline BuildOutline(Vector2 scale)
    {
        CffOutlineBuilder builder = CffOutlineBuilder.Rent();
        try
        {
            using CffEvaluationEngine engine = new(
                this.charStrings,
                this.globalSubrBuffers,
                this.localSubrBuffers,
                this.nominalWidthX,
                this.version,
                this.itemVariationStore,
                this.FVar,
                this.AVar,
                this.vsIndex);

            // The lists are per cache fill; they become the outline's retained stem arrays.
            List<float> horizontal = [];
            List<float> vertical = [];
            engine.CollectStems(horizontal, vertical);

            // A negated Y scale composed with the streaming sink's Y flip captures points
            // in Y up pixel space through sign exact arithmetic, so replaying them through
            // a unit scale transforming renderer reproduces the streaming path bit for bit.
            engine.RenderTo(builder, Vector2.Zero, new Vector2(scale.X, -scale.Y), Vector2.Zero, Matrix3x2.Identity);
            CffOutline outline = builder.ToOutline(ScaleStems(vertical, scale.X), ScaleStems(horizontal, scale.Y));

            // A counter mask covering three or more stems, and all of them, marks the
            // axis for counter equalization: the glyph was authored for even stem rhythm.
            outline.EqualizeVerticalCounters = engine.VerticalCounterStems >= 3 && engine.VerticalCounterStems == vertical.Count >> 1;
            outline.EqualizeHorizontalCounters = engine.HorizontalCounterStems >= 3 && engine.HorizontalCounterStems == horizontal.Count >> 1;
            return outline;
        }
        finally
        {
            builder.Release();
        }
    }

    /// <summary>
    /// Scales collected stem edges from charstring units into pixel space.
    /// </summary>
    /// <param name="edges">The collected edge pairs.</param>
    /// <param name="scale">The pixels per design unit scale for the stem axis.</param>
    /// <returns>The scaled edge pairs.</returns>
    private static float[] ScaleStems(List<float> edges, float scale)
    {
        if (edges.Count == 0)
        {
            return [];
        }

        float[] scaled = new float[edges.Count];
        for (int i = 0; i < scaled.Length; i++)
        {
            scaled[i] = edges[i] * scale;
        }

        return scaled;
    }
}
