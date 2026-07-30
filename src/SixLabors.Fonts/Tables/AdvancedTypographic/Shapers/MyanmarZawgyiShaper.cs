// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// Shapes text encoded using the legacy Zawgyi convention.
/// </summary>
internal sealed class MyanmarZawgyiShaper : DefaultShaper
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MyanmarZawgyiShaper"/> class.
    /// </summary>
    /// <param name="script">The script classification.</param>
    /// <param name="textOptions">The text options.</param>
    public MyanmarZawgyiShaper(ScriptClass script, TextOptions textOptions)
        : base(script, MarkZeroingMode.None, textOptions)
    {
        // Zawgyi assigns meaning to its encoded character sequence directly.
        // Canonical normalization would rewrite that sequence as Unicode text.
        this.NormalizationMode = NormalizationMode.None;

        // Retain the font's encoded advances and positioning. Zawgyi shaping does
        // not apply the mark-zeroing or fallback-positioning conventions used by
        // Unicode script engines.
        this.FallbackMarkPositioning = false;
    }
}
