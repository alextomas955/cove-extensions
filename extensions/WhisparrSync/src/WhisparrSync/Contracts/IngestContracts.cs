namespace WhisparrSync.Contracts;

/// <summary>
/// The inbound <c>/webhook</c> acknowledgement. Whisparr reads only the status code, so the body exists for a
/// human reading the delivery log; <c>OK</c> is returned for a handled delivery AND for a duplicate the ledger
/// already absorbed, because a retry that is refused looks like a fault to Whisparr's connection test.
/// </summary>
internal sealed record WebhookAckResponse(string Code);

/// <summary>
/// The <c>/import-log</c> response: the pre-reduced auto-import status the settings UI consumes.
/// </summary>
/// <remarks>
/// <see cref="LastEventTicks"/> is zero until the first delivery lands. <see cref="PipelineHealth"/> omits a
/// dependency nothing has ever observed rather than reporting it healthy.
/// </remarks>
internal sealed record ImportStatusResponse(
    long LastEventTicks, SyncHealthView SyncHealth, IReadOnlyList<DependencyHealthView> PipelineHealth);
