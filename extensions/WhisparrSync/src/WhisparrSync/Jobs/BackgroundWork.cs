using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace WhisparrSync.Jobs;

/// <summary>What an endpoint needs to hand work to the host's job list and run it off the request.</summary>
/// <remarks>
/// A run outlives the request that enqueued it, so it opens a scope of its own rather than holding
/// the request's services.
/// </remarks>
internal sealed record BackgroundWork(IJobService Jobs, IServiceScopeFactory Scopes);
