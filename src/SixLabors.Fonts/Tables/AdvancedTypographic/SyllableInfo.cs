// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using static SixLabors.Fonts.Unicode.Resources.IndicShapingData;

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Per-glyph syllable classification assigned by the Indic, Myanmar, and Universal
/// Shaping Engine shapers, stored by value on the glyph record so classification
/// allocates nothing and every comparison is an integer compare.
/// <see cref="Type"/> of <see cref="SyllableType.None"/> means no classification has
/// been assigned; consumers treat such glyphs as outside every syllable.
/// </summary>
#pragma warning disable SA1401 // Fields exposed for in-place mutation through the glyph record.
internal struct SyllableInfo
{
    /// <summary>
    /// The running syllable number within the shaping pass.
    /// </summary>
    public int Number;

    /// <summary>
    /// The syllable cluster type produced by the state machine, or
    /// <see cref="SyllableType.None"/> when unassigned.
    /// </summary>
    public SyllableType Type;

    /// <summary>
    /// The Indic or Myanmar shaping category.
    /// </summary>
    public Categories IndicCategory;

    /// <summary>
    /// The Indic or Myanmar positional class.
    /// </summary>
    public Positions IndicPosition;

    /// <summary>
    /// The Universal Shaping Engine category as the symbol index the state machine
    /// consumes, which is also the index into the generated category name table.
    /// </summary>
    public int UseCategory;

    /// <summary>
    /// Gets the Myanmar view of <see cref="IndicCategory"/>: the Myanmar shaper shares
    /// the Indic category storage and reads it through its own enum.
    /// </summary>
    public readonly MyanmarCategories MyanmarCategory => (MyanmarCategories)this.IndicCategory;
}
#pragma warning restore SA1401
