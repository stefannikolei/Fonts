// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Unicode.Resources;

/// <summary>
/// The character sequences that spell one vowel but read as another. A font is
/// not asked to render such a sequence: a dotted circle is placed before its
/// final character so the sequence cannot be mistaken for the vowel it
/// imitates. Generated from IndicShapingInvalidCluster.txt.
/// </summary>
internal static class VowelConstraintData
{
    /// <summary>
    /// The two character sequences as packed keys, in ascending order so a
    /// lookup is a binary search.
    /// </summary>
    private static readonly ulong[] PairData =
    [
        0x380130A009BEUL,
        0x3801316009C3UL,
        0x3801318009E2UL,
        0x442200A11038UL,
        0x44220161103EUL,
        0x442201E11042UL,
        0x7C0120A0093AUL,
        0x7C0120A0093BUL,
        0x7C0120A0093EUL,
        0x7C0120A00945UL,
        0x7C0120A00946UL,
        0x7C0120A00949UL,
        0x7C0120A0094AUL,
        0x7C0120A0094BUL,
        0x7C0120A0094CUL,
        0x7C0120A0094FUL,
        0x7C0120A00956UL,
        0x7C0120A00957UL,
        0x7C0120C0093AUL,
        0x7C0120C00945UL,
        0x7C0120C00946UL,
        0x7C0120C00947UL,
        0x7C0120C00948UL,
        0x7C0121200941UL,
        0x7C0121E00945UL,
        0x7C0121E00946UL,
        0x7C0121E00947UL,
        0xBC0150A00ABEUL,
        0xBC0150A00AC5UL,
        0xBC0150A00AC7UL,
        0xBC0150A00AC8UL,
        0xBC0150A00AC9UL,
        0xBC0150A00ACBUL,
        0xBC0150A00ACCUL,
        0xBC0158A00ABEUL,
        0xC00140A00A3EUL,
        0xC00140A00A48UL,
        0xC00140A00A4CUL,
        0xC0014E400A3FUL,
        0xC0014E400A40UL,
        0xC0014E400A47UL,
        0xC0014E600A41UL,
        0xC0014E600A42UL,
        0xC0014E600A4BUL,
        0x108224001122CUL,
        0x1082240011231UL,
        0x1082240011233UL,
        0x1082240C1122CUL,
        0x1082245811230UL,
        0x1082245811231UL,
        0x108224801122EUL,
        0x1100191200CBEUL,
        0x1100191600CBEUL,
        0x1100192400CCCUL,
        0x16401A0E00D57UL,
        0x16401A1200D57UL,
        0x16401A1C00D46UL,
        0x16401A2400D3EUL,
        0x16401A2400D57UL,
        0x16822C0011639UL,
        0x16822C001163AUL,
        0x16822C0211639UL,
        0x16822C021163AUL,
        0x1A40160A00B3EUL,
        0x1A40161E00B57UL,
        0x1A40162600B57UL,
        0x20022560112E0UL,
        0x20022560112E5UL,
        0x20022560112E6UL,
        0x20022560112E7UL,
        0x20022560112E8UL,
        0x20401B0A00DCFUL,
        0x20401B0A00DD0UL,
        0x20401B0A00DD1UL,
        0x20401B1600DDFUL,
        0x20401B1A00DD8UL,
        0x20401B1E00DDFUL,
        0x20401B2200DCAUL,
        0x20401B2200DD9UL,
        0x20401B2200DDAUL,
        0x20401B2200DDCUL,
        0x20401B2200DDDUL,
        0x20401B2200DDEUL,
        0x20401B2800DDFUL,
        0x22822D00116ADUL,
        0x22822D00116B4UL,
        0x22822D00116B5UL,
        0x22822D0C116B2UL,
        0x2340170A00BC2UL,
        0x2400182400C4CUL,
        0x2400182400C55UL,
        0x2400187E00C55UL,
        0x2400188C00C55UL,
        0x2400189400C55UL,
        0x25822902114B0UL,
        0x25822916114BAUL,
        0x2582291A114BAUL,
        0x25822954114B5UL,
        0x25822954114B6UL,
    ];

    /// <summary>
    /// The first two characters of each three character sequence, packed and
    /// ordered as the pairs are.
    /// </summary>
    private static readonly ulong[] TripleData =
    [
        0x7C012600094DUL,
    ];

    /// <summary>
    /// The final character of each three character sequence, positioned as its
    /// packed key is.
    /// </summary>
    private static readonly int[] TripleFinalData =
    [
        0x0907,
    ];

    /// <summary>
    /// Determines whether any sequence is written in the given script. Text in
    /// any other script carries no constrained sequence and is left alone.
    /// </summary>
    /// <param name="script">The script the text is written in.</param>
    /// <returns><see langword="true"/> when the script carries sequences.</returns>
    public static bool IsConstrainedScript(ScriptClass script)
        => script switch
        {
            ScriptClass.Bengali => true,
            ScriptClass.Brahmi => true,
            ScriptClass.Devanagari => true,
            ScriptClass.Gujarati => true,
            ScriptClass.Gurmukhi => true,
            ScriptClass.Kannada => true,
            ScriptClass.Khojki => true,
            ScriptClass.Khudawadi => true,
            ScriptClass.Malayalam => true,
            ScriptClass.Modi => true,
            ScriptClass.Oriya => true,
            ScriptClass.Sinhala => true,
            ScriptClass.Takri => true,
            ScriptClass.Tamil => true,
            ScriptClass.Telugu => true,
            ScriptClass.Tirhuta => true,
            _ => false,
        };

    /// <summary>
    /// Determines whether the two characters spell a constrained sequence.
    /// </summary>
    /// <param name="script">The script the text is written in.</param>
    /// <param name="first">The character that begins the sequence.</param>
    /// <param name="second">The character that follows it.</param>
    /// <returns><see langword="true"/> when the two are constrained.</returns>
    public static bool IsConstrainedPair(ScriptClass script, int first, int second)
        => Array.BinarySearch(PairData, Key(script, first, second)) >= 0;

    /// <summary>
    /// Determines whether the three characters spell a constrained sequence.
    /// </summary>
    /// <param name="script">The script the text is written in.</param>
    /// <param name="first">The character that begins the sequence.</param>
    /// <param name="second">The character that follows it.</param>
    /// <param name="third">The character that ends it.</param>
    /// <returns><see langword="true"/> when the three are constrained.</returns>
    public static bool IsConstrainedTriple(ScriptClass script, int first, int second, int third)
    {
        ulong key = Key(script, first, second);
        int index = Array.BinarySearch(TripleData, key);
        if (index < 0)
        {
            return false;
        }

        // Sequences sharing their first two characters sit together, so the
        // run around the found position holds every candidate final character.
        ReadOnlySpan<ulong> keys = TripleData;
        ReadOnlySpan<int> finals = TripleFinalData;
        int start = index;
        while (start > 0 && keys[start - 1] == key)
        {
            start--;
        }

        for (int i = start; i < keys.Length && keys[i] == key; i++)
        {
            if (finals[i] == third)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Packs a script and two characters into one ordered key.
    /// </summary>
    /// <param name="script">The script the text is written in.</param>
    /// <param name="first">The character that begins the sequence.</param>
    /// <param name="second">The character that follows it.</param>
    /// <returns>The packed key.</returns>
    private static ulong Key(ScriptClass script, int first, int second)
        => ((ulong)script << 42) | ((ulong)(uint)first << 21) | (uint)second;
}
