// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// Builds the fixed-size device alignment-zone table used by the CFF grid fitter.
/// </summary>
internal static class CffBlueZoneTable
{
    /// <summary>
    /// The maximum number of actual or family alignment zones accepted by GDI.
    /// </summary>
    public const int MaximumZoneCount = 12;

    /// <summary>
    /// Transforms and post-processes alignment zones into caller-owned storage without
    /// allocating per glyph or per fit.
    /// </summary>
    /// <param name="actualZones">The font's alignment zones in declaration order.</param>
    /// <param name="familyZones">The family alignment zones in declaration order.</param>
    /// <param name="verticalScale">The design-to-device vertical scale.</param>
    /// <param name="firstStandardHorizontalHeight">The first standard horizontal stem height in design units.</param>
    /// <param name="destination">The caller-owned zone storage.</param>
    /// <returns>The number of records written to <paramref name="destination"/>.</returns>
    public static int Prepare(
        HintZone[] actualZones,
        HintZone[] familyZones,
        float verticalScale,
        float firstStandardHorizontalHeight,
        Span<DeviceZone> destination)
    {
        int scale = CffFixedPoint.FromSingle(verticalScale);
        for (int i = 0; i < actualZones.Length; i++)
        {
            destination[i] = TransformZone(actualZones[i], scale);
        }

        if (familyZones.Length > 0)
        {
            // GDI holds at most twelve family records in its font context. Stack storage
            // retains that fixed bound and avoids making zone preparation a glyph allocation.
            Span<DeviceZone> transformedFamily = stackalloc DeviceZone[MaximumZoneCount];
            for (int i = 0; i < familyZones.Length; i++)
            {
                transformedFamily[i] = TransformZone(familyZones[i], scale);
            }

            SubstituteFamilyEdges(destination[..actualZones.Length], transformedFamily[..familyZones.Length]);

            // SetUpStemW supplies the magnitude of the transformed standard horizontal
            // height. RaiseTops is family-gated and uses a strict upper bound below two pixels.
            int standardHeightDevice = Magnitude(CffFixedPoint.Multiply(
                CffFixedPoint.FromSingle(firstStandardHorizontalHeight),
                scale));

            if (standardHeightDevice <= 0x1FFFF)
            {
                RaiseTops(destination[..actualZones.Length], standardHeightDevice);
            }
        }

        BoostBotLocations(destination[..actualZones.Length]);
        return actualZones.Length;
    }

    /// <summary>
    /// Converts one declarative zone to the native signed 16.16 record layout.
    /// </summary>
    /// <param name="zone">The declarative zone.</param>
    /// <param name="scale">The signed 16.16 vertical scale.</param>
    /// <returns>The transformed device-zone record.</returns>
    private static DeviceZone TransformZone(HintZone zone, int scale)
    {
        int designUpper = CffFixedPoint.FromSingle(zone.Top);
        int designLower = CffFixedPoint.FromSingle(zone.Bottom);

        return new DeviceZone(
            designUpper,
            designLower,
            CffFixedPoint.Multiply(designUpper, scale),
            CffFixedPoint.Multiply(designLower, scale),
            zone.IsBottom);
    }

    /// <summary>
    /// Replaces cached actual-zone device edges with nearby same-kind family edges.
    /// </summary>
    /// <param name="actualZones">The transformed actual zones.</param>
    /// <param name="familyZones">The transformed family zones.</param>
    private static void SubstituteFamilyEdges(Span<DeviceZone> actualZones, ReadOnlySpan<DeviceZone> familyZones)
    {
        const int initialDistance = 0x03E80000;

        for (int i = 0; i < actualZones.Length; i++)
        {
            DeviceZone actual = actualZones[i];
            int nearestUpperDistance = initialDistance;
            int nearestLowerDistance = initialDistance;
            int nearestUpper = -1;
            int nearestLower = -1;

            for (int j = 0; j < familyZones.Length; j++)
            {
                DeviceZone family = familyZones[j];
                if (actual.IsBottom != family.IsBottom)
                {
                    continue;
                }

                // Both searches use strict improvement, so equal distances retain the
                // earlier family record exactly as SetUpBlueValues does.
                int upperDistance = Magnitude(unchecked(family.DeviceUpper - actual.DeviceUpper));
                if (upperDistance < nearestUpperDistance)
                {
                    nearestUpperDistance = upperDistance;
                    nearestUpper = j;
                }

                int lowerDistance = Magnitude(unchecked(family.DeviceLower - actual.DeviceLower));
                if (lowerDistance < nearestLowerDistance)
                {
                    nearestLowerDistance = lowerDistance;
                    nearestLower = j;
                }
            }

            // Family substitution is edge-local: either edge may be replaced from a
            // different family record, and the design-space zone remains unchanged.
            if (nearestUpperDistance < CffFixedPoint.One)
            {
                actual.DeviceUpper = familyZones[nearestUpper].DeviceUpper;
            }

            if (nearestLowerDistance < CffFixedPoint.One)
            {
                actual.DeviceLower = familyZones[nearestLower].DeviceLower;
            }

            actualZones[i] = actual;
        }
    }

    /// <summary>
    /// Applies GDI's granularity-one family-zone top-edge adjustment.
    /// </summary>
    /// <param name="zones">The family-substituted actual zones.</param>
    /// <param name="standardHeightDevice">The transformed standard horizontal height in signed 16.16.</param>
    private static void RaiseTops(Span<DeviceZone> zones, int standardHeightDevice)
    {
        const int pixelMask = unchecked((int)0xFFFF0000);
        const int halfPixel = 0x8000;
        const int oneEighthPixel = 0x2000;

        // Zone zero is the baseline bottom zone. Top zones follow it contiguously, and
        // the native walk terminates rather than skipping when it reaches another bottom.
        for (int i = 1; i < zones.Length; i++)
        {
            DeviceZone zone = zones[i];
            if (zone.IsBottom)
            {
                return;
            }

            int delta = zone.DeviceUpper <= zone.DeviceLower
                ? standardHeightDevice
                : -standardHeightDevice;

            // The two arithmetic shifts are deliberately separate. Combining the terms
            // changes the low bit for negative or odd fixed-point inputs.
            int midpoint = (unchecked(zone.DeviceUpper + delta) >> 1) + (zone.DeviceUpper >> 1);
            if (((midpoint ^ zone.DeviceUpper) & pixelMask) != 0)
            {
                continue;
            }

            int nearest = unchecked(zone.DeviceUpper + halfPixel) & pixelMask;
            int distance = Magnitude(unchecked(zone.DeviceUpper - nearest));
            if (distance > oneEighthPixel)
            {
                zone.DeviceUpper = unchecked((zone.DeviceUpper & pixelMask) + CffFixedPoint.One);
                zones[i] = zone;
            }
        }
    }

    /// <summary>
    /// Applies GDI's granularity-one adjustment to later bottom-zone device uppers.
    /// </summary>
    /// <param name="zones">The transformed actual zones.</param>
    private static void BoostBotLocations(Span<DeviceZone> zones)
    {
        if (zones.Length <= 2 || zones[0].DeviceUpper != 0)
        {
            return;
        }

        for (int i = 1; i < zones.Length; i++)
        {
            DeviceZone zone = zones[i];
            if (!zone.IsBottom)
            {
                continue;
            }

            int magnitude = Magnitude(zone.DeviceUpper);
            if ((uint)(magnitude - 0x10001) < 0x7FFFU)
            {
                zone.DeviceUpper = -0x18001;
                zones[i] = zone;
            }
        }
    }

    /// <summary>
    /// Computes the wrapping signed magnitude used by the native fixed-point routines.
    /// </summary>
    /// <param name="value">The signed fixed-point value.</param>
    /// <returns>The value's magnitude, retaining <see cref="int.MinValue"/> on overflow.</returns>
    private static int Magnitude(int value)
    {
        int negated = unchecked(-value);
        return negated < 0 ? value : negated;
    }
}
