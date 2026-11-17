namespace MediaStudio.Configuration;

/// <summary>
/// The single privacy gate for work that leaves this machine. Every cloud provider (chat tiers, agents, speech,
/// document extractors) asks it before sending anything; local work is always allowed.
/// </summary>
public static class CloudGuard
{
    /// <summary>True when the work is local, or it is cloud work and <c>privacy.allowCloud</c> is true.</summary>
    public static bool Allows(bool allowCloud, bool isCloud) => !isCloud || allowCloud;

    /// <inheritdoc cref="Allows(bool, bool)"/>
    public static bool Allows(ProviderSettings settings, bool isCloud)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Allows(settings.Privacy.AllowCloud, isCloud);
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> when cloud work is attempted while <c>privacy.allowCloud</c> is false.</summary>
    /// <param name="what">What would go to the cloud, for example <c>speech.tts</c> or <c>documents:mistral</c>.</param>
    /// <param name="alternative">Optional hint naming the local alternative.</param>
    public static void Require(ProviderSettings settings, string what, bool isCloud = true, string? alternative = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Require(settings.Privacy.AllowCloud, what, isCloud, alternative);
    }

    /// <inheritdoc cref="Require(ProviderSettings, string, bool, string?)"/>
    public static void Require(bool allowCloud, string what, bool isCloud = true, string? alternative = null)
    {
        if (Allows(allowCloud, isCloud)) return;
        throw new InvalidOperationException(
            $"{what} uses a cloud service but privacy.allowCloud is false. Enable cloud in settings"
            + (alternative is null ? "." : $", or {alternative}."));
    }
}
