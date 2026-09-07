namespace WhisparrSync.Options;

/// <summary>
/// The single predicate deciding which required stored options are unusable. Pure and host-free: it reads the
/// stored values and issues no request.
/// </summary>
/// <remarks>
/// Every key is derived from that option's OWN stored value, never from a transport classification and never
/// from one composite boolean — a named refusal derived from a proxy is how a confident, wrong message ships.
/// </remarks>
internal static class ConfigCompletenessGuard
{
    /// <summary>
    /// The camelCase keys of the required options that are unset, ordered address → key. Empty when the stored
    /// configuration can support an operation.
    /// </summary>
    /// <remarks>
    /// Only settings the settings UI actually exposes are ever named — the two metadata-endpoint fields are
    /// advanced and unsurfaced, so naming one would point the user at a control that does not exist. The order is
    /// the form's own, so a multi-key sentence reads in setup order.
    /// </remarks>
    public static IReadOnlyList<string> MissingRequiredOptions(WhisparrOptions options)
    {
        var missing = new List<string>(2);

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            missing.Add("baseUrl");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            missing.Add("apiKey");
        }

        return missing;
    }
}
