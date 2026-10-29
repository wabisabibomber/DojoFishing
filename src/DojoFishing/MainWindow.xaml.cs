using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DojoFishing.Audio;
using DojoFishing.Automation;
using DojoFishing.Detection;
using DojoFishing.Processes;

namespace DojoFishing;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly SoundTemplate template = SoundTemplate.LoadEmbedded();
    private AudioSession? session;
    private CancellationTokenSource? starting;
    private ProcessChoice? activeProcess;
    private bool stopping, closing, allowClose;
    private Task stopTask = Task.CompletedTask;
    private HwndSource? source;
    private bool hotkeyRegistered;
    private const int HotkeyId = 0xDF10;
    private double lastPlotted = -1;
    private DateTime lastProcessCheck;
    private bool settingsReady, savingSettings;
    private Settings? savedSettings;
    private string? lastSettingsError;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowMessage);
        };
        Loaded += (_, _) =>
        {
            bool loaded = true;
            try { ApplySettings(Settings.LoadCurrent()); }
            catch (Exception ex) { loaded = false; AddLog("設定読み込み失敗: " + ex.Message); ApplySettings(new Settings()); }
            settingsReady = true;
            if (loaded) CommitSettings();
            RefreshChoices(selectSingleMatch: true);
        };
        timer.Tick += Tick;
        Closing += OnClosing;
    }

    private void AddLog(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AddLog(message)); return; }
        EventLog.Items.Add($"{DateTime.Now:HH:mm:ss.fff}  {message}");
        while (EventLog.Items.Count > 1000) EventLog.Items.RemoveAt(0);
        EventLog.ScrollIntoView(EventLog.Items[EventLog.Items.Count - 1]);
    }

    private void Error(Exception ex)
    {
        AddLog("エラー: " + ex.Message);
        MessageBox.Show(this, ex.Message, "DojoFishing", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private Settings ReadSettings(bool commitThreshold = true)
    {
        if (commitThreshold) ThresholdText.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (Validation.GetHasError(ThresholdText)) throw new FormatException("検出閾値を数値で入力してください。");
        var value = new Settings
        {
            ProcessFilter = ProcessFilter.Text,
            Threshold = Threshold.Value,
            IntervalMs = int.Parse(Interval.Text, CultureInfo.CurrentCulture),
            Hotkey = Hotkey.Text.Trim()
        };
        value.Validate();
        NativeInput.ParseHotkey(value.Hotkey);
        return value;
    }

    private void ApplySettings(Settings value)
    {
        ProcessFilter.Text = value.ProcessFilter;
        Processes.SelectedIndex = -1;
        Threshold.Value = value.Threshold;
        Interval.Text = value.IntervalMs.ToString(CultureInfo.CurrentCulture);
        Hotkey.Text = value.Hotkey;
    }

    private void RefreshChoices(bool selectSingleMatch = false)
    {
        var selected = Processes.SelectedItem as ProcessChoice;
        var choices = ProcessChoice.Find(ProcessFilter.Text);
        Processes.ItemsSource = choices;
        Processes.SelectedItem = choices.FirstOrDefault(x => x.Id == selected?.Id && x.StartTime == selected.StartTime)
            ?? (selectSingleMatch && choices.Count == 1 ? choices[0] : null);
    }

    private void ProcessDropDownOpened(object? sender, EventArgs e)
    {
        try { RefreshChoices(); }
        catch (Exception ex) { AddLog("プロセス一覧の取得失敗: " + ex.Message); }
    }

    private void CommitSettings(bool commitThreshold = true)
    {
        if (!settingsReady || savingSettings || session != null || starting != null || stopping) return;
        savingSettings = true;
        try
        {
            var value = ReadSettings(commitThreshold);
            if (value != savedSettings) { value.Save(Settings.DefaultPath); savedSettings = value; }
            Plot.Threshold = value.Threshold;
            Plot.InvalidateVisual();
            lastSettingsError = null;
        }
        catch (Exception ex)
        {
            if (lastSettingsError != ex.Message) AddLog("設定を反映できません: " + ex.Message);
            lastSettingsError = ex.Message;
        }
        finally { savingSettings = false; }
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => CommitSettings();
    private void ThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => CommitSettings(commitThreshold: false);
    private void SettingCommitted(object sender, KeyboardFocusChangedEventArgs e) => CommitSettings();
    private void SettingKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitSettings(); e.Handled = true; }
    }

    private bool RegisterStopKey(string text)
    {
        if (source == null) return false;
        if (hotkeyRegistered) NativeInput.UnregisterHotKey(source.Handle, HotkeyId);
        var key = NativeInput.ParseHotkey(text);
        hotkeyRegistered = NativeInput.RegisterHotKey(source.Handle, HotkeyId, key.Modifiers, key.Key);
        return hotkeyRegistered;
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            AddLog("緊急停止ホットキー");
            _ = StopAsync();
        }
        return IntPtr.Zero;
    }

    private async void ToggleMonitoring(object sender, RoutedEventArgs e)
    {
        if (starting != null || session != null || stopping) await StopAsync();
        else await StartLiveAsync();
    }

    private async Task StartLiveAsync()
    {
        if (session != null || starting != null || stopping || closing) return;
        bool success = false;
        try
        {
            var value = ReadSettings();
            var target = Processes.SelectedItem as ProcessChoice
                ?? throw new InvalidOperationException("対象プロセスを選択してください。");
            activeProcess = target;
            if (!target.IsAlive()) throw new InvalidOperationException("対象プロセスが終了しました。一覧を開いて選び直してください。");
            if (!RegisterStopKey(value.Hotkey)) throw new InvalidOperationException("緊急停止ホットキーを変更してください。");
            value.Save(Settings.DefaultPath);
            starting = new CancellationTokenSource();
            SetRunning(true);
            StateText.Text = "開始中…";
            ScoreText.Text = "0.000"; CountText.Text = "0"; LevelBar.Value = -100;
            Plot.Clear(); Plot.Threshold = value.Threshold; lastPlotted = -1;
            session = new AudioSession(value, template, target.Id,
                () => target.IsAlive() && NativeInput.IsTargetForeground(target.Id));
            var current = session;
            current.Log += AddLog;
            current.Faulted += message => Dispatcher.BeginInvoke(async () =>
            {
                if (session == current) { AddLog("監視エラー: " + message); await StopAsync(); }
            });
            await current.StartLiveAsync(starting.Token);
            timer.Start();
            success = true;
        }
        catch (OperationCanceledException) { AddLog("開始を中断しました。"); }
        catch (Exception ex) { Error(ex); }
        finally
        {
            bool canceled = starting?.IsCancellationRequested == true;
            starting?.Dispose(); starting = null;
            if (canceled || !success) await StopAsync();
        }
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (session == null || stopping) return;
        var value = session.Latest;
        ScoreText.Text = value.Score.ToString("F3");
        CountText.Text = session.Detections.ToString();
        LevelBar.Value = Math.Clamp(value.Db, -100, 0);
        MetricsText.Text = $"入力 {value.Db:F1}dBFS / 解析 {value.ComputeMs:F2}ms / 遅延 {session.QueueDelayMs:F0}ms";
        StateText.Text = session.Busy ? "シーケンス実行中" : session.Armed ? "監視中" : "解除待ち";
        if (value.AudioSeconds != lastPlotted) { Plot.Add(value.AudioSeconds, value.Score); lastPlotted = value.AudioSeconds; }
        if (DateTime.UtcNow - lastProcessCheck > TimeSpan.FromMilliseconds(500))
        {
            lastProcessCheck = DateTime.UtcNow;
            if (activeProcess?.IsAlive() != true) { AddLog("対象プロセスが終了したため停止します。"); await StopAsync(); }
        }
    }
    private void SetRunning(bool running)
    {
        Configuration.IsEnabled = !running && !closing;
        MonitorButton.Content = running ? "監視停止" : "監視開始";
        MonitorButton.Background = new SolidColorBrush(running ? Color.FromRgb(30, 132, 73) : Color.FromRgb(25, 75, 121));
        MonitorButton.IsEnabled = !closing && !stopping;
    }
    private Task StopAsync()
    {
        starting?.Cancel();
        if (starting != null) { StateText.Text = "開始処理をキャンセル中…"; MonitorButton.IsEnabled = false; return Task.CompletedTask; }
        if (stopping) return stopTask;
        stopping = true;
        SetRunning(true);
        stopTask = StopCoreAsync();
        return stopTask;
    }
    private async Task StopCoreAsync()
    {
        timer.Stop();
        var current = session; session = null;
        try { if (current != null) { await current.DisposeAsync(); AddLog("監視停止。"); } }
        catch (Exception ex) { Error(ex); }
        finally { StateText.Text = "停止"; stopping = false; SetRunning(false); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        SetRunning(session != null);
        starting?.Cancel();
        while (starting != null) await Task.Delay(20);
        await StopAsync();
        CommitSettings();
        if (source != null)
        {
            NativeInput.UnregisterHotKey(source.Handle, HotkeyId);
            source.RemoveHook(WindowMessage);
        }
        allowClose = true;
        // StopAsync may complete synchronously. Defer Close until this Closing event has unwound.
        _ = Dispatcher.BeginInvoke(Close);
    }
}
