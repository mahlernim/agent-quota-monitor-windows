using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentQuotaMonitor;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        int exit = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                TestState();
                await TestWindow(args.Length == 1 ? args[0] : null);
                Console.WriteLine($"Passed {_checks} native account checks. Provider requests 0. Settings writes 0.");
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        }));
        app.Run();
        return exit;
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        ++_checks;
    }
    private static string Snapshot(params string[] ids) => JsonSerializer.Serialize(new
    {
        enabledProviders = new[] { "codex", "copilot" },
        connections = new { clients = new { codex = true, claude = true, antigravity = true, copilot = true } },
        accounts = ids.Select(id => new
        {
            id, provider = "codex", label = "shared@example.test", status = "stale", source = "Synthetic official client",
            identityStatus = "Verified stable identity", error = "rate_limited", lastSuccess = 1700000000, nextAttempt = 1900000000,
            groups = new[] { new { label = "Premium", plan = "Synthetic plan", models = new[] { "model-a" },
                buckets = new[] { new { label = "Month", amountRemaining = 12.5, entitlement = 100, unit = "credits", available = false } } } }
        })
    });
    private static AccountStatus[] Parse(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return AccountStatus.Parse(document.RootElement);
    }
    private static void TestState()
    {
        var state = new AccountLayoutState();
        state.Observe(Parse(Snapshot("a", "b", "c")));
        state.Begin();
        Check(state.Move("b", -1) && state.Order.SequenceEqual(new[] { "b", "a", "c" }), "Stable identities determine order even with identical email labels");
        Check(!state.Move("b", -1) && !state.Move("missing", 1), "Moves stay within the current draft");
        state.Observe(Parse(Snapshot("c", "b", "a")));
        Check(!state.HasConflict && state.Displayed.Select(account => account.Id).SequenceEqual(new[] { "b", "a", "c" }), "Polling preserves the draft order");
        state.Observe(Parse(Snapshot("a", "b", "c", "d")));
        Check(state.HasConflict && !state.Move("a", 1) && state.Order.Length == 3, "Membership changes preserve and block the draft");
        state.Cancel();
        Check(!state.Editing && state.Displayed.Count == 4, "Cancel returns to the newest account list");
        try { Parse(Snapshot("a", "a")); throw new InvalidOperationException("Duplicate identities were accepted"); }
        catch (JsonException) { ++_checks; }
        AccountStatus details = Parse(Snapshot("a"))[0];
        Check(details.QuotaDetails.Contains("Synthetic plan") && details.QuotaDetails.Contains("model-a") &&
            details.QuotaDetails.Contains(12.5.ToString("0.##", CultureInfo.CurrentCulture) + " / 100 credits remaining") && details.QuotaDetails.Contains("Currently unavailable"), "Detailed quotas retain plan, models, units and availability");
        Check(AccountStatus.Timestamp(double.MaxValue, "Unknown") == "Unknown" && AccountStatus.Timestamp(null, "Never") == "Never", "Invalid or absent timestamps stay unknown");
        JsonObject suspended = JsonNode.Parse(Snapshot("suspended"))!.AsObject();
        suspended["accounts"]![0]!["retryState"] = "suspended";
        suspended["accounts"]![0]!["nextAttempt"] = null;
        Check(Parse(suspended.ToJsonString())[0].Guidance.Contains("Automatic reads are paused"), "Suspended retry state has explicit native guidance");

        AccountStatus Antigravity(string source, string error)
        {
            JsonObject sample = JsonNode.Parse(Snapshot("antigravity"))!.AsObject();
            JsonObject row = sample["accounts"]![0]!.AsObject();
            row["provider"] = "antigravity";
            row["source"] = source;
            row["error"] = error;
            return Parse(sample.ToJsonString())[0];
        }
        const string cliSource = "Official Antigravity CLI /usage (desktop app not required)";
        const string desktopSource = "Official running Antigravity local service";
        foreach (string error in new[] { "sign_in_required", "local_session_unavailable" })
        {
            string cliGuidance = Antigravity(cliSource, error).Guidance;
            Check(cliGuidance.Contains("run agy -p /usage", StringComparison.Ordinal) &&
                cliGuidance.Contains("press Refresh", StringComparison.Ordinal) &&
                !cliGuidance.Contains("desktop app", StringComparison.Ordinal),
                "Stale Antigravity CLI " + error + " points to interactive CLI recovery");
            string desktopGuidance = Antigravity(desktopSource, error).Guidance;
            Check(desktopGuidance.Contains("Antigravity desktop app", StringComparison.Ordinal) &&
                !desktopGuidance.Contains("agy", StringComparison.Ordinal),
                "Antigravity desktop " + error + " stays with desktop recovery");
        }
        Check(Antigravity(cliSource, "antigravity_cli_failed").Guidance.Contains("run agy -p /usage", StringComparison.Ordinal),
            "Failed CLI reads retain the CLI recovery command");
        Check(Antigravity(cliSource, "antigravity_cli_auth_unsupported").Guidance.Contains("custom provider", StringComparison.Ordinal) &&
            Antigravity(cliSource, "antigravity_cli_auth_unsupported").Guidance.Contains("API key", StringComparison.Ordinal),
            "Unsupported Antigravity auth mode points to configuration recovery");
        JsonObject claudeSample = JsonNode.Parse(Snapshot("claude"))!.AsObject();
        claudeSample["accounts"]![0]!["provider"] = "claude";
        claudeSample["accounts"]![0]!["error"] = "sign_in_required";
        string claudeGuidance = Parse(claudeSample.ToJsonString())[0].Guidance;
        Check(claudeGuidance.Contains("claude auth status", StringComparison.Ordinal) &&
            claudeGuidance.Contains("read still fails after renewal", StringComparison.Ordinal),
            "Rejected Claude quota reads distinguish client renewal from full sign-in");
        JsonObject codexSample = JsonNode.Parse(Snapshot("codex"))!.AsObject();
        codexSample["accounts"]![0]!["error"] = "sign_in_required";
        codexSample["accounts"]![0]!["sessionRenewedAt"] = 1789779319;
        AccountStatus codex = Parse(codexSample.ToJsonString())[0];
        Check(codex.Guidance.Contains("Open the Codex app or CLI to renew", StringComparison.Ordinal) &&
            codex.Guidance.Contains("Sign in again only if", StringComparison.Ordinal) && codex.SessionRenewedAt == 1789779319,
            "A rejected Codex session points to renewal before sign-in and keeps its renewal time");
        claudeSample["accounts"]![0]!["error"] = "session_expired";
        claudeSample["accounts"]![0]!["sessionExpiresAt"] = 1790000000;
        AccountStatus expired = Parse(claudeSample.ToJsonString())[0];
        Check(expired.Guidance.Contains("send any message in Claude Code", StringComparison.Ordinal) &&
            expired.Guidance.Contains("resumes by itself", StringComparison.Ordinal) && expired.Guidance.Contains("only if Claude Code reports", StringComparison.Ordinal) &&
            expired.Problem!.Summary.Contains("Send any message in Claude Code") && !expired.Problem.Summary.Contains("Open Claude Code") &&
            expired.SessionExpiresAt == 1790000000,
            "An expired Claude session explains that an idle open Claude Code renews on use and keeps its expiry");
        string missingCli = Antigravity(desktopSource, "antigravity_cli_unavailable").Guidance;
        Check(missingCli.Contains("Install the official CLI from Settings", StringComparison.Ordinal) &&
            missingCli.Contains("Gemini CLI does not report", StringComparison.Ordinal),
            "A missing Antigravity CLI explains the install option and the Gemini CLI difference");
        Check(Antigravity(cliSource, "network_unavailable").Guidance.Contains("when the network returns", StringComparison.Ordinal) &&
            Antigravity(cliSource, "schema_changed").Guidance.Contains("Check for a monitor update", StringComparison.Ordinal),
            "Offline and format-change failures have specific guidance");
        TestCards();
        TestProblems();
    }

    private static void TestCards()
    {
        JsonObject Card(string status, string resetsAt, string error = "")
        {
            JsonObject sample = JsonNode.Parse(Snapshot("card"))!.AsObject();
            JsonObject account = sample["accounts"]![0]!.AsObject();
            account["status"] = status;
            account["error"] = error;
            account["groups"] = new JsonArray(new JsonObject { ["id"] = "g", ["label"] = "Codex", ["buckets"] = new JsonArray(
                new JsonObject { ["id"] = "b", ["label"] = "Five-hour window", ["remaining"] = 40, ["windowSeconds"] = 18000, ["resetsAt"] = resetsAt }) });
            return sample;
        }
        QuotaItem Item(JsonObject sample)
        {
            using JsonDocument document = JsonDocument.Parse(sample.ToJsonString());
            return QuotaItem.Parse(document.RootElement, DateTimeOffset.Parse("2026-09-24T12:00:00Z")).Single();
        }
        QuotaItem stale = Item(Card("stale", "2026-09-24T10:00:00Z", "local_session_unavailable"));
        Check(stale.Remaining is null && stale.Tooltip.Contains("Reset since the last read", StringComparison.Ordinal),
            "A stale value from before its reset is shown as unknown");
        Check(stale.Tooltip.Contains("Local session unavailable", StringComparison.Ordinal), "Card tooltips include account guidance");
        QuotaItem pending = Item(Card("stale", "2026-09-24T14:00:00Z"));
        Check(pending.Remaining == 40, "A stale value before its reset keeps the cached percentage");
        QuotaItem live = Item(Card("live", "2026-09-24T10:00:00Z"));
        Check(live.Remaining == 40 && live.Tooltip.Contains("Reset due", StringComparison.Ordinal),
            "A live value past its reset keeps the provider reading while waiting");
    }

    private static async Task TestWindow(string? previewPath)
    {
        using var handler = new SyntheticBackend { Status = Snapshot("a", "b", "c") };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") };
        var owner = new Window { Left = -32000, Top = -32000, Width = 1, Height = 1, ShowActivated = false, ShowInTaskbar = false };
        int changed = 0, installs = 0;
        bool backendReady = true, confirmInstall = false;
        var clientInstalls = new List<OfficialInstall>();
        owner.Show();
        var window = new AccountsWindow(owner, http, () => ++changed, backendReady: () => backendReady,
            confirmCliInstall: _ => confirmInstall, startCliInstall: () => ++installs,
            confirmInstall: (_, _) => confirmInstall, startInstall: clientInstalls.Add)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Field<DispatcherTimer>(window, "_timer").Stop();
            await Call(window, "PollAsync");
            var signInButtons = Field<Dictionary<string, Button>>(window, "_signInButtons");
            Check((string)signInButtons["antigravity"].Content == "Open desktop app", "Antigravity action identifies the desktop client");
            var body = (StackPanel)((ScrollViewer)((DockPanel)window.Content).Children.OfType<ScrollViewer>().Single()).Content;
            static string Inline(TextBlock block) => string.Concat(block.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));
            Check(body.Children.OfType<TextBlock>().Any(block => Inline(block).Contains("reads quota while the desktop app is closed", StringComparison.Ordinal) &&
                Inline(block).Contains("Open desktop app doesn't sign in the CLI", StringComparison.Ordinal) && block.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any()),
                "Settings keeps a one-line Antigravity hint with a link instead of a long paragraph");
            Check(!body.Children.OfType<Button>().Any(button => button.Content is string text && text.Contains("Save monitored providers")),
                "Providers have no separate Save button");
            AccountLayoutState layout = Field<AccountLayoutState>(window, "_layout");
            Check(layout.Loaded && layout.Displayed.Count == 3, "The actual Settings window reads the synthetic backend");
            Button installCli = Field<Button>(window, "_installCli");
            Check(installCli.Visibility == Visibility.Collapsed, "The CLI install offer stays hidden unless agy is reported missing");
            JsonObject missing = JsonNode.Parse(handler.Status)!.AsObject();
            missing["connections"]!["clients"]!["antigravityCli"] = false;
            handler.Status = missing.ToJsonString();
            await Call(window, "PollAsync");
            Check(installCli.Visibility == Visibility.Visible, "A missing agy shows the CLI install offer");
            int requestsBeforeInstall = handler.Requests;
            Click(installCli);
            Check(installs == 0 && handler.Requests == requestsBeforeInstall, "Declining the confirmation runs nothing");
            confirmInstall = true;
            Click(installCli);
            Check(installs == 1 && handler.Requests == requestsBeforeInstall &&
                Field<TextBlock>(window, "_message").Text.Contains("installer opened in PowerShell", StringComparison.Ordinal),
                "A confirmed install opens only the visible official installer");
            Check(AntigravityCliInstall.Confirmation.Contains(AntigravityCliInstall.Command, StringComparison.Ordinal) &&
                AntigravityCliInstall.Script.Contains(AntigravityCliInstall.Command, StringComparison.Ordinal) &&
                AntigravityCliInstall.Command == "irm https://antigravity.google/cli/install.ps1 | iex" &&
                AntigravityCliInstall.StartInfo().ArgumentList.Contains("-NoExit") && !AntigravityCliInstall.StartInfo().UseShellExecute,
                "The confirmation shows the exact official command that the visible window runs");
            missing["connections"]!["clients"]!["antigravityCli"] = true;
            handler.Status = missing.ToJsonString();
            await Call(window, "PollAsync");
            Check(installCli.Visibility == Visibility.Collapsed, "The offer disappears once agy is installed");
            var installButtons = Field<Dictionary<string, Button>>(window, "_clientInstallButtons");
            Check(installButtons.Values.All(button => button.Visibility == Visibility.Collapsed) && signInButtons["codex"].Visibility == Visibility.Visible,
                "Installed clients keep Sign in and hide install buttons");
            missing["connections"]!["clients"]!["codex"] = false;
            handler.Status = missing.ToJsonString();
            await Call(window, "PollAsync");
            Check(installButtons["codex"].Visibility == Visibility.Visible && signInButtons["codex"].Visibility == Visibility.Collapsed &&
                installButtons["claude"].Visibility == Visibility.Collapsed && (string)installButtons["codex"].Content == "Install Codex",
                "A missing Codex client offers its installer in place of Sign in");
            confirmInstall = false;
            Click(installButtons["codex"]);
            Check(clientInstalls.Count == 0, "Declining a client install runs nothing");
            confirmInstall = true;
            Click(installButtons["codex"]);
            Check(clientInstalls.SequenceEqual(new[] { OfficialInstall.Codex }), "A confirmed client install runs only the matching official installer");
            Check(OfficialInstall.Codex.Command == "irm https://chatgpt.com/codex/install.ps1 | iex" && OfficialInstall.Codex.BypassPolicy &&
                OfficialInstall.Codex.StartInfo().ArgumentList.Contains("Bypass") && OfficialInstall.Codex.Confirmation.Contains(OfficialInstall.Codex.Command) &&
                OfficialInstall.Claude.Command == "irm https://claude.ai/install.ps1 | iex" && !OfficialInstall.Claude.StartInfo().ArgumentList.Contains("Bypass") &&
                OfficialInstall.Claude.Script.Contains(OfficialInstall.Claude.Command) && OfficialInstall.Claude.StartInfo().ArgumentList.Contains("-NoExit"),
                "Client installers show and run only the documented official commands in a visible window");
            missing["connections"]!["clients"]!["codex"] = true;
            handler.Status = missing.ToJsonString();
            await Call(window, "PollAsync");
            Check(installButtons["codex"].Visibility == Visibility.Collapsed && signInButtons["codex"].Visibility == Visibility.Visible,
                "Sign in returns once the client is found");
            await TestCopilot(window, handler, missing, clientInstalls);
            confirmInstall = false;
            await TestProviders(window, handler);
            await TestNames(window, handler);
            Check(!Move(window, "b", -1), "Rows cannot move outside Edit order");
            Click(Field<Button>(window, "_editOrder"));
            Check(Move(window, "b", -1) && layout.Order.SequenceEqual(new[] { "b", "a", "c" }), "Moving a row acts on the stable account ID");
            Check(Tagged<Border>(Field<StackPanel>(window, "_accounts"), "b") is Border movedRow && movedRow.IsKeyboardFocusWithin,
                "Focus stays on the moved row");
            Check(Move(window, "c", -2) && layout.Order.SequenceEqual(new[] { "c", "b", "a" }) && Move(window, "c", 2) && layout.Order.SequenceEqual(new[] { "b", "a", "c" }),
                "A drop moves a row by several places inside the draft");
            handler.Status = Snapshot("c", "a", "b");
            await Call(window, "PollAsync");
            Check(layout.Order.SequenceEqual(new[] { "b", "a", "c" }), "The actual status poll preserves unsaved moves");
            int changedBeforeSave = changed;
            await Call(window, "SaveOrderAsync");
            using (JsonDocument request = JsonDocument.Parse(handler.LastLayout!))
            {
                Check(request.RootElement.GetProperty("order").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "b", "a", "c" }) &&
                    request.RootElement.GetProperty("removed").GetArrayLength() == 0, "Save sends exact IDs without hiding accounts");
            }
            Check(!layout.Editing && changed == changedBeforeSave + 1 && Field<TextBlock>(window, "_message").Text == "Account order saved.", "Save applies and preserves its confirmation after polling");
            handler.GetStatus = HttpStatusCode.ServiceUnavailable;
            await Call(window, "PollAsync");
            Check(Field<TextBlock>(window, "_health").Text.Length > 0 && Field<TextBlock>(window, "_message").Text == "Account order saved.", "Poll failures have separate feedback");
            handler.GetStatus = HttpStatusCode.OK;
            handler.PostStatus = HttpStatusCode.BadRequest;
            await Call(window, "RemoveAsync", "b");
            await Call(window, "PollAsync");
            Check(Field<TextBlock>(window, "_health").Text.Length == 0 && Field<TextBlock>(window, "_message").Text == "Synthetic operation failure.", "A healthy poll clears only its own failure and preserves the action error");
            handler.PostStatus = HttpStatusCode.OK;

            Click(Field<Button>(window, "_editOrder"));
            Move(window, "a", -1);
            string[] draft = layout.Order;
            handler.LayoutStatus = HttpStatusCode.Conflict;
            await Call(window, "SaveOrderAsync");
            Check(layout.Editing && layout.HasConflict && layout.Order.SequenceEqual(draft) && !Field<Button>(window, "_saveOrder").IsEnabled, "A server conflict preserves the draft and blocks resubmission");
            Click(Field<Button>(window, "_cancelOrder"));
            Check(!layout.Editing && Field<Button>(window, "_editOrder").IsEnabled, "Cancel releases a server conflict");
            handler.LayoutStatus = HttpStatusCode.OK;

            Click(Field<Button>(window, "_editOrder"));
            Move(window, "a", -1);
            string[] saved = layout.Order;
            var pending = new TaskCompletionSource<HttpResponseMessage>();
            string oldSnapshot = handler.Status;
            handler.NextGet = pending.Task;
            Task poll = Call(window, "PollAsync");
            await Call(window, "SaveOrderAsync");
            pending.SetResult(SyntheticBackend.Json(oldSnapshot));
            await poll;
            Check(!layout.Editing && layout.Displayed.Select(account => account.Id).SequenceEqual(saved), "A status request started before Save cannot overwrite the saved order");

            Click(Field<Button>(window, "_editOrder"));
            string[] prior = layout.Order;
            handler.Status = Snapshot("a", "b", "c", "new-account");
            await Call(window, "PollAsync");
            Check(layout.HasConflict && layout.Order.SequenceEqual(prior) && !Field<Button>(window, "_saveOrder").IsEnabled, "Discovery during editing blocks stale layout writes");
            Click(Field<Button>(window, "_cancelOrder"));
            Check(layout.Displayed.Count == 4, "Cancel displays newly discovered accounts");
            string firstId = layout.Displayed[0].Id;
            Button toggle = Tagged<Button>(Field<StackPanel>(window, "_accounts"), "details:" + firstId)!;
            Check(Tagged<TextBlock>(Field<StackPanel>(window, "_accounts"), "detail-text:" + firstId) is null, "Details start collapsed");
            Click(toggle);
            string details = Tagged<TextBlock>(Field<StackPanel>(window, "_accounts"), "detail-text:" + firstId)!.Text;
            Check(details.Contains("Last read") && details.Contains("Next read") && details.Contains("Synthetic official client") && details.Contains("Synthetic plan") &&
                !details.Contains("Verified stable identity") && !details.Contains("Account ID"), "Details show useful fields and leave internal identity values to Copy details");
            string copied = layout.Displayed[0].Diagnostics();
            Check(copied.Contains("Verified stable identity") && copied.Contains("Account ID · " + firstId) && copied.Contains("Error · rate_limited") &&
                !copied.Contains("shared@example.test") && !copied.Contains("test-only"), "Copy details uses an explicit field list without the account label");
            await Call(window, "StartConnectionAsync", "codex");
            Check(handler.LastPath == "/api/connections/start" && JsonNode.Parse(handler.LastBody!)!["provider"]!.GetValue<string>() == "codex", "Official sign-in retains the selected provider");
            await Call(window, "StartConnectionAsync", "antigravity");
            Check(handler.LastPath == "/api/connections/start" && JsonNode.Parse(handler.LastBody!)!["provider"]!.GetValue<string>() == "antigravity" &&
                Field<TextBlock>(window, "_message").Text.Contains("Opening Antigravity desktop app", StringComparison.Ordinal),
                "Antigravity action requests the desktop client and does not claim CLI sign-in");
            await Call(window, "RemoveAsync", "b");
            Check(handler.LastPath == "/api/accounts/remove" && JsonNode.Parse(handler.LastBody!)!["accountId"]!.GetValue<string>() == "b", "Account removal retains its stable identity");
            await Call(window, "RestoreAsync");
            Check(handler.LastPath == "/api/accounts/restore" && Field<TextBlock>(window, "_message").Text == "Hidden accounts restored.", "Hidden-account restore still works");
            handler.Status = Snapshot("a", "a");
            await Call(window, "PollAsync");
            Check(layout.Displayed.Count == 4 && Field<TextBlock>(window, "_health").Text.Length > 0 && Field<TextBlock>(window, "_message").Text == "Hidden accounts restored.", "Invalid account identities retain the last good account list and operation feedback");
            Click(Field<Button>(window, "_editOrder"));
            backendReady = false;
            int requestsBeforeUnavailable = handler.Requests;
            await Call(window, "PollAsync");
            await Call(window, "RemoveAsync", "b");
            await Call(window, "SaveOrderAsync");
            Check(handler.Requests == requestsBeforeUnavailable && layout.Editing && Field<TextBlock>(window, "_health").Text.Contains("update settings are still available"),
                "Unavailable backend blocks account reads and writes while preserving the Settings window and draft");
            backendReady = true;
            Click(Field<Button>(window, "_cancelOrder"));
            Check(body.Children.OfType<TextBlock>().SelectMany(block => block.Inlines.OfType<System.Windows.Documents.Hyperlink>())
                .Any(link => link.NavigateUri?.AbsoluteUri == "https://github.com/mahlernim/agent-quota-monitor-windows/issues"), "Settings links to the project's issue page");
            if (previewPath is not null)
            {
                JsonObject preview = JsonNode.Parse(Snapshot("sample-codex", "sample-claude", "sample-copilot"))!.AsObject();
                string[] providers = { "codex", "claude", "copilot" };
                string[] labels = { "alex@example.test", "jordan@example.test", "team@example.test" };
                for (int index = 0; index < 3; ++index)
                {
                    JsonObject account = preview["accounts"]![index]!.AsObject();
                    account["provider"] = providers[index];
                    account["label"] = labels[index];
                    account["source"] = "Synthetic preview data";
                    account["status"] = "live";
                    account["error"] = "";
                    account["lastSuccess"] = 1790000000;
                    account["nextAttempt"] = 1790000300;
                    if (index != 2) account["groups"] = new JsonArray();
                }
                handler.Status = preview.ToJsonString();
                await Call(window, "PollAsync");
                Field<TextBlock>(window, "_message").Text = "Synthetic preview. No provider account is connected.";
                Click(Tagged<Button>(Field<StackPanel>(window, "_accounts"), "details:sample-copilot")!);
                window.Height = 920;
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string path = Path.GetFullPath(previewPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = File.Create(path);
                encoder.Save(output);
                Console.WriteLine("Synthetic Settings preview saved to " + path);
            }
            Click(Field<Button>(window, "_editOrder"));
            PressEscape(window);
            Check(!layout.Editing && window.IsVisible, "Esc discards an unsaved order draft before it closes Settings");
            PressEscape(window);
            Check(closed, "Esc closes Settings when nothing is being edited");
        }
        finally { if (!closed) window.Close(); owner.Close(); }
    }

    private static async Task TestCopilot(AccountsWindow window, SyntheticBackend handler, JsonObject status, List<OfficialInstall> installs)
    {
        Button setup = Field<Button>(window, "_copilotSetup"), connect = Field<Button>(window, "_copilotConnect");
        Check(setup.Visibility == Visibility.Collapsed && connect.Visibility == Visibility.Collapsed, "Copilot setup controls stay hidden when not needed");
        status["connections"]!["clients"]!["copilotReady"] = false;
        handler.Status = status.ToJsonString();
        await Call(window, "PollAsync");
        Check(setup.Visibility == Visibility.Visible && connect.Visibility == Visibility.Collapsed, "Missing Copilot components offer Set up Copilot");
        Click(setup);
        Check(installs.Last() == OfficialInstall.Copilot, "A confirmed setup runs only the Copilot setup script");
        string script = OfficialInstall.Copilot.Script;
        // Saved for an optional PowerShell parser check. Nothing here runs the scripts.
        string scripts = Path.Combine(Path.GetTempPath(), "aqm-installer-scripts");
        Directory.CreateDirectory(scripts);
        foreach ((string name, OfficialInstall installer) in new[] { ("antigravity", OfficialInstall.Antigravity), ("claude", OfficialInstall.Claude),
                     ("codex", OfficialInstall.Codex), ("copilot", OfficialInstall.Copilot) })
            File.WriteAllText(Path.Combine(scripts, name + ".ps1"), installer.Script);
        Check(script.Contains("--id $id --exact --source winget") && !script.Contains("--accept") && script.Contains("'GitHub.cli'") &&
            script.Contains("'OpenJS.NodeJS.LTS'") && script.Contains("'GitHub.Copilot'") && script.Contains(OfficialInstall.CopilotSdk) &&
            script.Contains("gh auth status") && !script.Contains("python") && OfficialInstall.Copilot.Confirmation.Contains("administrator approval"),
            "Copilot setup installs only missing official tools, leaves license prompts to winget, and needs no Python");

        JsonObject unlinked = JsonNode.Parse(status.ToJsonString())!.AsObject();
        unlinked["connections"]!["clients"]!["copilotReady"] = true;
        JsonArray accounts = unlinked["accounts"]!.AsArray();
        JsonObject pending = accounts[0]!.DeepClone().AsObject();
        pending["id"] = "copilot-pending"; pending["provider"] = "copilot"; pending["error"] = "copilot_not_connected"; pending["status"] = "pending";
        accounts.Add(pending);
        handler.Status = unlinked.ToJsonString();
        await Call(window, "PollAsync");
        Check(setup.Visibility == Visibility.Collapsed && connect.Visibility == Visibility.Visible, "A completed setup without a linked account offers Connect");
        await Call(window, "ConnectCopilotAsync");
        JsonNode body = JsonNode.Parse(handler.LastBody!)!;
        Check(handler.LastPath == "/api/connections/start" && body["provider"]!.GetValue<string>() == "copilot" && body["verify"]!.GetValue<bool>() &&
            body["accountId"]!.GetValue<string>() == "copilot-pending", "Connect links the existing GitHub CLI account without a new sign-in");
        status["connections"]!["clients"]!["copilotReady"] = true;
        handler.Status = status.ToJsonString();
        await Call(window, "PollAsync");
    }

    private static async Task TestProviders(AccountsWindow window, SyntheticBackend handler)
    {
        var checks = Field<Dictionary<string, CheckBox>>(window, "_providerChecks");
        Check(checks["codex"].IsChecked == true && checks["claude"].IsChecked == false && checks.Values.All(check => check.IsEnabled),
            "Provider checkboxes load the saved selection");
        checks["claude"].IsChecked = true;
        await Call(window, "SaveProvidersAsync");
        Check(handler.LastPath == "/api/providers" && JsonNode.Parse(handler.LastBody!)!["enabled"]!.AsArray().Select(value => value!.GetValue<string>())
            .SequenceEqual(new[] { "codex", "claude", "copilot" }), "A provider change saves the full selection right away");

        handler.PostStatus = HttpStatusCode.BadRequest;
        checks["claude"].IsChecked = false;
        await Call(window, "SaveProvidersAsync");
        Check(checks["claude"].IsChecked == true && Field<TextBlock>(window, "_message").Text == "Synthetic operation failure.",
            "A failed save restores the last confirmed selection");
        handler.PostStatus = HttpStatusCode.OK;

        // A slow first write must not overwrite later clicks, and a skipped middle write is never sent.
        int before = handler.PostBodies.Count;
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        handler.NextPost = slow.Task;
        checks["antigravity"].IsChecked = true;
        Task first = Call(window, "SaveProvidersAsync");
        checks["copilot"].IsChecked = false;
        Task second = Call(window, "SaveProvidersAsync");
        checks["claude"].IsChecked = false;
        Task third = Call(window, "SaveProvidersAsync");
        slow.SetResult(SyntheticBackend.Json("{\"error\":\"Synthetic slow failure.\"}", HttpStatusCode.BadRequest));
        await Task.WhenAll(first, second, third);
        Check(handler.PostBodies.Count == before + 2 && JsonNode.Parse(handler.PostBodies[^1])!["enabled"]!.AsArray().Select(value => value!.GetValue<string>())
            .SequenceEqual(new[] { "codex", "antigravity" }) && checks["antigravity"].IsChecked == true && checks["copilot"].IsChecked == false &&
            checks["claude"].IsChecked == false, "Only the newest selection is sent after a slow write, and a stale failure changes nothing");
    }

    private static async Task TestNames(AccountsWindow window, SyntheticBackend handler)
    {
        var initials = Field<Dictionary<string, TextBox>>(window, "_initialsFields");
        var names = Field<Dictionary<string, TextBox>>(window, "_nameFields");
        Check(initials["antigravity-gemini"].Text == "AG" && initials["antigravity-claude-gpt"].Text == "AC" && names["antigravity-claude-gpt"].Text == "Claude and GPT",
            "Name fields start with the AG and AC defaults and a truthful combined name");
        int before = handler.PostBodies.Count;
        initials["antigravity-gemini"].Text = "A-G";
        await Call(window, "SaveNamesAsync");
        Check(handler.PostBodies.Count == before && Field<TextBlock>(window, "_message").Text.Contains("letters or digits"), "Invalid initials are rejected without a request");
        initials["antigravity-gemini"].Text = "GEM";
        names["antigravity-gemini"].Text = "Gemini Pro";
        await Call(window, "SaveNamesAsync");
        JsonNode saved = JsonNode.Parse(handler.LastBody!)!["quotaLabels"]!;
        Check(handler.LastPath == "/api/desktop" && saved["antigravity-gemini"]!["initials"]!.GetValue<string>() == "GEM" &&
            QuotaNames.For("antigravity-gemini") == new QuotaName("GEM", "Gemini Pro"), "Valid names are saved as desktop preferences and applied");
        var item = new QuotaItem { Code = "GM", Window = "5h", Provider = "antigravity" };
        Check(item.Label == "Gemini Pro 5h" && item.ShortLabel == "GEM 5h" && item.IdentityColor == "#287bc1",
            "Renamed labels never change the internal code or ring color");
        QuotaNames.Set(QuotaNames.Defaults);
        foreach (string type in QuotaNames.Types) { initials[type].Text = QuotaNames.Defaults[type].Initials; names[type].Text = QuotaNames.Defaults[type].Name; }
    }

    private static void TestProblems()
    {
        AccountProblem? Problem(string provider, string error, string source = "Synthetic official client")
        {
            JsonObject sample = JsonNode.Parse(Snapshot("problem"))!.AsObject();
            sample["accounts"]![0]!["provider"] = provider;
            sample["accounts"]![0]!["error"] = error;
            sample["accounts"]![0]!["source"] = source;
            return Parse(sample.ToJsonString())[0].Problem;
        }
        const string cli = "Official Antigravity CLI /usage (desktop app not required)";
        const string desktop = "Official running Antigravity local service";
        foreach (string error in new[] { "sign_in_required", "local_session_unavailable", "antigravity_cli_failed" })
            Check(Problem("antigravity", error, cli)?.Action == "copy-agy", "Antigravity CLI " + error + " points to agy, never the desktop app or Sign in");
        Check(Problem("antigravity", "local_session_unavailable", desktop)?.Action == "open-desktop" &&
            Problem("antigravity", "sign_in_required", desktop)?.Action == "open-desktop", "Antigravity desktop problems open the desktop app");
        Check(Problem("antigravity", "antigravity_cli_unavailable", desktop)?.Action == "install-cli", "A missing CLI offers the confirmed installer");
        Check(Problem("codex", "sign_in_required")?.Action == "sign-in" && Problem("copilot", "local_session_unavailable")?.Action == "sign-in",
            "Official-client sign-in problems offer Sign in");
        Check(Problem("claude", "session_expired")?.Action is null, "An expired Claude session is explained without launching a client");
        Check(Problem("codex", "client_not_installed")?.Action == "install-codex" && Problem("claude", "client_not_installed")?.Action == "install-claude",
            "A missing client offers its official installer instead of Sign in");
        Check(Problem("copilot", "copilot_setup_required")?.Action == "setup-copilot" && Problem("copilot", "copilot_not_connected")?.Action == "connect-copilot" &&
            !Problem("copilot", "copilot_setup_required")!.Summary.Contains("Setup-Copilot.ps1"),
            "Copilot banners separate missing setup from an unlinked account and never point to the source script");
        Check(Problem("codex", "network_unavailable")?.Action == "retry" && Problem("claude", "connection_or_response_error")?.Action == "retry" &&
            Problem("codex", "schema_changed")?.Action == "check-updates" && Problem("codex", "rate_limited")?.Action is null,
            "Network, format, and cooldown problems choose matching actions");
        Check(Problem("codex", "") is null, "A healthy account has no banner");
        Check(QuotaNames.Defaults["antigravity-gemini"] == new QuotaName("AG", "Gemini") && QuotaNames.Defaults["antigravity-claude-gpt"] == new QuotaName("AC", "Claude and GPT") &&
            QuotaNames.Defaults.Values.All(name => QuotaNames.ValidInitials(name.Initials) && QuotaNames.ValidName(name.Name)),
            "Defaults use AG and AC and fit the name limits");
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static Task Call(object target, string name, params object[] args) => (Task)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void PressEscape(Window window) => window.RaiseEvent(
        new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
    // Drag and drop and Alt+arrow keys both call MoveRow on the unsaved draft.
    private static bool Move(AccountsWindow window, string id, int offset) =>
        (bool)window.GetType().GetMethod("MoveRow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { id, offset })!;
    private static T? Tagged<T>(DependencyObject node, string tag) where T : FrameworkElement
    {
        if (node is T match && Equals(match.Tag, tag)) return match;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject next && Tagged<T>(next, tag) is T found) return found;
        return null;
    }
}

internal sealed class SyntheticBackend : HttpMessageHandler
{
    internal int Requests { get; private set; }
    internal string Status { get; set; } = "{}";
    internal HttpStatusCode GetStatus { get; set; } = HttpStatusCode.OK;
    internal HttpStatusCode PostStatus { get; set; } = HttpStatusCode.OK;
    internal HttpStatusCode LayoutStatus { get; set; } = HttpStatusCode.OK;
    internal string? LastLayout { get; private set; }
    internal string? LastPath { get; private set; }
    internal string? LastBody { get; private set; }
    internal Task<HttpResponseMessage>? NextGet { get; set; }
    internal Task<HttpResponseMessage>? NextPost { get; set; }
    internal List<string> PostBodies { get; } = [];
    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ++Requests;
        if (request.Method == HttpMethod.Get)
        {
            if (NextGet is not null) { Task<HttpResponseMessage> pending = NextGet; NextGet = null; return await pending; }
            return Json(Status, GetStatus);
        }
        LastPath = request.RequestUri!.AbsolutePath;
        LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        PostBodies.Add(LastBody);
        if (NextPost is not null) { Task<HttpResponseMessage> pending = NextPost; NextPost = null; return await pending; }
        if (LastPath == "/api/accounts/layout")
        {
            LastLayout = LastBody;
            if (LayoutStatus == HttpStatusCode.OK)
            {
                JsonObject snapshot = JsonNode.Parse(Status)!.AsObject();
                var accounts = snapshot["accounts"]!.AsArray().ToDictionary(account => account!["id"]!.GetValue<string>(), account => account!.DeepClone());
                string[] order = JsonNode.Parse(LastLayout)!["order"]!.AsArray().Select(id => id!.GetValue<string>()).ToArray();
                snapshot["accounts"] = new JsonArray(order.Select(id => accounts[id]).ToArray());
                Status = snapshot.ToJsonString();
            }
            return Json("{\"error\":\"Account list changed. Cancel editing and try again.\"}", LayoutStatus);
        }
        return Json("{\"error\":\"Synthetic operation failure.\"}", PostStatus);
    }
}
