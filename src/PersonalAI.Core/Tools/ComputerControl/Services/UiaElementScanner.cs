using System.Runtime.Versioning;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using PersonalAI.Core.Tools.ComputerControl.Native;

namespace PersonalAI.Core.Tools.ComputerControl.Services;

[SupportedOSPlatform("windows")]
public sealed class UiaElementScanner : IUiaElementScanner
{
    private const int MaxElements = 150;
    private const string NoElementsMessage = "No interactive elements detected.";

    private static readonly HashSet<ControlType> InterestingControlTypes =
    [
        ControlType.Button, ControlType.Edit, ControlType.CheckBox, ControlType.ComboBox,
        ControlType.Hyperlink, ControlType.MenuItem, ControlType.ListItem, ControlType.TabItem,
        ControlType.RadioButton
    ];

    public (string Text, int Count) Scan()
    {
        // The element tree is a nicety on top of the screenshot file — never let a UIA failure
        // (e.g. a missing interop assembly) take the whole screenshot action down.
        try
        {
            return ScanCore();
        }
        catch (Exception ex)
        {
            return ($"UI Automation unavailable ({ex.GetType().Name}: {ex.Message}).", 0);
        }
    }

    private static (string Text, int Count) ScanCore()
    {
        using var automation = new UIA3Automation();

        AutomationElement? root;
        try
        {
            var hwnd = Win32.GetForegroundWindow();
            root = hwnd != nint.Zero ? automation.FromHandle(hwnd) : null;
            root ??= automation.GetDesktop();
        }
        catch
        {
            try { root = automation.GetDesktop(); }
            catch { return (NoElementsMessage, 0); }
        }

        if (root is null)
            return (NoElementsMessage, 0);

        AutomationElement[] descendants;
        try
        {
            descendants = root.FindAllDescendants();
        }
        catch
        {
            return (NoElementsMessage, 0);
        }

        var sb = new StringBuilder();
        var index = 0;
        foreach (var element in descendants)
        {
            if (index >= MaxElements) break;
            if (!TryDescribe(element, out var line)) continue;

            sb.AppendLine($"[{index}] {line}");
            index++;
        }

        return index == 0 ? (NoElementsMessage, 0) : (sb.ToString().TrimEnd('\r', '\n'), index);
    }

    private static bool TryDescribe(AutomationElement element, out string line)
    {
        line = "";
        try
        {
            if (element.IsOffscreen) return false;

            var controlType = element.ControlType;
            if (!InterestingControlTypes.Contains(controlType)) return false;

            var rect = element.BoundingRectangle;
            if (rect.IsEmpty) return false;

            var name = element.Name ?? "";
            line = $"{controlType} \"{name}\" @ ({rect.X},{rect.Y}) {rect.Width}x{rect.Height}";
            return true;
        }
        catch
        {
            // UIA elements can throw (e.g. COMException) for stale/unsupported elements — skip them.
            return false;
        }
    }
}
