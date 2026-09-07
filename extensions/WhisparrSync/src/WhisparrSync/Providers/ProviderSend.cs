using System.Text.Json;

namespace WhisparrSync.Providers;

/// <summary>What one send to a provider came back with.</summary>
/// <remarks>
/// A body that arrived, or nothing, and whether the provider itself stated the answer. A refusal is
/// the provider's own answer and re-sending only collects it again, so the two nulls are held apart:
/// one is worth another attempt and one is not.
/// </remarks>
/// <param name="Body">The answer the provider served, or null where none arrived.</param>
/// <param name="WasDefinite">The provider stated the answer, so another attempt would repeat it.</param>
internal readonly record struct ProviderSend(JsonElement? Body, bool WasDefinite)
{
    /// <summary>Nothing arrived, and another attempt may still reach the provider.</summary>
    internal static ProviderSend Nothing => new(null, WasDefinite: false);

    /// <summary>The provider stated a refusal.</summary>
    internal static ProviderSend Refused => new(null, WasDefinite: true);

    /// <summary>The provider served <paramref name="body"/>.</summary>
    internal static ProviderSend Carrying(JsonElement body) => new(body, WasDefinite: true);
}
