// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts;

/// <summary>
/// Contains a composed logical text line and the retained source text its line
/// break opportunities are queried from.
/// </summary>
internal readonly struct LogicalTextLine
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LogicalTextLine"/> struct.
    /// </summary>
    /// <param name="textLine">The composed logical text line.</param>
    /// <param name="sourceText">The retained source text used to query line break opportunities.</param>
    /// <param name="wordSegments">The collected word-boundary segment runs.</param>
    /// <param name="hyphenationMarkers">The visible hyphenation markers created for soft hyphen entries.</param>
    public LogicalTextLine(
        TextLine textLine,
        char[] sourceText,
        List<WordSegmentRun> wordSegments,
        List<GlyphLayoutData> hyphenationMarkers)
    {
        this.TextLine = textLine;
        this.SourceText = sourceText;
        this.WordSegments = wordSegments;
        this.HyphenationMarkers = hyphenationMarkers;
    }

    /// <summary>
    /// Gets the composed logical text line.
    /// </summary>
    public TextLine TextLine { get; }

    /// <summary>
    /// Gets the retained source text. Browsers keep the source text alive and query
    /// break opportunities through a lazy cursor per layout pass; retaining the text
    /// here lets every wrapping length do the same instead of materializing the
    /// paragraph's break candidates.
    /// </summary>
    public char[] SourceText { get; }

    /// <summary>
    /// Gets the collected word-boundary segment runs.
    /// </summary>
    public List<WordSegmentRun> WordSegments { get; }

    /// <summary>
    /// Gets the visible hyphenation markers created for soft hyphen entries.
    /// </summary>
    public List<GlyphLayoutData> HyphenationMarkers { get; }
}
