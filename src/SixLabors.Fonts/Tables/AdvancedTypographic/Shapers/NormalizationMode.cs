// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// States how far a shaper wants its text taken apart and put back together before
/// it is shaped.
/// </summary>
internal enum NormalizationMode
{
    /// <summary>
    /// Leaves the text as it was written. Used by a script whose own engine reads
    /// the characters as they stand.
    /// </summary>
    None,

    /// <summary>
    /// Takes the text apart as far as the font allows and leaves it apart.
    /// </summary>
    Decomposed,

    /// <summary>
    /// Takes the text apart, orders the marks, then joins the marks back onto the
    /// character they follow wherever the font offers the joined form. A character
    /// standing on its own that the font already draws is left untouched.
    /// </summary>
    ComposedDiacritics,

    /// <summary>
    /// As <see cref="ComposedDiacritics"/>, but every character is taken apart
    /// first, including one standing on its own that the font already draws. Used by
    /// a script whose engine needs the parts of a character it would otherwise
    /// never see.
    /// </summary>
    ComposedDiacriticsNoShortCircuit
}
