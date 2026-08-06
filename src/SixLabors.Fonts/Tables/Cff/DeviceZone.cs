// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// One CFF alignment-zone record in the signed 16.16 representation consumed by
/// the grid fitter.
/// </summary>
internal struct DeviceZone
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceZone"/> struct.
    /// </summary>
    /// <param name="designUpper">The upper design-space edge in signed 16.16.</param>
    /// <param name="designLower">The lower design-space edge in signed 16.16.</param>
    /// <param name="deviceUpper">The transformed upper device-space edge in signed 16.16.</param>
    /// <param name="deviceLower">The transformed lower device-space edge in signed 16.16.</param>
    /// <param name="isBottom">Whether the record is a bottom alignment zone.</param>
    public DeviceZone(
        int designUpper,
        int designLower,
        int deviceUpper,
        int deviceLower,
        bool isBottom)
    {
        this.DesignUpper = designUpper;
        this.DesignLower = designLower;
        this.DeviceUpper = deviceUpper;
        this.DeviceLower = deviceLower;
        this.IsBottom = isBottom;
    }

    /// <summary>
    /// Gets the upper design-space edge in signed 16.16.
    /// </summary>
    public int DesignUpper { get; }

    /// <summary>
    /// Gets the lower design-space edge in signed 16.16.
    /// </summary>
    public int DesignLower { get; }

    /// <summary>
    /// Gets or sets the transformed upper device-space edge in signed 16.16.
    /// </summary>
    public int DeviceUpper { get; set; }

    /// <summary>
    /// Gets or sets the transformed lower device-space edge in signed 16.16.
    /// </summary>
    public int DeviceLower { get; set; }

    /// <summary>
    /// Gets a value indicating whether this record is a bottom alignment zone.
    /// </summary>
    public bool IsBottom { get; }

    /// <summary>
    /// Gets the flat design-space edge in signed 16.16.
    /// </summary>
    public readonly int DesignFlat => this.IsBottom ? this.DesignUpper : this.DesignLower;
}
