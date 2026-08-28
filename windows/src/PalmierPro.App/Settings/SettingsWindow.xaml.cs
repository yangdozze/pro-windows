using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PalmierPro.Agent.Clients;
using PalmierPro.App.Theme;
using PalmierPro.App.Update;
using PalmierPro.Core.Localization;
using PalmierPro.Core.Settings;
using PalmierPro.Core.Telemetry;
using PalmierPro.Media.Caches;
using PalmierPro.Media.Ml;

namespace PalmierPro.App.Settings;

public sealed partial class SettingsWindow : Window
{
    private bool _ready;

    public SettingsWindow()
    {
        InitializeComponent();
        AppAppearanceController.Track(this);
        Title = L10n.String("settings.title");
        try
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(720, 520));
        }
        catch
        {
            // Unpackaged / pre-HWND resize can throw on some hosts.
        }

        ApplyLocalizedNav();
        _ready = true;
        ShowPane("general");
        SelectNavTag("general");
    }

    private void ApplyLocalizedNav()
    {
        foreach (var obj in NavList.Items)
        {
            if (obj is not ListViewItem item || item.Tag is not string tag) continue;
            item.Content = tag switch
            {
                "appearance" => L10n.String("settings.appearance"),
                "privacy" => L10n.String("settings.privacy"),
                "agent" => L10n.String("settings.agent"),
                "storage" => L10n.String("settings.storage"),
                "updates" => L10n.String("settings.updates"),
                _ => L10n.String("settings.general"),
            };
        }
    }

    private void SelectNavTag(string tag)
    {
        foreach (var obj in NavList.Items)
        {
            if (obj is ListViewItem item && item.Tag as string == tag)
            {
                NavList.SelectedItem = item;
                return;
            }
        }
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged can fire during InitializeComponent before PaneHost exists.
        if (!_ready || PaneHost is null) return;
        if (NavList.SelectedItem is ListViewItem item && item.Tag is string tag)
            ShowPane(tag);
    }

    private void ShowPane(string tag)
    {
        if (PaneHost is null) return;
        PaneHost.Children.Clear();
        switch (tag)
        {
            case "appearance": BuildAppearance(); break;
            case "privacy": BuildPrivacy(); break;
            case "agent": BuildAgent(); break;
            case "storage": BuildStorage(); break;
            case "updates": BuildUpdates(); break;
            default: BuildGeneral(); break;
        }
    }

    private void BuildGeneral()
    {
        var settings = SettingsStore.Shared.Current;
        PaneHost.Children.Add(Header(L10n.String("settings.general")));
        PaneHost.Children.Add(Label(L10n.String("settings.language")));
        var lang = new ComboBox { Width = 220 };
        var languages = L10n.SupportedLanguages;
        foreach (var code in languages)
            lang.Items.Add(code);
        var selected = L10n.Normalize(settings.AppLanguage);
        var idx = -1;
        for (var i = 0; i < languages.Count; i++)
        {
            if (languages[i].Equals(selected, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }
        lang.SelectedIndex = idx >= 0 ? idx : 0;
        lang.SelectionChanged += (_, _) =>
        {
            if (lang.SelectedItem is string code)
            {
                SettingsStore.Shared.Update(s => s.AppLanguage = code);
                PaneHost.Children.Add(Hint(L10n.String("settings.restartNote")));
            }
        };
        PaneHost.Children.Add(lang);

        PaneHost.Children.Add(Labeled(
            "Notifications",
            Toggle(settings.NotificationsEnabled,
                v => SettingsStore.Shared.Update(s => s.NotificationsEnabled = v))));
    }

    private void BuildAppearance()
    {
        var settings = SettingsStore.Shared.Current;
        PaneHost.Children.Add(Header(L10n.String("settings.appearance")));
        PaneHost.Children.Add(Label(L10n.String("settings.appearanceMode")));
        var mode = new ComboBox { Width = 220 };
        string[] modes = ["system", "dark", "light"];
        foreach (var m in modes) mode.Items.Add(m);
        var current = (settings.AppAppearance ?? "system").Trim().ToLowerInvariant();
        var modeReady = false;
        mode.SelectionChanged += (_, _) =>
        {
            if (!modeReady) return;
            if (mode.SelectedItem is string m)
                AppAppearanceController.SetAppearance(m);
        };
        mode.SelectedIndex = current switch
        {
            "dark" => 1,
            "light" => 2,
            _ => 0,
        };
        modeReady = true;
        PaneHost.Children.Add(mode);
    }

    private void BuildPrivacy()
    {
        var settings = SettingsStore.Shared.Current;
        PaneHost.Children.Add(Header(L10n.String("settings.privacy")));
        PaneHost.Children.Add(Labeled(
            L10n.String("settings.shareUsage"),
            Toggle(settings.AnalyticsEnabled, v =>
            {
                SettingsStore.Shared.Update(s => s.AnalyticsEnabled = v);
                AppTelemetry.Configure(v, SettingsStore.Shared.Current.TelemetryEnabled);
                ProductionTelemetry.Configure(v, SettingsStore.Shared.Current.TelemetryEnabled);
            })));
        PaneHost.Children.Add(Labeled(
            L10n.String("settings.crashReports"),
            Toggle(settings.TelemetryEnabled, v =>
            {
                SettingsStore.Shared.Update(s => s.TelemetryEnabled = v);
                AppTelemetry.Configure(SettingsStore.Shared.Current.AnalyticsEnabled, v);
                ProductionTelemetry.Configure(SettingsStore.Shared.Current.AnalyticsEnabled, v);
            })));
        PaneHost.Children.Add(Hint("Usage events are allowlisted. Prompts and file paths are never sent."));
    }

    private void BuildAgent()
    {
        var settings = SettingsStore.Shared.Current;
        PaneHost.Children.Add(Header(L10n.String("settings.agent")));
        PaneHost.Children.Add(Labeled(
            L10n.String("settings.mcpEnabled"),
            Toggle(settings.McpEnabled, v => SettingsStore.Shared.Update(s => s.McpEnabled = v))));
        PaneHost.Children.Add(Label("AI provider"));
        var provider = new ComboBox { Width = 320 };
        provider.Items.Add(new ComboBoxItem { Content = "Anthropic", Tag = AgentProvider.Anthropic });
        provider.Items.Add(new ComboBoxItem { Content = "OpenAI", Tag = AgentProvider.OpenAI });

        var model = new ComboBox { Width = 320, IsEditable = true };
        var modelReady = false;

        static IReadOnlyList<(string Label, string Id)> Models(AgentProvider selected) => selected switch
        {
            AgentProvider.OpenAI =>
            [
                ("GPT-5.6 Luna", "gpt-5.6-luna"),
                ("GPT-5.6 Terra", "gpt-5.6-terra"),
                ("GPT-5.6 Sol", "gpt-5.6-sol"),
            ],
            _ =>
            [
                ("Claude Sonnet 5", "claude-sonnet-5"),
                ("Claude Opus 5", "claude-opus-5"),
                ("Claude Fable 5", "claude-fable-5"),
            ],
        };

        void PopulateModels(AgentProvider selected, string? preferred)
        {
            modelReady = false;
            model.Items.Clear();
            var wanted = string.IsNullOrWhiteSpace(preferred) ? selected.DefaultModel() : preferred.Trim();
            var selectedIndex = -1;
            foreach (var entry in Models(selected))
            {
                model.Items.Add(new ComboBoxItem { Content = entry.Label, Tag = entry.Id });
                if (entry.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    selectedIndex = model.Items.Count - 1;
            }
            if (selectedIndex < 0)
            {
                model.Items.Add(new ComboBoxItem { Content = wanted, Tag = wanted });
                selectedIndex = model.Items.Count - 1;
            }
            model.SelectedIndex = selectedIndex;
            modelReady = true;
        }

        var selectedProvider = AgentProviderExtensions.Parse(settings.AgentProvider);
        provider.SelectedIndex = selectedProvider == AgentProvider.OpenAI ? 1 : 0;
        PopulateModels(selectedProvider, settings.AgentModel);

        provider.SelectionChanged += (_, _) =>
        {
            if (provider.SelectedItem is not ComboBoxItem { Tag: AgentProvider selected }) return;
            var id = selected == AgentProvider.OpenAI ? "openai" : "anthropic";
            SettingsStore.Shared.Update(s =>
            {
                s.AgentProvider = id;
                s.AgentModel = selected.DefaultModel();
            });
            PopulateModels(selected, selected.DefaultModel());
        };
        model.SelectionChanged += (_, _) =>
        {
            if (!modelReady || model.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            SettingsStore.Shared.Update(s => s.AgentModel = id);
        };
        model.LostFocus += (_, _) =>
        {
            var value = model.Text.Trim();
            if (!string.IsNullOrEmpty(value))
                SettingsStore.Shared.Update(s => s.AgentModel = value);
        };
        PaneHost.Children.Add(provider);
        PaneHost.Children.Add(Label("Agent model"));
        PaneHost.Children.Add(model);

        AddApiKeyEditor(AgentProvider.Anthropic);
        AddApiKeyEditor(AgentProvider.OpenAI);
        PaneHost.Children.Add(Hint(
            "The Agent uses your selected provider directly and can execute Palmier editing tools. " +
            "Environment variables ANTHROPIC_API_KEY and OPENAI_API_KEY are also supported."));

        PaneHost.Children.Add(Label("Whisper model (on-device STT)"));
        var whisperStatus = new TextBlock
        {
            Opacity = 0.65,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = $"Current: {settings.WhisperModelSize ?? "tiny"}",
        };
        var whisper = new ComboBox { Width = 220 };
        whisper.Items.Add(new ComboBoxItem { Content = "tiny (fast, ~75 MB)", Tag = "tiny" });
        whisper.Items.Add(new ComboBoxItem { Content = "base (better, ~140 MB)", Tag = "base" });
        whisper.Items.Add(new ComboBoxItem { Content = "small (best local, ~460 MB)", Tag = "small" });
        var current = (settings.WhisperModelSize ?? "tiny").Trim().ToLowerInvariant();
        var whisperReady = false;
        whisper.SelectionChanged += async (_, _) =>
        {
            if (!whisperReady) return;
            if (whisper.SelectedItem is not ComboBoxItem { Tag: string size }) return;
            SettingsStore.Shared.Update(s => s.WhisperModelSize = size);
            whisperStatus.Text = $"Downloading ggml-{size}.bin if needed…";
            whisper.IsEnabled = false;
            try
            {
                LocalMlBootstrap.EnsureRegistered();
                var path = await ModelAssetInstaller.EnsureWhisperSizeAsync(size);
                LocalMlBootstrap.RefreshEngines();
                whisperStatus.Text = path is not null
                    ? $"Ready: {Path.GetFileName(path)}"
                    : $"Could not download ggml-{size}.bin — check network. Falling back to any installed model.";
            }
            catch (Exception ex)
            {
                whisperStatus.Text = ex.Message;
            }
            finally
            {
                whisper.IsEnabled = true;
            }
        };
        whisper.SelectedIndex = current switch { "base" => 1, "small" => 2, _ => 0 };
        whisperReady = true;
        PaneHost.Children.Add(whisper);
        PaneHost.Children.Add(whisperStatus);
        PaneHost.Children.Add(Hint(
            "Larger models improve captions and Agent inspect_media / get_transcript quality. " +
            "Models are stored under LocalAppData\\PalmierPro\\models."));

        void AddApiKeyEditor(AgentProvider selected)
        {
            PaneHost.Children.Add(Label($"{selected.DisplayName()} API key"));
            var key = new PasswordBox
            {
                Width = 320,
                PlaceholderText = AgentApiKey.Load(selected) is null
                    ? $"Paste {selected.DisplayName()} API key"
                    : "Configured — paste a new key to replace",
                PasswordRevealMode = PasswordRevealMode.Peek,
            };
            var status = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.7,
                Text = AgentApiKey.Load(selected) is null ? "Not configured" : "Configured",
            };
            var save = new Button { Content = "Save key" };
            var remove = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0) };
            save.Click += (_, _) =>
            {
                var value = key.Password.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    status.Text = "Paste a key first.";
                    return;
                }
                AgentApiKey.Save(selected, value);
                key.Password = "";
                key.PlaceholderText = "Configured — paste a new key to replace";
                status.Text = "Saved.";
            };
            remove.Click += (_, _) =>
            {
                AgentApiKey.Delete(selected);
                key.Password = "";
                key.PlaceholderText = $"Paste {selected.DisplayName()} API key";
                status.Text = Environment.GetEnvironmentVariable(selected.EnvironmentVariable()) is null
                    ? "Removed."
                    : $"Using {selected.EnvironmentVariable()} from the environment.";
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(save);
            buttons.Children.Add(remove);
            PaneHost.Children.Add(key);
            PaneHost.Children.Add(buttons);
            PaneHost.Children.Add(status);
        }
    }

    private void BuildStorage()
    {
        PaneHost.Children.Add(Header(L10n.String("settings.storage")));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var clear = new Button { Content = L10n.String("settings.clearCaches") };
        clear.Click += (_, _) =>
        {
            try
            {
                new DiskCache("BeatAnalysis").Clear();
                new DiskCache("Waveforms").Clear();
                new DiskCache("Thumbnails").Clear();
                var search = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PalmierPro", "search");
                if (Directory.Exists(search)) Directory.Delete(search, true);
                status.Text = "Caches cleared.";
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        };
        PaneHost.Children.Add(clear);
        PaneHost.Children.Add(status);
        PaneHost.Children.Add(Hint(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PalmierPro")));
    }

    private void BuildUpdates()
    {
        PaneHost.Children.Add(Header(L10n.String("settings.updates")));
        var status = new TextBlock
        {
            Text = UpdateService.Shared.UpdateAvailable
                ? $"Update available: {UpdateService.Shared.AvailableVersion}"
                : "You're up to date (or running a unpackaged build).",
            TextWrapping = TextWrapping.Wrap,
        };
        var check = new Button { Content = L10n.String("settings.checkUpdates") };
        check.Click += async (_, _) =>
        {
            status.Text = "Checking…";
            var ver = await UpdateService.Shared.CheckAsync();
            status.Text = ver is not null
                ? $"Update available: {ver}"
                : UpdateService.Shared.LastError ?? "No updates found.";
        };
        var apply = new Button { Content = "Download and restart", Margin = new Thickness(0, 8, 0, 0) };
        apply.Click += async (_, _) =>
        {
            status.Text = "Downloading…";
            var ok = await UpdateService.Shared.DownloadAndApplyAsync();
            if (!ok) status.Text = UpdateService.Shared.LastError ?? "Update failed.";
        };
        PaneHost.Children.Add(check);
        PaneHost.Children.Add(apply);
        PaneHost.Children.Add(status);
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Opacity = 0.75,
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Opacity = 0.55,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
    };

    private static ToggleSwitch Toggle(bool on, Action<bool> set)
    {
        var t = new ToggleSwitch { IsOn = on };
        var wired = false;
        t.Loaded += (_, _) => wired = true;
        t.Toggled += (_, _) =>
        {
            if (!wired) return;
            set(t.IsOn);
        };
        return t;
    }

    private static StackPanel Labeled(string label, FrameworkElement control)
        => new()
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = label },
                control,
            },
        };
}
