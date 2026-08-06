// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// The stems a charstring declared live for one run of outline points. A charstring
/// selects its active hints with a hintmask operator, and the selection holds until the
/// next one. Fitting a run through a map built from any other selection puts stems where
/// the font did not ask for them.
/// </summary>
internal readonly struct CffHintRegion
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CffHintRegion"/> struct.
    /// </summary>
    /// <param name="pointStart">The index of the first outline point the mask governs.</param>
    /// <param name="mask">The active stem bits, in declaration order, horizontal stems first.</param>
    /// <param name="stemCount">The number of stems declared when the mask was read.</param>
    public CffHintRegion(int pointStart, CffHintMask mask, int stemCount)
    {
        this.PointStart = pointStart;
        this.Mask = mask;
        this.StemCount = stemCount;
    }

    /// <summary>Gets the index of the first outline point the mask governs.</summary>
    public int PointStart { get; }

    /// <summary>Gets the active stem bits, in declaration order, horizontal stems first.</summary>
    public CffHintMask Mask { get; }

    /// <summary>Gets the number of stems declared when the mask was read.</summary>
    public int StemCount { get; }
}
