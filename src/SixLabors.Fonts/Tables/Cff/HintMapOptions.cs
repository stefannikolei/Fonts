// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// The parameters one hint map fit runs under.
/// </summary>
internal readonly struct HintMapOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HintMapOptions"/> struct.
    /// </summary>
    /// <param name="fitHorizontal">Whether the horizontal axis is fitted.</param>
    /// <param name="fitVertical">Whether the vertical axis is fitted.</param>
    /// <param name="zones">The declared alignment zones in design units.</param>
    /// <param name="familyZones">The family alignment zones in design units.</param>
    /// <param name="blueFuzz">The fuzz distance extending each zone band, in design units.</param>
    /// <param name="blueScale">The pixels per design unit ratio below which overshoots are suppressed.</param>
    /// <param name="blueShift">The design unit overshoot at which a captured edge keeps a whole pixel of overshoot.</param>
    /// <param name="expansionFactor">The counter expansion factor in signed 16.16 form.</param>
    /// <param name="standardHeights">The standard stem heights for the other axis, in design units, most preferred first.</param>
    /// <param name="standardWidths">The standard stem widths for the axis, in design units, most preferred first.</param>
    /// <param name="linesPerEm">The maximum transformed em extent in signed 16.16 device units.</param>
    /// <param name="lockFixMapOk">Whether the charstring operators permit the close-lock map fixup.</param>
    /// <param name="horizontalScale">The factor converting design units into pixels across.</param>
    /// <param name="anchorScale">The factor converting design units into pixels up.</param>
    public HintMapOptions(bool fitHorizontal, bool fitVertical, HintZone[] zones, HintZone[] familyZones, float blueFuzz, float blueScale, float blueShift, int expansionFactor, float[] standardWidths, float[] standardHeights, int linesPerEm, bool lockFixMapOk, float horizontalScale, float anchorScale)
    {
        this.FitHorizontal = fitHorizontal;
        this.FitVertical = fitVertical;
        this.Zones = zones;
        this.FamilyZones = familyZones;
        this.BlueFuzz = blueFuzz;
        this.BlueScale = blueScale;
        this.BlueShift = blueShift;
        this.ExpansionFactor = expansionFactor;
        this.StandardWidths = standardWidths;
        this.StandardHeights = standardHeights;
        this.LinesPerEm = linesPerEm;
        this.LockFixMapOk = lockFixMapOk;
        this.HorizontalScale = horizontalScale;
        this.AnchorScale = anchorScale;
    }

    /// <summary>Gets a value indicating whether the horizontal axis is fitted.</summary>
    public bool FitHorizontal { get; }

    /// <summary>Gets a value indicating whether the vertical axis is fitted.</summary>
    public bool FitVertical { get; }

    /// <summary>Gets the declared alignment zones in design units.</summary>
    public HintZone[] Zones { get; }

    /// <summary>Gets the family alignment zones in design units.</summary>
    public HintZone[] FamilyZones { get; }

    /// <summary>Gets the fuzz distance extending each zone band, in design units.</summary>
    public float BlueFuzz { get; }

    /// <summary>Gets the pixels per design unit ratio below which overshoots are suppressed.</summary>
    public float BlueScale { get; }

    /// <summary>Gets the design unit overshoot at which a captured edge keeps a whole pixel of overshoot.</summary>
    public float BlueShift { get; }

    /// <summary>Gets the counter expansion factor in signed 16.16 form.</summary>
    public int ExpansionFactor { get; }

    /// <summary>Gets the standard vertical stem widths in design units, most preferred first.</summary>
    public float[] StandardWidths { get; }

    /// <summary>Gets the standard horizontal stem widths in design units, most preferred first.</summary>
    public float[] StandardHeights { get; }

    /// <summary>Gets the maximum transformed em extent in signed 16.16 device units.</summary>
    public int LinesPerEm { get; }

    /// <summary>Gets a value indicating whether the charstring operators permit the close-lock map fixup.</summary>
    public bool LockFixMapOk { get; }

    /// <summary>Gets the factor converting design units into pixels across.</summary>
    public float HorizontalScale { get; }

    /// <summary>Gets the factor converting design units into pixels up.</summary>
    public float AnchorScale { get; }
}
