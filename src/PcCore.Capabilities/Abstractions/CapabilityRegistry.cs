using System.Diagnostics.CodeAnalysis;

namespace PcCore.Capabilities.Abstractions;

/// <summary>
/// The set of actions this PC Core instance exposes. Adding a capability is a registration,
/// not a change to the dispatcher or any transport.
/// </summary>
public interface ICapabilityRegistry
{
    bool TryGet(string action, [NotNullWhen(true)] out ICapabilityAction? capability);

    IReadOnlyList<ICapabilityAction> Actions { get; }
}

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly Dictionary<string, ICapabilityAction> _actions;

    public CapabilityRegistry(IEnumerable<ICapabilityAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        _actions = new Dictionary<string, ICapabilityAction>(StringComparer.Ordinal);

        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.Name))
            {
                throw new InvalidOperationException(
                    $"Capability {action.GetType().Name} has an empty name.");
            }

            // A duplicate name would make dispatch ambiguous and could silently shadow a
            // capability. Fail at startup rather than at request time.
            if (!_actions.TryAdd(action.Name, action))
            {
                throw new InvalidOperationException(
                    $"Duplicate capability action '{action.Name}' " +
                    $"({_actions[action.Name].GetType().Name} and {action.GetType().Name}).");
            }
        }

        Actions = _actions.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<ICapabilityAction> Actions { get; }

    public bool TryGet(string action, [NotNullWhen(true)] out ICapabilityAction? capability)
    {
        capability = null;
        return !string.IsNullOrWhiteSpace(action) && _actions.TryGetValue(action, out capability);
    }
}
