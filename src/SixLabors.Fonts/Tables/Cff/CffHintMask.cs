// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// The fixed 96-bit stem selection carried by a Type 2 hintmask or cntrmask operator.
/// </summary>
internal readonly struct CffHintMask
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CffHintMask"/> struct.
    /// </summary>
    /// <param name="first">The selection bits for stems 0 through 31.</param>
    /// <param name="second">The selection bits for stems 32 through 63.</param>
    /// <param name="third">The selection bits for stems 64 through 95.</param>
    public CffHintMask(uint first, uint second, uint third)
    {
        this.First = first;
        this.Second = second;
        this.Third = third;
    }

    /// <summary>Gets the selection bits for stems 0 through 31.</summary>
    public uint First { get; }

    /// <summary>Gets the selection bits for stems 32 through 63.</summary>
    public uint Second { get; }

    /// <summary>Gets the selection bits for stems 64 through 95.</summary>
    public uint Third { get; }

    /// <summary>
    /// Determines whether the stem at the given declaration index is selected.
    /// </summary>
    /// <param name="stemIndex">The zero-based stem declaration index.</param>
    /// <returns><see langword="true"/> when the stem is selected.</returns>
    public bool IsSet(int stemIndex)
    {
        uint word = stemIndex < 32
            ? this.First
            : stemIndex < 64
                ? this.Second
                : this.Third;

        return (word & (1U << (stemIndex & 31))) != 0;
    }
}
