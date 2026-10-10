using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace FindCopy.App;

internal static class AccessibilityStatus
{
    internal static void Set(TextBlock target, string text)
    {
        if (target.Text == text) return;
        target.Text = text;
        if (text.Length == 0) return;
        var peer = UIElementAutomationPeer.FromElement(target) ?? UIElementAutomationPeer.CreatePeerForElement(target);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
