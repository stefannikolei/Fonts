// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Diagnostics;
using SixLabors.Fonts.Tables.AdvancedTypographic;
using SixLabors.Fonts.Unicode;
using static SixLabors.Fonts.Unicode.Resources.IndicShapingData;

namespace SixLabors.Fonts;

/// <summary>
/// Contains supplementary data that allows the shaping of glyphs. Stored by value in
/// the shaping buffer's flat storage; call sites mutate through the buffer indexer so
/// writes land in place.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
internal struct GlyphShapingData
{
#pragma warning disable SA1401 // Fields exposed so shaping mutates embedded values in place.
    /// <summary>
    /// The shaping bounds. A field rather than a property so positioning lookups
    /// mutate the embedded value in place and re-seeding is plain value assignment.
    /// </summary>
    public GlyphShapingBounds Bounds;

    /// <summary>
    /// The syllable classification assigned by the complex-script shapers, stored by
    /// value so classification never allocates. A <see cref="SyllableInfo.Type"/> of
    /// <see cref="SyllableType.None"/> means no classification has been assigned.
    /// </summary>
    public SyllableInfo Syllable;
#pragma warning restore SA1401

    private ushort glyphId;

    /// <summary>
    /// Packed boolean shaping state. Single bits keep the record narrow; the bool
    /// properties are the only readers and writers.
    /// </summary>
    private ushort flags;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlyphShapingData"/> struct.
    /// </summary>
    /// <param name="textRun">The text run.</param>
    public GlyphShapingData(TextRun textRun) => this.TextRun = textRun;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlyphShapingData"/> struct.
    /// </summary>
    /// <param name="data">The data to copy properties from.</param>
    /// <param name="clearFeatures">Whether to clear features.</param>
    public GlyphShapingData(GlyphShapingData data, bool clearFeatures = false)
    {
        this.GlyphId = data.GlyphId;
        this.CodePointIndex = data.CodePointIndex;
        this.CodePoint = data.CodePoint;
        this.CodePointCount = data.CodePointCount;
        this.Direction = data.Direction;
        this.TextRun = data.TextRun;
        this.LigatureId = data.LigatureId;
        this.IsLigated = data.IsLigated;
        this.LigatureComponent = data.LigatureComponent;
        this.MarkAttachment = data.MarkAttachment;
        this.CursiveAttachment = data.CursiveAttachment;
        this.IsSubstituted = data.IsSubstituted;
        this.IsDecomposed = data.IsDecomposed;
        this.IsPlaceholder = data.IsPlaceholder;
        this.IsPositioned = data.IsPositioned;
        this.IsKerned = data.IsKerned;

        this.Syllable = data.Syllable;

        if (!clearFeatures)
        {
            this.RegisteredFeatureMask = data.RegisteredFeatureMask;
            this.FeatureMask = data.FeatureMask;
        }

        this.AppliedFeatureMask = data.AppliedFeatureMask;

        this.Bounds = data.Bounds;
        this.CachedShapingClass = data.CachedShapingClass;
        this.ShapingClassCacheKey = data.ShapingClassCacheKey;
    }

    /// <summary>
    /// Gets or sets the glyph id. Setting this value invalidates the cached shaping class.
    /// </summary>
    public ushort GlyphId
    {
        get => this.glyphId;
        set
        {
            if (this.glyphId != value)
            {
                this.glyphId = value;
                this.ShapingClassCacheKey = -1;
            }
        }
    }

    /// <summary>
    /// Gets or sets the cached glyph shaping class, avoiding repeated GDEF lookups.
    /// </summary>
    public GlyphShapingClass CachedShapingClass { get; set; }

    /// <summary>
    /// Gets or sets the cache key for <see cref="CachedShapingClass"/>.
    /// A value of <c>-1</c> indicates the cache is invalid. Valid entries store the glyph id.
    /// </summary>
    public int ShapingClassCacheKey { get; set; } = -1;

    /// <summary>
    /// Gets or sets the zero-based index within the input codepoint collection of the
    /// leading codepoint this glyph represents.
    /// </summary>
    public int CodePointIndex { get; set; }

    /// <summary>
    /// Gets or sets the leading codepoint.
    /// </summary>
    public CodePoint CodePoint { get; set; }

    /// <summary>
    /// Gets or sets the codepoint count represented by this glyph.
    /// </summary>
    public int CodePointCount { get; set; } = 1;

    /// <summary>
    /// Gets or sets the text direction.
    /// </summary>
    public TextDirection Direction { get; set; }

    /// <summary>
    /// Gets or sets the text run this glyph belongs to.
    /// </summary>
    public TextRun TextRun { get; set; }

    /// <summary>
    /// Gets or sets the id of any ligature this glyph is a member of.
    /// </summary>
    public int LigatureId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the glyph is ligated.
    /// </summary>
    public bool IsLigated
    {
        readonly get => (this.flags & 1) != 0;
        set => this.flags = value ? (ushort)(this.flags | 1) : (ushort)(this.flags & ~1);
    }

    /// <summary>
    /// Gets or sets the ligature component index of the glyph.
    /// </summary>
    public int LigatureComponent { get; set; } = -1;

    /// <summary>
    /// Gets or sets the index of any mark attachment.
    /// </summary>
    public int MarkAttachment { get; set; } = -1;

    /// <summary>
    /// Gets or sets the index of any cursive attachment.
    /// </summary>
    public int CursiveAttachment { get; set; } = -1;

    /// <summary>
    /// Gets or sets the mask of features a shaper has registered for this glyph, enabled
    /// or not. Bits are assigned by the shaping pass's <see cref="ShapingFeatureMap"/>.
    /// Enabling a feature only ever reveals a registered bit; a feature that was never
    /// registered for the glyph cannot be enabled.
    /// </summary>
    public ulong RegisteredFeatureMask { get; set; }

    /// <summary>
    /// Gets or sets the mask of features currently enabled for this glyph: the subset of
    /// <see cref="RegisteredFeatureMask"/> a lookup application gate tests with a single
    /// bitwise AND.
    /// </summary>
    public ulong FeatureMask { get; set; }

    /// <summary>
    /// Gets or sets the mask of features whose lookups actually changed this glyph.
    /// Read after shaping, for example to detect that a vertical alternate was
    /// substituted. Survives the copy into the positioning collection, which is why the
    /// substitution and positioning collections must share one
    /// <see cref="ShapingFeatureMap"/>.
    /// </summary>
    public ulong AppliedFeatureMask { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this glyph is the result of a substitution.
    /// </summary>
    public bool IsSubstituted
    {
        readonly get => (this.flags & (1 << 1)) != 0;
        set => this.flags = value ? (ushort)(this.flags | (1 << 1)) : (ushort)(this.flags & ~(1 << 1));
    }

    /// <summary>
    /// Gets or sets a value indicating whether this glyph is the result of a decomposition substitution
    /// </summary>
    public bool IsDecomposed
    {
        readonly get => (this.flags & (1 << 2)) != 0;
        set => this.flags = value ? (ushort)(this.flags | (1 << 2)) : (ushort)(this.flags & ~(1 << 2));
    }

    /// <summary>
    /// Gets or sets a value indicating whether this glyph represents an inline placeholder.
    /// A placeholder's bidi run lives on the buffer, keyed by codepoint offset.
    /// </summary>
    public bool IsPlaceholder
    {
        readonly get => (this.flags & (1 << 3)) != 0;
        set => this.flags = value ? (ushort)(this.flags | (1 << 3)) : (ushort)(this.flags & ~(1 << 3));
    }

    /// <summary>
    /// Gets or sets a value indicating whether this glyph has been positioned.
    /// </summary>
    public bool IsPositioned
    {
        readonly get => (this.flags & (1 << 4)) != 0;
        set => this.flags = value ? (ushort)(this.flags | (1 << 4)) : (ushort)(this.flags & ~(1 << 4));
    }

    /// <summary>
    /// Gets or sets a value indicating whether this glyph has been kerned.
    /// </summary>
    public bool IsKerned
    {
        readonly get => (this.flags & (1 << 5)) != 0;
        set => this.flags = value ? (ushort)(this.flags | (1 << 5)) : (ushort)(this.flags & ~(1 << 5));
    }

    private string DebuggerDisplay
        => FormattableString
        .Invariant($" {this.GlyphId} : {this.CodePoint.ToDebuggerDisplay()} : {CodePoint.GetScriptClass(this.CodePoint)} : {this.Direction} : {this.TextRun.TextAttributes} : {this.LigatureId} : {this.LigatureComponent} : {this.IsDecomposed}");

    /// <summary>
    /// Clears the registered and enabled feature masks while preserving the applied
    /// mask, matching the semantics of copying with cleared features. Positioning
    /// reuses the substituted glyph data and re-plans its own features, but the applied
    /// record of what substitution did must survive for consumers such as vertical
    /// alternate detection.
    /// </summary>
    public void ClearFeatures()
    {
        this.RegisteredFeatureMask = 0;
        this.FeatureMask = 0;
    }

    public string ToDebuggerDisplay() => this.DebuggerDisplay;
}
