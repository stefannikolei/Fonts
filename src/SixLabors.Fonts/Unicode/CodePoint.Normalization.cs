// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SixLabors.Fonts.Unicode.Resources;

namespace SixLabors.Fonts.Unicode;

/// <content>
/// Canonical decomposition, canonical composition, and the combining classes that
/// order the marks between the two.
/// <para>
/// Sources. The tables both searches read are generated from the Unicode Character
/// Database; see <see cref="NormalizationData"/> for which files and which
/// fields. The Hangul arithmetic that stands in for those tables is the Hangul
/// syllable composition and decomposition of UAX #15, and follows
/// <c>_hb_ucd_decompose_hangul</c> and <c>_hb_ucd_compose_hangul</c> in
/// <c>hb-ucd.cc</c> of HarfBuzz 14.2.1, including the order the two cases are tested
/// in.
/// </para>
/// </content>
public readonly partial struct CodePoint
{
    /// <summary>
    /// The number of bits each scalar value occupies in a packed table entry.
    /// </summary>
    private const int NormalizationEntryShift = 21;

    /// <summary>
    /// The mask of one scalar value within a packed table entry.
    /// </summary>
    private const ulong NormalizationEntryMask = (1UL << NormalizationEntryShift) - 1;

    /// <summary>
    /// The number of bytes one packed table entry occupies.
    /// </summary>
    private const int NormalizationEntrySize = sizeof(ulong);

    /// <summary>
    /// The number of low code-point bits covered by one decomposition index page.
    /// </summary>
    private const int DecompositionPageShift = 7;

    /// <summary>
    /// The number of bytes occupied by one decomposition page boundary.
    /// </summary>
    private const int DecompositionPageBoundarySize = sizeof(ushort);

    /// <summary>
    /// The first Hangul leading consonant.
    /// </summary>
    private const uint HangulLeadBase = 0x1100;

    /// <summary>
    /// The first Hangul vowel.
    /// </summary>
    private const uint HangulVowelBase = 0x1161;

    /// <summary>
    /// The Hangul trailing consonant base, one below the first trailing consonant
    /// so that the zero offset stands for a syllable that has none.
    /// </summary>
    private const uint HangulTrailBase = 0x11A7;

    /// <summary>
    /// The first Hangul syllable.
    /// </summary>
    private const uint HangulSyllableBase = 0xAC00;

    /// <summary>
    /// The number of Hangul leading consonants.
    /// </summary>
    private const uint HangulLeadCount = 19;

    /// <summary>
    /// The number of Hangul vowels.
    /// </summary>
    private const uint HangulVowelCount = 21;

    /// <summary>
    /// The number of Hangul trailing consonant slots, counting the empty one that
    /// stands for a syllable without a trailing consonant.
    /// </summary>
    private const uint HangulTrailCount = 28;

    /// <summary>
    /// The number of syllables that share one leading consonant.
    /// </summary>
    private const uint HangulVowelTrailCount = HangulVowelCount * HangulTrailCount;

    /// <summary>
    /// The number of Hangul syllables.
    /// </summary>
    private const uint HangulSyllableCount = HangulLeadCount * HangulVowelTrailCount;

    /// <summary>
    /// Gets the canonical combining class of the given code point.
    /// </summary>
    /// <param name="codePoint">The code point to evaluate.</param>
    /// <returns>The canonical combining class.</returns>
    public static int GetCanonicalCombiningClass(CodePoint codePoint)
        => UnicodeData.GetCanonicalCombiningClass(codePoint.value);

    /// <summary>
    /// Gets the class that orders a mark against the marks around it.
    /// <para>
    /// This is the canonical combining class with the classes of several scripts
    /// renumbered, so that sorting by it leaves the marks of those scripts in the
    /// order they are drawn rather than the order the standard assigns. Hebrew,
    /// Arabic, Syriac, Telugu, Thai, Lao and Tibetan all order differently from
    /// their assigned classes.
    /// </para>
    /// </summary>
    /// <param name="codePoint">The code point to evaluate.</param>
    /// <returns>The ordering class.</returns>
    public static int GetMarkOrderingClass(CodePoint codePoint)
        => UnicodeData.GetMarkOrderingClass(codePoint.value);

    /// <summary>
    /// Tries to take the given code point apart into the pair of code points it is
    /// canonically equivalent to.
    /// </summary>
    /// <param name="codePoint">The code point to take apart.</param>
    /// <param name="first">When this method returns, contains the first code point.</param>
    /// <param name="second">
    /// When this method returns, contains the second code point, or the default when
    /// the code point stands for a single other one.
    /// </param>
    /// <returns><see langword="true"/> if the code point comes apart.</returns>
    public static bool TryDecompose(CodePoint codePoint, out CodePoint first, out CodePoint second)
    {
        uint value = codePoint.value;

        // A Hangul syllable comes apart by arithmetic rather than by table, which is
        // why the tables leave the eleven thousand of them out. The syllables are
        // laid out so that a syllable's index counts, from the outside in, its
        // leading consonant, then its vowel, then its trailing consonant, with the
        // zero trailing slot standing for a syllable that has none.
        //
        // Subtracting the base leaves that index. An unsigned compare against the
        // count then rejects everything below the base too, because a value below it
        // wraps to a very large number, so one compare does the work of two.
        uint syllable = value - HangulSyllableBase;
        if (syllable < HangulSyllableCount)
        {
            uint trail = syllable % HangulTrailCount;
            if (trail != 0)
            {
                // The syllable carries a trailing consonant, so it parts into the
                // same syllable without one, reached by clearing the trailing slot,
                // and that consonant on its own.
                first = new CodePoint(HangulSyllableBase + (syllable - trail));
                second = new CodePoint(HangulTrailBase + trail);
            }
            else
            {
                // The syllable is a leading consonant and a vowel only, so it parts
                // into the two of them. Dividing by the number of syllables that
                // share a leading consonant gives which consonant; what remains,
                // divided by the trailing slots, gives which vowel.
                first = new CodePoint(HangulLeadBase + (syllable / HangulVowelTrailCount));
                second = new CodePoint(HangulVowelBase + ((syllable % HangulVowelTrailCount) / HangulTrailCount));
            }

            return true;
        }

        if (value < NormalizationData.FirstDecompositionCodePoint || value > NormalizationData.LastDecompositionCodePoint)
        {
            // The generated bounds reject the overwhelmingly common ASCII path
            // before it constructs spans or reads a page boundary.
            first = default;
            second = default;
            return false;
        }

        // Every other character is looked up. The table is ordered by the character
        // that decomposes, which the packing puts in the high bits, so the entries
        // are searched as the plain integers they are.
        ReadOnlySpan<byte> entries = NormalizationData.Decompositions;
        int low;
        int high;
        if (value <= char.MaxValue)
        {
            // The generated boundaries narrow a Basic Multilingual Plane lookup to
            // one 128-code-point page. Empty pages have equal boundaries and fail
            // immediately; populated pages search only their handful of entries.
            ReadOnlySpan<byte> pageStarts = NormalizationData.DecompositionPageStarts;
            int pageOffset = (int)(value >> DecompositionPageShift) * DecompositionPageBoundarySize;
            low = BinaryPrimitives.ReadUInt16LittleEndian(pageStarts[pageOffset..]);
            high = BinaryPrimitives.ReadUInt16LittleEndian(pageStarts[(pageOffset + DecompositionPageBoundarySize)..]) - 1;
        }
        else
        {
            // Supplementary decompositions are sparse enough that the complete
            // table remains smaller than a page index covering every Unicode plane.
            low = 0;
            high = (entries.Length / NormalizationEntrySize) - 1;
        }

        while (low <= high)
        {
            // The midpoint is computed on unsigned values so that a large table
            // cannot overflow the sum into a negative index.
            int middle = (int)(((uint)low + (uint)high) >> 1);
            ulong entry = ReadEntry(entries, middle);
            uint key = (uint)(entry >> (NormalizationEntryShift * 2));

            if (value < key)
            {
                high = middle - 1;
            }
            else if (value > key)
            {
                low = middle + 1;
            }
            else
            {
                // The two parts sit in the lower lanes. A singleton decomposition
                // stores zero in the last lane, which the caller reads as absent.
                first = new CodePoint((uint)((entry >> NormalizationEntryShift) & NormalizationEntryMask));
                second = new CodePoint((uint)(entry & NormalizationEntryMask));
                return true;
            }
        }

        first = default;
        second = default;
        return false;
    }

    /// <summary>
    /// Tries to join the given pair of code points into the single code point they
    /// are canonically equivalent to.
    /// </summary>
    /// <param name="first">The first code point.</param>
    /// <param name="second">The second code point.</param>
    /// <param name="composed">When this method returns, contains the joined code point.</param>
    /// <returns><see langword="true"/> if the pair joins.</returns>
    public static bool TryCompose(CodePoint first, CodePoint second, out CodePoint composed)
    {
        uint a = first.value;
        uint b = second.value;

        // Hangul joins by the arithmetic that takes a syllable apart, run backwards,
        // and in the same two cases. Each unsigned compare against a count also
        // rejects everything below the base it subtracted, because a value below it
        // wraps to a very large number.

        // A leading consonant followed by a vowel builds the syllable that has no
        // trailing consonant, by laying the two out at their strides.
        uint lead = a - HangulLeadBase;
        uint vowel = b - HangulVowelBase;
        if (lead < HangulLeadCount && vowel < HangulVowelCount)
        {
            composed = new CodePoint(HangulSyllableBase + (lead * HangulVowelTrailCount) + (vowel * HangulTrailCount));
            return true;
        }

        // A syllable followed by a trailing consonant fills that syllable's empty
        // trailing slot. The syllable must have an empty one, which is what a zero
        // remainder against the trailing stride means; a syllable that already
        // carries a trailing consonant takes no second one.
        uint syllable = a - HangulSyllableBase;
        uint trail = b - HangulTrailBase;
        if (syllable < HangulSyllableCount && syllable % HangulTrailCount == 0
            && trail is > 0 and < HangulTrailCount)
        {
            composed = new CodePoint(a + trail);
            return true;
        }

        // Every other pair is looked up. The table is ordered by the pair, which the
        // packing puts in the high lanes, so packing the pair the same way turns the
        // two-value search into one integer comparison per step.
        ulong sought = ((ulong)a << NormalizationEntryShift) | b;
        ReadOnlySpan<byte> entries = NormalizationData.Compositions;
        int low = 0;
        int high = (entries.Length / NormalizationEntrySize) - 1;
        while (low <= high)
        {
            // The midpoint is computed on unsigned values so that a large table
            // cannot overflow the sum into a negative index.
            int middle = (int)(((uint)low + (uint)high) >> 1);
            ulong entry = ReadEntry(entries, middle);

            // Shifting the joined character out of the low lane leaves exactly the
            // packed pair, which is the key being searched for.
            ulong key = entry >> NormalizationEntryShift;

            if (sought < key)
            {
                high = middle - 1;
            }
            else if (sought > key)
            {
                low = middle + 1;
            }
            else
            {
                // A pair the table holds always joins: a pair that must not be put
                // back together is left out of the table when it is generated, so
                // nothing needs to be excluded here.
                composed = new CodePoint((uint)(entry & NormalizationEntryMask));
                return true;
            }
        }

        composed = default;
        return false;
    }

    /// <summary>
    /// Reads one packed entry from a normalization table.
    /// <para>
    /// The tables are held as bytes because only a span of a one-byte type is a blob
    /// in the assembly's read-only data; a span of a wider type would be a fresh
    /// array on every access, allocating once per lookup. Reading the eight bytes back
    /// is a single load on a little-endian machine.
    /// </para>
    /// </summary>
    /// <param name="entries">The table.</param>
    /// <param name="index">The zero-based index of the entry.</param>
    /// <returns>The packed entry.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadEntry(ReadOnlySpan<byte> entries, int index)
        => BinaryPrimitives.ReadUInt64LittleEndian(entries[(index * NormalizationEntrySize)..]);
}
