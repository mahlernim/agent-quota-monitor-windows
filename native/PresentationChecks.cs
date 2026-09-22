using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AgentQuotaMonitor;

// Exercises real native presentation with isolated sample data, without opening a backend.
internal static class PresentationChecks
{
    internal static void Run()
    {
        var now = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        QuotaItem Read(string status = "live", double? remaining = 70, int? duration = 18000,
            DateTimeOffset? reset = null, bool available = true, bool unlimited = false)
        {
            var data = new { accounts = new[] { new { id = "account", provider = "claude", label = "Sample",
                status, groups = new[] { new { id = "group", label = "Sample quota", buckets = new[] {
                    new { id = "bucket", label = "Sample window", remaining, windowSeconds = duration,
                        resetsAt = reset?.ToString("O"), available, unlimited } } } } } } };
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(data));
            return QuotaItem.Parse(document.RootElement, now).Single();
        }
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
        Check(Read(reset: now.AddHours(2.5)).TimeRemaining == 50, "Half of a five-hour window remains");
        Check(Read(duration: 604800, reset: now.AddDays(3.5)).TimeRemaining == 50, "Half of a weekly window remains");
        Check(Read().TimeRemaining is null, "Unknown reset has no time ring");
        Check(Read(reset: now).TimeRemaining is null, "Reset boundary awaits provider confirmation");
        Check(Read(reset: now.AddSeconds(-1)).TimeRemaining is null, "Expired reset has no time ring");
        Check(Read(reset: now.AddHours(6)).TimeRemaining is null, "Implausible reset has no time ring");
        Check(Read(status: "stale", reset: now.AddHours(2)).TimeRemaining is null, "Stale quota has no pace estimate");
        Check(Read(remaining: null, reset: now.AddHours(2)).TimeRemaining is null, "Unknown quota has no pace estimate");
        Check(Read(duration: 3600, reset: now.AddMinutes(30)).TimeRemaining is null, "Unsupported window has no inferred timing");
        Check(Read(available: false, reset: now.AddHours(2)).TimeRemaining is null, "Unavailable bucket has no time ring");
        Check(Read(remaining: null, unlimited: true).Unlimited, "Explicit unlimited remains distinct from unknown");
        Check(!Read(remaining: null).Unlimited, "Missing quota is not unlimited");

        var a = new QuotaItem { Key = "a", AccountId = "a", Provider = "claude", Account = "Account A", Code = "CL" };
        var b = new QuotaItem { Key = "b", AccountId = "b", Provider = "claude", Account = "Account B", Code = "CL" };
        var pins = new HashSet<string> { a.Key };
        var window = new MainWindow(_ => {}, _ => {}, () => {}, () => {}, () => {}, () => {});
        window.SetData(new[] { a, b }, a.Key, pins);
        var first = Texts(window).Where(t => t.StartsWith("claude · Account ")).ToArray();
        window.SetData(new[] { b, a }, a.Key, pins);
        var reordered = Texts(window).Where(t => t.StartsWith("claude · Account ")).ToArray();
        Check(first.SequenceEqual(new[] { "claude · Account A", "claude · Account B" }), "Initial account order is rendered");
        Check(reordered.SequenceEqual(first.Reverse()), "Saved backend account order is rendered");
        Check(Texts(window).Count(t => t.Contains("Tray")) == 1 && pins.SetEquals(new[] { a.Key }),
            "Reordering retains a single tray selection and stable pins");
        window.SetData(Array.Empty<QuotaItem>(), a.Key, pins);
        Check(Texts(window).Any(t => t.Contains("Open Settings")), "Empty state points to native Settings");
        CheckPins(Check);
        CheckPercentageFormatting(Check);
    }

    private static void CheckPercentageFormatting(Action<bool, string> check)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            foreach ((string culture, string expected) in new[] { ("de-DE", "71,1%"), ("en-US", "71.1%"), ("ko-KR", "71.1%") })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                using var data = JsonDocument.Parse("{\"accounts\":[{\"id\":\"locale\",\"provider\":\"claude\",\"label\":\"Sample\",\"status\":\"live\",\"groups\":[{\"id\":\"g\",\"label\":\"Sample\",\"buckets\":[{\"id\":\"b\",\"label\":\"Window\",\"remaining\":71.1}]}]}]}");
                QuotaItem item = QuotaItem.Parse(data.RootElement).Single();
                check(Donut.LabelFor(item) == expected && item.Tooltip.Contains(expected) && TrayController.BuildTooltip(item).Contains(expected),
                    "Donut, quota tooltip and tray use the same percentage format in " + culture);
                check(Donut.LabelFor(new QuotaItem()) == "?" && TrayController.BuildTooltip(new QuotaItem()).Contains("Unknown") &&
                    Donut.LabelFor(new QuotaItem { Unlimited = true }) == "∞" && TrayController.BuildTooltip(new QuotaItem { Unlimited = true }).Contains("Unlimited"),
                    "Unknown and unlimited states remain distinct in " + culture);
                check(JsonSerializer.Serialize(new { remaining = 71.1 }) == "{\"remaining\":71.1}", "Serialized quotas remain invariant in " + culture);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void CheckPins(Action<bool, string> check)
    {
        var a = new QuotaItem { Key = "pin-a", AccountId = "a", Provider = "claude", Account = "Pin account A", Code = "CL", Window = "5h", Status = "live", Remaining = 70 };
        var b = new QuotaItem { Key = "pin-b", AccountId = "b", Provider = "claude", Account = "Pin account B", Code = "CL", Window = "5h", Status = "live", Remaining = 60 };
        IReadOnlyList<QuotaItem> rows = new[] { a, b };
        var pins = new HashSet<string> { a.Key };
        string selected = a.Key;
        int trayActions = 0, pinActions = 0;
        QuotaItem? lastPinnedItem = null;
        MainWindow? window = null;
        window = new MainWindow(item =>
        {
            selected = item.Key; ++trayActions;
            window!.SetData(rows, selected, pins);
        }, item =>
        {
            if (!pins.Add(item.Key)) pins.Remove(item.Key);
            lastPinnedItem = item; ++pinActions;
            window!.SetData(rows, selected, pins);
        }, () => {}, () => {}, () => {}, () => {})
        {
            Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        Button Pin(string account) => Elements<Button>(window).Single(button => AutomationProperties.GetName(button).Contains(account));
        Path Glyph(Button button) => (Path)((Viewbox)button.Content).Child;
        void FlushUi() => window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle, CancellationToken.None, TimeSpan.FromSeconds(5));
        try
        {
            window.SetData(rows, selected, pins);
            window.Show();
            window.UpdateLayout();
            FlushUi();
            Button pinA = Pin(a.Account), pinB = Pin(b.Account);
            check(Glyph(pinA).Fill is SolidColorBrush pinned && pinned.Color == Color.FromRgb(124, 58, 237)
                && Glyph(pinB).Fill is null && Glyph(pinB).Stroke is SolidColorBrush unpinned && unpinned.Color == Color.FromRgb(100, 116, 139),
                "Filled violet and hollow slate pins distinguish saved floating selections");
            check(pinA.Content is Viewbox && pinA.Focusable && pinA.IsTabStop && AutomationProperties.GetName(pinA).StartsWith("Unpin ")
                && AutomationProperties.GetName(pinB).StartsWith("Pin ") && AutomationProperties.GetHelpText(pinA).Contains("tray selection"),
                "Pin controls are keyboard reachable and identify the account and independent action");
            var hit = pinA.InputHitTest(new Point(1, 1)) as DependencyObject;
            check(hit is not null && (ReferenceEquals(hit, pinA) || pinA.IsAncestorOf(hit)), "The full pin button area accepts input outside its inset glyph");
            pinA.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(pinActions == 1 && trayActions == 0 && selected == a.Key && pins.Count == 0 && Glyph(pinA).Fill is null,
                "Unpinning leaves the tray selection intact and updates the glyph");
            var invoke = (IInvokeProvider)new ButtonAutomationPeer(pinB).GetPattern(PatternInterface.Invoke);
            invoke.Invoke();
            FlushUi();
            check(pinActions == 2 && trayActions == 0 && selected == a.Key && pins.SetEquals(new[] { b.Key }) && Glyph(pinB).Fill is not null,
                "Accessible pin activation changes only the floating selection");
            Button trayB = Elements<Button>(window).Single(button => button.Content is Donut donut && donut.Item?.Key == b.Key);
            trayB.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(trayActions == 1 && pinActions == 2 && selected == b.Key && pins.SetEquals(new[] { b.Key }),
                "Selecting a tray quota leaves the floating pins intact");
            pinB.Focus();
            check(ReferenceEquals(FocusManager.GetFocusedElement(window), pinB), "The pin receives logical keyboard focus");
            var refreshedB = new QuotaItem { Key = b.Key, AccountId = b.AccountId, Provider = b.Provider, Account = b.Account,
                Code = b.Code, Window = b.Window, Status = "live", Remaining = 45 };
            rows = new[] { a, refreshedB };
            window.SetData(rows, selected, pins);
            check(ReferenceEquals(Pin(b.Account), pinB) && ReferenceEquals(FocusManager.GetFocusedElement(window), pinB),
                "A quota refresh preserves the focused pin control");
            pinB.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(ReferenceEquals(lastPinnedItem, refreshedB) && pins.Count == 0 && selected == b.Key,
                "Pin callbacks use the refreshed quota object without changing the tray selection");
            rows = new[] { refreshedB, a };
            window.SetData(rows, selected, pins);
            FlushUi();
            check(ReferenceEquals(FocusManager.GetFocusedElement(window), Pin(b.Account)), "Account reordering restores focus to the same stable pin");
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Elements<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) yield return match;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject element)
                foreach (T value in Elements<T>(element)) yield return value;
    }

    private static IEnumerable<string> Texts(DependencyObject node)
    {
        if (node is TextBlock text) yield return text.Text;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject element)
                foreach (string value in Texts(element)) yield return value;
    }
}
