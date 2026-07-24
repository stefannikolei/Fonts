// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using static SixLabors.Fonts.Unicode.Resources.IndicShapingData;

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Per-glyph syllable classification assigned by the Indic, Myanmar, and Universal
/// Shaping Engine shapers, packed into a single word of byte lanes so classification
/// allocates nothing, every comparison is an integer compare, and the glyph record
/// stays narrow. <see cref="Type"/> of <see cref="SyllableType.None"/> means no
/// classification has been assigned; consumers treat such glyphs as outside every
/// syllable.
/// </summary>
internal struct SyllableInfo
{
    private ulong bits;

    /// <summary>
    /// Gets or sets the running syllable number within the shaping pass. Stored as a
    /// byte: consumers only compare the numbers of nearby glyphs for equality, and
    /// adjacent syllables always differ by one, so wrap-around cannot alias them.
    /// </summary>
    public int Number
    {
        readonly get => (byte)this.bits;
        set => this.bits = (this.bits & ~0xFFUL) | (byte)value;
    }

    /// <summary>
    /// Gets or sets the syllable cluster type produced by the state machine, or
    /// <see cref="SyllableType.None"/> when unassigned.
    /// </summary>
    public SyllableType Type
    {
        readonly get => (SyllableType)(byte)(this.bits >> 8);
        set => this.bits = (this.bits & ~(0xFFUL << 8)) | ((ulong)(byte)value << 8);
    }

    /// <summary>
    /// Gets or sets the Indic or Myanmar shaping category.
    /// </summary>
    public Categories IndicCategory
    {
        readonly get => (Categories)(byte)(this.bits >> 16);
        set => this.bits = (this.bits & ~(0xFFUL << 16)) | ((ulong)(byte)value << 16);
    }

    /// <summary>
    /// Gets or sets the Indic or Myanmar positional class.
    /// </summary>
    public Positions IndicPosition
    {
        readonly get => (Positions)(byte)(this.bits >> 24);
        set => this.bits = (this.bits & ~(0xFFUL << 24)) | ((ulong)(byte)value << 24);
    }

    /// <summary>
    /// Gets or sets the Universal Shaping Engine category as the symbol index the
    /// state machine consumes, which is also the index into the generated category
    /// name table.
    /// </summary>
    public int UseCategory
    {
        readonly get => (byte)(this.bits >> 32);
        set => this.bits = (this.bits & ~(0xFFUL << 32)) | ((ulong)(byte)value << 32);
    }

    /// <summary>
    /// Gets the Myanmar view of <see cref="IndicCategory"/>: the Myanmar shaper shares
    /// the Indic category storage and reads it through its own enum.
    /// </summary>
    public readonly MyanmarCategories MyanmarCategory => (MyanmarCategories)this.IndicCategory;
}
