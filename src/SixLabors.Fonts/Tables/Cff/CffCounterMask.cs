// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// A cntrmask event in charstring declaration order.
/// </summary>
internal readonly struct CffCounterMask
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CffCounterMask"/> struct.
    /// </summary>
    /// <param name="mask">The selected stems in declaration order.</param>
    /// <param name="stemCount">The number of stems declared when the operator was read.</param>
    /// <param name="declarationOrder">The zero-based order of the cntrmask operator in the charstring.</param>
    public CffCounterMask(CffHintMask mask, int stemCount, int declarationOrder)
    {
        this.Mask = mask;
        this.StemCount = stemCount;
        this.DeclarationOrder = declarationOrder;
    }

    /// <summary>Gets the selected stems in declaration order.</summary>
    public CffHintMask Mask { get; }

    /// <summary>Gets the number of stems declared when the operator was read.</summary>
    public int StemCount { get; }

    /// <summary>Gets the zero-based order of the cntrmask operator in the charstring.</summary>
    public int DeclarationOrder { get; }
}
