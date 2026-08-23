using Elarion.Abstractions.Pipeline;

namespace Watchtower.Application.Pipeline;

/// <summary>
/// Watchtower's own decorator pipeline, applied to every handler in the assembly.
/// </summary>
/// <remarks>
/// Deliberately one entry long. The framework auto-attaches the gates that matter (observability,
/// audit, authorization, feature flags, validation) from the handlers' own attributes, so this list
/// exists for the one cross-cutting concern that is a property of the <em>database</em> rather than of
/// any handler: a lost optimistic-concurrency race.
/// </remarks>
[DecoratorList(typeof(ConcurrencyConflictDecorator<,>))]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
public sealed class WatchtowerPipelineAttribute : Attribute;
