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

    public GlyphShapingClass(bool isMark, bool isBase, bool isLigature, ushort markAttachmentType)
    {
        this.IsMark = isMark;
        this.IsBase = isBase;
        this.IsLigature = isLigature;
        this.MarkAttachmentType = markAttachmentType;
        this.Props = (ushort)((isBase ? BaseProp : 0)
            | (isLigature ? LigatureProp : 0)
            | (isMark ? MarkProp : 0)
            | (markAttachmentType << 8));
    }

    /// <summary>
    /// Gets the class packed into a single word: the low byte carries the glyph class
    /// bits and the high byte the mark attachment class, so a skip decision is bitwise
    /// arithmetic instead of a branch per class.
    /// </summary>
    public ushort Props { get; }

    public bool IsMark { get; }

    public bool IsBase { get; }

    public bool IsLigature { get; }

    public ushort MarkAttachmentType { get; }
}
