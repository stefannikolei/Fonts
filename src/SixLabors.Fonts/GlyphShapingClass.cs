// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts;

internal readonly struct GlyphShapingClass
{
    /// <summary>
    /// The <see cref="Props"/> bit for base glyphs.
    /// </summary>
    public const ushort BaseProp = 1;

    /// <summary>
    /// The <see cref="Props"/> bit for ligature glyphs.
    /// </summary>
    public const ushort LigatureProp = 2;

    /// <summary>
    /// The <see cref="Props"/> bit for mark glyphs.
    /// </summary>
    public const ushort MarkProp = 4;

    /// <summary>
    /// The shift positioning the mark attachment class in the high byte of
    /// <see cref="Props"/>. Lookup flags address attachment classes through their own
    /// high byte, so the eight-bit range covers every class a lookup can reference.
    /// </summary>
    public const int MarkAttachmentTypeShift = 8;

    public GlyphShapingClass(bool isMark, bool isBase, bool isLigature, ushort markAttachmentType)
        => this.Props = (ushort)((isBase ? BaseProp : 0)
            | (isLigature ? LigatureProp : 0)
            | (isMark ? MarkProp : 0)
            | (markAttachmentType << MarkAttachmentTypeShift));

    /// <summary>
    /// Gets the class packed into a single word: the low byte carries the glyph class
    /// bits and the high byte the mark attachment class, so a skip decision is bitwise
    /// arithmetic instead of a branch per class. The word is the struct's only storage;
    /// the class properties are derived from it.
    /// </summary>
    public ushort Props { get; }

    /// <summary>
    /// Gets a value indicating whether the glyph is classified as a mark.
    /// </summary>
    public bool IsMark => (this.Props & MarkProp) != 0;

    /// <summary>
    /// Gets a value indicating whether the glyph is classified as a base.
    /// </summary>
    public bool IsBase => (this.Props & BaseProp) != 0;

    /// <summary>
    /// Gets a value indicating whether the glyph is classified as a ligature.
    /// </summary>
    public bool IsLigature => (this.Props & LigatureProp) != 0;
}
