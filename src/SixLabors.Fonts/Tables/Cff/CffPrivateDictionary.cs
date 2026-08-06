// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// Represents data from a CFF Private DICT, which contains font-level hinting
/// values and local subroutine references.
/// </summary>
internal class CffPrivateDictionary
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CffPrivateDictionary"/> class.
    /// </summary>
    /// <param name="localSubrRawBuffers">The local subroutine byte buffers.</param>
    /// <param name="defaultWidthX">The default glyph width.</param>
    /// <param name="nominalWidthX">The nominal width bias.</param>
    /// <param name="hintingValues">The declarative hinting values carried by the Private DICT.</param>
    public CffPrivateDictionary(byte[][]? localSubrRawBuffers, int defaultWidthX, int nominalWidthX, CffHintingValues hintingValues)
    {
        this.LocalSubrRawBuffers = localSubrRawBuffers;
        this.DefaultWidthX = defaultWidthX;
        this.NominalWidthX = nominalWidthX;
        this.HintingValues = hintingValues;
    }

    /// <summary>
    /// Gets or sets the local subroutine raw byte buffers referenced by the Private DICT.
    /// </summary>
    public byte[][]? LocalSubrRawBuffers { get; set; }

    /// <summary>
    /// Gets or sets the default width for glyphs that do not specify a width in the charstring.
    /// </summary>
    public int DefaultWidthX { get; set; }

    /// <summary>
    /// Gets or sets the nominal width used as a bias for charstring width values.
    /// </summary>
    public int NominalWidthX { get; set; }

    /// <summary>
    /// Gets the declarative hinting values carried by the Private DICT.
    /// </summary>
    public CffHintingValues HintingValues { get; }
}
