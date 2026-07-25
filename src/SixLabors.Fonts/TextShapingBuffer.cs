// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts;

/// <summary>
/// A reusable, caller-owned buffer that receives the glyph stream of a shaping call.
/// Each call replaces the contents; storage grows to its high-water mark and is
/// retained, so repeated shaping through one instance does not allocate.
/// </summary>
/// <remarks>
/// An instance is not thread safe: use one buffer per shaping thread and reuse it
/// across calls.
/// </remarks>
public sealed class TextShapingBuffer
{
    /// <summary>
    /// The flat glyph storage. Only the first <see cref="Count"/> records are live;
    /// capacity beyond the count is retained scratch.
    /// </summary>
    private ShapedGlyph[] glyphs = [];

    /// <summary>
    /// Gets the number of shaped glyphs the last shaping call produced.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Gets the shaped glyphs in logical order.
    /// </summary>
    public ReadOnlySpan<ShapedGlyph> Glyphs => this.glyphs.AsSpan(0, this.Count);

    /// <summary>
    /// Gets a read-only reference to the shaped glyph at the given index.
    /// </summary>
    /// <param name="index">The zero-based glyph index.</param>
    public ref readonly ShapedGlyph this[int index]
    {
        get
        {
            Guard.MustBeBetweenOrEqualTo(index, 0, this.Count - 1, nameof(index));
            return ref this.glyphs[index];
        }
    }

    /// <summary>
    /// Removes all glyphs while retaining the storage.
    /// </summary>
    public void Clear() => this.Count = 0;

    /// <summary>
    /// Begins replacing the contents: empties the buffer, ensures capacity for the
    /// given record count, and returns the writable storage. The written records
    /// become visible when <see cref="Commit"/> publishes their count.
    /// </summary>
    /// <param name="capacity">The record capacity to reserve.</param>
    /// <returns>The writable storage span.</returns>
    internal Span<ShapedGlyph> Reserve(int capacity)
    {
        this.Count = 0;
        if (this.glyphs.Length < capacity)
        {
            this.glyphs = new ShapedGlyph[Math.Max(capacity, Math.Max(64, this.glyphs.Length * 2))];
        }

        return this.glyphs.AsSpan(0, capacity);
    }

    /// <summary>
    /// Publishes the number of records written to the reserved storage.
    /// </summary>
    /// <param name="count">The record count.</param>
    internal void Commit(int count) => this.Count = count;
}
