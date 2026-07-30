// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Runtime.CompilerServices;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts;

/// <summary>
/// Helper methods to throw exceptions
/// </summary>
internal static class FontsThrowHelper
{
    /// <summary>
    /// Throws an <see cref="GlyphMissingException"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T ThrowGlyphMissingException<T>(CodePoint codePoint)
        => throw new GlyphMissingException(codePoint);

    /// <summary>
    /// Throws an <see cref="FontException"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowDefaultInstance()
        => throw new FontException("Cannot use the default value type instance to create a font.");

    /// <summary>
    /// Throws an <see cref="InvalidOperationException"/> recording that shaping
    /// state was queried outside a segment window, where no shape plan is current.
    /// Kept out of the accessor so it stays inlinable.
    /// </summary>
    /// <typeparam name="T">The declared result type of the failed accessor.</typeparam>
    /// <returns>Never returns; the type satisfies the caller's flow analysis.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T ThrowNoCurrentShapePlan<T>()
        => throw new InvalidOperationException(
            "No shape plan is current; the operation is only valid while a segment is being shaped.");
}
