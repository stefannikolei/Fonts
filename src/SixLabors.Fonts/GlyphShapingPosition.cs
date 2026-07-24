// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts;

/// <summary>
/// One glyph's positioning-phase state, stored in a stream parallel to the glyph
/// records: the shaping bounds that accumulate placement and advance adjustments,
/// the attachment links, and the positioned and kerned marks. Entries are seeded
/// alongside the metrics stream after substitution; structural buffer operations
/// before seeding need not preserve alignment, matching the metrics stream.
/// </summary>
internal struct GlyphShapingPosition
{
    /// <summary>
    /// The <see cref="flags"/> bit recording <see cref="IsPositioned"/>.
    /// </summary>
    private const byte PositionedFlag = 1 << 0;

    /// <summary>
    /// The <see cref="flags"/> bit recording <see cref="IsKerned"/>.
    /// </summary>
    private const byte KernedFlag = 1 << 1;

#pragma warning disable SA1401 // Fields exposed so positioning mutates embedded values in place.
    /// <summary>
    /// The shaping bounds. A field rather than a property so positioning lookups
    /// mutate the embedded value in place and re-seeding is plain value assignment.
    /// </summary>
    public GlyphShapingBounds Bounds;

    /// <summary>
    /// The index of any mark attachment, or <c>-1</c> when unattached.
    /// </summary>
    public int MarkAttachment;

    /// <summary>
    /// The offset of any cursive attachment, or <c>-1</c> when unattached.
    /// </summary>
    public int CursiveAttachment;
#pragma warning restore SA1401

    /// <summary>
    /// Packed boolean positioning state addressed through the named flag constants
    /// above.
    /// </summary>
    private byte flags;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlyphShapingPosition"/> struct
    /// in its seeded state: unattached, unpositioned, with the given bounds.
    /// </summary>
    /// <param name="bounds">The seeded shaping bounds.</param>
    public GlyphShapingPosition(GlyphShapingBounds bounds)
    {
        this.Bounds = bounds;
        this.MarkAttachment = -1;
        this.CursiveAttachment = -1;
        this.flags = 0;
    }

    /// <summary>
    /// Gets or sets a value indicating whether this glyph has been positioned.
    /// </summary>
    public bool IsPositioned
    {
        readonly get => (this.flags & PositionedFlag) != 0;
        set => this.flags = value ? (byte)(this.flags | PositionedFlag) : (byte)(this.flags & ~PositionedFlag);
    }

    /// <summary>
    /// Gets or sets a value indicating whether this glyph has been kerned.
    /// </summary>
    public bool IsKerned
    {
        readonly get => (this.flags & KernedFlag) != 0;
        set => this.flags = value ? (byte)(this.flags | KernedFlag) : (byte)(this.flags & ~KernedFlag);
    }
}
