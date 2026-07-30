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
    /// <summary>
    /// The bit offset of the <see cref="Type"/> byte lane in <see cref="bits"/>.
    /// </summary>
    private const int TypeShift = 8;

    /// <summary>
    /// The bit offset of the <see cref="IndicCategory"/> byte lane in <see cref="bits"/>.
    /// </summary>
    private const int CategoryShift = 16;

    /// <summary>
    /// The bit offset of the <see cref="IndicPosition"/> byte lane in <see cref="bits"/>.
    /// </summary>
    private const int PositionShift = 24;

    /// <summary>
    /// The bit offset of the <see cref="UseCategory"/> byte lane in <see cref="bits"/>.
    /// </summary>
    private const int UseCategoryShift = 32;

    /// <summary>
    /// The packed classification: one byte lane per property, with
    /// <see cref="Number"/> in the lowest byte and the lanes above it addressed
    /// through the shift constants.
    /// </summary>
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
        readonly get => (SyllableType)(byte)(this.bits >> TypeShift);
        set => this.bits = (this.bits & ~(0xFFUL << TypeShift)) | ((ulong)(byte)value << TypeShift);
    }

    /// <summary>
    /// Gets or sets the Indic or Myanmar shaping category.
    /// </summary>
    public Categories IndicCategory
    {
        readonly get => (Categories)(byte)(this.bits >> CategoryShift);
        set => this.bits = (this.bits & ~(0xFFUL << CategoryShift)) | ((ulong)(byte)value << CategoryShift);
    }

    /// <summary>
    /// Gets or sets the Indic or Myanmar positional class.
    /// </summary>
    public Positions IndicPosition
    {
        readonly get => (Positions)(byte)(this.bits >> PositionShift);
        set => this.bits = (this.bits & ~(0xFFUL << PositionShift)) | ((ulong)(byte)value << PositionShift);
    }

    /// <summary>
    /// Gets or sets the Universal Shaping Engine category as the symbol index the
    /// state machine consumes, which is also the index into the generated category
    /// name table.
    /// </summary>
    public int UseCategory
    {
        readonly get => (byte)(this.bits >> UseCategoryShift);
        set => this.bits = (this.bits & ~(0xFFUL << UseCategoryShift)) | ((ulong)(byte)value << UseCategoryShift);
    }

    /// <summary>
    /// Gets the Myanmar view of <see cref="IndicCategory"/>: the Myanmar shaper shares
    /// the Indic category storage and reads it through its own enum.
    /// </summary>
    public readonly MyanmarCategories MyanmarCategory => (MyanmarCategories)this.IndicCategory;
}
