// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;

namespace SixLabors.Fonts;

/// <summary>
/// A reusable, caller-owned buffer holding text, the properties it is shaped
/// under, and the glyphs shaping produces. Text and properties are set on the
/// buffer, the buffer is shaped, and the glyphs are read back from it. Each shaping
/// call replaces the glyphs.
/// </summary>
/// <remarks>
/// <see cref="TextShaper.Shape(Font, TextShapingBuffer)"/> treats the text as one
/// unwrapped logical line. <see cref="TextShaper.ShapeRun(Font,
/// TextShapingBuffer)"/> treats it as one directional run. An instance is not
/// thread safe.
/// </remarks>
public sealed class TextShapingBuffer
{
    /// <summary>
    /// The flat glyph storage. Only the first <see cref="Count"/> records are live;
    /// capacity beyond the count is retained scratch.
    /// </summary>
    private ShapedGlyph[] glyphs = [];

    /// <summary>
    /// The text storage, so a buffer that is refilled from a span does not allocate.
    /// </summary>
    private char[] text = [];

    /// <summary>
    /// The number of characters of <see cref="text"/> that are live.
    /// </summary>
    private int textLength;

    /// <summary>
    /// Gets the text of the run.
    /// </summary>
    public ReadOnlySpan<char> Text => this.text.AsSpan(0, this.textLength);

    /// <summary>
    /// Gets or sets the base direction of a logical line, or the direction of a
    /// directional run.
    /// </summary>
    public TextDirection Direction { get; set; } = TextDirection.LeftToRight;

    /// <summary>
    /// Gets or sets the language the run is written in, which selects the language
    /// specific behaviour of the font's features.
    /// </summary>
    public CultureInfo Language { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>
    /// Gets the number of shaped glyphs the last shaping call produced.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Gets the shaped glyphs in visual order for a logical line, or reading order
    /// for a directional run.
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
    /// Replaces the text of the run, discarding any glyphs already shaped.
    /// </summary>
    /// <param name="value">The text of the run.</param>
    public void Add(ReadOnlySpan<char> value)
    {
        if (this.text.Length < value.Length)
        {
            this.text = new char[Math.Max(value.Length, Math.Max(64, this.text.Length * 2))];
        }

        value.CopyTo(this.text);
        this.textLength = value.Length;
        this.Count = 0;
    }

    /// <inheritdoc cref="Add(ReadOnlySpan{char})"/>
    public void Add(string value)
    {
        Guard.NotNull(value, nameof(value));

        this.Add(value.AsSpan());
    }

    /// <summary>
    /// Removes the text and the glyphs while retaining the storage.
    /// </summary>
    public void Clear()
    {
        this.Count = 0;
        this.textLength = 0;
    }

    /// <summary>
    /// Begins replacing the glyphs: empties them, ensures capacity for the given
    /// record count, and returns the writable storage. The written records become
    /// visible when <see cref="Commit"/> publishes their count.
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
