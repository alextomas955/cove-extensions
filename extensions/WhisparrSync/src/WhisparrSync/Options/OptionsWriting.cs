namespace WhisparrSync.Options;

/// <summary>The stored options and the gate every write to them passes through.</summary>
/// <remarks>
/// A writer that held one without the other could load, fold and save outside the gate, which is
/// the sequence the gate exists to serialise.
/// </remarks>
internal sealed record OptionsWriting(OptionsStore Store, OptionsWriteGate Gate);
