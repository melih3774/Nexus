using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace PcAssistant.Windows;

public enum UiAction { Invoke, Select, SetValue, Toggle, Expand, Collapse, Focus, ScrollIntoView }
public sealed record UiNode(string Reference, string? ParentReference, int Depth, string Name, string ControlType,
    string AutomationId, PixelRect? Bounds, bool Enabled, bool Offscreen, bool Password, IReadOnlyList<string> Actions,
    bool KeyboardFocusable = false, bool HasKeyboardFocus = false, string? Value = null);
public sealed record UiSnapshot(long Hwnd, DateTimeOffset ExpiresAt, IReadOnlyList<UiNode> Nodes, bool Truncated);

/// <summary>Result of a targeted search: the matches, and honest counts around them.</summary>
public sealed record UiFindResult(long Hwnd, DateTimeOffset ExpiresAt, IReadOnlyList<UiNode> Matches,
    int Examined, bool Truncated, string Note);

public sealed class UiAutomationService : IDisposable
{
    private sealed record Entry(AutomationElement Element, int[] RuntimeId, long Hwnd, int ProcessId, DateTimeOffset ExpiresAt);
    private readonly ApartmentWorker worker = new(ApartmentState.MTA);
    private readonly Dictionary<string, Entry> references = [];
    private readonly ConcurrentDictionary<string, (UiNode Node, long Hwnd, DateTimeOffset Expires)> descriptions = new();

    public string DescribeReference(string reference)
    {
        if (!descriptions.TryGetValue(reference, out var item) || item.Expires < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("UI reference is stale; take a fresh snapshot.");
        return $"Window {item.Hwnd}: {item.Node.ControlType} '{item.Node.Name}' (reference {reference})";
    }

    /// <summary>
    /// A bounded breadth-first read of a window's control tree.
    ///
    /// Bounded on three axes — depth, node count and wall clock — because an accessibility
    /// tree has no natural size and a browser or an IDE will happily produce tens of thousands
    /// of nodes. When a bound is hit the result says so rather than pretending it is complete.
    /// </summary>
    public Task<UiSnapshot> SnapshotAsync(long hwnd, int maxDepth = 5, int maxNodes = 150, CancellationToken cancellationToken = default)
    {
        if (maxDepth is < 0 or > 12) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (maxNodes is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        Native.Window(hwnd);
        return worker.RunAsync(operationToken =>
        {
            var (nodes, truncated, expires, _) = Walk(hwnd, maxDepth, maxNodes, maxNodes, null, null, operationToken);
            return new UiSnapshot(hwnd, expires, nodes, truncated);
        }, cancellationToken);
    }

    /// <summary>
    /// The targeted question a full snapshot is a poor answer to: "where is the Save button?"
    ///
    /// Searching deeper than a snapshot would while returning far less is the point — a
    /// control eight levels down in a dialog is reachable here without dumping the seven
    /// levels above it into the conversation.
    /// </summary>
    public Task<UiFindResult> FindAsync(long hwnd, string? name = null, string? controlType = null,
        int maxResults = 12, int maxDepth = 10, int maxExamined = 2500, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(controlType))
            throw new ArgumentException("Give a name fragment, a control type, or both.", nameof(name));
        if (name is { Length: > 256 }) throw new ArgumentOutOfRangeException(nameof(name), "Use a short name fragment.");
        if (maxResults is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(maxResults));
        if (maxDepth is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (maxExamined is < 1 or > 20000) throw new ArgumentOutOfRangeException(nameof(maxExamined));
        Native.Window(hwnd);

        var type = NormalizeControlType(controlType);
        return worker.RunAsync(operationToken =>
        {
            var (nodes, truncated, expires, examined) =
                Walk(hwnd, maxDepth, maxResults, maxExamined, name, type, operationToken);
            var note = nodes.Count == 0
                ? $"No control in window {hwnd} matched after examining {examined} nodes" +
                  (truncated ? " before the search bound was reached" : "") +
                  ". The application may draw this control itself rather than exposing it to UI Automation: " +
                  "capture the window and act on the image with input instead."
                : $"{nodes.Count} match(es) after examining {examined} nodes. References expire in two minutes and " +
                  "any later snapshot or find replaces them.";
            return new UiFindResult(hwnd, expires, nodes, examined, truncated, note);
        }, cancellationToken);
    }

    /// <summary>
    /// The single tree walk behind both a snapshot and a find. <paramref name="nameFragment"/>
    /// and <paramref name="controlType"/> filter what is returned; everything else is still
    /// descended into, which is what lets a find reach deeper than it reports.
    /// </summary>
    private (IReadOnlyList<UiNode> Nodes, bool Truncated, DateTimeOffset Expires, int Examined) Walk(
        long hwnd, int maxDepth, int maxResults, int maxExamined,
        string? nameFragment, ControlType? controlType, CancellationToken operationToken)
    {
        using var dpi = new Native.DpiScope();

        // Every fresh observation deliberately invalidates old references: a reference that
        // outlived the tree it came from is the one thing worse than not having it.
        references.Clear();
        descriptions.Clear();

        var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        var watch = Stopwatch.StartNew();
        var root = AutomationElement.FromHandle(Native.Window(hwnd));
        var pending = new Queue<(AutomationElement Element, string? Parent, int Depth)>();
        pending.Enqueue((root, null, 0));
        var result = new List<UiNode>();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var truncated = false;
        var examined = 0;
        var filtering = nameFragment is not null || controlType is not null;

        while (pending.Count > 0 && result.Count < maxResults && examined < maxExamined)
        {
            operationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) { truncated = true; break; }
            var (element, parent, depth) = pending.Dequeue();
            examined++;
            try
            {
                var current = element.Current;
                var actions = new List<string>();
                string? value = null;
                if (!current.IsPassword)
                {
                    AddPattern(InvokePattern.Pattern, "Invoke"); AddPattern(SelectionItemPattern.Pattern, "Select");
                    AddPattern(ValuePattern.Pattern, "SetValue"); AddPattern(TogglePattern.Pattern, "Toggle");
                    if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _)) actions.AddRange(["Expand", "Collapse"]);
                    if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out _)) actions.Add("ScrollIntoView");
                    if (current.IsKeyboardFocusable) actions.Add("Focus");
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
                        value = Bound(((ValuePattern)valuePattern).Current.Value ?? "", 512);
                }

                var matches = !filtering ||
                    ((nameFragment is null || current.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase) ||
                      current.AutomationId.Contains(nameFragment, StringComparison.OrdinalIgnoreCase)) &&
                     (controlType is null || Equals(current.ControlType, controlType)));

                if (matches)
                {
                    var rect = current.BoundingRectangle;
                    var reference = prefix + "-" + result.Count;
                    var node = new UiNode(reference, parent, depth, current.IsPassword ? "[password]" : Bound(current.Name),
                        current.ControlType.ProgrammaticName, Bound(current.AutomationId),
                        rect.IsEmpty ? null : new PixelRect((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height),
                        current.IsEnabled, current.IsOffscreen, current.IsPassword, actions,
                        current.IsKeyboardFocusable, current.HasKeyboardFocus, current.IsPassword ? null : value);
                    result.Add(node);
                    references[reference] = new Entry(element, element.GetRuntimeId(), hwnd, current.ProcessId, expires);
                    descriptions[reference] = (node, hwnd, expires);
                }

                // Password subtrees are never descended into: nothing under a credential
                // control is worth the risk of reading it out by accident.
                if (depth < maxDepth && !current.IsPassword)
                {
                    var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                    while (child != null)
                    {
                        if (watch.Elapsed > TimeSpan.FromSeconds(5) || pending.Count + examined >= maxExamined) { truncated = true; break; }
                        // An unfiltered walk keeps the old bound: its queue is its output.
                        if (!filtering && pending.Count + result.Count >= maxResults) { truncated = true; break; }
                        pending.Enqueue((child, matches ? result[^1].Reference : parent, depth + 1));
                        operationToken.ThrowIfCancellationRequested();
                        child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                    }
                }
                else if (!current.IsPassword && TreeWalker.ControlViewWalker.GetFirstChild(element) is not null) truncated = true;

                void AddPattern(AutomationPattern pattern, string name) { if (element.TryGetCurrentPattern(pattern, out _)) actions.Add(name); }
            }
            catch (ElementNotAvailableException) { truncated = true; }
        }

        return (result, truncated || pending.Count > 0 || examined >= maxExamined, expires, examined);
    }

    /// <summary>Accepts "Button", "button" or the full "ControlType.Button" programmatic name.</summary>
    private static ControlType? NormalizeControlType(string? controlType)
    {
        if (string.IsNullOrWhiteSpace(controlType)) return null;
        var wanted = controlType.Trim();
        if (wanted.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase)) wanted = wanted["ControlType.".Length..];
        foreach (var candidate in typeof(ControlType).GetFields()
                     .Where(f => f.IsStatic && f.FieldType == typeof(ControlType))
                     .Select(f => (ControlType)f.GetValue(null)!))
        {
            if (candidate.ProgrammaticName.EndsWith("." + wanted, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        throw new ArgumentException(
            $"Unknown control type '{controlType}'. Use a UI Automation control type such as Button, Edit, CheckBox, " +
            "MenuItem, ListItem, TabItem, Tree, Text or Window.", nameof(controlType));
    }

    public async Task<string> ActAsync(string reference, UiAction action, string? value = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (value?.Length > 16000) throw new ArgumentOutOfRangeException(nameof(value));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));

        // Same contract as input: the approved operation takes the foreground itself rather than assuming a
        // separate focus call survived the owner's approval click.
        if (descriptions.TryGetValue(reference, out var known) && known.Expires >= DateTimeOffset.UtcNow)
        {
            ProtectedWindows.EnsureNotProtectedWindow(known.Hwnd);
            var foreground = await ForegroundService.EnsureAsync(known.Hwnd, cancellationToken).ConfigureAwait(false);
            if (!foreground.Verified) throw new ForegroundUnavailableException(foreground.Message);
        }

        return await worker.RunAsync(operationToken =>
        {
            if (!references.TryGetValue(reference, out var entry) || entry.ExpiresAt < DateTimeOffset.UtcNow)
                throw new InvalidOperationException("UI reference is stale; take a fresh snapshot.");
            // Checked again against the resolved element, not only the recorded window: a
            // provider can move an element between snapshot and action.
            ProtectedWindows.EnsureNotProtectedWindow(entry.Hwnd);
            ProtectedWindows.EnsureNotProtectedProcess(entry.ProcessId);
            Native.Window(entry.Hwnd);
            if (Native.GetForegroundWindow() != (nint)entry.Hwnd) throw new ForegroundUnavailableException($"Window {entry.Hwnd} is not foreground; take a fresh snapshot and retry.");
            var element = entry.Element;
            var current = element.Current;
            if (!element.GetRuntimeId().SequenceEqual(entry.RuntimeId) || current.ProcessId != entry.ProcessId)
                throw new InvalidOperationException("UI element identity changed; take a fresh snapshot.");
            if (current.IsPassword) throw new CredentialEntryException(InputService.CredentialRule);
            if (!current.IsEnabled) throw new InvalidOperationException("The control must be enabled.");
            // Scrolling something into view is the one action whose whole purpose is to fix
            // being offscreen, so it is the one action that may start there.
            if (current.IsOffscreen && action != UiAction.ScrollIntoView)
                throw new InvalidOperationException(
                    "The control is offscreen. Use ui_action scrollIntoView first, or scroll the window, then observe again.");
            // A provider can move an existing element; recheck it still belongs to the selected window.
            var ancestor = element;
            for (var depth = 0; ; depth++)
            {
                if (ancestor.Current.NativeWindowHandle == entry.Hwnd) break;
                if (depth >= 64 || (ancestor = TreeWalker.RawViewWalker.GetParent(ancestor)) is null)
                    throw new InvalidOperationException("The control no longer belongs to the selected window.");
            }
            operationToken.ThrowIfCancellationRequested();
            if (Native.GetForegroundWindow() != (nint)entry.Hwnd) throw new ForegroundUnavailableException($"Window {entry.Hwnd} lost the foreground while the UI reference was resolved; nothing was done.");
            switch (action)
            {
                case UiAction.Invoke: Pattern<InvokePattern>(InvokePattern.Pattern).Invoke(); break;
                case UiAction.Select: Pattern<SelectionItemPattern>(SelectionItemPattern.Pattern).Select(); break;
                case UiAction.SetValue:
                    if (value is null) throw new ArgumentException("SetValue requires a value.", nameof(value));
                    var pattern = Pattern<ValuePattern>(ValuePattern.Pattern);
                    if (pattern.Current.IsReadOnly) throw new InvalidOperationException("The value is read-only.");
                    pattern.SetValue(value); break;
                case UiAction.Toggle: Pattern<TogglePattern>(TogglePattern.Pattern).Toggle(); break;
                case UiAction.Expand: Pattern<ExpandCollapsePattern>(ExpandCollapsePattern.Pattern).Expand(); break;
                case UiAction.Collapse: Pattern<ExpandCollapsePattern>(ExpandCollapsePattern.Pattern).Collapse(); break;
                case UiAction.Focus:
                    if (!current.IsKeyboardFocusable) throw new InvalidOperationException("The control cannot take keyboard focus.");
                    element.SetFocus(); break;
                case UiAction.ScrollIntoView: Pattern<ScrollItemPattern>(ScrollItemPattern.Pattern).ScrollIntoView(); break;
            }
            return "UI action completed. That is what the provider reported, not proof of what the application did: " +
                   "observe again — ui_snapshot, ui_find or capture_window — and confirm the intended change before reporting success.";
            T Pattern<T>(AutomationPattern id) => element.TryGetCurrentPattern(id, out var result) ? (T)result : throw new InvalidOperationException(
                $"This control does not support {id.ProgrammaticName}. If no UI Automation pattern fits, capture the window " +
                "and act on the image with input instead of trying the same action again.");
        }, cancellationToken);
    }

    private static string Bound(string value, int max = 1024) => value[..Math.Min(value.Length, max)];
    public void Dispose() => worker.Dispose();
}
