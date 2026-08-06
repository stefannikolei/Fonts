// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// Implements the signed 16.16 arithmetic used by the native CFF grid fitter.
/// </summary>
internal static class CffFixedPoint
{
    /// <summary>
    /// The signed 16.16 representation of one.
    /// </summary>
    public const int One = 1 << 16;

    /// <summary>
    /// Converts a parsed CFF value to signed 16.16, saturating values outside the
    /// representable range and rounding halfway values away from zero.
    /// </summary>
    /// <param name="value">The parsed value.</param>
    /// <returns>The signed 16.16 value.</returns>
    public static int FromSingle(float value) => RoundAndSaturate((double)value * One);

    /// <summary>
    /// Converts a signed 16.16 value to a single-precision value.
    /// </summary>
    /// <param name="value">The signed 16.16 value.</param>
    /// <returns>The corresponding single-precision value.</returns>
    public static float ToSingle(int value) => value * (1F / One);

    /// <summary>
    /// Multiplies two signed 16.16 values, saturating overflow and rounding halfway
    /// values away from zero.
    /// </summary>
    /// <param name="left">The first signed 16.16 value.</param>
    /// <param name="right">The second signed 16.16 value.</param>
    /// <returns>The signed 16.16 product.</returns>
    public static int Multiply(int left, int right)
    {
        long product = (long)left * right;
        long quotient = product / One;
        long remainder = product % One;

        // Integer division truncates toward zero. FixedMul advances an exact half, or
        // anything beyond it, one unit away from zero before applying saturation.
        if (Math.Abs(remainder) * 2 >= One)
        {
            quotient += Math.Sign(product);
        }

        return Saturate(quotient);
    }

    /// <summary>
    /// Divides one signed 16.16 value by another, saturating overflow and rounding
    /// halfway values away from zero.
    /// </summary>
    /// <param name="dividend">The signed 16.16 dividend.</param>
    /// <param name="divisor">The signed 16.16 divisor.</param>
    /// <returns>The signed 16.16 quotient.</returns>
    public static int Divide(int dividend, int divisor)
    {
        if (divisor == 0)
        {
            // The native fixed divider returns the saturated value with the dividend's
            // sign rather than raising an arithmetic exception for a zero denominator.
            return dividend < 0 ? int.MinValue : int.MaxValue;
        }

        long numerator = (long)dividend * One;
        long quotient = numerator / divisor;
        long remainder = numerator % divisor;
        long denominatorMagnitude = Math.Abs((long)divisor);
        if (Math.Abs(remainder) * 2 >= denominatorMagnitude)
        {
            quotient += Math.Sign(numerator) * Math.Sign(divisor);
        }

        return Saturate(quotient);
    }

    /// <summary>
    /// Applies the native fixed-point conversion's rounding and saturation policy.
    /// </summary>
    /// <param name="value">The unrounded signed value.</param>
    /// <returns>The rounded and saturated 32-bit value.</returns>
    private static int RoundAndSaturate(double value)
    {
        // Adding half with the value's sign followed by truncation reproduces the
        // native conversion, including its away-from-zero handling of exact halves.
        double rounded = value < 0D ? value - 0.5D : value + 0.5D;
        if (rounded >= int.MaxValue)
        {
            return int.MaxValue;
        }

        if (rounded <= int.MinValue)
        {
            return int.MinValue;
        }

        return (int)rounded;
    }

    /// <summary>
    /// Saturates a signed integer intermediate to the fixed-point storage range.
    /// </summary>
    /// <param name="value">The exact integer intermediate.</param>
    /// <returns>The saturated 32-bit value.</returns>
    private static int Saturate(long value)
    {
        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        if (value <= int.MinValue)
        {
            return int.MinValue;
        }

        return (int)value;
    }
}
