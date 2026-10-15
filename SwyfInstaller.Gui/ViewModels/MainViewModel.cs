using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwyfInstaller.Gui.Services;

namespace SwyfInstaller.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SelfUpdater _updater = new();
    private CancellationTokenSource _runCts;

    [ObservableProperty] private string _gameDir = "";
    [ObservableProperty] private string _gameFolderStatus = "Step 1: pick your game folder to begin.";
    [ObservableProperty] private bool _isGameFolderValid;
    [ObservableProperty] private string _installedTag = "";
    [ObservableProperty] private string _statusMessage = "Ready. Pick Install to download the latest mod.";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateButtonText = "Update app";
    private SelfUpdateInfo _pendingUpdate;

    public string InstalledLabel => InstalledTag == "" ? "Mod status unknown — install to find out" : "Installed mod: " + InstalledTag;
    public string AppVersion => "v" + SelfUpdater.CurrentVersion;

    partial void OnInstalledTagChanged(string value) => OnPropertyChanged(nameof(InstalledLabel));

    partial void OnGameDirChanged(string value) => ValidateGameDir();

    public async Task InitializeAsync()
    {
        LoadSavedState();
        ValidateGameDir();
        if (!IsGameFolderValid)
            await AutoDetectAsync(silent: true);
        await CheckForUpdatesAsync(manual: false);
    }

    private static string WorkDir() => Path.Combine(Path.GetTempPath(), "SwyfInstaller");

    private SwyfInstaller.Store.AppConfig LoadBackendConfig()
    {
        var cfg = SwyfInstaller.Store.LoadConfig();
        if (GameDir.Trim() != "")
        {
            if (!string.Equals(cfg.GameDir, GameDir.Trim(), StringComparison.OrdinalIgnoreCase))
                cfg.InstalledTag = "";
            cfg.GameDir = GameDir.Trim();
        }
        return cfg;
    }

    private void LoadSavedState()
    {
        var cfg = SwyfInstaller.Store.LoadConfig();
        GameDir = cfg.GameDir;
        InstalledTag = cfg.InstalledTag;
    }

    private void ValidateGameDir()
    {
        string dir = GameDir?.Trim() ?? "";
        if (dir == "")
        {
            GameFolderStatus = "Step 1: pick your game folder to begin (Browse or Detect).";
            IsGameFolderValid = false;
            return;
        }
        try
        {
            if (SwyfInstaller.GameDir.IsGameDir(dir))
            {
                GameFolderStatus = "Found Scam With Your Friends.exe — ready to install.";
                IsGameFolderValid = true;
            }
            else
            {
                GameFolderStatus = "That folder does not contain Scam With Your Friends.exe. Pick the folder Steam opens via Properties > Installed Files > Browse.";
                IsGameFolderValid = false;
            }
        }
        catch
        {
            GameFolderStatus = "That folder path looks invalid. Try Browse again.";
            IsGameFolderValid = false;
        }
    }

    private bool RequireGameFolder(string action)
    {
        ValidateGameDir();
        if (IsGameFolderValid) return true;
        MessageBox.Show(
            "Pick a valid game folder first (the one containing \"Scam With Your Friends.exe\").\n\nTip: in Steam, right-click the game > Properties > Installed Files > Browse, then copy that path here or use Detect.",
            "Game folder needed for " + action,
            MessageBoxButton.OK, MessageBoxImage.Information);
        StatusMessage = "Waiting for a valid game folder before " + action.ToLowerInvariant() + ".";
        return false;
    }

    private void AppendLog(string line)
    {
        LogText += line + "\n";
        if (LogText.Length > 40000)
            LogText = LogText.Substring(LogText.Length - 40000);
    }

    [RelayCommand]
    private void BrowseGameDir()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the game folder (contains Scam With Your Friends.exe)"
        };
        if (dlg.ShowDialog() != true) return;
        if (!SwyfInstaller.GameDir.IsGameDir(dlg.FolderName))
        {
            MessageBox.Show("That folder does not contain Scam With Your Friends.exe.\n\nIn Steam: right-click the game > Properties > Installed Files > Browse, then select that folder.",
                "Not a game folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            GameDir = dlg.FolderName;
            return;
        }
        GameDir = dlg.FolderName;
        AppendLog("Game folder: " + GameDir);
    }

    [RelayCommand]
    private async Task DetectGameDirAsync()
    {
        if (IsBusy) return;
        await AutoDetectAsync(silent: false);
    }

    private async Task AutoDetectAsync(bool silent)
    {
        if (!silent)
        {
            if (IsBusy) return;
            IsBusy = true;
        }
        if (!silent) StatusMessage = "Looking for your Steam game folder…";
        try
        {
            var candidates = await Task.Run(() => SwyfInstaller.GameDir.FindCandidates());
            if (candidates.Count == 0)
            {
                if (!silent)
                {
                    MessageBox.Show("No game folder detected. In Steam: right-click the game > Properties > Installed Files > Browse, then use the Browse button here instead.", "Not found",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    StatusMessage = "No game folder found automatically — use Browse.";
                }
                else
                {
                    GameFolderStatus = "Couldn't find your game automatically — press Detect or Browse.";
                }
                return;
            }
            GameDir = candidates[0];
            try
            {
                var cfg = LoadBackendConfig();
                InstalledTag = cfg.InstalledTag;
                SwyfInstaller.Store.SaveConfig(cfg);
            }
            catch { }
            if (candidates.Count > 1)
                AppendLog("Multiple installs found, using the first one. Others:\n- " + string.Join("\n- ", candidates.Skip(1)));
            AppendLog("Detected: " + GameDir);
            GameFolderStatus = "Found your game automatically — ready to install.";
            if (!silent) StatusMessage = "Ready";
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox.Show(ex.Message, "Detect failed", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusMessage = "Detect failed — try Browse instead.";
            }
        }
        finally { if (!silent) IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsBusy) return;
        if (!RequireGameFolder("Install")) return;
        if (MessageBox.Show("Download the latest mod and set it up in your game folder?\n\nYour provider settings and backups are kept if you reinstall later.",
                "Install latest mod",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunOpAsync((log, bar, ct) =>
            SwyfInstaller.Ops.InstallFlowAsync(LoadBackendConfig(), WorkDir(), autoYes: true, applyPatch: true, visibleWindow: true, log, bar, ct),
            "Installing… downloading the latest mod, then applying it to your game.");
    }

    [RelayCommand]
    private async Task UpdateModAsync()
    {
        if (IsBusy) return;
        if (!RequireGameFolder("Update")) return;
        if (MessageBox.Show("Update to the latest mod?\n\nOld mod files are removed first. Your settings, sign-in session and backups are always kept.",
                "Update mod",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunOpAsync((log, bar, ct) =>
            SwyfInstaller.Ops.UpdateFlowAsync(LoadBackendConfig(), WorkDir(), autoYes: true, applyPatch: true, force: false, visibleWindow: true, log, bar, ct),
            "Updating… removing old files, downloading the latest mod.");
    }

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (IsBusy) return;
        if (!RequireGameFolder("Verify")) return;
        var cfg = LoadBackendConfig();
        await RunOpAsync((log, _, _) =>
            Task.FromResult(SwyfInstaller.Ops.VerifyFlow(cfg, log)),
            "Verifying… checking your installed files for changes.");
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (IsBusy) return;
        if (!RequireGameFolder("Uninstall")) return;
        if (MessageBox.Show("Remove the mod from your game?\n\nYour settings and backups are kept, so reinstalling later is easy.",
                "Uninstall mod",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var cfg = LoadBackendConfig();
        await RunOpAsync((log, _, _) =>
            Task.FromResult(SwyfInstaller.Ops.UninstallFlow(cfg, autoYes: true, full: false, visibleWindow: true, log)),
            "Uninstalling… restoring the original game files.");
    }

    [RelayCommand]
    private void CopyLog()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LogText))
            {
                StatusMessage = "Nothing in the log to copy yet.";
                return;
            }
            Clipboard.SetText(LogText);
            StatusMessage = "Details log copied — paste it when asking for help.";
            AppendLog("Log copied to clipboard.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not copy the log: " + ex.Message, "Copy failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void CopyRecommended()
    {
        const string setup =
            "Base URL: https://openrouter.ai/api/v1\n" +
            "Model ID: meta-llama/llama-3.3-70b-instruct\n" +
            "API key: create one at https://openrouter.ai/workspaces/default/keys (shown only once — copy it right away)";
        try
        {
            Clipboard.SetText(setup);
            StatusMessage = "Recommended setup copied — paste it into the F8 panel in-game.";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not copy: " + ex.Message, "Copy failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void CancelRun()
    {
        try { _runCts?.Cancel(); } catch { }
    }

    private static readonly string[] SummaryPrefixes =
    {
        "OK:", "Installed ", "Updated to ", "Already up to date",
        "Uninstall done", "Full uninstall done.", "Download verified."
    };

    private static string FriendlyStatus(List<string> lines, int code)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string t = lines[i].Trim();
            if (t.Contains("problem(s)")) return t + " Use Update to repair, or ask for help with the details log below.";
            foreach (string p in SummaryPrefixes)
            {
                if (t.StartsWith(p, StringComparison.Ordinal)) return t + " You can close this window and start the game.";
            }
        }
        return code == 0 ? "Done. Start the game and press F8 to set up your AI provider." : "Something failed (exit " + code + ") — check the details log below or press Copy log when asking for help.";
    }

    private async Task RunOpAsync(Func<IProgress<string>, IProgress<double>, CancellationToken, Task<int>> op, string phase)
    {
        if (IsBusy) return;
        IsBusy = true;
        _runCts = new CancellationTokenSource();
        IsProgressIndeterminate = true;
        Progress = 0;
        StatusMessage = phase;
        var lines = new List<string>();
        var log = new Progress<string>(line => { lines.Add(line); AppendLog(line); });
        var bar = new Progress<double>(v =>
        {
            if (v < 0) IsProgressIndeterminate = true;
            else { IsProgressIndeterminate = false; Progress = v / 100.0; }
        });
        try
        {
            int code = await op(log, bar, _runCts.Token);
            StatusMessage = FriendlyStatus(lines, code);
            LoadSavedState();
            ValidateGameDir();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled. Nothing was half-installed — run it again when ready.";
            AppendLog("Cancelled.");
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed: " + ex.Message;
            AppendLog("ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        await CheckForUpdatesAsync(manual: true);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (IsBusy && !manual) return;
        if (manual) StatusMessage = "Checking for app updates…";
        try
        {
            var info = await _updater.CheckForUpdatesAsync();
            if (info == null)
            {
                if (manual)
                {
                    StatusMessage = "This installer app is up to date (v" + SelfUpdater.CurrentVersion + ").";
                    AppendLog("App is up to date (v" + SelfUpdater.CurrentVersion + ").");
                }
                return;
            }
            _pendingUpdate = info;
            UpdateButtonText = "Update app to v" + info.Version;
            UpdateAvailable = true;
            StatusMessage = "Installer app update available: v" + info.Version + " — press the update button above.";
            AppendLog("App update available: v" + info.Version + " — " + info.Name);
            if (manual && !string.IsNullOrWhiteSpace(info.Notes))
                AppendLog(info.Notes.Length > 800 ? info.Notes.Substring(0, 800) + "…" : info.Notes);
        }
        catch (Exception ex)
        {
            if (manual)
            {
                StatusMessage = "Update check failed — you can still install the mod below.";
                AppendLog("Update check failed: " + ex.Message);
                MessageBox.Show("Could not check for updates: " + ex.Message, "Update check",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallUpdateAsync()
    {
        var info = _pendingUpdate;
        if (info == null || IsBusy) return;
        if (MessageBox.Show("Download and install installer app v" + info.Version + " now? The app will restart.",
                "Update installer app",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsBusy = true;
        IsProgressIndeterminate = false;
        Progress = 0;
        try
        {
            AppendLog("Downloading app v" + info.Version + " …");
            var prog = new Progress<double>(f =>
            {
                Progress = f;
                StatusMessage = "Downloading app update… " + (int)(f * 100) + "%";
            });
            string dir = await _updater.DownloadAsync(info, prog, CancellationToken.None);
            AppendLog("Download verified. Restarting to apply…");
            SelfUpdater.InstallAndRestart(dir, info.SetupName);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusMessage = "App update failed: " + ex.Message;
            AppendLog("Update failed: " + ex.Message);
            MessageBox.Show("Update failed: " + ex.Message, "Update",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }
}
