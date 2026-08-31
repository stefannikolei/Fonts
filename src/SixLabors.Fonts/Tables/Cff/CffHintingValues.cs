// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// Carries the declarative hinting values from a CFF Private DICT: alignment zones,
/// standard stem widths and their policy parameters. CFF has no instruction programs;
/// these values describe the font's stems and zones and delegate the fitting policy to
/// the renderer. All coordinate values are in design units.
/// </summary>
internal sealed class CffHintingValues
{
    /// <summary>
    /// The values a Private DICT implies when it omits the corresponding operators.
    /// </summary>
    public static readonly CffHintingValues Empty = new([], [], [], [], 0.039625F, 7F, 1F, 0.06F, 0F, 0F, [], [], 0);

    /// <summary>
    /// Initializes a new instance of the <see cref="CffHintingValues"/> class.
    /// </summary>
    /// <param name="blueValues">The baseline and horizontal alignment zone pairs.</param>
    /// <param name="otherBlues">The descender alignment zone pairs.</param>
    /// <param name="familyBlues">The family-wide alignment zone pairs.</param>
    /// <param name="familyOtherBlues">The family-wide descender alignment zone pairs.</param>
    /// <param name="blueScale">The pixels-per-unit ratio below which overshoot suppression applies.</param>
    /// <param name="blueShift">The overshoot magnitude that renders once suppression stops.</param>
    /// <param name="blueFuzz">The fuzz distance extending each alignment zone.</param>
    /// <param name="expansionFactor">The counter expansion factor.</param>
    /// <param name="stdHW">The dominant horizontal stem width, or zero when unset.</param>
    /// <param name="stdVW">The dominant vertical stem width, or zero when unset.</param>
    /// <param name="stemSnapH">The horizontal stem width family.</param>
    /// <param name="stemSnapV">The vertical stem width family.</param>
    /// <param name="languageGroup">The language group selecting counter treatment.</param>
    public CffHintingValues(float[] blueValues, float[] otherBlues, float[] familyBlues, float[] familyOtherBlues, float blueScale, float blueShift, float blueFuzz, float expansionFactor, float stdHW, float stdVW, float[] stemSnapH, float[] stemSnapV, int languageGroup)
    {
        this.BlueValues = blueValues;
        this.OtherBlues = otherBlues;
        this.FamilyBlues = familyBlues;
        this.FamilyOtherBlues = familyOtherBlues;
        this.BlueScale = blueScale;
        this.BlueShift = blueShift;
        this.BlueFuzz = blueFuzz;
        this.ExpansionFactor = expansionFactor;
        this.ExpansionFactorFixed = CffFixedPoint.FromSingle(expansionFactor);
        this.StdHW = stdHW;
        this.StdVW = stdVW;
        this.StemSnapH = stemSnapH;
        this.StemSnapV = stemSnapV;
        this.HorizontalStemWidths = StandardWidths(stdHW, stemSnapH);
        this.VerticalStemWidths = StandardWidths(stdVW, stemSnapV);
        this.LanguageGroup = languageGroup;
        this.Zones = BuildZones(blueValues, otherBlues);
        this.FamilyZones = BuildZones(familyBlues, familyOtherBlues);

        // GDI performs this font-context setup entirely in signed 16.16. Converting the
        // parsed values once here preserves its product threshold and reciprocal rounding
        // without repeating any setup work for each glyph.
        int maximumZoneSpan = 0;
        for (int i = 0; i < this.Zones.Length; i++)
        {
            HintZone zone = this.Zones[i];
            int zoneSpan = CffFixedPoint.FromSingle(zone.Top) - CffFixedPoint.FromSingle(zone.Bottom);
            if (zoneSpan > maximumZoneSpan)
            {
                maximumZoneSpan = zoneSpan;
            }
        }

        int adjustedBlueScale = CffFixedPoint.FromSingle(blueScale);
        if (CffFixedPoint.Multiply(maximumZoneSpan, adjustedBlueScale) >= CffFixedPoint.One)
        {
            // AdjustBlueScale subtracts one fixed-point unit after the rounded reciprocal,
            // ensuring the tallest zone multiplied by the result remains below one.
            adjustedBlueScale = CffFixedPoint.Divide(CffFixedPoint.One, maximumZoneSpan) - 1;
        }

        this.AdjustedBlueScaleFixed = adjustedBlueScale;
        this.AdjustedBlueScale = CffFixedPoint.ToSingle(adjustedBlueScale);
    }

    /// <summary>
    /// Gets the alignment zones in design units, precomputed for the grid fitter: the
    /// baseline zone, the top zones from the blue values, and the descender zones from
    /// the other blues.
    /// </summary>
    public HintZone[] Zones { get; }

    /// <summary>
    /// Gets the family alignment zones used to replace nearby cached device edges.
    /// </summary>
    public HintZone[] FamilyZones { get; }

    /// <summary>
    /// Gets the baseline and horizontal alignment zone pairs in declaration order.
    /// </summary>
    public float[] BlueValues { get; }

    /// <summary>
    /// Gets the descender alignment zone pairs.
    /// </summary>
    public float[] OtherBlues { get; }

    /// <summary>
    /// Gets the family-wide alignment zone pairs.
    /// </summary>
    public float[] FamilyBlues { get; }

    /// <summary>
    /// Gets the family-wide descender alignment zone pairs.
    /// </summary>
    public float[] FamilyOtherBlues { get; }

    /// <summary>
    /// Gets the pixels-per-unit ratio below which overshoots snap into their zones.
    /// </summary>
    public float BlueScale { get; }

    /// <summary>
    /// Gets the BlueScale value constrained by the tallest alignment zone for grid fitting.
    /// </summary>
    public float AdjustedBlueScale { get; }

    /// <summary>
    /// Gets the zone-height-constrained BlueScale in signed 16.16 form.
    /// </summary>
    public int AdjustedBlueScaleFixed { get; }

    /// <summary>
    /// Gets the overshoot magnitude that renders once suppression stops.
    /// </summary>
    public float BlueShift { get; }

    /// <summary>
    /// Gets the fuzz distance extending each alignment zone.
    /// </summary>
    public float BlueFuzz { get; }

    /// <summary>
    /// Gets the counter expansion factor.
    /// </summary>
    public float ExpansionFactor { get; }

    /// <summary>
    /// Gets the counter expansion factor in signed 16.16 form.
    /// </summary>
    public int ExpansionFactorFixed { get; }

    /// <summary>
    /// Gets the dominant horizontal stem width, or zero when unset.
    /// </summary>
    public float StdHW { get; }

    /// <summary>
    /// Gets the dominant vertical stem width, or zero when unset.
    /// </summary>
    public float StdVW { get; }

    /// <summary>
    /// Gets the horizontal stem width family.
    /// </summary>
    public float[] StemSnapH { get; }

    /// <summary>
    /// Gets the vertical stem width family.
    /// </summary>
    public float[] StemSnapV { get; }

    /// <summary>
    /// Gets the horizontal stem widths in the order used by standard-width fitting.
    /// </summary>
    public float[] HorizontalStemWidths { get; }

    /// <summary>
    /// Gets the vertical stem widths in the order used by standard-width fitting.
    /// </summary>
    public float[] VerticalStemWidths { get; }

    /// <summary>
    /// Gets the language group selecting counter treatment.
    /// </summary>
    public int LanguageGroup { get; }

    /// <summary>
    /// Builds one ordered GDI zone table from BlueValues and OtherBlues pairs.
    /// </summary>
    /// <param name="blueValues">The baseline and top-zone pairs.</param>
    /// <param name="otherBlues">The additional bottom-zone pairs.</param>
    /// <returns>The zones in declaration order.</returns>
    private static HintZone[] BuildZones(float[] blueValues, float[] otherBlues)
    {
        // The first BlueValues pair and every OtherBlues pair are bottom zones. Later
        // BlueValues pairs are top zones. SetUpBlueValues preserves this declaration order.
        int zoneCount = (blueValues.Length >> 1) + (otherBlues.Length >> 1);
        if (zoneCount == 0)
        {
            return [];
        }

        HintZone[] zones = new HintZone[zoneCount];
        int z = 0;
        for (int i = 0; i + 1 < blueValues.Length; i += 2)
        {
            zones[z++] = i == 0
                ? new HintZone(blueValues[i], blueValues[i + 1], blueValues[i + 1], true)
                : new HintZone(blueValues[i], blueValues[i + 1], blueValues[i], false);
        }

        for (int i = 0; i + 1 < otherBlues.Length; i += 2)
        {
            zones[z++] = new HintZone(otherBlues[i], otherBlues[i + 1], otherBlues[i + 1], true);
        }

        return zones;
    }

    /// <summary>
    /// Gathers one axis' standard widths once for the lifetime of the Private DICT.
    /// </summary>
    /// <param name="standard">The standard width, or zero when the font names none.</param>
    /// <param name="snaps">The alternative widths.</param>
    /// <returns>The widths in increasing order.</returns>
    private static float[] StandardWidths(float standard, float[] snaps)
    {
        if (snaps.Length == 0)
        {
            return standard > 0F ? [standard] : [];
        }

        // UseStdWidth walks an ordered table. The parsed values remain unchanged for
        // diagnostics while this one font-level copy is shared by every glyph.
        float[] result = (float[])snaps.Clone();
        Array.Sort(result);
        return result;
    }
}
