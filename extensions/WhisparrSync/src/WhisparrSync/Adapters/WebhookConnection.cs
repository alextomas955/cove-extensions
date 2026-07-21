namespace WhisparrSync.Adapters;

/// <summary>
/// The adapter's projection of the Cove webhook connection read back from Whisparr's notification list:
/// the row <see cref="Id"/> (the PUT-update target) and the <see cref="Url"/> lifted from its <c>url</c>
/// field — the connector-sourced webhook URL the settings read treats as authoritative. A <c>null</c>
/// result (never this record with a null <see cref="Url"/>) is how the read reports "no connection yet".
/// </summary>
internal sealed record WebhookConnection(int Id, string? Url);
