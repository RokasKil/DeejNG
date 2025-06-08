﻿// MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using Microsoft.Win32;

using System.Runtime;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using DeejNG.Dialogs;
using DeejNG.Services;
using Microsoft.VisualBasic.Logging;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using DeejNG.Classes;
using DeejNG.Models;
using static System.Windows.Forms.Design.AxImporter;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace DeejNG
{
    public partial class MainWindow : Window
    {

        #region Public Fields

        // You'll also need to make _channelControls accessible to allow the ChannelControl to get its index
        // In MainWindow.xaml.cs, change the private field to:
        public List<ChannelControl> _channelControls = new();

        #endregion Public Fields

        #region Private Fields
        private SessionCollection _cachedSessionsForMeters;
        private DateTime _lastMeterSessionRefresh = DateTime.MinValue;
        private bool _isCalibrating = false;
        private DateTime _calibrationStartTime = DateTime.MinValue;
        private const int CALIBRATION_DELAY_MS = 500; // 500ms calibration period
        private bool _allowVolumeApplication = false;
        private Dictionary<int, float> _initialSliderValues = new();
        private bool _hasReceivedInitialData = false;
        private const int METER_SESSION_CACHE_MS = 1000;
        private bool _hasLoadedInitialSettings = false;
        private bool _serialPortFullyInitialized = false;
        private DateTime _lastUnmappedMeterUpdate = DateTime.MinValue;
        private readonly object _unmappedLock = new object();
        private DispatcherTimer _forceCleanupTimer;
        private int _sessionCacheHitCount = 0;
        private DateTime _lastForcedCleanup = DateTime.MinValue;

        private int _expectedSliderCount = -1; // Track expected number of sliders
        private bool _hasReceivedInitialSerialData = false;
        private DateTime _lastSettingsSave = DateTime.MinValue;
        private readonly object _settingsLock = new object();
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private readonly Dictionary<string, float> _lastInputVolume = new(StringComparer.OrdinalIgnoreCase);
        private AppSettings _appSettings = new();
        private MMDevice _audioDevice;
        private AudioService _audioService;
        private bool _disableSmoothing = false;
        private bool _expectingData = false;
        private bool _hasSyncedMuteStates = false;
        private Dictionary<string, MMDevice> _inputDeviceMap = new();
        private bool _isClosing = false;
        private bool _isConnected = false;
        private bool _isInitializing = true;
        private string _lastConnectedPort = string.Empty;
        private DateTime _lastDeviceRefresh = DateTime.MinValue;
        private DateTime _lastSessionRefresh = DateTime.MinValue;
        private DateTime _lastValidDataTimestamp = DateTime.MinValue;
        private bool _metersEnabled = true;
        private DispatcherTimer _meterTimer;
        private int _noDataCounter = 0;
        private Dictionary<int, string> _processNameCache = new();
        private Dictionary<string, IAudioSessionEventsHandler> _registeredHandlers = new();
        private StringBuilder _serialBuffer = new();
        private bool _serialDisconnected = false;
        private SerialPort _serialPort;
        private DispatcherTimer _serialReconnectTimer;
        private DispatcherTimer _serialWatchdogTimer;
        private DispatcherTimer _sessionCacheTimer;
        private List<(AudioSessionControl session, string sessionId, string instanceId)> _sessionIdCache = new();
      //  private Dictionary<string, AudioSessionControl> _sessionLookup = new();
        private AudioEndpointVolume _systemVolume;
        // Track connection state

        private bool isDarkTheme = false;

        #endregion Private Fields

        #region Public Constructors

        public MainWindow()
        {
            _isInitializing = true;

            InitializeComponent();
            Loaded += MainWindow_Loaded;

            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");

            _audioService = new AudioService();
            BuildInputDeviceCache();

            // Load ports BEFORE loading settings so ComboBox is populated
            LoadAvailablePorts();

            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _systemVolume = _audioDevice.AudioEndpointVolume;
            _systemVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;

            _meterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _meterTimer.Tick += UpdateMeters;
            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            StartSessionCacheUpdater();

            // Initialize serial reconnect timer
            _serialReconnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _serialReconnectTimer.Tick += SerialReconnectTimer_Tick;

            // Initialize the watchdog timer
            _serialWatchdogTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _serialWatchdogTimer.Tick += SerialWatchdogTimer_Tick;
            _serialWatchdogTimer.Start();

            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            CreateNotifyIconContextMenu();
            IconHandler.AddIconToRemovePrograms("DeejNG");
            SetDisplayIcon();

            // Load settings but don't auto-connect to serial port yet
            LoadSettingsWithoutSerialConnection();

            _isInitializing = false;
            if (_appSettings.StartMinimized)
            {
                WindowState = WindowState.Minimized;
                Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }
            _forceCleanupTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(10)
            };
            _forceCleanupTimer.Tick += ForceCleanupTimer_Tick;
            _forceCleanupTimer.Start();
            // IMPROVED: Multiple attempts to connect with better timing
            SetupAutomaticSerialConnection();
        }
        #endregion Public Constructors

        #region Private Properties

        private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeejNG", "settings.json");

        #endregion Private Properties

        #region Public Methods

        public Dictionary<int, List<AudioTarget>> GetAllAssignedTargets()
        {
            var result = new Dictionary<int, List<AudioTarget>>();

            for (int i = 0; i < _channelControls.Count; i++)
            {
                result[i] = new List<AudioTarget>(_channelControls[i].AudioTargets);
            }

            return result;
        }

        public List<string> GetCurrentTargets()
        {
            return _channelControls
                .Select(c => c.TargetExecutable?.Trim().ToLowerInvariant())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();
        }

        #endregion Public Methods

        #region Protected Methods

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;

            // Stop all timers
            _meterTimer?.Stop();
            _sessionCacheTimer?.Stop();

            // Clean up all registered event handlers
            foreach (var target in _registeredHandlers.Keys.ToList())
            {
                try
                {
                    _registeredHandlers.Remove(target);
                    Debug.WriteLine($"[Cleanup] Unregistered event handler for {target} on application close");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Failed to unregister handler for {target} on close: {ex.Message}");
                }
            }

            // Existing cleanup code
            try
            {
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= SerialPort_DataReceived;

                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }

                    _serialPort.Dispose();
                    _serialPort = null;
                }

                // Unsubscribe from all events
                if (_systemVolume != null)
                {
                    _systemVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
                }

                _isConnected = false;
                UpdateConnectionStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error while closing serial port: {ex.Message}", "Cleanup Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            foreach (var sessionTuple in _sessionIdCache)
            {
                try
                {
                    if (sessionTuple.session != null)
                        Marshal.ReleaseComObject(sessionTuple.session);
                }
                catch { }
            }
            _sessionIdCache.Clear();
            _meterTimer?.Stop();
            _sessionCacheTimer?.Stop();
            _serialReconnectTimer?.Stop();
            base.OnClosed(e);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                // Only hide the window and show the NotifyIcon when minimized
                this.Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }
            else
            {
                // Ensure the NotifyIcon is hidden when the window is not minimized
                MyNotifyIcon.Visibility = Visibility.Collapsed;
            }

            base.OnStateChanged(e);
        }

        #endregion Protected Methods

        #region Private Methods
        // Improved automatic serial connection logic
        // Replace these methods in your MainWindow.xaml.cs

      

        // NEW: Setup automatic serial connection with retry logic
        private void SetupAutomaticSerialConnection()
        {
            var connectionAttempts = 0;
            const int maxAttempts = 5;
            var attemptTimer = new DispatcherTimer();

            attemptTimer.Tick += (s, e) =>
            {
                connectionAttempts++;
                Debug.WriteLine($"[AutoConnect] Attempt #{connectionAttempts}");

                if (TryConnectToSavedPort())
                {
                    Debug.WriteLine("[AutoConnect] Successfully connected!");
                    attemptTimer.Stop();
                    return;
                }

                if (connectionAttempts >= maxAttempts)
                {
                    Debug.WriteLine($"[AutoConnect] Failed after {maxAttempts} attempts");
                    attemptTimer.Stop();
                    return;
                }

                // Increase interval for subsequent attempts
                attemptTimer.Interval = TimeSpan.FromSeconds(Math.Min(2 * connectionAttempts, 10));
            };

            // Start first attempt after 2 seconds
            attemptTimer.Interval = TimeSpan.FromSeconds(2);
            attemptTimer.Start();
        }
        private void ForceCleanupTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing) return;

            try
            {
                Debug.WriteLine("[ForceCleanup] Starting aggressive cleanup...");

                // Force garbage collection before cleanup
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                // Clean up session cache more aggressively
                CleanupSessionCacheAggressively();

                // Clean up process cache
                CleanupProcessCache();

                // Clean up input device cache
                CleanupInputDeviceCache();

                // Refresh audio device reference
                RefreshAudioDeviceReference();

                // Force cleanup of meter session cache
                _cachedSessionsForMeters = null;
                _lastMeterSessionRefresh = DateTime.MinValue;

                _lastForcedCleanup = DateTime.Now;

                Debug.WriteLine("[ForceCleanup] Cleanup completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ForceCleanup] Error: {ex.Message}");
            }
        }
        private void CleanupSessionCacheAggressively()
        {
            try
            {
                // More aggressive cleanup - keep only last 20 sessions instead of 100
                if (_sessionIdCache.Count > 20)
                {
                    var sessionsToRemove = _sessionIdCache.Take(_sessionIdCache.Count - 20).ToList();

                    foreach (var sessionTuple in sessionsToRemove)
                    {
                        try
                        {
                            if (sessionTuple.session != null)
                            {
                                // Properly release COM object
                                Marshal.FinalReleaseComObject(sessionTuple.session);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Cleanup] Failed to release COM object: {ex.Message}");
                        }
                    }

                    _sessionIdCache = _sessionIdCache.Skip(_sessionIdCache.Count - 20).ToList();
                    Debug.WriteLine($"[Cleanup] Reduced session cache to {_sessionIdCache.Count} items");
                }

                // Also clean up any null or invalid sessions
                _sessionIdCache = _sessionIdCache.Where(t => t.session != null).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cleanup] Error in session cache cleanup: {ex.Message}");
            }
        }

        // 5. Improved process cache cleanup
        private void CleanupProcessCache()
        {
            try
            {
                // Keep only processes that are still running
                var runningPids = new HashSet<int>();

                try
                {
                    foreach (var proc in Process.GetProcesses())
                    {
                        try
                        {
                            runningPids.Add(proc.Id);
                            proc.Dispose(); // Dispose process objects
                        }
                        catch { }
                    }
                }
                catch { }

                var keysToRemove = _processNameCache.Keys.Where(pid => !runningPids.Contains(pid)).ToList();
                foreach (var key in keysToRemove)
                {
                    _processNameCache.Remove(key);
                }

                // Limit cache size more aggressively
                if (_processNameCache.Count > 50)
                {
                    var oldestKeys = _processNameCache.Keys.Take(_processNameCache.Count - 30).ToList();
                    foreach (var key in oldestKeys)
                    {
                        _processNameCache.Remove(key);
                    }
                }

                Debug.WriteLine($"[Cleanup] Process cache now has {_processNameCache.Count} entries");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cleanup] Error in process cache cleanup: {ex.Message}");
            }
        }

        // 6. Input device cache cleanup
        private void CleanupInputDeviceCache()
        {
            try
            {
                // Rebuild input device cache from scratch
                _inputDeviceMap.Clear();
                BuildInputDeviceCache();
                Debug.WriteLine("[Cleanup] Rebuilt input device cache");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cleanup] Error rebuilding input device cache: {ex.Message}");
            }
        }

        // 7. Refresh audio device reference
        private void RefreshAudioDeviceReference()
        {
            try
            {
                // Get fresh audio device reference
                var newDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _audioDevice = newDevice;
                Debug.WriteLine("[Cleanup] Refreshed audio device reference");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cleanup] Error refreshing audio device: {ex.Message}");
            }
        }

        // 8. Improved CleanupDeadSessions method (replace existing)
        private void CleanupDeadSessions()
        {
            var processIds = new HashSet<int>();

            try
            {
                // Get current process IDs with proper exception handling
                var processes = Process.GetProcesses();
                foreach (var proc in processes)
                {
                    try
                    {
                        if (proc != null && !proc.HasExited)
                        {
                            processIds.Add(proc.Id);
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Process doesn't exist or has exited - skip it
                    }
                    catch (InvalidOperationException)
                    {
                        // Process has exited - skip it  
                    }
                    catch
                    {
                        // Any other exception - skip it
                    }
                    finally
                    {
                        try
                        {
                            proc?.Dispose();
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Error] Failed to enumerate processes: {ex.Message}");
                return;
            }

            // Clean process name cache
            var deadPids = _processNameCache.Keys.Where(pid => !processIds.Contains(pid)).ToList();
            foreach (var pid in deadPids)
            {
                _processNameCache.Remove(pid);
            }

            // Clean session cache
            if (_sessionIdCache.Count > 100)
            {
                var sessionsToRemove = _sessionIdCache.Take(_sessionIdCache.Count - 100).ToList();

                foreach (var sessionTuple in sessionsToRemove)
                {
                    try
                    {
                        if (sessionTuple.session != null)
                        {
                            Marshal.ReleaseComObject(sessionTuple.session);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Error] Failed to release COM object: {ex.Message}");
                    }
                }

                _sessionIdCache = _sessionIdCache.Skip(_sessionIdCache.Count - 100).ToList();
            }

            // Clean input device cache
            if (_inputDeviceMap.Count > 20)
            {
                var keysToRemove = _inputDeviceMap.Keys.Take(_inputDeviceMap.Count - 10).ToList();
                foreach (var key in keysToRemove)
                {
                    _inputDeviceMap.Remove(key);
                }
            }

            Debug.WriteLine($"[Cleanup] Removed {deadPids.Count} dead processes from cache. " +
                           $"Process cache: {_processNameCache.Count}, " +
                           $"Session cache: {_sessionIdCache.Count}, " +
                           $"Input device cache: {_inputDeviceMap.Count}");
        }
        private bool TryConnectToSavedPort()
        {
            try
            {
                // Skip if already connected
                if (_isConnected && !_serialDisconnected)
                {
                    return true;
                }

                var settings = LoadSettingsFromDisk();
                if (string.IsNullOrWhiteSpace(settings?.PortName))
                {
                    Debug.WriteLine("[AutoConnect] No saved port name");
                    return false;
                }

                // Refresh available ports first
                LoadAvailablePorts();

                var availablePorts = SerialPort.GetPortNames();
                if (!availablePorts.Contains(settings.PortName))
                {
                    Debug.WriteLine($"[AutoConnect] Saved port '{settings.PortName}' not in available ports: [{string.Join(", ", availablePorts)}]");
                    return false;
                }

                // Update ComboBox selection to match the saved port
                Dispatcher.Invoke(() =>
                {
                    ComPortSelector.SelectedItem = settings.PortName;
                });

                Debug.WriteLine($"[AutoConnect] Attempting to connect to saved port: {settings.PortName}");

                // Attempt connection
                InitSerial(settings.PortName, 9600);

                // Return true if connection succeeded
                return _isConnected && !_serialDisconnected;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoConnect] Exception during connection attempt: {ex.Message}");
                return false;
            }
        }
        private void LoadSettingsWithoutSerialConnection()
        {
            try
            {
                var settings = LoadSettingsFromDisk();

                // Apply UI settings
                ApplyTheme(settings?.IsDarkTheme == true ? "Dark" : "Light");
                InvertSliderCheckBox.IsChecked = settings?.IsSliderInverted ?? false;
                ShowSlidersCheckBox.IsChecked = settings?.VuMeters ?? true;

                bool showMeters = settings?.VuMeters ?? true;
                ShowSlidersCheckBox.IsChecked = showMeters;
                SetMeterVisibilityForAll(showMeters);
                DisableSmoothingCheckBox.IsChecked = settings?.DisableSmoothing ?? false;

                // Handle startup settings
                StartOnBootCheckBox.Checked -= StartOnBootCheckBox_Checked;
                StartOnBootCheckBox.Unchecked -= StartOnBootCheckBox_Unchecked;

                bool isInStartup = IsStartupEnabled();
                _appSettings.StartOnBoot = isInStartup;
                StartOnBootCheckBox.IsChecked = isInStartup;

                StartOnBootCheckBox.Checked += StartOnBootCheckBox_Checked;
                StartOnBootCheckBox.Unchecked += StartOnBootCheckBox_Unchecked;

                StartMinimizedCheckBox.IsChecked = settings?.StartMinimized ?? false;
                StartMinimizedCheckBox.Checked += StartMinimizedCheckBox_Checked;

                // CRITICAL: Generate sliders from saved settings
                if (settings?.SliderTargets != null && settings.SliderTargets.Count > 0)
                {
                    _expectedSliderCount = settings.SliderTargets.Count;
                    GenerateSliders(settings.SliderTargets.Count);
                }
                else
                {
                    // Default to 4 sliders if no settings exist
                    _expectedSliderCount = 4;
                    GenerateSliders(4);
                }

                _hasLoadedInitialSettings = true;

                foreach (var ctrl in _channelControls)
                    ctrl.SetMeterVisibility(showMeters);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to load settings without serial: {ex.Message}");
                // Fallback: generate default sliders
                _expectedSliderCount = 4;
                GenerateSliders(4);
                _hasLoadedInitialSettings = true;
            }
        }
       
        private static void SetDisplayIcon()
        {
            //only run in Release

            try
            {
                // executable file
                var exePath = Environment.ProcessPath;
                if (!System.IO.File.Exists(exePath))
                {
                    return;
                }

                //DisplayIcon == "dfshim.dll,2" => 
                var myUninstallKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                string[]? mySubKeyNames = myUninstallKey?.GetSubKeyNames();
                for (int i = 0; i < mySubKeyNames?.Length; i++)
                {
                    RegistryKey? myKey = myUninstallKey?.OpenSubKey(mySubKeyNames[i], true);
                    // ClickOnce(Publish)
                    // Publish -> Settings -> Options 
                    // Publish Options -> Description -> Product name (is your DisplayName)
                    var displayName = (string?)myKey?.GetValue("DisplayName");
                    if (displayName?.Contains("YourApp") == true)
                    {
                        myKey?.SetValue("DisplayIcon", exePath + ",0");
                        break;
                    }
                }
                DeejNG.Settings.Default.IsFirstRun = false;
                DeejNG.Settings.Default.Save();
            }
            catch { }

        }

        private void ApplyInputDeviceVolume(string deviceName, float level, bool isMuted)
        {
            if (!_inputDeviceMap.TryGetValue(deviceName.ToLowerInvariant(), out var mic))
            {
                mic = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

                if (mic != null)
                {
                    _inputDeviceMap[deviceName.ToLowerInvariant()] = mic;
                }
            }

            if (mic != null)
            {
                float previous = _lastInputVolume.TryGetValue(deviceName, out var lastVol) ? lastVol : -1f;

                if (Math.Abs(previous - level) > 0.01f)
                {
                    mic.AudioEndpointVolume.Mute = isMuted || level <= 0.01f;
                    mic.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                    _lastInputVolume[deviceName] = level;
                }
            }
        }

        // Break out the input volume handling logic to a separate method to simplify the main method
        private void ApplyInputVolume(ChannelControl ctrl, string target, float level)
        {
            try
            {
                if (!_inputDeviceMap.TryGetValue(target, out var mic))
                {
                    mic = new MMDeviceEnumerator()
                        .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                        .FirstOrDefault(d => d.FriendlyName.Equals(target, StringComparison.OrdinalIgnoreCase));

                    if (mic != null)
                    {
                        _inputDeviceMap[target] = mic;
                    }
                }

                if (mic != null)
                {
                    float previous = _lastInputVolume.TryGetValue(target, out var lastVol) ? lastVol : -1f;

                    if (Math.Abs(previous - level) > 0.01f)
                    {
                        mic.AudioEndpointVolume.Mute = ctrl.IsMuted || level <= 0.01f;
                        mic.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                        _lastInputVolume[target] = level;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Setting mic volume: {ex.Message}");
            }
        }

        private void ApplyTheme(string theme)
        {
            isDarkTheme = theme == "Dark";
            Uri themeUri;
            if (theme == "Dark")
            {
                themeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Dark.xaml");
                isDarkTheme = true;
            }
            else
            {
                themeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml");
                isDarkTheme = false;
            }

            // Update the theme
            var existingTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == themeUri);
            if (existingTheme == null)
            {
                existingTheme = new ResourceDictionary() { Source = themeUri };
                Application.Current.Resources.MergedDictionaries.Add(existingTheme);
            }

            // Remove the other theme
            var otherThemeUri = isDarkTheme
                ? new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml")
                : new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Dark.xaml");

            var currentTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == otherThemeUri);
            if (currentTheme != null)
            {
                Application.Current.Resources.MergedDictionaries.Remove(currentTheme);
            }
        }



        private void ApplyVolumeToTargets(ChannelControl ctrl, List<AudioTarget> targets, float level)
        {
            // Simple check - don't apply volumes until we're ready
            if (!_allowVolumeApplication)
            {
                return;
            }

            foreach (var target in targets)
            {
                try
                {
                    if (target.IsInputDevice)
                    {
                        ApplyInputDeviceVolume(target.Name, level, ctrl.IsMuted);
                    }
                    else if (string.Equals(target.Name, "unmapped", StringComparison.OrdinalIgnoreCase))
                    {
                        // Handle unmapped applications with throttling
                        lock (_unmappedLock)
                        {
                            var mappedApps = GetAllMappedApplications();
                            mappedApps.Remove("unmapped"); // Don't exclude unmapped from itself

                            _audioService.ApplyVolumeToUnmappedApplications(level, ctrl.IsMuted, mappedApps);
                        }
                    }
                    else
                    {
                        _audioService.ApplyVolumeToTarget(target.Name, level, ctrl.IsMuted);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Applying volume to {target.Name}: {ex.Message}");
                }
            }
        }
        private void AudioEndpointVolume_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            Dispatcher.Invoke(() =>
            {
                var systemControl = _channelControls.FirstOrDefault(c =>
                    string.Equals(c.TargetExecutable, "system", StringComparison.OrdinalIgnoreCase));

                if (systemControl != null)
                {

                    systemControl.SetMuted(data.Muted);
                }
                else
                {

                }
            });
        }

        private void BuildInputDeviceCache()
        {
            try
            {
                _inputDeviceMap.Clear();
                var devices = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                foreach (var d in devices)
                {
                    var key = d.FriendlyName.Trim().ToLowerInvariant();
                    if (!_inputDeviceMap.ContainsKey(key))
                    {
                        _inputDeviceMap[key] = d;
                    }
                }

                Debug.WriteLine($"[Init] Cached {_inputDeviceMap.Count} input devices.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Init] Failed to build input device cache: {ex.Message}");
            }
        }


        private void ComPortSelector_DropDownOpened(object sender, EventArgs e)
        {
            Debug.WriteLine("[UI] COM port dropdown opened, refreshing ports...");
            LoadAvailablePorts();
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected && !_serialDisconnected)
            {
                // Already connected, so user wants to disconnect
                Disconnect_Click(sender, e);
            }
            else
            {
                // Not connected, so user wants to connect
                if (ComPortSelector.SelectedItem is string selectedPort)
                {
                    Debug.WriteLine($"[Manual] User clicked connect for port: {selectedPort}");
                    // Update button state immediately
                    ConnectButton.IsEnabled = false;
                    ConnectButton.Content = "Connecting...";
                    // Try connection
                    InitSerial(selectedPort, 9600);
                    // UpdateConnectionStatus() called within InitSerial will typically handle
                    // the button state correctly after connection attempt. 
                    // This timer can ensure the button is re-evaluated if InitSerial doesn't update UI
                    // or for a minimum 'Connecting...' display time.
                    var resetTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(2)
                    };
                    resetTimer.Tick += (s, args) =>
                    {
                        resetTimer.Stop();
                        UpdateConnectionStatus(); // Ensure button state is accurate after attempt
                    };
                    resetTimer.Start();
                }
                else
                {
                    MessageBox.Show("Please select a COM port first.", "No Port Selected",
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void CreateNotifyIconContextMenu()
        {

            try
            {
                ContextMenu contextMenu = new ContextMenu();

                // Show/Hide Window
                MenuItem showHideMenuItem = new MenuItem();
                showHideMenuItem.Header = "Show/Hide";
                showHideMenuItem.Click += ShowHideMenuItem_Click;

                // Exit
                MenuItem exitMenuItem = new MenuItem();
                exitMenuItem.Header = "Exit";
                exitMenuItem.Click += ExitMenuItem_Click;

                contextMenu.Items.Add(showHideMenuItem);

                contextMenu.Items.Add(new Separator()); // Separator before exit
                contextMenu.Items.Add(exitMenuItem);

                MyNotifyIcon.ContextMenu = contextMenu;
            }
            catch (Exception ex)
            {

            }
        }

        private void DisableSmoothingCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _disableSmoothing = true;
            SaveSettings();
        }

        private void DisableSmoothingCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            _disableSmoothing = false;
            SaveSettings();
        }

        private void DisableStartup()
        {
            string appName = "DeejNG";
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                key?.DeleteValue(appName, false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete startup key: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void EnableStartup()
        {
            string appName = "DeejNG";

            try
            {
                string shortcutPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Start Menu", "Programs",
                   
                    "DeejNG",      // ← Product name
                    "DeejNG.appref-ms");

                if (File.Exists(shortcutPath))
                {
                    using RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    key?.SetValue(appName, $"\"{shortcutPath}\"", RegistryValueKind.String);
                }
                else
                {
                    MessageBox.Show($"Startup shortcut not found:\n{shortcutPath}", "Startup Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to set startup key: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {

            Application.Current.Shutdown();
        }

        private void GenerateSliders(int count)
        {
            SliderPanel.Children.Clear();
            _channelControls.Clear();

            var savedSettings = LoadSettingsFromDisk();
            var savedTargetGroups = savedSettings?.SliderTargets ?? new List<List<AudioTarget>>();
            var savedInputModes = savedSettings?.InputModes ?? new List<bool>();

            _isInitializing = true;
            _allowVolumeApplication = false; // Disable ALL volume operations until first data

            Debug.WriteLine("[Init] Generating sliders - ALL volume operations DISABLED until first data");

            for (int i = 0; i < count; i++)
            {
                var control = new ChannelControl();

                // Set targets for this control
                List<AudioTarget> targetsForThisControl;

                if (i < savedTargetGroups.Count && savedTargetGroups[i].Count > 0)
                {
                    targetsForThisControl = savedTargetGroups[i];
                }
                else
                {
                    targetsForThisControl = new List<AudioTarget>();

                    if (i == 0)
                    {
                        targetsForThisControl.Add(new AudioTarget
                        {
                            Name = "system",
                            IsInputDevice = false
                        });
                    }
                }

                control.AudioTargets = targetsForThisControl;

                if (i < savedInputModes.Count)
                {
                    control.InputModeCheckBox.IsChecked = savedInputModes[i];
                }

                control.SetMuted(false);
                // DON'T set initial volume - let hardware data set it

                control.TargetChanged += (_, _) => SaveSettings();

                // VolumeOrMuteChanged will check _allowVolumeApplication flag
                control.VolumeOrMuteChanged += (targets, vol, mute) =>
                {
                    if (!_allowVolumeApplication) return;
                    ApplyVolumeToTargets(control, targets, vol);
                };

                control.SessionDisconnected += (sender, target) =>
                {
                    Debug.WriteLine($"[MainWindow] Received session disconnected for {target}");
                    if (_registeredHandlers.TryGetValue(target, out var handler))
                    {
                        _registeredHandlers.Remove(target);
                        Debug.WriteLine($"[MainWindow] Removed handler for disconnected session: {target}");
                    }
                };

                _channelControls.Add(control);
                SliderPanel.Children.Add(control);
            }

            SetMeterVisibilityForAll(ShowSlidersCheckBox.IsChecked ?? true);

            // DON'T start meters or sync mute states until first data received
            Debug.WriteLine("[Init] Sliders generated, waiting for first hardware data before completing initialization");
        }
        // Add a method to reset calibration state (call this when serial disconnects)
        private void ResetCalibrationState()
        {
            _isCalibrating = false;
            _hasReceivedInitialData = false;
            _initialSliderValues.Clear();
            _calibrationStartTime = DateTime.MinValue;
            Debug.WriteLine("[Calibration] Reset calibration state");
        }
        // Method to handle serial disconnection
        private void HandleSerialDisconnection()
        {
            if (_serialDisconnected) return;

            Debug.WriteLine("[Serial] Disconnection detected");
            _serialDisconnected = true;
            _isConnected = false;

            // Reset volume application flag
            _allowVolumeApplication = false;

            // Update UI on the dispatcher thread
            Dispatcher.BeginInvoke(() =>
            {
                ConnectionStatus.Text = "Disconnected - Reconnecting...";
                ConnectionStatus.Foreground = Brushes.Red;
                ConnectButton.IsEnabled = true;
            });

            // Try to clean up the serial port
            try
            {
                if (_serialPort != null)
                {
                    if (_serialPort.IsOpen)
                        _serialPort.Close();

                    // Keep the serial port object but clean up event handlers
                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to cleanup serial port: {ex.Message}");
            }

            // Make sure reconnect timer is running
            if (!_serialReconnectTimer.IsEnabled)
            {
                _serialReconnectTimer.Start();
            }
        }

        private void HandleSliderData(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            try
            {
                string[] parts = data.Split('|');

                if (parts.Length == 0)
                {
                    return; // Skip empty data
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Generate sliders if needed
                        if (_channelControls.Count == 0)
                        {
                            Debug.WriteLine("[WARNING] No sliders exist, generating default set");
                            _expectedSliderCount = Math.Max(parts.Length, 4);
                            GenerateSliders(_expectedSliderCount);
                            return;
                        }

                        // NEW: If incoming data has MORE parts than we have sliders, regenerate
                        if (parts.Length > _channelControls.Count)
                        {
                            Debug.WriteLine($"[INFO] Hardware sending {parts.Length} sliders but we only have {_channelControls.Count}. Regenerating sliders to match hardware.");

                            // Save current targets before regenerating
                            var currentTargets = _channelControls.Select(c => c.AudioTargets).ToList();
                            var currentInputModes = _channelControls.Select(c => c.InputModeCheckBox.IsChecked ?? false).ToList();

                            _expectedSliderCount = parts.Length;
                            GenerateSliders(parts.Length);

                            // Restore saved targets for existing sliders
                            for (int i = 0; i < Math.Min(currentTargets.Count, _channelControls.Count); i++)
                            {
                                _channelControls[i].AudioTargets = currentTargets[i];
                                _channelControls[i].InputModeCheckBox.IsChecked = currentInputModes[i];
                            }

                            // Save the new configuration
                            SaveSettings();
                            return;
                        }

                        // If incoming data has fewer parts, just log it (don't reduce sliders)
                        if (_channelControls.Count != parts.Length)
                        {
                            Debug.WriteLine($"[INFO] Incoming data has {parts.Length} parts but we have {_channelControls.Count} sliders. Processing available data only.");
                        }

                        // Process the data for existing sliders only
                        int maxIndex = Math.Min(parts.Length, _channelControls.Count);

                        for (int i = 0; i < maxIndex; i++)
                        {
                            if (!float.TryParse(parts[i].Trim(), out float level)) continue;

                            // Handle hardware noise - snap small values to exact zero
                            if (level <= 10) level = 0; // Hardware values 0-10 become exact 0
                            if (level >= 1013) level = 1023; // Hardware values 1013-1023 become exact max

                            level = Math.Clamp(level / 1023f, 0f, 1f);
                            if (InvertSliderCheckBox.IsChecked ?? false)
                                level = 1f - level;

                            var ctrl = _channelControls[i];
                            var targets = ctrl.AudioTargets;

                            if (targets.Count == 0) continue;

                            float currentVolume = ctrl.CurrentVolume;
                            if (Math.Abs(currentVolume - level) < 0.01f) continue;

                            // Always suppress events until we're ready
                            ctrl.SmoothAndSetVolume(level, suppressEvent: !_allowVolumeApplication, disableSmoothing: _disableSmoothing);

                            // Apply volume to all targets for this control ONLY if allowed
                            if (_allowVolumeApplication)
                            {
                                ApplyVolumeToTargets(ctrl, targets, level);
                            }
                        }

                        // Enable volume application and do full setup after first successful data processing
                        if (!_allowVolumeApplication)
                        {
                            _allowVolumeApplication = true;
                            _isInitializing = false;

                            Debug.WriteLine("[Init] First data received - enabling volume application and completing setup");

                            // NOW it's safe to do the full initialization
                            SyncMuteStates();

                            if (!_meterTimer.IsEnabled)
                            {
                                _meterTimer.Start();
                            }
                        }

                        // Mark serial port as fully initialized after receiving valid data
                        if (!_serialPortFullyInitialized)
                        {
                            _serialPortFullyInitialized = true;
                            Debug.WriteLine("[Serial] Port fully initialized and receiving data");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ERROR] Processing slider data in UI thread: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Parsing slider data: {ex.Message}");
            }
        }
        private void InitSerial(string portName, int baudRate)
        {
            try
            {
                // Validate port name
                if (string.IsNullOrWhiteSpace(portName))
                {
                    Debug.WriteLine("[Serial] Invalid port name provided");
                    return;
                }

                var availablePorts = SerialPort.GetPortNames();
                if (!availablePorts.Contains(portName))
                {
                    Debug.WriteLine($"[Serial] Port {portName} not in available ports: [{string.Join(", ", availablePorts)}]");

                    // Update UI to show disconnected state
                    Dispatcher.Invoke(() =>
                    {
                        _isConnected = false;
                        _serialDisconnected = true;
                        UpdateConnectionStatus();
                    });
                    return;
                }

                // Close existing connection if any
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    try
                    {
                        _serialPort.Close();
                        _serialPort.DataReceived -= SerialPort_DataReceived;
                        _serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Serial] Error closing existing port: {ex.Message}");
                    }
                }

                _serialPort = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000
                };

                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.ErrorReceived += SerialPort_ErrorReceived;

                _serialPort.Open();

                // Update connection state
                _isConnected = true;
                _lastConnectedPort = portName;
                _serialDisconnected = false;
                _serialPortFullyInitialized = false;

                // Reset volume application flag on new connection
                _allowVolumeApplication = false;

                // Reset the watchdog variables
                _lastValidDataTimestamp = DateTime.Now;
                _noDataCounter = 0;
                _expectingData = false;

                // Make sure the watchdog is running
                if (!_serialWatchdogTimer.IsEnabled)
                {
                    _serialWatchdogTimer.Start();
                }

                // Update UI
                Dispatcher.Invoke(() =>
                {
                    UpdateConnectionStatus();
                    // Ensure ComboBox shows the connected port
                    ComPortSelector.SelectedItem = portName;
                });

                // Start the reconnect timer
                if (!_serialReconnectTimer.IsEnabled)
                {
                    _serialReconnectTimer.Start();
                }

                Debug.WriteLine($"[Serial] Successfully connected to {portName} - waiting for data before applying ANY volumes");

                // Save settings only if we've loaded initial settings
                if (_hasLoadedInitialSettings)
                {
                    SaveSettings();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Serial] Failed to open port {portName}: {ex.Message}");

                // Update connection state
                _isConnected = false;
                _serialDisconnected = true;
                _serialPortFullyInitialized = false;

                Dispatcher.Invoke(() =>
                {
                    UpdateConnectionStatus();
                });

                // Don't show error dialog during automatic connection attempts
                if (_hasLoadedInitialSettings)
                {
                    // Only show error if this was a manual connection attempt
                    var isManualAttempt = ComPortSelector.SelectedItem?.ToString() == portName;
                    if (isManualAttempt)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show($"Failed to open serial port {portName}: {ex.Message}",
                                          "Serial Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                    }
                }
            }
        }

        private void InvertSlider_Checked(object sender, RoutedEventArgs e)
        {
            SaveInvertState();
        }

        private void InvertSlider_Unchecked(object sender, RoutedEventArgs e)
        {

            SaveInvertState();
        }

        private bool IsStartupEnabled()
        {
            const string appName = "DeejNG";
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            var value = key?.GetValue(appName) as string;
            return !string.IsNullOrEmpty(value);
        }

        private void LoadAvailablePorts()
        {
            try
            {
                var availablePorts = SerialPort.GetPortNames();

                // Store current selection if any
                string currentSelection = ComPortSelector.SelectedItem as string;

                // Update the ComboBox
                ComPortSelector.ItemsSource = availablePorts;

                Debug.WriteLine($"[Ports] Found {availablePorts.Length} ports: [{string.Join(", ", availablePorts)}]");

                // Try to restore previous selection first
                if (!string.IsNullOrEmpty(currentSelection) && availablePorts.Contains(currentSelection))
                {
                    ComPortSelector.SelectedItem = currentSelection;
                    Debug.WriteLine($"[Ports] Restored previous selection: {currentSelection}");
                }
                // Then try saved port from settings
                else if (!string.IsNullOrEmpty(_lastConnectedPort) && availablePorts.Contains(_lastConnectedPort))
                {
                    ComPortSelector.SelectedItem = _lastConnectedPort;
                    Debug.WriteLine($"[Ports] Selected saved port: {_lastConnectedPort}");
                }
                // Finally, select first available port
                else if (availablePorts.Length > 0)
                {
                    ComPortSelector.SelectedIndex = 0;
                    Debug.WriteLine($"[Ports] Selected first available port: {availablePorts[0]}");
                }
                else
                {
                    ComPortSelector.SelectedIndex = -1;
                    Debug.WriteLine("[Ports] No ports available");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to load available ports: {ex.Message}");
                ComPortSelector.ItemsSource = new string[0];
                ComPortSelector.SelectedIndex = -1;
            }
        }
        private void LoadSettings()
        {
            var settings = LoadSettingsFromDisk();
            if (!string.IsNullOrWhiteSpace(settings?.PortName))
            {
                InitSerial(settings.PortName, 9600);
            }

            ApplyTheme(settings?.IsDarkTheme == true ? "Dark" : "Light");
            InvertSliderCheckBox.IsChecked = settings?.IsSliderInverted ?? false;
            ShowSlidersCheckBox.IsChecked = settings?.VuMeters ?? true;

            bool showMeters = settings?.VuMeters ?? true;
            ShowSlidersCheckBox.IsChecked = showMeters;
            SetMeterVisibilityForAll(showMeters);
            DisableSmoothingCheckBox.IsChecked = settings?.DisableSmoothing ?? false;

            // ✅ Unsubscribe events temporarily
            StartOnBootCheckBox.Checked -= StartOnBootCheckBox_Checked;
            StartOnBootCheckBox.Unchecked -= StartOnBootCheckBox_Unchecked;

            bool isInStartup = IsStartupEnabled();
            _appSettings.StartOnBoot = isInStartup;
            StartOnBootCheckBox.IsChecked = isInStartup;

            // ✅ Re-subscribe after setting the value
            StartOnBootCheckBox.Checked += StartOnBootCheckBox_Checked;
            StartOnBootCheckBox.Unchecked += StartOnBootCheckBox_Unchecked;

            StartMinimizedCheckBox.IsChecked = settings?.StartMinimized ?? false;
            StartMinimizedCheckBox.Checked += StartMinimizedCheckBox_Checked;
            foreach (var ctrl in _channelControls)
                ctrl.SetMeterVisibility(showMeters);
        }

        private AppSettings LoadSettingsFromDisk()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    _appSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                    return _appSettings; // ✅ return the same reference
                }
            }
            catch { }

            _appSettings = new AppSettings();
            return _appSettings;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SliderScrollViewer.Visibility = Visibility.Visible;
            StartOnBootCheckBox.IsChecked = _appSettings.StartOnBoot;
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                // Hide the window and show the NotifyIcon when minimized
                this.Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }
            else
            {
                // Ensure the NotifyIcon is hidden when the window is not minimized
                MyNotifyIcon.Visibility = Visibility.Collapsed;
            }
        }

        private void MyNotifyIcon_Click(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Show();
                this.WindowState = WindowState.Normal;

                // ✅ Force WPF to recalculate layout now that we're visible
                this.InvalidateMeasure();
                this.UpdateLayout();
            }
            else
            {
                this.WindowState = WindowState.Minimized;
                this.Hide();
            }
        }

        private void RefreshPorts_Click(object sender, RoutedEventArgs e)
        {
            // Refresh the list of available ports
            LoadAvailablePorts();

            // Show a message if no ports are available
            if (ComPortSelector.Items.Count == 0)
            {
                MessageBox.Show("No serial ports detected. Please connect your device and try again.",
                                "No Ports Available",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
            }
            else
            {
                // If we're in disconnected state and there are ports available,
                // attempt to reconnect to the last known port
                if (_serialDisconnected && !string.IsNullOrEmpty(_lastConnectedPort) &&
                    ComPortSelector.Items.Contains(_lastConnectedPort))
                {
                    ComPortSelector.SelectedItem = _lastConnectedPort;
                    InitSerial(_lastConnectedPort, 9600);
                }
            }
        }

        private void SaveInvertState()
        {
            try
            {
                var settings = LoadSettingsFromDisk() ?? new AppSettings();
                settings.IsSliderInverted = InvertSliderCheckBox.IsChecked ?? false;
                var json = JsonSerializer.Serialize(settings);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving inversion settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveSettings()
        {
            if (_isInitializing)
            {
                Debug.WriteLine("[Settings] Skipping save during initialization");
                return;
            }

            if (!_hasLoadedInitialSettings)
            {
                Debug.WriteLine("[Settings] Skipping save - initial settings not loaded yet");
                return;
            }

            // Prevent too frequent saves
            if ((DateTime.Now - _lastSettingsSave).TotalMilliseconds < 500)
            {
                Debug.WriteLine("[Settings] Skipping save - too frequent");
                return;
            }

            lock (_settingsLock)
            {
                try
                {
                    // Validate that we have meaningful data to save
                    if (_channelControls.Count == 0)
                    {
                        Debug.WriteLine("[Settings] Skipping save - no channel controls");
                        return;
                    }

                    var settings = new AppSettings
                    {
                        PortName = _serialPort?.PortName ?? string.Empty,
                        SliderTargets = _channelControls.Select(c => c.AudioTargets ?? new List<AudioTarget>()).ToList(),
                        IsDarkTheme = isDarkTheme,
                        IsSliderInverted = InvertSliderCheckBox.IsChecked ?? false,
                        VuMeters = ShowSlidersCheckBox.IsChecked ?? true,
                        StartOnBoot = StartOnBootCheckBox.IsChecked ?? false,
                        StartMinimized = StartMinimizedCheckBox.IsChecked ?? false,
                        InputModes = _channelControls.Select(c => c.InputModeCheckBox.IsChecked ?? false).ToList(),
                        DisableSmoothing = DisableSmoothingCheckBox.IsChecked ?? false
                    };

                    // Additional validation
                    if (settings.SliderTargets.Count != _channelControls.Count)
                    {
                        Debug.WriteLine("[Settings] Warning: Slider targets count mismatch");
                    }

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    var json = JsonSerializer.Serialize(settings, options);

                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(SettingsPath);
                    if (!Directory.Exists(dir) && dir != null)
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(SettingsPath, json);
                    _lastSettingsSave = DateTime.Now;

                    Debug.WriteLine($"[Settings] Saved successfully with {settings.SliderTargets.Count} slider configurations");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Failed to save settings: {ex.Message}");
                }
            }
        }
        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                // If we're here but the port is not open, we have a disconnection
                HandleSerialDisconnection();
                return;
            }

            try
            {
                string incoming = _serialPort.ReadExisting();

                // Update timestamp when we receive data
                _lastValidDataTimestamp = DateTime.Now;
                _expectingData = true; // We're now expecting regular data
                _noDataCounter = 0;    // Reset the counter since we got data

                // Check buffer size and truncate if it gets too large
                if (_serialBuffer.Length > 8192) // 8KB limit
                {
                    _serialBuffer.Clear();
                    Debug.WriteLine("[WARNING] Serial buffer exceeded limit and was cleared");
                }

                _serialBuffer.Append(incoming);

                while (true)
                {
                    string buffer = _serialBuffer.ToString();
                    int newLineIndex = buffer.IndexOf('\n');
                    if (newLineIndex == -1) break;

                    string line = buffer.Substring(0, newLineIndex).Trim();
                    _serialBuffer.Remove(0, newLineIndex + 1);

                    Dispatcher.BeginInvoke(() => HandleSliderData(line));
                }
            }
            catch (IOException)
            {
                // IO Exception can indicate device disconnection
                HandleSerialDisconnection();
            }
            catch (InvalidOperationException)
            {
                // This can happen if the port is closed while we're reading
                HandleSerialDisconnection();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Serial read: {ex.Message}");
                _serialBuffer.Clear(); // Clear the buffer on unexpected errors
            }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            Debug.WriteLine($"[Serial] Error received: {e.EventType}");

            // Check for disconnection conditions
            if (e.EventType == SerialError.Frame || e.EventType == SerialError.RXOver ||
                e.EventType == SerialError.Overrun || e.EventType == SerialError.RXParity)
            {
                HandleSerialDisconnection();
            }
        }

        private void SerialReconnectTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing || !_serialDisconnected) return;

            Debug.WriteLine("[SerialReconnect] Attempting to reconnect...");

            // Try the saved port first
            if (TryConnectToSavedPort())
            {
                Debug.WriteLine("[SerialReconnect] Successfully reconnected to saved port");
                return;
            }

            // If saved port doesn't work, try any available port
            try
            {
                var availablePorts = SerialPort.GetPortNames();

                if (availablePorts.Length == 0)
                {
                    Debug.WriteLine("[SerialReconnect] No serial ports available");
                    Dispatcher.Invoke(() =>
                    {
                        ConnectionStatus.Text = "Waiting for device...";
                        ConnectionStatus.Foreground = Brushes.Orange;
                        LoadAvailablePorts(); // Refresh the dropdown
                    });
                    return;
                }

                // Try the first available port if our saved port isn't available
                string portToTry = availablePorts[0];
                Debug.WriteLine($"[SerialReconnect] Trying first available port: {portToTry}");

                InitSerial(portToTry, 9600);

                if (_isConnected)
                {
                    Debug.WriteLine($"[SerialReconnect] Successfully connected to {portToTry}");
                    _serialDisconnected = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SerialReconnect] Failed to reconnect: {ex.Message}");
            }
        }
        private void SerialWatchdogTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing || !_isConnected || _serialDisconnected) return;

            try
            {
                // First check if the port is actually open
                if (_serialPort == null || !_serialPort.IsOpen)
                {
                    Debug.WriteLine("[SerialWatchdog] Serial port closed unexpectedly");
                    HandleSerialDisconnection();
                    return;
                }

                // Check if we're receiving data
                if (_expectingData)
                {
                    TimeSpan elapsed = DateTime.Now - _lastValidDataTimestamp;

                    // If it's been more than 5 seconds without data, assume disconnected
                    if (elapsed.TotalSeconds > 5)
                    {
                        _noDataCounter++;
                        Debug.WriteLine($"[SerialWatchdog] No data received for {elapsed.TotalSeconds:F1} seconds (count: {_noDataCounter})");

                        // After 3 consecutive timeouts, consider disconnected
                        if (_noDataCounter >= 3)
                        {
                            Debug.WriteLine("[SerialWatchdog] Too many timeouts, considering disconnected");
                            HandleSerialDisconnection();
                            _noDataCounter = 0;
                            return;
                        }

                        // Try to write a single byte to test connection
                        try
                        {
                            // A zero-length write doesn't always work, so use a simple linefeed
                            // Most Arduino-based controllers will ignore this
                            _serialPort.Write(new byte[] { 10 }, 0, 1);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SerialWatchdog] Exception when testing connection: {ex.Message}");
                            HandleSerialDisconnection();
                            return;
                        }
                    }
                    else
                    {
                        // Reset counter if we're getting data
                        _noDataCounter = 0;
                    }
                }
                else if (_isConnected && (DateTime.Now - _lastValidDataTimestamp).TotalSeconds > 10)
                {
                    // If we haven't seen any data for 10 seconds after connecting,
                    // we may need to set the flag to start expecting data
                    _expectingData = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SerialWatchdog] Error: {ex.Message}");
            }
        }
        private void SetMeterVisibilityForAll(bool show)
        {
            _metersEnabled = show;

            foreach (var ctrl in _channelControls)
            {
                ctrl.SetMeterVisibility(show);
            }
        }

        // In MainWindow.xaml.cs, modify SerialPort_DataReceived method:
        private void ShowHideMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (this.IsVisible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
                this.WindowState = WindowState.Normal;
            }
        }

        private void ShowSlidersCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            foreach (var ctrl in _channelControls)
                ctrl.SetMeterVisibility(true);
            SetMeterVisibilityForAll(true);
            SaveSettings();
        }

        private void ShowSlidersCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            foreach (var ctrl in _channelControls)
                ctrl.SetMeterVisibility(false);
            SetMeterVisibilityForAll(false);
            SaveSettings();
        }

        private void StartMinimizedCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _appSettings.StartMinimized = true;
            SaveSettings();
        }

        private void StartMinimizedCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _appSettings.StartMinimized = false;
            SaveSettings();
        }

        private void StartOnBootCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            EnableStartup();
            _appSettings.StartOnBoot = true;
            SaveSettings();
        }
        private void StartOnBootCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            //check the value in _settings

            DisableStartup();
            _appSettings.StartOnBoot = false;
            SaveSettings();
        }

        private void StartSessionCacheUpdater()
        {
            _sessionCacheTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };

            int tickCount = 0;

            _sessionCacheTimer.Tick += (_, _) =>
            {
                if (_isClosing) return;

                try
                {
                    var defaultDevice = new MMDeviceEnumerator()
                        .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                    _audioDevice = defaultDevice;

                    var sessions = defaultDevice.AudioSessionManager.Sessions;
                    var activeSessions = new Dictionary<string, AudioSessionControl>();
                    var activeProcesses = new Dictionary<int, string>();

                    // Get all current sessions with proper exception handling
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        try
                        {
                            if (session == null) continue;

                            int processId = (int)session.GetProcessID;
                            string processName = "";

                            if (!_processNameCache.TryGetValue(processId, out processName))
                            {
                                try
                                {
                                    // FIXED: Proper exception handling here too
                                    var process = Process.GetProcessById(processId);
                                    if (process != null && !process.HasExited)
                                    {
                                        processName = Path.GetFileNameWithoutExtension(process.MainModule?.FileName ?? "")
                                                    ?.ToLowerInvariant() ?? "";
                                        process.Dispose();
                                    }
                                }
                                catch (ArgumentException)
                                {
                                    // Process doesn't exist
                                    processName = "";
                                }
                                catch (InvalidOperationException)
                                {
                                    // Process has exited
                                    processName = "";
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[ERROR] Getting process name for PID {processId}: {ex.Message}");
                                    processName = "";
                                }

                                _processNameCache[processId] = processName;
                            }

                            // Only process if we got a valid name
                            if (!string.IsNullOrEmpty(processName))
                            {
                                activeProcesses[processId] = processName;

                                string sessionId = session.GetSessionIdentifier?.ToLowerInvariant() ?? "";
                                string instanceId = session.GetSessionInstanceIdentifier?.ToLowerInvariant() ?? "";

                                string sessionKey = $"{processName}_{instanceId}";
                                activeSessions[sessionKey] = session;

                                if (!_sessionIdCache.Any(t => t.sessionId == sessionId && t.instanceId == instanceId))
                                {
                                    _sessionIdCache.Add((session, sessionId, instanceId));
                                }
                            }
                        }
                        catch (ArgumentException)
                        {
                            // Session no longer valid - skip it
                            continue;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ERROR] Processing session in cache updater: {ex.Message}");
                            continue;
                        }
                    }

                    // Clean up stale entries
                    var pidsToRemove = _processNameCache.Keys
                        .Where(pid => !activeProcesses.ContainsKey(pid))
                        .ToList();

                    foreach (var pid in pidsToRemove)
                    {
                        _processNameCache.Remove(pid);
                    }

                    // Resync and cleanup on intervals
                    if (++tickCount % 3 == 0)
                    {
                        foreach (var control in _channelControls)
                        {
                            control.ResetConnectionState();
                        }
                        SyncMuteStates();
                    }

                    if (tickCount % 12 == 0)
                    {
                        CleanupDeadSessions();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Session cache update: {ex.Message}");
                }
            };

            _sessionCacheTimer.Start();
        }

        private void SyncMuteStates()
        {
            if (!_allowVolumeApplication)
            {
                Debug.WriteLine("[Sync] Skipping SyncMuteStates - volume application disabled");
                return;
            }

            try
            {
                var audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = audioDevice.AudioSessionManager.Sessions;

                // Get all mapped applications for unmapped functionality
                var allMappedApps = GetAllMappedApplications();

                // First, unregister old handlers that aren't needed anymore
                var currentTargets = GetCurrentTargets();
                var targetsToRemove = _registeredHandlers.Keys
                    .Where(t => !currentTargets.Contains(t))
                    .ToList();

                foreach (var target in targetsToRemove)
                {
                    if (_registeredHandlers.TryGetValue(target, out var handler))
                    {
                        try
                        {
                            _registeredHandlers.Remove(target);
                            Debug.WriteLine($"[Cleanup] Unregistered event handler for {target}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ERROR] Failed to unregister handler for {target}: {ex.Message}");
                        }
                    }
                }

                // Create a dictionary for quick process name lookup
                var processDict = new Dictionary<int, string>();

                // Build map of sessions by process name for quicker lookup
                var sessionsByProcess = new Dictionary<string, AudioSessionControl>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        int pid = (int)s.GetProcessID;

                        // Get the process name
                        string procName;
                        if (!processDict.TryGetValue(pid, out procName))
                        {
                            try
                            {
                                var proc = Process.GetProcessById(pid);
                                procName = proc.ProcessName.ToLowerInvariant();
                                processDict[pid] = procName;
                            }
                            catch
                            {
                                procName = "";
                            }
                        }

                        // Only store if we got a valid process name
                        if (!string.IsNullOrEmpty(procName))
                        {
                            sessionsByProcess[procName] = s;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ERROR] Mapping session in SyncMuteStates: {ex.Message}");
                    }
                }

                foreach (var ctrl in _channelControls)
                {
                    var targets = ctrl.AudioTargets;
                    foreach (var target in targets)
                    {
                        if (target.IsInputDevice) continue; // Skip input devices for mute sync

                        string targetName = target.Name?.Trim().ToLower();
                        bool isMuted = false;

                        if (string.IsNullOrEmpty(targetName))
                        {
                            continue;
                        }

                        if (targetName == "system")
                        {
                            isMuted = audioDevice.AudioEndpointVolume.Mute;
                            ctrl.SetMuted(isMuted);
                            _audioService.ApplyMuteStateToTarget(targetName, isMuted);
                        }
                        else if (targetName == "unmapped")
                        {
                            // For unmapped, we can't really get a single mute state since it controls multiple apps
                            // Just ensure the unmapped applications follow this control's mute state
                            var mappedAppsForUnmapped = new HashSet<string>(allMappedApps);
                            mappedAppsForUnmapped.Remove("unmapped"); // Don't exclude unmapped from itself

                            _audioService.ApplyMuteStateToUnmappedApplications(ctrl.IsMuted, mappedAppsForUnmapped);
                        }
                        else
                        {
                            // Check if we have a matching session in our dictionary
                            if (sessionsByProcess.TryGetValue(targetName, out var matchedSession))
                            {
                                isMuted = matchedSession.SimpleAudioVolume.Mute;

                                // Only register if not already registered for this target
                                if (!_registeredHandlers.ContainsKey(targetName))
                                {
                                    try
                                    {
                                        var handler = new AudioSessionEventsHandler(ctrl);
                                        matchedSession.RegisterEventClient(handler);
                                        _registeredHandlers[targetName] = handler;
                                        Debug.WriteLine($"[Event] Registered handler for {targetName}");
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"[ERROR] Failed to register handler for {targetName}: {ex.Message}");
                                    }
                                }

                                ctrl.SetMuted(isMuted);
                                _audioService.ApplyVolumeToTarget(targetName, ctrl.CurrentVolume, isMuted);
                            }
                        }
                    }
                }

                _hasSyncedMuteStates = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] In SyncMuteStates: {ex.Message}");
            }
        }
        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            isDarkTheme = !isDarkTheme;
            string theme = isDarkTheme ? "Dark" : "Light";
            ApplyTheme(theme);
            SaveSettings();

        }

        private void UpdateConnectionStatus()
        {
            string statusText;
            Brush statusColor;

            if (_isConnected && !_serialDisconnected)
            {
                statusText = $"Connected to {_serialPort?.PortName ?? _lastConnectedPort}";
                statusColor = Brushes.Green;
            }
            else if (_serialDisconnected)
            {
                statusText = "Disconnected - Reconnecting...";
                statusColor = Brushes.Orange;
            }
            else
            {
                statusText = "Disconnected";
                statusColor = Brushes.Red;
            }

            // Update UI elements
            ConnectionStatus.Text = statusText;
            ConnectionStatus.Foreground = statusColor;

            // Always enable the button, just change the text
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = (_isConnected && !_serialDisconnected) ? "Disconnect" : "Connect";

            Debug.WriteLine($"[Status] {statusText}");
        }

        // NEW: Add disconnect functionality
        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    _serialPort.Close();
                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                }

                _isConnected = false;
                _serialDisconnected = true;
                _serialPortFullyInitialized = false;

                UpdateConnectionStatus();

                Debug.WriteLine("[Manual] User disconnected serial port");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to disconnect: {ex.Message}");
            }
        }
        // Update your existing GetUnmappedApplicationsPeakLevel method in MainWindow.xaml.cs:

        private float GetUnmappedApplicationsPeakLevel(HashSet<string> mappedApplications)
        {
            // Throttle meter updates for unmapped to reduce load
            var now = DateTime.Now;
            if ((now - _lastUnmappedMeterUpdate).TotalMilliseconds < 200) // 200ms throttle for meters
            {
                return 0; // Return 0 to reduce processing load
            }
            _lastUnmappedMeterUpdate = now;

            float highestPeak = 0;

            try
            {
                var sessions = _cachedSessionsForMeters;
                if (sessions == null) return 0;

                int processedSessions = 0;

                for (int i = 0; i < sessions.Count && processedSessions < 10; i++) // Limit processing to 10 sessions max
                {
                    var session = sessions[i];
                    try
                    {
                        if (session == null) continue;

                        int pid = (int)session.GetProcessID;

                        // Skip system sessions and low PIDs
                        if (pid <= 4) continue;

                        string processName = "";

                        if (!_processNameCache.TryGetValue(pid, out processName))
                        {
                            try
                            {
                                var process = Process.GetProcessById(pid);
                                if (process != null && !process.HasExited)
                                {
                                    processName = process.ProcessName.ToLowerInvariant();
                                    process.Dispose();
                                }
                                else
                                {
                                    processName = "";
                                }
                            }
                            catch
                            {
                                processName = "";
                            }

                            _processNameCache[pid] = processName;
                        }

                        // Skip if we couldn't get a valid process name
                        if (string.IsNullOrEmpty(processName)) continue;

                        // Skip if this application is mapped to a slider
                        if (mappedApplications.Contains(processName)) continue;

                        // Get the peak level for this unmapped session
                        try
                        {
                            float peak = session.AudioMeterInformation.MasterPeakValue;
                            if (peak > highestPeak)
                                highestPeak = peak;
                            processedSessions++;
                        }
                        catch
                        {
                            // Session became invalid - ignore
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Getting unmapped peak levels: {ex.Message}");
            }

            return highestPeak;
        }
        private void UpdateMeters(object? sender, EventArgs e)
        {
            if (!_metersEnabled || _isClosing) return;

            const float visualGain = 1.5f;
            const float systemCalibrationFactor = 2.0f;

            if ((DateTime.Now - _lastMeterSessionRefresh).TotalMilliseconds > METER_SESSION_CACHE_MS)
            {
                try
                {
                    _cachedSessionsForMeters = _audioDevice.AudioSessionManager.Sessions;
                    _lastMeterSessionRefresh = DateTime.Now;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Failed to cache sessions for meters: {ex.Message}");
                    return;
                }
            }

            try
            {
                var sessions = _cachedSessionsForMeters;
                if (sessions == null) return;

                foreach (var ctrl in _channelControls)
                {
                    try
                    {
                        var targets = ctrl.AudioTargets;
                        if (targets.Count == 0)
                        {
                            ctrl.UpdateAudioMeter(0);
                            continue;
                        }

                        float highestPeak = 0;
                        bool allMuted = true;

                        // Process input device targets
                        var inputTargets = targets.Where(t => t.IsInputDevice).ToList();
                        foreach (var target in inputTargets)
                        {
                            try
                            {
                                if (!_inputDeviceMap.TryGetValue(target.Name.ToLowerInvariant(), out var mic))
                                {
                                    mic = new MMDeviceEnumerator()
                                        .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                                        .FirstOrDefault(d => d.FriendlyName.Equals(target.Name, StringComparison.OrdinalIgnoreCase));

                                    if (mic != null)
                                        _inputDeviceMap[target.Name.ToLowerInvariant()] = mic;
                                }

                                if (mic != null)
                                {
                                    float peak = mic.AudioMeterInformation.MasterPeakValue;
                                    if (peak > highestPeak)
                                        highestPeak = peak;

                                    if (!mic.AudioEndpointVolume.Mute)
                                        allMuted = false;
                                }
                            }
                            catch (ArgumentException)
                            {
                                // Device no longer exists - remove from cache
                                _inputDeviceMap.Remove(target.Name.ToLowerInvariant());
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[ERROR] Input device meter {target.Name}: {ex.Message}");
                            }
                        }

                        // Process output app targets
                        var outputTargets = targets.Where(t => !t.IsInputDevice).ToList();
                        foreach (var target in outputTargets)
                        {
                            try
                            {
                                if (string.Equals(target.Name, "system", StringComparison.OrdinalIgnoreCase))
                                {
                                    float peak = _audioDevice.AudioMeterInformation.MasterPeakValue;
                                    float systemVol = _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                                    peak *= systemVol * systemCalibrationFactor;

                                    if (peak > highestPeak)
                                        highestPeak = peak;

                                    if (!_audioDevice.AudioEndpointVolume.Mute)
                                        allMuted = false;
                                }
                                else if (string.Equals(target.Name, "unmapped", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Get peak levels for all unmapped applications
                                    var mappedApps = GetAllMappedApplications();
                                    mappedApps.Remove("unmapped"); // Don't exclude unmapped from itself

                                    float unmappedPeak = GetUnmappedApplicationsPeakLevel(mappedApps);

                                    if (unmappedPeak > highestPeak)
                                        highestPeak = unmappedPeak;

                                    // For mute state, if the slider itself isn't muted, consider unmapped as not muted
                                    if (!ctrl.IsMuted)
                                        allMuted = false;
                                }
                                else
                                {
                                    // Find matching session with proper exception handling
                                    AudioSessionControl? matchingSession = null;
                                    string targetName = target.Name.ToLowerInvariant();

                                    for (int i = 0; i < sessions.Count; i++)
                                    {
                                        var s = sessions[i];
                                        try
                                        {
                                            if (s == null) continue;

                                            int pid = (int)s.GetProcessID;

                                            if (!_processNameCache.TryGetValue(pid, out string procName))
                                            {
                                                try
                                                {
                                                    var process = Process.GetProcessById(pid);
                                                    if (process != null && !process.HasExited)
                                                    {
                                                        procName = process.ProcessName.ToLowerInvariant();
                                                        process.Dispose();
                                                    }
                                                    else
                                                    {
                                                        procName = "";
                                                    }
                                                }
                                                catch (ArgumentException)
                                                {
                                                    procName = "";
                                                }
                                                catch (InvalidOperationException)
                                                {
                                                    procName = "";
                                                }
                                                catch
                                                {
                                                    procName = "";
                                                }

                                                _processNameCache[pid] = procName;
                                            }

                                            // Skip if we couldn't get a valid process name
                                            if (string.IsNullOrEmpty(procName)) continue;

                                            if (procName == targetName)
                                            {
                                                matchingSession = s;
                                                break;
                                            }
                                        }
                                        catch (ArgumentException)
                                        {
                                            // Session no longer valid - skip it
                                            continue;
                                        }
                                        catch (Exception sessionEx)
                                        {
                                            Debug.WriteLine($"[ERROR] Session access: {sessionEx.Message}");
                                            continue;
                                        }
                                    }

                                    if (matchingSession != null)
                                    {
                                        try
                                        {
                                            float peak = matchingSession.AudioMeterInformation.MasterPeakValue;
                                            if (peak > highestPeak)
                                                highestPeak = peak;

                                            if (!matchingSession.SimpleAudioVolume.Mute)
                                                allMuted = false;
                                        }
                                        catch (ArgumentException)
                                        {
                                            // Session became invalid - ignore this update
                                        }
                                        catch (Exception ex)
                                        {
                                            Debug.WriteLine($"[ERROR] Session meter: {ex.Message}");
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[ERROR] Output target meter {target.Name}: {ex.Message}");
                            }
                        }

                        float finalLevel = ctrl.IsMuted || allMuted ? 0 : Math.Min(highestPeak * visualGain, 1.0f);
                        ctrl.UpdateAudioMeter(finalLevel);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ERROR] Control meter update: {ex.Message}");
                        ctrl.UpdateAudioMeter(0);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Meter update: {ex.Message}");
            }
        }
        private HashSet<string> GetAllMappedApplications()
        {
            var mappedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ctrl in _channelControls)
            {
                foreach (var target in ctrl.AudioTargets)
                {
                    if (!target.IsInputDevice && !string.IsNullOrWhiteSpace(target.Name))
                    {
                        mappedApps.Add(target.Name.ToLowerInvariant());
                    }
                }
            }

            return mappedApps;
        }
        #endregion Private Methods

        #region Private Classes

        private class AppSettings
        {
            #region Public Properties

            public bool DisableSmoothing { get; set; }
            // 👇 ADD THIS:
            public List<bool> InputModes { get; set; } = new();

            public bool IsDarkTheme { get; set; }
            public bool IsSliderInverted { get; set; }
            public List<bool> MuteStates { get; set; } = new();
            public string? PortName { get; set; }
            public List<List<AudioTarget>> SliderTargets { get; set; } = new();
            public bool StartMinimized { get; set; } = false;
            public bool StartOnBoot { get; set; }
            public bool VuMeters { get; set; } = true;

            #endregion Public Properties
        }

        #endregion Private Classes
    }
    static class IconHandler
    {

        #region Private Properties

        static string IconPath => Path.Combine(AppContext.BaseDirectory, "icon.ico");

        #endregion Private Properties

        #region Public Methods

        public static void AddIconToRemovePrograms(string productName)
        {
            try
            {
                // Ensure the icon exists
                if (File.Exists(IconPath))
                {
                    // Open the Uninstall registry key
                    var uninstallKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                    if (uninstallKey != null)
                    {
                        foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                        {
                            using (var subKey = uninstallKey.OpenSubKey(subKeyName, writable: true))
                            {
                                if (subKey == null) continue;

                                // Check the display name of the application
                                var displayName = subKey.GetValue("DisplayName") as string;
                                if (string.Equals(displayName, productName, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Set the DisplayIcon value
                                    subKey.SetValue("DisplayIcon", IconPath);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle errors gracefully
                Console.WriteLine($"Error setting uninstall icon: {ex.Message}");
            }
        }

        #endregion Public Methods

    }
}
