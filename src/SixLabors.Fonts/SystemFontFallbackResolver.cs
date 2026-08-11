// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Collections.Concurrent;
using System.Globalization;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts;

/// <summary>
/// Resolves fallback font families from the fonts installed on the current machine using the
/// operating system's character-to-font matching service.
/// Results depend on the machine's installed fonts: the same input can resolve different
/// families, or none, on different machines.
/// </summary>
public sealed class SystemFontFallbackResolver : IFontFallbackResolver
{
    /// <summary>
    /// Match results per code point, requested family, style, and culture. Misses are cached
    /// alongside hits so repeated text costs one native query per distinct key regardless of
    /// outcome. The requested family participates because it biases the native match.
    /// </summary>
    private readonly ConcurrentDictionary<(int CodePoint, string Family, FontStyle Style, string Culture), (bool Matched, FontFamily Family)> cache = new();

    /// <inheritdoc/>
    public bool TryResolve(CodePoint codePoint, FontFamily requestedFamily, FontStyle style, CultureInfo? culture, out FontFamily family)
    {
        // The requested family name biases each platform's match toward stylistically
        // compatible faces. A name unknown to the system degrades to an unbiased match on
        // every platform, so file-loaded families need no special handling.
        (bool Matched, FontFamily Family) result = this.cache.GetOrAdd(
            (codePoint.Value, requestedFamily.Name, style, culture?.Name ?? string.Empty),
            static (_, arg) => SystemFonts.Collection.TryMatchCharacter(arg.CodePoint, arg.Style, arg.Family, arg.Culture, out FontMatch match)
                ? (true, match.Family)
                : (false, default),
            (CodePoint: codePoint, Family: requestedFamily.Name, Style: style, Culture: culture));

        family = result.Family;
        return result.Matched;
    }
}
