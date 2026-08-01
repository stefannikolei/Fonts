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
    public static readonly CffHintingValues Empty = new([], [], [], [], 0.039625F, 7F, 1F, 0F, 0F, [], [], 0);

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
    /// <param name="stdHW">The dominant horizontal stem width, or zero when unset.</param>
    /// <param name="stdVW">The dominant vertical stem width, or zero when unset.</param>
    /// <param name="stemSnapH">The horizontal stem width family.</param>
    /// <param name="stemSnapV">The vertical stem width family.</param>
    /// <param name="languageGroup">The language group selecting counter treatment.</param>
    public CffHintingValues(float[] blueValues, float[] otherBlues, float[] familyBlues, float[] familyOtherBlues, float blueScale, float blueShift, float blueFuzz, float stdHW, float stdVW, float[] stemSnapH, float[] stemSnapV, int languageGroup)
    {
        this.BlueValues = blueValues;
        this.OtherBlues = otherBlues;
        this.FamilyBlues = familyBlues;
        this.FamilyOtherBlues = familyOtherBlues;
        this.BlueScale = blueScale;
        this.BlueShift = blueShift;
        this.BlueFuzz = blueFuzz;
        this.StdHW = stdHW;
        this.StdVW = stdVW;
        this.StemSnapH = stemSnapH;
        this.StemSnapV = stemSnapV;
        this.LanguageGroup = languageGroup;

        // The first zone straddles the baseline; each subsequent zone contributes its flat
        // edge, the lower value of the pair, which is the height round and flat tops share
        // once overshoots are suppressed. Computed once here so per glyph fitting never
        // allocates.
        if (blueValues.Length < 4)
        {
            this.BlueFlats = [];
        }
        else
        {
            float[] flats = new float[(blueValues.Length - 2) / 2];
            for (int i = 0; i < flats.Length; i++)
            {
                flats[i] = blueValues[2 + (i * 2)];
            }

            this.BlueFlats = flats;
        }
    }

    /// <summary>
    /// Gets the flat edges of the top alignment zones in design units, precomputed for the
    /// grid fitter's anchor list.
    /// </summary>
    public float[] BlueFlats { get; }

    /// <summary>
    /// Gets the baseline and horizontal alignment zone pairs, lowest first.
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
    /// Gets the overshoot magnitude that renders once suppression stops.
    /// </summary>
    public float BlueShift { get; }

    /// <summary>
    /// Gets the fuzz distance extending each alignment zone.
    /// </summary>
    public float BlueFuzz { get; }

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
    /// Gets the language group selecting counter treatment.
    /// </summary>
    public int LanguageGroup { get; }
}
