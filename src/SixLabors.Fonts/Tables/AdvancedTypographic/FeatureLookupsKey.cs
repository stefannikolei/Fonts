// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Cache key for resolved feature lookups: the stage feature, the script, and the
/// language system candidates the resolution ladder selected against. Language tags
/// compare by sequence because each shaping pass resolves a fresh array from the
/// culture.
/// </summary>
internal readonly struct FeatureLookupsKey : IEquatable<FeatureLookupsKey>
{
    private readonly uint feature;
    private readonly Unicode.ScriptClass script;
    private readonly Tag[] languageTags;

    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureLookupsKey"/> struct.
    /// </summary>
    /// <param name="feature">The stage feature tag.</param>
    /// <param name="script">The script class.</param>
    /// <param name="languageTags">The candidate language system tags, most specific first.</param>
    public FeatureLookupsKey(Tag feature, Unicode.ScriptClass script, Tag[] languageTags)
    {
        this.feature = feature.Value;
        this.script = script;
        this.languageTags = languageTags;
    }

    /// <inheritdoc />
    public bool Equals(FeatureLookupsKey other)
    {
        if (this.feature != other.feature || this.script != other.script)
        {
            return false;
        }

        Tag[] tags = this.languageTags;
        Tag[] otherTags = other.languageTags;
        if (ReferenceEquals(tags, otherTags))
        {
            return true;
        }

        if (tags.Length != otherTags.Length)
        {
            return false;
        }

        for (int i = 0; i < tags.Length; i++)
        {
            if (tags[i] != otherTags[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FeatureLookupsKey key && this.Equals(key);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(this.feature);
        hash.Add(this.script);
        for (int i = 0; i < this.languageTags.Length; i++)
        {
            hash.Add(this.languageTags[i].Value);
        }

        return hash.ToHashCode();
    }
}
