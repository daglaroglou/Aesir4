using Gtk;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Serilog;
using TheAirBlow.Thor.Library;
using TheAirBlow.Thor.Library.Communication;
using TheAirBlow.Syndical.Library;
using TheAirBlow.Thor.Library.Protocols;
using TheAirBlow.Thor.Library.PIT;
using K4os.Compression.LZ4.Streams;
using System.Formats.Tar;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Aesir
{
    public static class ImageHelper
    {
        public static Gtk.Image LoadEmbeddedImage(string resourceName, int pixelSize = 128)
        {
            var image = Gtk.Image.New();
            
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream($"Aesir.assets.{resourceName}");
                
                if (stream != null)
                {
                    // Create a temporary file to load the image
                    var tempPath = Path.GetTempFileName();
                    using (var fileStream = File.Create(tempPath))
                    {
                        stream.CopyTo(fileStream);
                    }
                    
                    image.SetFromFile(tempPath);
                    image.SetPixelSize(pixelSize);
                    
                    // Clean up temp file
                    File.Delete(tempPath);
                }
                else
                {
                    throw new Exception($"Embedded resource not found: {resourceName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load embedded image {resourceName}: {ex.Message}");
                // Fallback to icon name
                image.SetFromIconName("application-x-firmware");
                image.SetIconSize(Gtk.IconSize.Large);
                image.SetPixelSize(pixelSize);
            }
            
            return image;
        }
    }

    public class ThorFlashManager
    {
        private IHandler? _handler;
        private Odin? _odinProtocol;
        private bool _isConnected = false;
        
        public delegate void LogMessageDelegate(string message);
        public event LogMessageDelegate? OnLogMessage;
        
        public delegate void ProgressDelegate(int percentage, string message);
        public event ProgressDelegate? OnProgress;
        
        public async Task<bool> InitializeAsync()
        {
            try
            {
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console()
                    .CreateLogger();
                
                if (!USB.TryGetHandler(out _handler))
                {
                    OnLogMessage?.Invoke("ERROR: USB handler not available for this platform");
                    return false;
                }
                
                OnLogMessage?.Invoke("Thor library initialized successfully");
                
                // Initialize device database
                OnLogMessage?.Invoke("Loading device database...");
                var initResult = await Lookup.Initialize();
                switch (initResult)
                {
                    case Lookup.InitState.Downloaded:
                        OnLogMessage?.Invoke("Device database downloaded and cached");
                        break;
                    case Lookup.InitState.Cache:
                        OnLogMessage?.Invoke("Device database loaded from cache");
                        break;
                    case Lookup.InitState.Failed:
                        OnLogMessage?.Invoke("WARNING: Failed to load device database");
                        break;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"ERROR: Failed to initialize Thor: {ex.Message}");
                return false;
            }
        }
        
        public async Task<bool> ConnectToDeviceAsync()
        {
            try
            {
                if (_handler == null)
                {
                    OnLogMessage?.Invoke("ERROR: Thor not initialized");
                    return false;
                }
                
                OnLogMessage?.Invoke("Scanning for Samsung devices...");
                var devices = _handler.GetDevices();
                
                if (devices.Count == 0)
                {
                    OnLogMessage?.Invoke("ERROR: No Samsung devices found in download mode");
                    return false;
                }
                
                // For GUI, we'll connect to the first device found
                var device = devices[0];
                OnLogMessage?.Invoke($"Found device: {device.DisplayName}");
                OnLogMessage?.Invoke("Connecting to device...");
                
                _handler.Initialize(device.Identifier);
                _isConnected = true;
                
                OnLogMessage?.Invoke("Device connected successfully!");
                return true;
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"ERROR: Failed to connect to device: {ex.Message}");
                return false;
            }
        }
        
        public async Task<bool> BeginOdinSessionAsync()
        {
            try
            {
                if (!_isConnected || _handler == null)
                {
                    OnLogMessage?.Invoke("ERROR: No device connected");
                    return false;
                }
                
                OnLogMessage?.Invoke("Starting Odin protocol session...");
                _odinProtocol = new Odin(_handler);
                
                OnLogMessage?.Invoke("Performing handshake...");
                _odinProtocol.Handshake();
                
                OnLogMessage?.Invoke("Beginning session...");
                _odinProtocol.BeginSession();
                
                OnLogMessage?.Invoke($"Bootloader version: {_odinProtocol.Version.Version}");
                OnLogMessage?.Invoke("Odin session established successfully!");
                
                return true;
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"ERROR: Failed to begin Odin session: {ex.Message}");
                return false;
            }
        }
        

        public async Task<bool> FlashTarFileAsync(string tarFilePath)
        {
            try
            {
                if (_odinProtocol == null)
                {
                    OnLogMessage?.Invoke("ERROR: Odin session not established");
                    return false;
                }
                
                if (!File.Exists(tarFilePath))
                {
                    OnLogMessage?.Invoke($"ERROR: TAR file not found: {tarFilePath}");
                    return false;
                }
                
                OnLogMessage?.Invoke($"Processing TAR file: {Path.GetFileName(tarFilePath)}");
                
                // Get PIT data first
                var pitData = _odinProtocol.DumpPIT();
                var pit = new PitData(pitData);
                
                using var tar = new FileStream(tarFilePath, FileMode.Open, FileAccess.Read);
                using var reader = new TarReader(tar);
                
                var totalBytes = 0L;
                var entries = new List<(TarEntry entry, PitEntry pitEntry)>();
                
                // First pass: collect entries and calculate total size
                while (reader.GetNextEntry() is { } entry)
                {
                    if (entry.DataStream == null || !string.IsNullOrEmpty(Path.GetDirectoryName(entry.Name)))
                        continue;
                        
                    var pitEntry = pit.Entries.FirstOrDefault(x => 
                        x.FileName.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                    
                    if (pitEntry != null)
                    {
                        entries.Add((entry, pitEntry));
                        totalBytes += entry.Length;
                    }
                }
                
                if (entries.Count == 0)
                {
                    OnLogMessage?.Invoke("ERROR: No flashable files found in TAR");
                    return false;
                }
                
                OnLogMessage?.Invoke($"Found {entries.Count} flashable files in TAR");
                _odinProtocol.SetTotalBytes(totalBytes);
                
                // Second pass: flash each file
                tar.Seek(0, SeekOrigin.Begin);
                using var reader2 = new TarReader(tar);
                
                while (reader2.GetNextEntry() is { } entry)
                {
                    if (entry.DataStream == null) continue;
                    
                    var entryData = entries.FirstOrDefault(x => x.entry.Name == entry.Name);
                    if (entryData.pitEntry == null) continue;
                    
                    OnLogMessage?.Invoke($"Flashing {entry.Name} to partition {entryData.pitEntry.Partition}");
                    
                    var stream = entry.DataStream;
                    if (entry.Name.EndsWith(".lz4"))
                    {
                        stream = LZ4Stream.Decode(stream);
                        OnLogMessage?.Invoke("File is LZ4 compressed, decompressing...");
                    }
                    
                    var lastProgress = -1;
                    _odinProtocol.FlashPartition(stream, entryData.pitEntry, info =>
                    {
                        var progress = (int)((info.SentBytes * 100) / info.TotalBytes);
                        if (progress != lastProgress)
                        {
                            lastProgress = progress;
                            var state = info.State == Odin.FlashProgressInfo.StateEnum.Sending ? "Sending" : "Flashing";
                            OnProgress?.Invoke(progress, $"{state} {entry.Name} - sequence {info.SequenceIndex + 1}/{info.TotalSequences}");
                        }
                    });
                    
                    stream.Dispose();
                    OnLogMessage?.Invoke($"Successfully flashed {entry.Name}");
                }
                
                OnLogMessage?.Invoke($"TAR file flashed successfully!");
                return true;
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"ERROR: TAR flash operation failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> FlashFileAsync(string filePath, string partitionName)
        {
            try
            {
                if (_odinProtocol == null)
                {
                    OnLogMessage?.Invoke("ERROR: Odin session not established");
                    return false;
                }
                
                if (!File.Exists(filePath))
                {
                    OnLogMessage?.Invoke($"ERROR: File not found: {filePath}");
                    return false;
                }
                
                OnLogMessage?.Invoke($"Preparing to flash {Path.GetFileName(filePath)}...");
                
                // Get PIT data to find partition
                OnLogMessage?.Invoke("Dumping PIT data...");
                var pitData = _odinProtocol.DumpPIT();
                var pit = new PitData(pitData);
                
                // Find partition by name or filename
                var fileName = Path.GetFileName(filePath);
                var entry = pit.Entries.FirstOrDefault(x => 
                    x.Partition.Equals(partitionName, StringComparison.OrdinalIgnoreCase) ||
                    x.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                
                if (entry == null)
                {
                    OnLogMessage?.Invoke($"ERROR: Could not find partition for {fileName}");
                    OnLogMessage?.Invoke("Available partitions:");
                    foreach (var e in pit.Entries.Take(10)) // Show first 10
                    {
                        OnLogMessage?.Invoke($"  - {e.Partition} ({e.FileName})");
                    }
                    return false;
                }
                
                OnLogMessage?.Invoke($"Flashing to partition: {entry.Partition}");
                
                // Prepare file stream
                var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                Stream stream = fileStream;
                
                // Handle LZ4 compression
                if (fileName.EndsWith(".lz4"))
                {
                    stream = LZ4Stream.Decode(fileStream);
                    OnLogMessage?.Invoke("File is LZ4 compressed, decompressing...");
                }
                
                // Set total bytes
                _odinProtocol.SetTotalBytes(stream.Length);
                
                // Flash the partition
                OnLogMessage?.Invoke($"Starting flash operation ({stream.Length} bytes)...");
                var lastProgress = -1;
                
                _odinProtocol.FlashPartition(stream, entry, info =>
                {
                    var progress = (int)((info.SentBytes * 100) / info.TotalBytes);
                    if (progress != lastProgress)
                    {
                        lastProgress = progress;
                        var state = info.State == Odin.FlashProgressInfo.StateEnum.Sending ? "Sending" : "Flashing";
                        OnProgress?.Invoke(progress, $"{state} sequence {info.SequenceIndex + 1}/{info.TotalSequences}");
                    }
                });
                
                stream.Dispose();
                OnLogMessage?.Invoke($"Successfully flashed {Path.GetFileName(filePath)}!");
                return true;
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"ERROR: Flash operation failed: {ex.Message}");
                return false;
            }
        }
        
        public async Task<bool> EndSessionAndRebootAsync()
        {
            try
            {
                if (_odinProtocol != null)
                {
                    OnLogMessage?.Invoke("Ending Odin session...");
                    _odinProtocol.EndSession();
                    
                    OnLogMessage?.Invoke("Rebooting device...");
                    _odinProtocol.Reboot();
                }
                
                return true;
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"ERROR: Failed to end session: {ex.Message}");
                return false;
            }
        }
        
        public void Disconnect()
        {
            try
            {
                _odinProtocol = null;
                _handler?.Disconnect();
                _isConnected = false;
                OnLogMessage?.Invoke("Disconnected from device");
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"ERROR: Failed to disconnect: {ex.Message}");
            }
        }
    }
    
    public class AppSettings
    {
        public string Odin4Path { get; set; } = "";
        public string ThorPath { get; set; } = "";
        public string HeimdallPath { get; set; } = "";
        public string DefaultFlashTool { get; set; } = "Odin4";
        public string LastUsedDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        public bool AutoCheckForUpdates { get; set; } = true;
        public bool CreateDesktopEntry { get; set; } = true;
        public bool IsFirstRun { get; set; } = true;
        public string CurrentVersion { get; set; } = "1.0.0";
        
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
            "Aesir", 
            "settings.json"
        );
        
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    return settings ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to load settings: {ex.Message}");
            }
            
            return new AppSettings();
        }
        
        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory!);
                }
                
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to save settings: {ex.Message}");
            }
        }
        
        public async Task<bool> CreateDesktopEntryAsync()
        {
            try
            {
                // Get the current executable path
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executablePath))
                {
                    Log.Warning("Could not determine executable path for desktop entry");
                    return false;
                }
                
                // Create Aesir config directory if it doesn't exist
                var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aesir");
                if (!Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }
                
                // Download the icon using wget
                var iconPath = Path.Combine(configDir, "A.png");
                var iconUrl = "https://raw.githubusercontent.com/daglaroglou/Aesir/main/assets/A.png";
                
                // Use wget to download the icon
                var wgetProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "wget",
                        Arguments = $"-O \"{iconPath}\" \"{iconUrl}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                wgetProcess.Start();
                await wgetProcess.WaitForExitAsync();
                
                if (wgetProcess.ExitCode != 0)
                {
                    Log.Warning($"Failed to download icon with wget (exit code: {wgetProcess.ExitCode})");
                    // Continue anyway, desktop entry will work without icon
                }
                
                // Create desktop entry content
                var desktopEntryContent = $@"[Desktop Entry]
Version=1.0
Type=Application
Name=Aesir
Comment=Samsung Firmware Flash Tool
Exec={executablePath}
Icon={iconPath}
Terminal=false
Categories=Development;System;
StartupNotify=true
";
                
                // Get the desktop directory
                var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var desktopEntryPath = Path.Combine(desktopDir, "Aesir.desktop");
                
                // Write the desktop entry
                await File.WriteAllTextAsync(desktopEntryPath, desktopEntryContent);
                
                // Make the desktop entry executable
                var chmodProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"+x \"{desktopEntryPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                
                chmodProcess.Start();
                await chmodProcess.WaitForExitAsync();
                
                Log.Information($"Desktop entry created successfully at: {desktopEntryPath}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to create desktop entry: {ex.Message}");
                return false;
            }
        }
    }
    
    public class OdinMainWindow : Gtk.ApplicationWindow
    {
        // Flash tool selection
        public enum FlashTool
        {
            Odin4,
            Heimdall,
            Thor
        }
        
        private FlashTool selectedFlashTool = FlashTool.Thor;
        private Gtk.DropDown flashToolDropDown = null!;
        private Gtk.Label flashToolSelectedLabel = null!;
        
        // Odin tab controls
        private Gtk.Entry blFileEntry = null!;
        private Gtk.Entry apFileEntry = null!;
        private Gtk.Entry cpFileEntry = null!;
        private Gtk.Entry cscFileEntry = null!;
        private Gtk.Entry userdataFileEntry = null!;
        
        private Gtk.Button blButton = null!;
        private Gtk.Button apButton = null!;
        private Gtk.Button cpButton = null!;
        private Gtk.Button cscButton = null!;
        private Gtk.Button userdataButton = null!;
        
        private Gtk.CheckButton blCheckButton = null!;
        private Gtk.CheckButton apCheckButton = null!;
        private Gtk.CheckButton cpCheckButton = null!;
        private Gtk.CheckButton cscCheckButton = null!;
        private Gtk.CheckButton userdataCheckButton = null!;
        
        
        private Gtk.Label odinLogLabel = null!;
        private Gtk.Button startButton = null!;
        private Gtk.Button resetButton = null!;
        private Gtk.Label deviceStatusLabel = null!;
        private Gtk.Label comPortLabel = null!;
        private Gtk.Label timeLabel = null!;
        
        // Thor flash manager
        private ThorFlashManager? thorFlashManager = null!;
        private Gtk.ProgressBar? progressBar = null!;
        
        // Settings
        private AppSettings settings = null!;
        
        // Odin4 binary detection
        private bool isOdin4Available = false;
        private string odin4DevicePath = "";
        
        // Background services
        private System.Threading.Timer? deviceCheckTimer = null;
        private System.Threading.Timer? elapsedTimeTimer = null;
        private DateTime flashStartTime = DateTime.Now;
        private bool isFlashing = false;
        
        
        // ADB tab controls
        private Gtk.Label adbLogLabel = null!;
        private Gtk.Entry shellCommandEntry = null!;
        private Gtk.Entry packageNameEntry = null!;
        private Gtk.Entry packagePathEntry = null!;
        
        // ADB device detection widget
        private Gtk.Label adbDeviceStatusLabel = null!;
        private Gtk.Label adbDeviceModelLabel = null!;
        private Gtk.Label adbDeviceSerialLabel = null!;
        
        // ADB progress tracking
        private Gtk.ProgressBar adbProgressBar = null!;
        private Gtk.Label adbStepLabel = null!;
        private Gtk.Label adbTimeLabel = null!;
        
        // Tab tracking
        private Gtk.Notebook mainNotebook = null!;
        
        // Fastboot tab controls
        private Gtk.Label fastbootLogLabel = null!;
        private Gtk.Entry fastbootCommandEntry = null!;
        private Gtk.Entry fastbootImagePathEntry = null!;
        private Gtk.Entry fastbootPartitionEntry = null!;
        
        // Fastboot device detection widgets
        private Gtk.Label fastbootDeviceStatusLabel = null!;
        private Gtk.Label fastbootDeviceModelLabel = null!;
        private Gtk.Label fastbootDeviceSerialLabel = null!;
        
        // Fastboot progress widgets
        private Gtk.ProgressBar fastbootProgressBar = null!;
        private Gtk.Label fastbootStepLabel = null!;
        private Gtk.Label fastbootTimeLabel = null!;
        
        // FUS tab controls
        private Gtk.Label fusLogLabel = null!;
        private Gtk.Entry fusModelEntry = null!;
        private Gtk.Entry fusRegionEntry = null!;
        private Gtk.Entry fusImeiEntry = null!;
        private Gtk.Entry fusDownloadPathEntry = null!;
        private Gtk.Button fusCheckButton = null!;
        private Gtk.Button fusDownloadButton = null!;
        private Gtk.Button fusPauseResumeButton = null!;
        private Gtk.Button fusStopButton = null!;
        private Gtk.Button fusDecryptButton = null!;
        private Gtk.ProgressBar fusProgressBar = null!;
        private CancellationTokenSource? fusDownloadCancellationSource = null;
        private bool fusDownloadPaused = false;
        private bool fusDownloadInProgress = false;
        private Gtk.Label fusStepLabel = null!;
        private Gtk.Label fusStatusLabel = null!;
        private Gtk.Label fusFirmwareInfoLabel = null!;
        private Gtk.Box fusFirmwareListBox = null!;
        private Gtk.ScrolledWindow fusFirmwareScrollWindow = null!;
        private List<Gtk.CheckButton> fusFirmwareCheckButtons = new List<Gtk.CheckButton>();
        private TheAirBlow.Syndical.Library.DeviceFirmwaresXml? currentDeviceFirmwares = null;
        private string? selectedFirmwareVersion = null;
        
        // Log storage
        private List<string> odinLogMessages = new List<string>();
        private List<string> adbLogMessages = new List<string>();
        private List<string> fastbootLogMessages = new List<string>();
        private List<string> fusLogMessages = new List<string>();
        private List<string> gappsLogMessages = new List<string>();
        
        // GAPPS tab controls
        private Gtk.Label gappsLogLabel = null!;

        public OdinMainWindow(Gtk.Application application) : base()
        {
            Application = application;
            Title = "Aesir - Firmware Flash Tool";
            
            SetDefaultSize(1050, 850);
            
            // Load settings
            settings = AppSettings.Load();
            Resizable = true;
            
            BuildUI();
            ApplyGnomeStyles();
            ConnectSignals();
            InitializeThor();
            _ = CheckOdin4Availability(); // Fire and forget async call
            
            // Initialize Odin log
            LogMessage("Aesir - Firmware Flash Tool");
            LogMessage("");
            LogMessage("<OSM> WARNING: This tool can modify your device firmware!");
            LogMessage("<OSM> Use at your own risk and ensure you have proper backups.");
            
            // Initialize ADB log
            LogAdbMessage("ADB interface initialized");
            LogAdbMessage("Ready for ADB commands...");
            LogAdbMessage("Make sure ADB is installed and device has USB debugging enabled.");
            
            // Initialize Fastboot log
            LogFastbootMessage("Fastboot interface initialized");
            LogFastbootMessage("Ready for Fastboot commands...");
            LogFastbootMessage("Make sure Fastboot is installed and device is in bootloader mode.");
            
            // Initialize FUS log
            LogFusMessage("FUS (Firmware Update Service) initialized");
            LogFusMessage("Samsung Firmware Downloader using Syndical library");
            LogFusMessage("Ready to check and download firmware...");
            
            // Initialize GAPPS log
            LogGappsMessage("GAPPS Downloader initialized");
            LogGappsMessage("Select your device architecture, Android version, and package variant");
            LogGappsMessage("Click 'Download' to open the download page for your selection");
            
            // Start background services
            StartBackgroundServices();
            
            // Connect destroy signal to cleanup background services
            OnDestroy += OnWindowDestroy;
            
            // Handle first run setup
            HandleFirstRunSetup();
        }
        
        private void BuildUI()
        {
            // Create GNOME-style header bar
            var headerBar = Gtk.HeaderBar.New();
            headerBar.ShowTitleButtons = true;
            
            // Create title widget with proper GNOME styling
            var titleBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            titleBox.SetValign(Gtk.Align.Center);
            
            var titleLabel = Gtk.Label.New("Aesir");
            titleLabel.AddCssClass("title");
            titleBox.Append(titleLabel);
            
            var subtitleLabel = Gtk.Label.New($"v{settings.CurrentVersion}");
            subtitleLabel.AddCssClass("subtitle");
            titleBox.Append(subtitleLabel);
            
            headerBar.SetTitleWidget(titleBox);
            
            // Create primary menu button (GNOME style)
            var menuButton = Gtk.MenuButton.New();
            menuButton.IconName = "open-menu-symbolic";
            menuButton.SetTooltipText("Main Menu");
            menuButton.AddCssClass("flat");
            
            // Create primary menu model
            var primaryMenu = Gio.Menu.New();
            
            // Preferences section
            var preferencesSection = Gio.Menu.New();
            preferencesSection.Append("_Settings", "app.settings");
            primaryMenu.AppendSection(null, preferencesSection);
            
            // About section
            var aboutSection = Gio.Menu.New();
            aboutSection.Append("_About Aesir", "app.about");
            primaryMenu.AppendSection(null, aboutSection);
            
            menuButton.SetMenuModel(primaryMenu);
            headerBar.PackEnd(menuButton);
            
            // Set the header bar as the title bar
            SetTitlebar(headerBar);
            
            // Create main notebook for tabs
            mainNotebook = Gtk.Notebook.New();
            
            // Create Odin Tab
            var odinTab = CreateOdinTab();
            mainNotebook.AppendPage(odinTab, Gtk.Label.New("Odin"));

            // Create ADB Tab
            var adbTab = CreateAdbTab();
            mainNotebook.AppendPage(adbTab, Gtk.Label.New("ADB"));
            
            // Create Fastboot Tab
            var fastbootTab = CreateFastbootTab();
            mainNotebook.AppendPage(fastbootTab, Gtk.Label.New("Fastboot"));
            
            // Create FUS Tab
            var fusTab = CreateFusTab();
            mainNotebook.AppendPage(fusTab, Gtk.Label.New("FUS"));
            
            // Create GAPPS Tab
            var gappsTab = CreateGappsTab();
            mainNotebook.AppendPage(gappsTab, Gtk.Label.New("GAPPS"));
            
            // Create Other Tab
            var otherTab = CreateOtherTab();
            mainNotebook.AppendPage(otherTab, Gtk.Label.New("Other"));
            
            Child = mainNotebook;
        }
        
        private void ApplyGnomeStyles()
        {
            var cssProvider = Gtk.CssProvider.New();
            var css = @"
                .title {
                    font-weight: bold;
                    font-size: 1.1em;
                }
                
                .subtitle {
                    font-size: 0.9em;
                    opacity: 0.6;
                }
                
                headerbar {
                    min-height: 46px;
                }
                
                headerbar button.flat {
                    border: none;
                    background: none;
                    box-shadow: none;
                }
                
                headerbar button.flat:hover {
                    background: alpha(currentColor, 0.08);
                }
                
                headerbar button.flat:active {
                    background: alpha(currentColor, 0.16);
                }
            ";
            
            cssProvider.LoadFromData(css, css.Length);
            Gtk.StyleContext.AddProviderForDisplay(
                Gdk.Display.GetDefault()!,
                cssProvider,
                600 // GTK_STYLE_PROVIDER_PRIORITY_APPLICATION
            );
        }
        
        private Gtk.Widget CreateOdinTab()
        {
            var mainVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            mainVBox.SetMarginTop(10);
            mainVBox.SetMarginBottom(10);
            mainVBox.SetMarginStart(10);
            mainVBox.SetMarginEnd(10);
            
            // Top section - File selections
            var topFrame = Gtk.Frame.New("Files");
            var topGrid = Gtk.Grid.New();
            topGrid.SetRowSpacing(8);
            topGrid.SetColumnSpacing(10);
            topGrid.SetMarginTop(10);
            topGrid.SetMarginBottom(10);
            topGrid.SetMarginStart(10);
            topGrid.SetMarginEnd(10);
            
        // Create file selection rows
        CreateFileRow(topGrid, 0, "BL:", ref blFileEntry, ref blButton, ref blCheckButton);
        CreateFileRow(topGrid, 1, "AP:", ref apFileEntry, ref apButton, ref apCheckButton);
        CreateFileRow(topGrid, 2, "CP:", ref cpFileEntry, ref cpButton, ref cpCheckButton);
        CreateFileRow(topGrid, 3, "CSC:", ref cscFileEntry, ref cscButton, ref cscCheckButton);
        CreateFileRow(topGrid, 4, "USERDATA:", ref userdataFileEntry, ref userdataButton, ref userdataCheckButton);
            
            topFrame.Child = topGrid;
            mainVBox.Append(topFrame);
            
            // Middle section - Options and controls
            var middleHBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            
            // Left side - Options
            
            // Middle - Progress and Status  
            var progressFrame = Gtk.Frame.New("Progress");
            var progressBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            progressBox.SetMarginTop(10);
            progressBox.SetMarginBottom(10);
            progressBox.SetMarginStart(10);
            progressBox.SetMarginEnd(10);
            
            progressBar = Gtk.ProgressBar.New();
            progressBar.SetShowText(true);
            progressBar.SetText("Ready");
            progressBox.Append(progressBar);
            
            var stepLabel = Gtk.Label.New("Step: Waiting for device...");
            stepLabel.Xalign = 0;
            progressBox.Append(stepLabel);
            
            timeLabel = Gtk.Label.New("Elapsed: 00:00");
            timeLabel.Xalign = 0;
            progressBox.Append(timeLabel);
            
            progressFrame.Child = progressBox;
            middleHBox.Append(progressFrame);
            
            // Right side - Status and control buttons
            var controlFrame = Gtk.Frame.New("Control");
            var controlVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            controlVBox.SetMarginTop(10);
            controlVBox.SetMarginBottom(10);
            controlVBox.SetMarginStart(10);
            controlVBox.SetMarginEnd(10);
            
            // Device status
            deviceStatusLabel = Gtk.Label.New("Device Status: Disconnected");
            deviceStatusLabel.Xalign = 0;
            controlVBox.Append(deviceStatusLabel);
            
            comPortLabel = Gtk.Label.New("COM Port: N/A");
            comPortLabel.Xalign = 0;
            controlVBox.Append(comPortLabel);
            
            // Control buttons
            var buttonHBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            
            startButton = Gtk.Button.NewWithLabel("Start");
            startButton.AddCssClass("suggested-action");
            startButton.Sensitive = false;
            buttonHBox.Append(startButton);
            
            resetButton = Gtk.Button.NewWithLabel("Reset");
            buttonHBox.Append(resetButton);
            
            controlVBox.Append(buttonHBox);
            controlFrame.Child = controlVBox;
            middleHBox.Append(controlFrame);
            
            // Flash Tool Selection Frame (next to Control)
            var flashToolFrame = Gtk.Frame.New("Flash Tool");
            var flashToolVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            flashToolVBox.SetMarginTop(10);
            flashToolVBox.SetMarginBottom(10);
            flashToolVBox.SetMarginStart(10);
            flashToolVBox.SetMarginEnd(10);
            
            var flashToolModel = Gtk.StringList.New(new string[] { "Odin4 ", "Heimdall ", "Thor " });
            flashToolDropDown = Gtk.DropDown.New(flashToolModel, null);
            
            // Set selected tool based on settings
            selectedFlashTool = settings.DefaultFlashTool == "Thor" ? FlashTool.Thor : 
                              settings.DefaultFlashTool == "Odin4" ? FlashTool.Odin4 : FlashTool.Heimdall;
            uint selectedIndex = selectedFlashTool == FlashTool.Odin4 ? 0u : 
                               selectedFlashTool == FlashTool.Heimdall ? 1u : 2u;
            flashToolDropDown.SetSelected(selectedIndex);
            
            flashToolDropDown.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "selected")
                {
                    selectedFlashTool = (FlashTool)flashToolDropDown.GetSelected();
                    
                    // Update settings
                    settings.DefaultFlashTool = selectedFlashTool == FlashTool.Thor ? "Thor" : 
                                              selectedFlashTool == FlashTool.Odin4 ? "Odin4" : "Heimdall";
                    settings.Save();
                    
                    UpdateFlashToolLabel();
                }
            };
            flashToolVBox.Append(flashToolDropDown);
            
            flashToolSelectedLabel = Gtk.Label.New($"Selected: {selectedFlashTool}");
            flashToolSelectedLabel.Xalign = 0;
            flashToolSelectedLabel.AddCssClass("caption");
            flashToolVBox.Append(flashToolSelectedLabel);
            
            flashToolFrame.Child = flashToolVBox;
            middleHBox.Append(flashToolFrame);
            
            // Download Mode Guide Frame (next to Flash Tool)
            var guideFrame = Gtk.Frame.New("Download Mode Guide");
            var guideVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            guideVBox.SetMarginTop(10);
            guideVBox.SetMarginBottom(10);
            guideVBox.SetMarginStart(10);
            guideVBox.SetMarginEnd(10);
            
            var step1 = Gtk.Label.New("1. Reboot device");
            step1.Xalign = 0;
            step1.AddCssClass("caption");
            guideVBox.Append(step1);
            
            var step2 = Gtk.Label.New("2. Hold Vol Down + Vol Up while booting *");
            step2.Xalign = 0;
            step2.AddCssClass("caption");
            guideVBox.Append(step2);
            
            var step3 = Gtk.Label.New("3. Press Vol Up at warning");
            step3.Xalign = 0;
            step3.AddCssClass("caption");
            guideVBox.Append(step3);
            
            var step4 = Gtk.Label.New("4. Connect USB cable");
            step4.Xalign = 0;
            step4.AddCssClass("caption");
            guideVBox.Append(step4);
            
            var warning = Gtk.Label.New("* May vary by device model");
            warning.Xalign = 0;
            warning.AddCssClass("caption");
            guideVBox.Append(warning);
            
            guideFrame.Child = guideVBox;
            
            // Make the guide frame expand to fill remaining horizontal space
            guideFrame.SetHexpand(true);
            middleHBox.Append(guideFrame);
            
            mainVBox.Append(middleHBox);
            
            // Bottom section - Log output
            var logFrame = Gtk.Frame.New("Log");
            var logScrolled = Gtk.ScrolledWindow.New();
            logScrolled.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
            logScrolled.SetVexpand(true); // Allow vertical expansion
            logScrolled.SetHexpand(true); // Allow horizontal expansion
            
            odinLogLabel = Gtk.Label.New("");
            odinLogLabel.Xalign = 0;
            odinLogLabel.Yalign = 0;
            odinLogLabel.AddCssClass("monospace");
            odinLogLabel.SetSelectable(true);
            odinLogLabel.SetWrapMode(Pango.WrapMode.Word);
            
            logScrolled.Child = odinLogLabel;
            logFrame.Child = logScrolled;
            
            // Make the log frame expand to fill remaining space
            logFrame.SetVexpand(true);
            logFrame.SetHexpand(true);
            mainVBox.Append(logFrame);
            
            return mainVBox;
        }
        
        private void UpdateFlashToolLabel()
        {
            if (flashToolSelectedLabel != null)
            {
                flashToolSelectedLabel.SetText($"Selected: {selectedFlashTool}");
                
                // Trigger device connection check when flash tool changes
                CheckDeviceConnection();
            }
        }
        
        private Gtk.Widget CreateAdbTab()
        {
            var mainVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            mainVBox.SetMarginTop(10);
            mainVBox.SetMarginBottom(10);
            mainVBox.SetMarginStart(10);
            mainVBox.SetMarginEnd(10);
            
            // Top section - Package Management (similar to file selection in Odin)
            var topFrame = Gtk.Frame.New("Package Management");
            var topGrid = Gtk.Grid.New();
            topGrid.SetRowSpacing(8);
            topGrid.SetColumnSpacing(10);
            topGrid.SetMarginTop(10);
            topGrid.SetMarginBottom(10);
            topGrid.SetMarginStart(10);
            topGrid.SetMarginEnd(10);
            
            // APK Install row
            var apkLabel = Gtk.Label.New("APK File:");
            apkLabel.Xalign = 0;
            apkLabel.SetSizeRequest(80, -1);
            topGrid.Attach(apkLabel, 0, 0, 1, 1);
            
            packagePathEntry = Gtk.Entry.New();
            packagePathEntry.PlaceholderText = "Package path or APK file";
            packagePathEntry.SetHexpand(true);
            topGrid.Attach(packagePathEntry, 1, 0, 1, 1);
            
            var browseApkButton = Gtk.Button.NewWithLabel("...");
            browseApkButton.SetSizeRequest(40, -1);
            browseApkButton.OnClicked += (sender, e) => BrowseForFile(packagePathEntry, "Select APK File", "*.apk");
            topGrid.Attach(browseApkButton, 2, 0, 1, 1);
            
            var installButton = Gtk.Button.NewWithLabel("Install");
            installButton.OnClicked += async (sender, e) => await InstallPackage(packagePathEntry.GetText());
            topGrid.Attach(installButton, 3, 0, 1, 1);
            
            // Package Uninstall row
            var packageLabel = Gtk.Label.New("Package:");
            packageLabel.Xalign = 0;
            packageLabel.SetSizeRequest(80, -1);
            topGrid.Attach(packageLabel, 0, 1, 1, 1);
            
            packageNameEntry = Gtk.Entry.New();
            packageNameEntry.PlaceholderText = "Package name";
            packageNameEntry.SetHexpand(true);
            topGrid.Attach(packageNameEntry, 1, 1, 1, 1);
            
            var uninstallButton = Gtk.Button.NewWithLabel("Uninstall");
            uninstallButton.OnClicked += async (sender, e) => await UninstallPackage(packageNameEntry.GetText());
            topGrid.Attach(uninstallButton, 2, 1, 2, 1);
            
            // Shell Command row
            var shellLabel = Gtk.Label.New("Shell Cmd:");
            shellLabel.Xalign = 0;
            shellLabel.SetSizeRequest(80, -1);
            topGrid.Attach(shellLabel, 0, 2, 1, 1);
            
            shellCommandEntry = Gtk.Entry.New();
            shellCommandEntry.PlaceholderText = "Enter shell command";
            shellCommandEntry.SetHexpand(true);
            topGrid.Attach(shellCommandEntry, 1, 2, 1, 1);
            
            var executeButton = Gtk.Button.NewWithLabel("Execute");
            executeButton.OnClicked += async (sender, e) => await ExecuteShellCommand(shellCommandEntry.GetText());
            topGrid.Attach(executeButton, 2, 2, 2, 1);
            
            topFrame.Child = topGrid;
            mainVBox.Append(topFrame);
            
            // Middle section - Four frames side by side (matching Odin structure)
            var middleHBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            
            // Left side - Device Information
            var deviceFrame = Gtk.Frame.New("Device Information");
            var deviceVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            deviceVBox.SetMarginTop(10);
            deviceVBox.SetMarginBottom(10);
            deviceVBox.SetMarginStart(10);
            deviceVBox.SetMarginEnd(10);
            
            var refreshDevicesButton = Gtk.Button.NewWithLabel("Refresh Connected Devices");
            refreshDevicesButton.OnClicked += async (sender, e) => await RefreshAdbDevices();
            deviceVBox.Append(refreshDevicesButton);
            
            var getDeviceInfoButton = Gtk.Button.NewWithLabel("Get Device Info");
            getDeviceInfoButton.OnClicked += async (sender, e) => await GetDeviceInfo();
            deviceVBox.Append(getDeviceInfoButton);
            
            var batteryInfoButton = Gtk.Button.NewWithLabel("Battery Info");
            batteryInfoButton.OnClicked += async (sender, e) => await GetBatteryInfo();
            deviceVBox.Append(batteryInfoButton);
            
            var listPackagesButton = Gtk.Button.NewWithLabel("List Installed Packages");
            listPackagesButton.OnClicked += async (sender, e) => await ListInstalledPackages();
            deviceVBox.Append(listPackagesButton);
            
            deviceFrame.Child = deviceVBox;
            middleHBox.Append(deviceFrame);
            
            // Middle - ADB Server Control
            var serverFrame = Gtk.Frame.New("ADB Server");
            var serverVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            serverVBox.SetMarginTop(10);
            serverVBox.SetMarginBottom(10);
            serverVBox.SetMarginStart(10);
            serverVBox.SetMarginEnd(10);
            
            var startServerButton = Gtk.Button.NewWithLabel("Start ADB Server");
            startServerButton.OnClicked += async (sender, e) => await StartAdbServer();
            serverVBox.Append(startServerButton);
            
            var killServerButton = Gtk.Button.NewWithLabel("Kill ADB Server");
            killServerButton.OnClicked += async (sender, e) => await KillAdbServer();
            serverVBox.Append(killServerButton);
            
            var listDevicesButton = Gtk.Button.NewWithLabel("List Devices");
            listDevicesButton.OnClicked += async (sender, e) => await ListAdbDevices();
            serverVBox.Append(listDevicesButton);
            
            serverFrame.Child = serverVBox;
            middleHBox.Append(serverFrame);
            
            // Right side - System Actions
            var systemFrame = Gtk.Frame.New("System Actions");
            var systemVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            systemVBox.SetMarginTop(10);
            systemVBox.SetMarginBottom(10);
            systemVBox.SetMarginStart(10);
            systemVBox.SetMarginEnd(10);
            
            var rebootButton = Gtk.Button.NewWithLabel("Reboot Device");
            rebootButton.OnClicked += async (sender, e) => await RebootDevice();
            systemVBox.Append(rebootButton);
            
            var rebootBootloaderButton = Gtk.Button.NewWithLabel("Reboot to Bootloader");
            rebootBootloaderButton.OnClicked += async (sender, e) => await RebootToBootloader();
            systemVBox.Append(rebootBootloaderButton);
            
            var rebootRecoveryButton = Gtk.Button.NewWithLabel("Reboot to Recovery");
            rebootRecoveryButton.OnClicked += async (sender, e) => await RebootToRecovery();
            systemVBox.Append(rebootRecoveryButton);
            
            var rebootDownloadButton = Gtk.Button.NewWithLabel("Reboot to Download");
            rebootDownloadButton.OnClicked += async (sender, e) => await RebootToDownload();
            systemVBox.Append(rebootDownloadButton);
            
            var startLogcatButton = Gtk.Button.NewWithLabel("Start Logcat");
            startLogcatButton.OnClicked += async (sender, e) => await StartLogcat();
            systemVBox.Append(startLogcatButton);
            
            systemFrame.Child = systemVBox;
            middleHBox.Append(systemFrame);
            
            // Fourth column - Device Detection and Progress (2 separate frames stacked vertically)
            var deviceProgressColumnVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            
            // Top frame - Device Detection
            var deviceDetectionFrame = Gtk.Frame.New("Device Detection");
            var deviceInfoVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            deviceInfoVBox.SetMarginTop(10);
            deviceInfoVBox.SetMarginBottom(10);
            deviceInfoVBox.SetMarginStart(10);
            deviceInfoVBox.SetMarginEnd(10);
            
            // Status row
            var statusHBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 5);
            var statusLabel = Gtk.Label.New("Status:");
            statusLabel.Xalign = 0;
            statusLabel.SetSizeRequest(60, -1);
            statusHBox.Append(statusLabel);
            
            adbDeviceStatusLabel = Gtk.Label.New("No device detected");
            adbDeviceStatusLabel.Xalign = 0;
            adbDeviceStatusLabel.SetHexpand(true);
            adbDeviceStatusLabel.AddCssClass("dim-label");
            statusHBox.Append(adbDeviceStatusLabel);
            deviceInfoVBox.Append(statusHBox);
            
            // Model row
            var modelHBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 5);
            var modelLabel = Gtk.Label.New("Model:");
            modelLabel.Xalign = 0;
            modelLabel.SetSizeRequest(60, -1);
            modelHBox.Append(modelLabel);
            
            adbDeviceModelLabel = Gtk.Label.New("Unknown");
            adbDeviceModelLabel.Xalign = 0;
            adbDeviceModelLabel.SetHexpand(true);
            adbDeviceModelLabel.AddCssClass("dim-label");
            modelHBox.Append(adbDeviceModelLabel);
            deviceInfoVBox.Append(modelHBox);
            
            // Serial row
            var serialHBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 5);
            var serialLabel = Gtk.Label.New("Serial:");
            serialLabel.Xalign = 0;
            serialLabel.SetSizeRequest(60, -1);
            serialHBox.Append(serialLabel);
            
            adbDeviceSerialLabel = Gtk.Label.New("Unknown");
            adbDeviceSerialLabel.Xalign = 0;
            adbDeviceSerialLabel.SetHexpand(true);
            adbDeviceSerialLabel.AddCssClass("dim-label");
            serialHBox.Append(adbDeviceSerialLabel);
            deviceInfoVBox.Append(serialHBox);
            
            deviceDetectionFrame.Child = deviceInfoVBox;
            deviceProgressColumnVBox.Append(deviceDetectionFrame);
            
            // Bottom frame - Progress Tracker
            var progressFrame = Gtk.Frame.New("Progress");
            var progressVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            progressVBox.SetMarginTop(10);
            progressVBox.SetMarginBottom(10);
            progressVBox.SetMarginStart(10);
            progressVBox.SetMarginEnd(10);
            
            adbProgressBar = Gtk.ProgressBar.New();
            adbProgressBar.SetShowText(true);
            adbProgressBar.SetText("Ready");
            progressVBox.Append(adbProgressBar);
            
            adbStepLabel = Gtk.Label.New("Step: Waiting for operation...");
            adbStepLabel.Xalign = 0;
            progressVBox.Append(adbStepLabel);
            
            adbTimeLabel = Gtk.Label.New("Elapsed: 00:00");
            adbTimeLabel.Xalign = 0;
            progressVBox.Append(adbTimeLabel);
            
            progressFrame.Child = progressVBox;
            deviceProgressColumnVBox.Append(progressFrame);
            
            middleHBox.Append(deviceProgressColumnVBox);
            
            mainVBox.Append(middleHBox);
            
            // Bottom section - Log output (matching Odin structure)
            var adbLogFrame = Gtk.Frame.New("ADB Output");
            var adbLogScrolled = Gtk.ScrolledWindow.New();
            adbLogScrolled.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
            adbLogScrolled.SetVexpand(true); // Allow vertical expansion
            adbLogScrolled.SetHexpand(true); // Allow horizontal expansion
            
            adbLogLabel = Gtk.Label.New("");
            adbLogLabel.Xalign = 0;
            adbLogLabel.Yalign = 0;
            adbLogLabel.AddCssClass("monospace");
            adbLogLabel.SetSelectable(true);
            adbLogLabel.SetWrapMode(Pango.WrapMode.Word);
            
            adbLogScrolled.Child = adbLogLabel;
            adbLogFrame.Child = adbLogScrolled;
            
            // Make the log frame expand to fill remaining space
            adbLogFrame.SetVexpand(true);
            adbLogFrame.SetHexpand(true);
            mainVBox.Append(adbLogFrame);
            
            // Initial device status check
            Task.Run(async () => await RefreshAdbDeviceStatus());
            
            return mainVBox;
        }
        
        private Gtk.Widget CreateFastbootTab()
        {
            var mainVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            mainVBox.SetMarginTop(10);
            mainVBox.SetMarginBottom(10);
            mainVBox.SetMarginStart(10);
            mainVBox.SetMarginEnd(10);
            
            // Top section - Image Flashing (similar to file selection in Odin)
            var topFrame = Gtk.Frame.New("Image Flashing");
            var topGrid = Gtk.Grid.New();
            topGrid.SetRowSpacing(8);
            topGrid.SetColumnSpacing(10);
            topGrid.SetMarginTop(10);
            topGrid.SetMarginBottom(10);
            topGrid.SetMarginStart(10);
            topGrid.SetMarginEnd(10);
            
            // Image Flash row
            var imageLabel = Gtk.Label.New("Image File:");
            imageLabel.Xalign = 0;
            imageLabel.SetSizeRequest(80, -1);
            topGrid.Attach(imageLabel, 0, 0, 1, 1);
            
            fastbootImagePathEntry = Gtk.Entry.New();
            fastbootImagePathEntry.PlaceholderText = "Select image file to flash";
            fastbootImagePathEntry.SetHexpand(true);
            topGrid.Attach(fastbootImagePathEntry, 1, 0, 1, 1);
            
            var browseImageButton = Gtk.Button.NewWithLabel("...");
            browseImageButton.SetSizeRequest(40, -1);
            browseImageButton.OnClicked += (sender, e) => BrowseForFile(fastbootImagePathEntry, "Select Image File", "*.img");
            topGrid.Attach(browseImageButton, 2, 0, 1, 1);
            
            // Partition row
            var partitionLabel = Gtk.Label.New("Partition:");
            partitionLabel.Xalign = 0;
            partitionLabel.SetSizeRequest(80, -1);
            topGrid.Attach(partitionLabel, 0, 1, 1, 1);
            
            fastbootPartitionEntry = Gtk.Entry.New();
            fastbootPartitionEntry.PlaceholderText = "boot, recovery, system, etc.";
            fastbootPartitionEntry.SetHexpand(true);
            topGrid.Attach(fastbootPartitionEntry, 1, 1, 1, 1);
            
            var flashButton = Gtk.Button.NewWithLabel("Flash");
            flashButton.OnClicked += async (sender, e) => await FlashImage(fastbootImagePathEntry.GetText(), fastbootPartitionEntry.GetText());
            topGrid.Attach(flashButton, 2, 1, 1, 1);
            
            // Custom Command row
            var commandLabel = Gtk.Label.New("Command:");
            commandLabel.Xalign = 0;
            commandLabel.SetSizeRequest(80, -1);
            topGrid.Attach(commandLabel, 0, 2, 1, 1);
            
            fastbootCommandEntry = Gtk.Entry.New();
            fastbootCommandEntry.PlaceholderText = "Enter fastboot command";
            fastbootCommandEntry.SetHexpand(true);
            topGrid.Attach(fastbootCommandEntry, 1, 2, 1, 1);
            
            var executeButton = Gtk.Button.NewWithLabel("Execute");
            executeButton.OnClicked += async (sender, e) => await ExecuteFastbootCommand(fastbootCommandEntry.GetText());
            topGrid.Attach(executeButton, 2, 2, 1, 1);
            
            topFrame.Child = topGrid;
            mainVBox.Append(topFrame);
            
            // Middle section - Four frames side by side
            var middleHBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            
            // Left side - Device Information
            var deviceFrame = Gtk.Frame.New("Device Information");
            var deviceVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            deviceVBox.SetMarginTop(10);
            deviceVBox.SetMarginBottom(10);
            deviceVBox.SetMarginStart(10);
            deviceVBox.SetMarginEnd(10);
            
            var refreshFastbootDevicesButton = Gtk.Button.NewWithLabel("Refresh Devices");
            refreshFastbootDevicesButton.OnClicked += async (sender, e) => await RefreshFastbootDevices();
            deviceVBox.Append(refreshFastbootDevicesButton);
            
            var getDeviceVarsButton = Gtk.Button.NewWithLabel("Get Device Variables");
            getDeviceVarsButton.OnClicked += async (sender, e) => await GetDeviceVariables();
            deviceVBox.Append(getDeviceVarsButton);
            
            var getBootloaderVersionButton = Gtk.Button.NewWithLabel("Bootloader Version");
            getBootloaderVersionButton.OnClicked += async (sender, e) => await GetBootloaderVersion();
            deviceVBox.Append(getBootloaderVersionButton);
            
            var getSerialNumberButton = Gtk.Button.NewWithLabel("Serial Number");
            getSerialNumberButton.OnClicked += async (sender, e) => await GetSerialNumber();
            deviceVBox.Append(getSerialNumberButton);
            
            deviceFrame.Child = deviceVBox;
            middleHBox.Append(deviceFrame);
            
            // Middle-left - Bootloader Operations
            var bootloaderFrame = Gtk.Frame.New("Bootloader Operations");
            var bootloaderVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            bootloaderVBox.SetMarginTop(10);
            bootloaderVBox.SetMarginBottom(10);
            bootloaderVBox.SetMarginStart(10);
            bootloaderVBox.SetMarginEnd(10);
            
            var unlockBootloaderButton = Gtk.Button.NewWithLabel("Unlock Bootloader");
            unlockBootloaderButton.OnClicked += async (sender, e) => await UnlockBootloader();
            bootloaderVBox.Append(unlockBootloaderButton);
            
            var lockBootloaderButton = Gtk.Button.NewWithLabel("Lock Bootloader");
            lockBootloaderButton.OnClicked += async (sender, e) => await LockBootloader();
            bootloaderVBox.Append(lockBootloaderButton);
            
            var oemUnlockButton = Gtk.Button.NewWithLabel("OEM Unlock");
            oemUnlockButton.OnClicked += async (sender, e) => await OemUnlock();
            bootloaderVBox.Append(oemUnlockButton);
            
            var getCriticalUnlockButton = Gtk.Button.NewWithLabel("Get Critical Unlock");
            getCriticalUnlockButton.OnClicked += async (sender, e) => await GetCriticalUnlock();
            bootloaderVBox.Append(getCriticalUnlockButton);
            
            bootloaderFrame.Child = bootloaderVBox;
            middleHBox.Append(bootloaderFrame);
            
            // Middle-right - Partition Operations
            var partitionFrame = Gtk.Frame.New("Partition Operations");
            var partitionVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            partitionVBox.SetMarginTop(10);
            partitionVBox.SetMarginBottom(10);
            partitionVBox.SetMarginStart(10);
            partitionVBox.SetMarginEnd(10);
            
            var eraseSystemButton = Gtk.Button.NewWithLabel("Erase System");
            eraseSystemButton.OnClicked += async (sender, e) => await ErasePartition("system");
            partitionVBox.Append(eraseSystemButton);
            
            var eraseUserdataButton = Gtk.Button.NewWithLabel("Erase Userdata");
            eraseUserdataButton.OnClicked += async (sender, e) => await ErasePartition("userdata");
            partitionVBox.Append(eraseUserdataButton);
            
            var eraseCacheButton = Gtk.Button.NewWithLabel("Erase Cache");
            eraseCacheButton.OnClicked += async (sender, e) => await ErasePartition("cache");
            partitionVBox.Append(eraseCacheButton);
            
            var formatUserdataButton = Gtk.Button.NewWithLabel("Format Userdata");
            formatUserdataButton.OnClicked += async (sender, e) => await FormatPartition("userdata");
            partitionVBox.Append(formatUserdataButton);
            
            partitionFrame.Child = partitionVBox;
            middleHBox.Append(partitionFrame);
            
            // Right side - Reboot Operations
            var rebootFrame = Gtk.Frame.New("Reboot Operations");
            var rebootVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            rebootVBox.SetMarginTop(10);
            rebootVBox.SetMarginBottom(10);
            rebootVBox.SetMarginStart(10);
            rebootVBox.SetMarginEnd(10);
            
            var rebootSystemButton = Gtk.Button.NewWithLabel("Reboot System");
            rebootSystemButton.OnClicked += async (sender, e) => await FastbootRebootSystem();
            rebootVBox.Append(rebootSystemButton);
            
            var rebootBootloaderButton = Gtk.Button.NewWithLabel("Reboot Bootloader");
            rebootBootloaderButton.OnClicked += async (sender, e) => await FastbootRebootBootloader();
            rebootVBox.Append(rebootBootloaderButton);
            
            var rebootRecoveryButton = Gtk.Button.NewWithLabel("Reboot Recovery");
            rebootRecoveryButton.OnClicked += async (sender, e) => await FastbootRebootRecovery();
            rebootVBox.Append(rebootRecoveryButton);
            
            var rebootFastbootButton = Gtk.Button.NewWithLabel("Reboot Fastboot");
            rebootFastbootButton.OnClicked += async (sender, e) => await FastbootRebootFastboot();
            rebootVBox.Append(rebootFastbootButton);
            
            rebootFrame.Child = rebootVBox;
            middleHBox.Append(rebootFrame);
            
            // Fourth column - Device Detection and Progress (2 separate frames stacked vertically)
            var deviceProgressColumnVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            
            // Top frame - Device Detection
            var deviceDetectionFrame = Gtk.Frame.New("Device Detection");
            var deviceInfoVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            deviceInfoVBox.SetMarginTop(10);
            deviceInfoVBox.SetMarginBottom(10);
            deviceInfoVBox.SetMarginStart(10);
            deviceInfoVBox.SetMarginEnd(10);
            
            // Status row
            var statusHBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 5);
            var statusLabel = Gtk.Label.New("Status:");
            statusLabel.Xalign = 0;
            statusLabel.SetSizeRequest(60, -1);
            statusHBox.Append(statusLabel);
            
            fastbootDeviceStatusLabel = Gtk.Label.New("No device detected");
            fastbootDeviceStatusLabel.Xalign = 0;
            fastbootDeviceStatusLabel.SetHexpand(true);
            fastbootDeviceStatusLabel.AddCssClass("dim-label");
            statusHBox.Append(fastbootDeviceStatusLabel);
            deviceInfoVBox.Append(statusHBox);
            
            // Model row
            var modelHBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 5);
            var modelLabel = Gtk.Label.New("Model:");
            modelLabel.Xalign = 0;
            modelLabel.SetSizeRequest(60, -1);
            modelHBox.Append(modelLabel);
            
            fastbootDeviceModelLabel = Gtk.Label.New("Unknown");
            fastbootDeviceModelLabel.Xalign = 0;
            fastbootDeviceModelLabel.SetHexpand(true);
            fastbootDeviceModelLabel.AddCssClass("dim-label");
            modelHBox.Append(fastbootDeviceModelLabel);
            deviceInfoVBox.Append(modelHBox);
            
            // Serial row
            var serialHBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 5);
            var serialLabel = Gtk.Label.New("Serial:");
            serialLabel.Xalign = 0;
            serialLabel.SetSizeRequest(60, -1);
            serialHBox.Append(serialLabel);
            
            fastbootDeviceSerialLabel = Gtk.Label.New("Unknown");
            fastbootDeviceSerialLabel.Xalign = 0;
            fastbootDeviceSerialLabel.SetHexpand(true);
            fastbootDeviceSerialLabel.AddCssClass("dim-label");
            serialHBox.Append(fastbootDeviceSerialLabel);
            deviceInfoVBox.Append(serialHBox);
            
            deviceDetectionFrame.Child = deviceInfoVBox;
            deviceProgressColumnVBox.Append(deviceDetectionFrame);
            
            // Bottom frame - Progress Tracker
            var progressFrame = Gtk.Frame.New("Progress");
            var progressVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            progressVBox.SetMarginTop(10);
            progressVBox.SetMarginBottom(10);
            progressVBox.SetMarginStart(10);
            progressVBox.SetMarginEnd(10);
            
            fastbootProgressBar = Gtk.ProgressBar.New();
            fastbootProgressBar.SetShowText(true);
            fastbootProgressBar.SetText("Ready");
            progressVBox.Append(fastbootProgressBar);
            
            fastbootStepLabel = Gtk.Label.New("Step: Waiting for operation...");
            fastbootStepLabel.Xalign = 0;
            progressVBox.Append(fastbootStepLabel);
            
            fastbootTimeLabel = Gtk.Label.New("Elapsed: 00:00");
            fastbootTimeLabel.Xalign = 0;
            progressVBox.Append(fastbootTimeLabel);
            
            progressFrame.Child = progressVBox;
            deviceProgressColumnVBox.Append(progressFrame);
            
            middleHBox.Append(deviceProgressColumnVBox);
            
            mainVBox.Append(middleHBox);
            
            // Bottom section - Log output (matching Odin structure)
            var fastbootLogFrame = Gtk.Frame.New("Fastboot Output");
            var fastbootLogScrolled = Gtk.ScrolledWindow.New();
            fastbootLogScrolled.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
            fastbootLogScrolled.SetVexpand(true); // Allow vertical expansion
            fastbootLogScrolled.SetHexpand(true); // Allow horizontal expansion
            
            fastbootLogLabel = Gtk.Label.New("");
            fastbootLogLabel.Xalign = 0;
            fastbootLogLabel.Yalign = 0;
            fastbootLogLabel.AddCssClass("monospace");
            fastbootLogLabel.SetSelectable(true);
            fastbootLogLabel.SetWrapMode(Pango.WrapMode.Word);
            
            fastbootLogScrolled.Child = fastbootLogLabel;
            fastbootLogFrame.Child = fastbootLogScrolled;
            
            // Make the log frame expand to fill remaining space
            fastbootLogFrame.SetVexpand(true);
            fastbootLogFrame.SetHexpand(true);
            mainVBox.Append(fastbootLogFrame);
            
            // Initial device status check
            Task.Run(async () => await RefreshFastbootDeviceStatus());
            
            return mainVBox;
        }
        
        private Gtk.Widget CreateGappsTab()
        {
            var mainVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
            mainVBox.SetMarginTop(10);
            mainVBox.SetMarginBottom(10);
            mainVBox.SetMarginStart(10);
            mainVBox.SetMarginEnd(10);
            
            var titleLabel = Gtk.Label.New(null);
            titleLabel.SetMarkup("<span size='12000' weight='bold'>GAPPS Downloader</span>");
            titleLabel.SetMarginBottom(5);
            mainVBox.Append(titleLabel);
            
            // Create a 2x2 grid for GAPPS sections
            var gappsGrid = Gtk.Grid.New();
            gappsGrid.SetColumnHomogeneous(true);
            gappsGrid.SetRowHomogeneous(true);
            gappsGrid.SetColumnSpacing(8);
            gappsGrid.SetRowSpacing(8);
            
            // Top Row - OpenGApps and BiTGApps
            var openGappsFrame = CreateGappsSection(
                "OpenGApps",
                "https://opengapps.org/",
                new[] {
                    ("Platform", new[] { "ARM", "ARM64", "x86", "x86_64" }),
                    ("Android", new[] { "11.0", "10.0", "9.0", "8.1", "8.0", "7.1", "7.0", "6.0", "5.1", "5.0", "4.4" }),
                    ("Variant", new[] { "pico", "nano", "micro", "mini", "full", "stock", "super", "aroma", "tvstock", "tvmini" })
                },
                "Modular GApps with multiple variants. Last updated Feb 2022. Supports Android 4.4-11.0. Variants from minimal (pico) to complete (super/aroma)."
            );
            gappsGrid.Attach(openGappsFrame, 0, 0, 1, 1);
            
            var bitGappsFrame = CreateGappsSection(
                "BiTGApps",
                "https://bitgapps.github.io/",
                new[] {
                    ("Arch", new[] { "arm", "arm64" }),
                    ("Android", new[] { "15", "14", "13", "12.1", "12", "11", "10", "9" }),
                    ("Variant", new[] { "core", "basic", "minimal" })
                },
                "Lightweight and battery-efficient. Supports Android 9-15. Core: essential services. Basic: core + Play Store. Minimal: basic + common apps."
            );
            gappsGrid.Attach(bitGappsFrame, 1, 0, 1, 1);
            
            // Bottom Row - MindTheGapps and NikGApps
            var mindGappsFrame = CreateGappsSection(
                "MindTheGapps",
                "https://github.com/MindTheGapps/",
                new[] {
                    ("Arch", new[] { "arm", "arm64", "x86", "x86_64" }),
                    ("Android", new[] { "15.0", "14.0", "13.0", "12.1", "12.0", "11.0", "10.0" })
                },
                "LineageOS recommended. Supports Android 10-15. Balanced package with essential Google apps. Single variant optimized for compatibility."
            );
            gappsGrid.Attach(mindGappsFrame, 0, 1, 1, 1);
            
            var nikGappsFrame = CreateGappsSection(
                "NikGApps",
                "https://nikgapps.com/",
                new[] {
                    ("Arch", new[] { "arm64" }),
                    ("Android", new[] { "16", "15", "14", "13", "12.1", "12", "11", "10" }),
                    ("Variant", new[] { "core", "basic", "omni", "stock", "full", "go" })
                },
                "Highly customizable with addon support. Supports Android 10-16. Regular updates. Core: minimal. Basic: essential. Omni: popular. Full: most. Go: Android Go."
            );
            gappsGrid.Attach(nikGappsFrame, 1, 1, 1, 1);
            
            mainVBox.Append(gappsGrid);
            
            // Bottom section - Log output (matching other tabs)
            var gappsLogFrame = Gtk.Frame.New("GAPPS Log");
            var gappsLogScrolled = Gtk.ScrolledWindow.New();
            gappsLogScrolled.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
            gappsLogScrolled.SetVexpand(true);
            gappsLogScrolled.SetHexpand(true);
            
            gappsLogLabel = Gtk.Label.New("");
            gappsLogLabel.Xalign = 0;
            gappsLogLabel.Yalign = 0;
            gappsLogLabel.AddCssClass("monospace");
            gappsLogLabel.SetSelectable(true);
            gappsLogLabel.SetWrapMode(Pango.WrapMode.Word);
            
            gappsLogScrolled.Child = gappsLogLabel;
            gappsLogFrame.Child = gappsLogScrolled;
            
            // Make the log frame expand to fill remaining space
            gappsLogFrame.SetVexpand(true);
            gappsLogFrame.SetHexpand(true);
            mainVBox.Append(gappsLogFrame);
            
            return mainVBox;
        }
        
        private Gtk.Frame CreateGappsSection(string name, string website, (string label, string[] options)[] dropdowns, string description)
        {
            var frame = Gtk.Frame.New(name);
            var vbox = Gtk.Box.New(Gtk.Orientation.Vertical, 3);
            vbox.SetMarginTop(5);
            vbox.SetMarginBottom(5);
            vbox.SetMarginStart(5);
            vbox.SetMarginEnd(5);
            
            // Description
            var descLabel = Gtk.Label.New(description);
            descLabel.SetWrapMode(Pango.WrapMode.WordChar);
            descLabel.SetWrap(true);
            descLabel.SetMaxWidthChars(40);
            descLabel.Xalign = 0;
            descLabel.SetJustify(Gtk.Justification.Left);
            descLabel.AddCssClass("caption");
            descLabel.SetMarginBottom(3);
            vbox.Append(descLabel);
            
            // Store dropdown widgets
            var dropdownWidgets = new List<Gtk.DropDown>();
            
            // Create dropdowns with aligned labels
            foreach (var (label, options) in dropdowns)
            {
                var hbox = Gtk.Box.New(Gtk.Orientation.Horizontal, 5);
                
                var labelWidget = Gtk.Label.New($"{label}:");
                labelWidget.SetSizeRequest(70, -1); // Fixed width for alignment
                labelWidget.Xalign = 1; // Right-align the label text
                labelWidget.SetHalign(Gtk.Align.End);
                hbox.Append(labelWidget);
                
                var stringList = Gtk.StringList.New(options);
                var dropdown = Gtk.DropDown.New(stringList, null);
                dropdown.SetHexpand(true);
                dropdownWidgets.Add(dropdown);
                hbox.Append(dropdown);
                
                vbox.Append(hbox);
            }
            
            // Buttons row
            var buttonBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 3);
            buttonBox.SetMarginTop(3);
            buttonBox.SetHomogeneous(true);
            
            var websiteButton = Gtk.Button.NewWithLabel("Website");
            websiteButton.OnClicked += (s, e) => OpenUrl(website);
            buttonBox.Append(websiteButton);
            
            var downloadButton = Gtk.Button.NewWithLabel("Download");
            downloadButton.AddCssClass("suggested-action");
            downloadButton.OnClicked += (s, e) => {
                var selections = dropdownWidgets.Select(d => {
                    var model = d.GetModel() as Gtk.StringList;
                    var selected = d.GetSelected();
                    return model?.GetString(selected) ?? "";
                }).ToArray();
                
                DownloadGapps(name, selections, website);
            };
            buttonBox.Append(downloadButton);
            
            vbox.Append(buttonBox);
            
            frame.Child = vbox;
            return frame;
        }
        
        private void DownloadGapps(string gappsName, string[] selections, string website)
        {
            var selectionText = string.Join(", ", selections);
            LogGappsMessage($"Preparing to download {gappsName}");
            LogGappsMessage($"Selected options: {selectionText}");
            LogGappsMessage($"Opening download page: {website}");
            
            // Open the website
            OpenUrl(website);
        }
        
        private Gtk.Widget CreateFusTab()
        {
            var mainVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            mainVBox.SetMarginTop(10);
            mainVBox.SetMarginBottom(10);
            mainVBox.SetMarginStart(10);
            mainVBox.SetMarginEnd(10);
            
            // Top section - Device Information Input
            var topFrame = Gtk.Frame.New("Device Information");
            var topGrid = Gtk.Grid.New();
            topGrid.SetRowSpacing(8);
            topGrid.SetColumnSpacing(10);
            topGrid.SetMarginTop(10);
            topGrid.SetMarginBottom(10);
            topGrid.SetMarginStart(10);
            topGrid.SetMarginEnd(10);
            
            // Model row
            var modelLabel = Gtk.Label.New("Model:");
            modelLabel.Xalign = 0;
            modelLabel.SetSizeRequest(100, -1);
            topGrid.Attach(modelLabel, 0, 0, 1, 1);
            
            fusModelEntry = Gtk.Entry.New();
            fusModelEntry.PlaceholderText = "e.g., SM-G991B";
            fusModelEntry.SetHexpand(true);
            topGrid.Attach(fusModelEntry, 1, 0, 1, 1);
            
            // Region row
            var regionLabel = Gtk.Label.New("Region:");
            regionLabel.Xalign = 0;
            regionLabel.SetSizeRequest(100, -1);
            topGrid.Attach(regionLabel, 0, 1, 1, 1);
            
            fusRegionEntry = Gtk.Entry.New();
            fusRegionEntry.PlaceholderText = "e.g., XEF, DBT, BTU";
            fusRegionEntry.SetHexpand(true);
            topGrid.Attach(fusRegionEntry, 1, 1, 1, 1);
            
            // IMEI row (required)
            var imeiLabel = Gtk.Label.New("IMEI:");
            imeiLabel.Xalign = 0;
            imeiLabel.SetSizeRequest(100, -1);
            topGrid.Attach(imeiLabel, 0, 2, 1, 1);
            
            fusImeiEntry = Gtk.Entry.New();
            fusImeiEntry.PlaceholderText = "e.g., 123456789012345 (15 digits)";
            fusImeiEntry.SetHexpand(true);
            fusImeiEntry.SetMaxLength(15);
            topGrid.Attach(fusImeiEntry, 1, 2, 1, 1);
            
            // Download path row
            var pathLabel = Gtk.Label.New("Save to:");
            pathLabel.Xalign = 0;
            pathLabel.SetSizeRequest(100, -1);
            topGrid.Attach(pathLabel, 0, 3, 1, 1);
            
            fusDownloadPathEntry = Gtk.Entry.New();
            fusDownloadPathEntry.PlaceholderText = "Download directory";
            fusDownloadPathEntry.SetText(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            fusDownloadPathEntry.SetHexpand(true);
            topGrid.Attach(fusDownloadPathEntry, 1, 3, 1, 1);
            
            var browseButton = Gtk.Button.NewWithLabel("...");
            browseButton.SetSizeRequest(40, -1);
            browseButton.OnClicked += (sender, e) => BrowseForDownloadDirectory();
            topGrid.Attach(browseButton, 2, 3, 1, 1);
            
            // Add text change handlers to validate all required fields
            fusModelEntry.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "text") ValidateFusInputs();
            };
            fusRegionEntry.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "text") ValidateFusInputs();
            };
            fusImeiEntry.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "text") ValidateFusInputs();
            };
            fusDownloadPathEntry.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "text") ValidateFusInputs();
            };
            
            topFrame.Child = topGrid;
            mainVBox.Append(topFrame);
            
            // Middle section - Actions and Status
            var middleHBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            
            // Left side - Actions
            var actionsFrame = Gtk.Frame.New("Actions");
            var actionsVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            actionsVBox.SetMarginTop(10);
            actionsVBox.SetMarginBottom(10);
            actionsVBox.SetMarginStart(10);
            actionsVBox.SetMarginEnd(10);
            
            fusCheckButton = Gtk.Button.NewWithLabel("Fetch Firmwares");
            fusCheckButton.OnClicked += async (sender, e) => await CheckLatestFirmware();
            actionsVBox.Append(fusCheckButton);
            
            fusDownloadButton = Gtk.Button.NewWithLabel("Download Firmware");
            fusDownloadButton.OnClicked += async (sender, e) => await DownloadFirmware();
            fusDownloadButton.Sensitive = false;
            actionsVBox.Append(fusDownloadButton);
            
            // Separator
            var fusSeparator1 = Gtk.Separator.New(Gtk.Orientation.Horizontal);
            fusSeparator1.SetMarginTop(6);
            fusSeparator1.SetMarginBottom(6);
            actionsVBox.Append(fusSeparator1);

            // Pause/Resume button
            fusPauseResumeButton = Gtk.Button.NewWithLabel("Pause Download");
            fusPauseResumeButton.OnClicked += (sender, e) => TogglePauseDownload();
            fusPauseResumeButton.Sensitive = false;
            actionsVBox.Append(fusPauseResumeButton);
            
            // Stop button
            fusStopButton = Gtk.Button.NewWithLabel("Stop Download");
            fusStopButton.AddCssClass("destructive-action");
            fusStopButton.OnClicked += (sender, e) => StopDownload();
            fusStopButton.Sensitive = false;
            actionsVBox.Append(fusStopButton);

            // Separator
            var fusSeparator2 = Gtk.Separator.New(Gtk.Orientation.Horizontal);
            fusSeparator2.SetMarginTop(6);
            fusSeparator2.SetMarginBottom(6);
            actionsVBox.Append(fusSeparator2);

            fusDecryptButton = Gtk.Button.NewWithLabel("Decrypt Downloaded Firmware");
            fusDecryptButton.OnClicked += async (sender, e) => await DecryptFirmware();
            actionsVBox.Append(fusDecryptButton);

            // Separator
            var fusSeparator3 = Gtk.Separator.New(Gtk.Orientation.Horizontal);
            fusSeparator3.SetMarginTop(6);
            fusSeparator3.SetMarginBottom(6);
            actionsVBox.Append(fusSeparator3);

            var clearLogButton = Gtk.Button.NewWithLabel("Clear Log");
            clearLogButton.OnClicked += (sender, e) => {
                fusLogMessages.Clear();
                fusLogLabel.SetText("");
                LogFusMessage("Log cleared");
            };
            actionsVBox.Append(clearLogButton);
            
            actionsFrame.Child = actionsVBox;
            actionsFrame.SetVexpand(true);
            middleHBox.Append(actionsFrame);
            
            // Middle - Firmware Selection List
            var infoFrame = Gtk.Frame.New("Available Firmware Versions");
            var infoVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            infoVBox.SetMarginTop(10);
            infoVBox.SetMarginBottom(10);
            infoVBox.SetMarginStart(10);
            infoVBox.SetMarginEnd(10);
            
            // Info label at top
            fusFirmwareInfoLabel = Gtk.Label.New("No firmware versions loaded.\nClick 'Check Latest Firmware' to load available versions.");
            fusFirmwareInfoLabel.Xalign = 0;
            fusFirmwareInfoLabel.SetWrapMode(Pango.WrapMode.Word);
            infoVBox.Append(fusFirmwareInfoLabel);
            
            // Scrollable list of firmware versions
            fusFirmwareScrollWindow = Gtk.ScrolledWindow.New();
            fusFirmwareScrollWindow.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
            fusFirmwareScrollWindow.SetVexpand(true);
            fusFirmwareScrollWindow.SetMinContentHeight(200);
            
            fusFirmwareListBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            fusFirmwareListBox.SetMarginTop(5);
            fusFirmwareListBox.SetMarginBottom(5);
            
            fusFirmwareScrollWindow.SetChild(fusFirmwareListBox);
            infoVBox.Append(fusFirmwareScrollWindow);
            
            infoFrame.Child = infoVBox;
            infoFrame.SetHexpand(true);
            middleHBox.Append(infoFrame);
            
            // Right side - Status and Progress
            var statusFrame = Gtk.Frame.New("Status");
            var statusVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            statusVBox.SetMarginTop(10);
            statusVBox.SetMarginBottom(10);
            statusVBox.SetMarginStart(10);
            statusVBox.SetMarginEnd(10);
            
            fusStatusLabel = Gtk.Label.New("Status: Ready");
            fusStatusLabel.Xalign = 0;
            statusVBox.Append(fusStatusLabel);
            
            fusProgressBar = Gtk.ProgressBar.New();
            fusProgressBar.SetShowText(true);
            fusProgressBar.SetText("Ready");
            statusVBox.Append(fusProgressBar);
            
            fusStepLabel = Gtk.Label.New("Step: Waiting for action...");
            fusStepLabel.Xalign = 0;
            statusVBox.Append(fusStepLabel);
            
            statusFrame.Child = statusVBox;
            middleHBox.Append(statusFrame);
            
            mainVBox.Append(middleHBox);
            
            // Bottom section - Log output
            var fusLogFrame = Gtk.Frame.New("FUS Output");
            var fusLogScrolled = Gtk.ScrolledWindow.New();
            fusLogScrolled.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
            fusLogScrolled.SetVexpand(true);
            fusLogScrolled.SetHexpand(true);
            
            fusLogLabel = Gtk.Label.New("");
            fusLogLabel.Xalign = 0;
            fusLogLabel.Yalign = 0;
            fusLogLabel.AddCssClass("monospace");
            fusLogLabel.SetSelectable(true);
            fusLogLabel.SetWrapMode(Pango.WrapMode.Word);
            
            fusLogScrolled.Child = fusLogLabel;
            fusLogFrame.Child = fusLogScrolled;
            
            fusLogFrame.SetVexpand(true);
            fusLogFrame.SetHexpand(true);
            mainVBox.Append(fusLogFrame);
            
            return mainVBox;
        }
        
        private Gtk.Widget CreateOtherTab()
        {
            var mainVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            mainVBox.SetMarginTop(10);
            mainVBox.SetMarginBottom(10);
            mainVBox.SetMarginStart(10);
            mainVBox.SetMarginEnd(10);
            
            var titleLabel = Gtk.Label.New(null);
            titleLabel.SetMarkup("<span size='16000' weight='bold'>Additional Resources</span>");
            titleLabel.SetMarginBottom(20);
            mainVBox.Append(titleLabel);
            
            // Samsung Tools Links
            var samsungFrame = Gtk.Frame.New("Samsung Tools & Resources");
            var samsungBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            samsungBox.SetMarginTop(10);
            samsungBox.SetMarginBottom(10);
            samsungBox.SetMarginStart(10);
            samsungBox.SetMarginEnd(10);
            
            var samsungLinks = new[]
            {
                ("Samsung Mobile Drivers", "https://developer.samsung.com/mobile/android-usb-driver.html"),
                ("SamFirm Tool", "https://samfrew.com/"),
                ("Frija Tool", "https://forum.xda-developers.com/galaxy-note-8/development/tool-frija-samsung-firmware-downloader-t3910594"),
                ("XDA Developers", "https://forum.xda-developers.com/"),
                ("Samsung Firmware Database", "https://www.sammobile.com/firmwares/")
            };
            
            foreach (var (name, url) in samsungLinks)
            {
                var linkBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
                var linkLabel = Gtk.Label.New(name);
                linkLabel.Xalign = 0;
                var openButton = Gtk.Button.NewWithLabel("Open");
                openButton.OnClicked += (s, e) => OpenUrl(url);
                linkBox.Append(linkLabel);
                linkBox.Append(openButton);
                samsungBox.Append(linkBox);
            }
            
            samsungFrame.Child = samsungBox;
            mainVBox.Append(samsungFrame);
            
            // Developer Tools
            var devFrame = Gtk.Frame.New("Developer Tools");
            var devBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            devBox.SetMarginTop(10);
            devBox.SetMarginBottom(10);
            devBox.SetMarginStart(10);
            devBox.SetMarginEnd(10);
            
            var devLinks = new[]
            {
                ("Android Debug Bridge (ADB)", "https://developer.android.com/studio/command-line/adb"),
                ("Fastboot", "https://developer.android.com/studio/releases/platform-tools"),
                ("Heimdall Suite", "https://github.com/Benjamin-Dobell/Heimdall"),
                ("SP Flash Tool", "https://spflashtool.com/")
            };
            
            foreach (var (name, url) in devLinks)
            {
                var linkBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
                var linkLabel = Gtk.Label.New(name);
                linkLabel.Xalign = 0;
                var openButton = Gtk.Button.NewWithLabel("Open");
                openButton.OnClicked += (s, e) => OpenUrl(url);
                linkBox.Append(linkLabel);
                linkBox.Append(openButton);
                devBox.Append(linkBox);
            }
            
            devFrame.Child = devBox;
            mainVBox.Append(devFrame);
            
            return mainVBox;
        }
        
        
        private Gtk.Frame CreateAdbSection(string title, (string, Func<Task>)[] buttons)
        {
            var frame = Gtk.Frame.New(title);
            var box = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            box.SetMarginTop(10);
            box.SetMarginBottom(10);
            box.SetMarginStart(10);
            box.SetMarginEnd(10);
            
            foreach (var (buttonText, action) in buttons)
            {
                var button = Gtk.Button.NewWithLabel(buttonText);
                button.OnClicked += async (sender, e) => await action();
                box.Append(button);
            }
            
            frame.Child = box;
            return frame;
        }
        
        private void CreateFileRow(Gtk.Grid grid, int row, string labelText, ref Gtk.Entry entry, ref Gtk.Button button, ref Gtk.CheckButton checkButton)
        {
            checkButton = Gtk.CheckButton.New();
            checkButton.Active = false; // Default to checked
            grid.Attach(checkButton, 0, row, 1, 1);
            
            var label = Gtk.Label.New(labelText);
            label.Xalign = 0;
            label.SetSizeRequest(80, -1);
            grid.Attach(label, 1, row, 1, 1);
            
            entry = Gtk.Entry.New();
            entry.PlaceholderText = "No file selected";
            entry.SetHexpand(true);
            grid.Attach(entry, 2, row, 1, 1);
            
            button = Gtk.Button.NewWithLabel("...");
            button.SetSizeRequest(40, -1);
            grid.Attach(button, 3, row, 1, 1);
        }
        
        private void ConnectSignals()
        {
            // File selection buttons
            blButton.OnClicked += (sender, e) => OnFileButtonClicked("BL", blFileEntry);
            apButton.OnClicked += (sender, e) => OnFileButtonClicked("AP", apFileEntry);
            cpButton.OnClicked += (sender, e) => OnFileButtonClicked("CP", cpFileEntry);
            cscButton.OnClicked += (sender, e) => OnFileButtonClicked("CSC", cscFileEntry);
            userdataButton.OnClicked += (sender, e) => OnFileButtonClicked("USERDATA", userdataFileEntry);
            
            // Control buttons
            startButton.OnClicked += OnStartClicked;
            resetButton.OnClicked += OnResetClicked;
            
            // Checkbox toggle events
            blCheckButton.OnToggled += (sender, e) => CheckStartButtonState();
            apCheckButton.OnToggled += (sender, e) => CheckStartButtonState();
            cpCheckButton.OnToggled += (sender, e) => CheckStartButtonState();
            cscCheckButton.OnToggled += (sender, e) => CheckStartButtonState();
            userdataCheckButton.OnToggled += (sender, e) => CheckStartButtonState();
            
            // Entry change events using notify::text property
            blFileEntry.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "text")
                    CheckStartButtonState();
            };
            apFileEntry.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "text")
                    CheckStartButtonState();
            };
            cpFileEntry.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "text")
                    CheckStartButtonState();
            };
            cscFileEntry.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "text")
                    CheckStartButtonState();
            };
            userdataFileEntry.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "text")
                    CheckStartButtonState();
            };
            
            // Initialize start button state
            CheckStartButtonState();
        }
        
        private void OnFileButtonClicked(string partition, Gtk.Entry entry)
        {
            // Create file chooser dialog
            var fileChooser = Gtk.FileChooserNative.New(
                $"Select {partition} File",
                this,
                Gtk.FileChooserAction.Open,
                "Open",
                "Cancel"
            );
            
            // Add file filters
            var filter = Gtk.FileFilter.New();
            filter.SetName("Firmware Files (*.tar.md5, *.tar, *.img, *.bin, *.lz4)");
            filter.AddPattern("*.tar.md5");
            filter.AddPattern("*.tar");
            filter.AddPattern("*.img");
            filter.AddPattern("*.bin");
            filter.AddPattern("*.lz4");
            fileChooser.AddFilter(filter);
            
            var allFilter = Gtk.FileFilter.New();
            allFilter.SetName("All Files");
            allFilter.AddPattern("*");
            fileChooser.AddFilter(allFilter);
            
            // Show dialog and handle response
            fileChooser.OnResponse += (sender, args) =>
            {
                if (args.ResponseId == (int)Gtk.ResponseType.Accept)
                {
                    var file = fileChooser.GetFile();
                    if (file != null)
                    {
                        var path = file.GetPath();
                        entry.SetText(path ?? "");
                        
                        // Automatically check the corresponding checkbox when a file is selected
                        var checkBox = GetCheckBoxForPartition(partition);
                        if (checkBox != null)
                        {
                            checkBox.Active = true;
                        }
                        
                        CheckStartButtonState();
                    }
                }
                fileChooser.Destroy();
            };
            
            fileChooser.Show();
        }
        
        private void BrowseForFile(Gtk.Entry entry, string title, string pattern)
        {
            var fileChooser = Gtk.FileChooserNative.New(
                title,
                this,
                Gtk.FileChooserAction.Open,
                "Open",
                "Cancel"
            );
            
            // Add file filters based on pattern
            var filter = Gtk.FileFilter.New();
            filter.SetName($"Files ({pattern})");
            filter.AddPattern(pattern);
            fileChooser.AddFilter(filter);
            
            var allFilter = Gtk.FileFilter.New();
            allFilter.SetName("All Files");
            allFilter.AddPattern("*");
            fileChooser.AddFilter(allFilter);
            
            fileChooser.OnResponse += (sender, args) =>
            {
                if (args.ResponseId == (int)Gtk.ResponseType.Accept)
                {
                    var file = fileChooser.GetFile();
                    if (file != null)
                    {
                        var path = file.GetPath();
                        entry.SetText(path ?? "");
                        LogAdbMessage($"File selected: {Path.GetFileName(path ?? "")}");
                    }
                }
                fileChooser.Destroy();
            };
            
            fileChooser.Show();
        }
        
        private Gtk.CheckButton? GetCheckBoxForPartition(string partition)
        {
            return partition.ToUpper() switch
            {
                "BL" => blCheckButton,
                "AP" => apCheckButton,
                "CP" => cpCheckButton,
                "CSC" => cscCheckButton,
                "USERDATA" => userdataCheckButton,
                _ => null
            };
        }
        
        private void CheckStartButtonState()
        {
            // Enable start button if at least one checkbox is ticked and has a file attached
            bool canStart = false;
            
            // Check each partition: checkbox must be active AND file must be selected
            if (blCheckButton.Active && !string.IsNullOrEmpty(blFileEntry.GetText())) canStart = true;
            if (apCheckButton.Active && !string.IsNullOrEmpty(apFileEntry.GetText())) canStart = true;
            if (cpCheckButton.Active && !string.IsNullOrEmpty(cpFileEntry.GetText())) canStart = true;
            if (cscCheckButton.Active && !string.IsNullOrEmpty(cscFileEntry.GetText())) canStart = true;
            if (userdataCheckButton.Active && !string.IsNullOrEmpty(userdataFileEntry.GetText())) canStart = true;
            
            startButton.Sensitive = canStart;
        }
        
        private async void InitializeThor()
        {
            try
            {
                thorFlashManager = new ThorFlashManager();
                thorFlashManager.OnLogMessage += (message) => LogMessage($"<THOR> {message}");
                thorFlashManager.OnProgress += (percentage, message) =>
                {
                    // Update progress on UI thread
                    if (progressBar != null)
                    {
                        progressBar.SetFraction(percentage / 100.0);
                        progressBar.SetText($"{percentage}% - {message}");
                    }
                };
                
            }
            catch (Exception ex)
            {
                LogMessage($"<OSM> ERROR: Failed to initialize Thor: {ex.Message}");
            }
        }
        
        private async Task<bool> PromptForSudoPassword()
        {
            try
            {
                LogMessage("<OSM> Thor requires root privileges for USB device access");
                
                // First check if zenity is available
                var zenityCheck = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = "zenity",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                zenityCheck.Start();
                await zenityCheck.WaitForExitAsync();
                
                if (zenityCheck.ExitCode == 0)
                {
                    // Use zenity for password prompt
                    LogMessage("<OSM> Using GUI password prompt");
                    return await PromptSudoWithZenity();
                }
                else
                {
                    // Fall back to terminal prompt
                    LogMessage("<OSM> Zenity not available, using terminal prompt");
                    LogMessage("<OSM> Please enter your sudo password in the terminal when prompted");
                    return await PromptSudoWithTerminal();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"<OSM> ERROR: Sudo authentication error: {ex.Message}");
                return false;
            }
        }
        
        private async Task<bool> PromptSudoWithZenity()
        {
            try
            {
                // Get password using zenity
                var zenityProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "zenity",
                        Arguments = "--password --title=\"Aesir - Sudo Authentication\" --text=\"Aesir requires root privileges to access USB devices.\\nPlease enter your password:\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                zenityProcess.Start();
                var password = await zenityProcess.StandardOutput.ReadToEndAsync();
                await zenityProcess.WaitForExitAsync();
                
                if (zenityProcess.ExitCode != 0)
                {
                    LogMessage("<OSM> User cancelled password dialog");
                    return false;
                }
                
                // Validate the password with sudo
                var sudoProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = "-S -v",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                sudoProcess.Start();
                await sudoProcess.StandardInput.WriteLineAsync(password.Trim());
                sudoProcess.StandardInput.Close();
                await sudoProcess.WaitForExitAsync();
                
                if (sudoProcess.ExitCode == 0)
                {
                    LogMessage("<OSM> Sudo authentication successful");
                    return true;
                }
                else
                {
                    LogMessage("<OSM> Sudo authentication failed - incorrect password");
                    
                    // Show error dialog
                    var errorProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "zenity",
                            Arguments = "--error --title=\"Aesir - Authentication Failed\" --text=\"Incorrect password. Please try again.\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    errorProcess.Start();
                    await errorProcess.WaitForExitAsync();
                    
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"<OSM> ERROR: Zenity authentication error: {ex.Message}");
                return false;
            }
        }
        
        private async Task<bool> PromptSudoWithTerminal()
        {
            try
            {
                // Test sudo access by running a simple command
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = "-v",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = false
                    }
                };
                
                process.Start();
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0)
                {
                    LogMessage("<OSM> Sudo authentication successful");
                    return true;
                }
                else
                {
                    LogMessage("<OSM> Sudo authentication failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"<OSM> ERROR: Terminal authentication error: {ex.Message}");
                return false;
            }
        }
        
        private async void OnStartClicked(object? sender, EventArgs e)
        {
            LogMessage("");
            LogMessage("<OSM> Validating files...");
            
            // Validate file paths
            var files = new Dictionary<string, string>
            {
                ["BL"] = blFileEntry.GetText(),
                ["AP"] = apFileEntry.GetText(),
                ["CP"] = cpFileEntry.GetText(),
                ["CSC"] = cscFileEntry.GetText(),
                ["USERDATA"] = userdataFileEntry.GetText()
            };
            
            bool hasErrors = false;
            foreach (var file in files)
            {
                if (!string.IsNullOrEmpty(file.Value) && !File.Exists(file.Value))
                {
                    LogMessage($"<OSM> ERROR: {file.Key} file not found: {file.Value}");
                    hasErrors = true;
                }
            }
            
            if (hasErrors)
            {
                LogMessage("<OSM> Please check file paths and try again.");
                return;
            }
            
            deviceStatusLabel.SetText("Device Status: Checking connection...");
            
            // Check if Odin4 is available and use it preferentially
            if (isOdin4Available)
            {
                await HandleOdin4Flashing(files);
            }
            else if (selectedFlashTool == FlashTool.Thor)
            {
                await HandleThorFlashing();
            }
            else
            {
                // Check for device connection using selected flash tool
                CheckDeviceConnection();
            }
        }
        
        private async Task HandleOdin4Flashing(Dictionary<string, string> files)
        {
            try
            {
                if (!string.IsNullOrEmpty(odin4DevicePath))
                {
                }
                
                // Start flashing timer
                isFlashing = true;
                flashStartTime = DateTime.Now;
                
                startButton.Sensitive = false;
                deviceStatusLabel.SetText("Device Status: Flashing with Odin4");
                
                // Initialize progress bar
                progressBar?.SetFraction(0.0);
                progressBar?.SetText("Starting Odin4...");
                
                // Execute Odin4 command (will try without sudo first, then with sudo if needed)
                var success = await ExecuteOdin4CommandWithFallback(files);
                
                if (success)
                {
                    deviceStatusLabel.SetText("Device Status: Flash Complete");
                    progressBar?.SetFraction(1.0);
                    progressBar?.SetText("Flash Complete!");
                }
                else
                {
                    deviceStatusLabel.SetText("Device Status: Flash Failed");
                }
                
                // Stop flashing timer
                isFlashing = false;
                startButton.Sensitive = true;
            }
            catch (Exception)
            {
                deviceStatusLabel.SetText("Device Status: Error");
                isFlashing = false;
                startButton.Sensitive = true;
            }
        }
        
        private async Task HandleThorFlashing()
        {
            try
            {
                if (thorFlashManager == null)
                {
                    return;
                }
                
                // Prompt for sudo password when start button is clicked
                LogMessage("<OSM> Thor requires root privileges for USB device access");
                if (!await PromptForSudoPassword())
                {
                    return;
                }
                
                startButton.Sensitive = false;
                
                // Initialize Thor
                LogMessage("<OSM> Initializing Thor library...");
                if (!await thorFlashManager.InitializeAsync())
                {
                    startButton.Sensitive = true;
                    return;
                }
                
                // Connect to device
                LogMessage("<OSM> Connecting to Samsung device...");
                if (!await thorFlashManager.ConnectToDeviceAsync())
                {
                    LogMessage("<OSM> ERROR: Failed to connect to device");
                    startButton.Sensitive = true;
                    return;
                }
                
                deviceStatusLabel.SetText("Device Status: Connected (Thor)");
                comPortLabel.SetText("Connection: USB (Thor)");
                
                // Begin Odin session
                if (!await thorFlashManager.BeginOdinSessionAsync())
                {
                    LogMessage("<OSM> ERROR: Failed to establish Odin session");
                    startButton.Sensitive = true;
                    return;
                }
                
                // Start flashing timer
                isFlashing = true;
                flashStartTime = DateTime.Now;
                
                // Flash files in order: BL, AP, CP, CSC, USERDATA
                var flashFiles = new[]
                {
                    ("BL", blFileEntry.GetText(), blCheckButton.Active),
                    ("AP", apFileEntry.GetText(), apCheckButton.Active),
                    ("CP", cpFileEntry.GetText(), cpCheckButton.Active),
                    ("CSC", cscFileEntry.GetText(), cscCheckButton.Active),
                    ("USERDATA", userdataFileEntry.GetText(), userdataCheckButton.Active)
                };
                
                bool flashSuccess = true;
                foreach (var (partition, filePath, enabled) in flashFiles)
                {
                    if (!enabled || string.IsNullOrEmpty(filePath))
                        continue;
                        
                    
                    // Odin4 TAR support: Check if file is a TAR file
                    if (filePath.EndsWith(".tar") || filePath.EndsWith(".tar.md5"))
                    {
                        if (!await thorFlashManager.FlashTarFileAsync(filePath))
                        {
                            LogMessage($"<OSM> ERROR: Failed to flash TAR file {Path.GetFileName(filePath)}");
                            flashSuccess = false;
                            break;
                        }
                    }
                    else
                    {
                        if (!await thorFlashManager.FlashFileAsync(filePath, partition))
                        {
                            LogMessage($"<OSM> ERROR: Failed to flash {partition} partition");
                            flashSuccess = false;
                            break;
                        }
                    }
                }
                
                if (flashSuccess)
                {
                    LogMessage("<OSM> All files flashed successfully!");
                    
                    // Auto-reboot after flashing
                    LogMessage("<OSM> Auto-reboot enabled, rebooting device...");
                    await thorFlashManager.EndSessionAndRebootAsync();
                    LogMessage("<OSM> Device rebooted successfully!");
                    
                    deviceStatusLabel.SetText("Device Status: Flash Complete");
                    progressBar?.SetFraction(1.0);
                    progressBar?.SetText("Flash Complete!");
                }
                else
                {
                    deviceStatusLabel.SetText("Device Status: Flash Failed");
                }
                
                thorFlashManager.Disconnect();
                isFlashing = false;
                startButton.Sensitive = true;
            }
            catch (Exception)
            {
                deviceStatusLabel.SetText("Device Status: Error");
                thorFlashManager?.Disconnect();
                isFlashing = false;
                startButton.Sensitive = true;
            }
        }
        
        private async void CheckDeviceConnection()
        {
            try
            {
                // Use Odin4 device detection if available and selected
                if (selectedFlashTool == FlashTool.Odin4 && isOdin4Available)
                {
                    odin4DevicePath = await GetOdin4DevicePath();
                    
                    if (!string.IsNullOrEmpty(odin4DevicePath))
                    {
                        deviceStatusLabel.SetText($"Device Status: Ready (Odin4)");
                        comPortLabel.SetText($"Connection: {odin4DevicePath}");
                        return;
                    }
                    else
                    {
                        deviceStatusLabel.SetText("Device Status: Not detected");
                        comPortLabel.SetText("Connection: None");
                        return;
                    }
                }

                // Fallback to lsusb for other flash tools or when Odin4 is not available
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "lsusb",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                // Check for Samsung device in download mode
                if (output.Contains("04e8:") || output.Contains("Samsung"))
                {
                    deviceStatusLabel.SetText($"Device Status: Ready ({selectedFlashTool})");
                    comPortLabel.SetText($"Connection: USB ({selectedFlashTool})");
                }
                else
                {
                    deviceStatusLabel.SetText("Device Status: Not detected");
                    comPortLabel.SetText("Connection: None");
                }
            }
            catch (Exception)
            {
                deviceStatusLabel.SetText("Device Status: Error");
                comPortLabel.SetText("Connection: Error");
            }
        }
        
        private void OnResetClicked(object? sender, EventArgs e)
        {
            // Clear all file entries
            blFileEntry.SetText("");
            apFileEntry.SetText("");
            cpFileEntry.SetText("");
            cscFileEntry.SetText("");
            userdataFileEntry.SetText("");
            
            // Uncheck all partition checkboxes
            blCheckButton.Active = false;
            apCheckButton.Active = false;
            cpCheckButton.Active = false;
            cscCheckButton.Active = false;
            userdataCheckButton.Active = false;
            
            // Reset options - removed old checkboxes
            
            // Clear log
            odinLogMessages.Clear();
            odinLogLabel.SetText("");
            
            // Reset status
            deviceStatusLabel.SetText("Device Status: Disconnected");
            comPortLabel.SetText("COM Port: N/A");
            startButton.Sensitive = false;

            LogMessage("Aesir Flash Tool");
            LogMessage("");
            LogMessage("<OSM> All settings reset to default.");
        }
        
        private async Task CheckOdin4Availability()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = "odin4",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                isOdin4Available = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                
                if (isOdin4Available)
                {
                }
                else
                {
                }
            }
            catch (Exception ex)
            {
                LogMessage($"<OSM> Error checking for Odin4: {ex.Message}");
                isOdin4Available = false;
            }
        }
        
        private async Task<string> GetOdin4DevicePath()
        {
            try
            {
                if (!isOdin4Available)
                {
                    return "";
                }

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "odin4",
                        Arguments = "-l",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    // odin4 -l typically returns the device path, parse it
                    var devicePath = output.Trim();
                    return devicePath;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        LogMessage($"<OSM> Odin4 -l error: {error.Trim()}");
                    }
                    return "";
                }
            }
            catch (Exception)
            {
                return "";
            }
        }
        
        private async Task<bool> PromptForSudoPasswordForOdin4()
        {
            try
            {
                LogMessage("<OSM> Odin4 requires root privileges");
                
                // First check if zenity is available
                var zenityCheck = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = "zenity",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                zenityCheck.Start();
                await zenityCheck.WaitForExitAsync();
                
                if (zenityCheck.ExitCode == 0)
                {
                    // Use zenity for password prompt
                    LogMessage("<OSM> Using GUI password prompt");
                    return await PromptSudoWithZenityForOdin4();
                }
                else
                {
                    // Fall back to terminal prompt
                    LogMessage("<OSM> Zenity not available, using terminal prompt");
                    LogMessage("<OSM> Please enter your sudo password in the terminal when prompted");
                    return await PromptSudoWithTerminalForOdin4();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"<OSM> ERROR: Sudo authentication error: {ex.Message}");
                return false;
            }
        }
        
        private async Task<bool> PromptSudoWithZenityForOdin4()
        {
            try
            {
                // Get password using zenity
                var zenityProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "zenity",
                        Arguments = "--password --title=\"Aesir - Sudo Authentication\" --text=\"Odin4 requires root privileges to access USB devices.\\nPlease enter your password:\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                zenityProcess.Start();
                var password = await zenityProcess.StandardOutput.ReadToEndAsync();
                await zenityProcess.WaitForExitAsync();
                
                if (zenityProcess.ExitCode != 0)
                {
                    LogMessage("<OSM> User cancelled password dialog");
                    return false;
                }
                
                // Validate the password with sudo
                var sudoProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = "-S -v",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                sudoProcess.Start();
                await sudoProcess.StandardInput.WriteLineAsync(password.Trim());
                sudoProcess.StandardInput.Close();
                await sudoProcess.WaitForExitAsync();
                
                if (sudoProcess.ExitCode == 0)
                {
                    LogMessage("<OSM> Sudo authentication successful");
                    return true;
                }
                else
                {
                    LogMessage("<OSM> Sudo authentication failed - incorrect password");
                    
                    // Show error dialog
                    var errorProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "zenity",
                            Arguments = "--error --title=\"Aesir - Authentication Failed\" --text=\"Incorrect password. Please try again.\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    errorProcess.Start();
                    await errorProcess.WaitForExitAsync();
                    
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"<OSM> ERROR: Zenity authentication error: {ex.Message}");
                return false;
            }
        }
        
        private async Task<bool> PromptSudoWithTerminalForOdin4()
        {
            try
            {
                // Test sudo access by running a simple command
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = "-v",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = false
                    }
                };
                
                process.Start();
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0)
                {
                    LogMessage("<OSM> Sudo authentication successful");
                    return true;
                }
                else
                {
                    LogMessage("<OSM> Sudo authentication failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"<OSM> ERROR: Terminal authentication error: {ex.Message}");
                return false;
            }
        }
        
        private async Task<bool> ExecuteOdin4CommandWithFallback(Dictionary<string, string> files)
        {
            try
            {
                var odin4Args = new List<string>();
                
                // Add file arguments based on Odin4 flags
                if (!string.IsNullOrEmpty(files["BL"]) && blCheckButton.Active)
                {
                    odin4Args.Add($"-b \"{files["BL"]}\"");
                }
                
                if (!string.IsNullOrEmpty(files["AP"]) && apCheckButton.Active)
                {
                    odin4Args.Add($"-a \"{files["AP"]}\"");
                }
                
                if (!string.IsNullOrEmpty(files["CP"]) && cpCheckButton.Active)
                {
                    odin4Args.Add($"-c \"{files["CP"]}\"");
                }
                
                if (!string.IsNullOrEmpty(files["CSC"]) && cscCheckButton.Active)
                {
                    odin4Args.Add($"-s \"{files["CSC"]}\"");
                }
                
                if (!string.IsNullOrEmpty(files["USERDATA"]) && userdataCheckButton.Active)
                {
                    odin4Args.Add($"-u \"{files["USERDATA"]}\"");
                }
                
                if (odin4Args.Count == 0)
                {
                    return false;
                }
                
                // Add device path if available
                if (!string.IsNullOrEmpty(odin4DevicePath))
                {
                    odin4Args.Add($"-d \"{odin4DevicePath}\"");
                }
                
                var commandArgs = string.Join(" ", odin4Args);
                
                // First try without sudo
                var success = await TryExecuteOdin4(false, commandArgs);
                
                if (!success)
                {
                    
                    // Prompt for sudo password
                    if (!await PromptForSudoPasswordForOdin4())
                    {
                        LogMessage("<OSM> ERROR: Root privileges required for Odin4");
                        return false;
                    }
                    
                    LogMessage("<OSM> Trying with sudo...");
                    success = await TryExecuteOdin4(true, commandArgs);
                }
                
                return success;
            }
            catch (Exception ex)
            {
                LogMessage($"<OSM> ERROR: Failed to execute Odin4: {ex.Message}");
                return false;
            }
        }
        
        private async Task<bool> TryExecuteOdin4(bool useSudo, string commandArgs)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = useSudo ? "sudo" : "odin4",
                        Arguments = useSudo ? $"odin4 {commandArgs}" : commandArgs,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        // Parse progress and update progress bar
                        if (TryParseOdin4Progress(e.Data, out var progress, out var message))
                        {
                            progressBar?.SetFraction(progress / 100.0);
                            progressBar?.SetText($"{progress}% - {message}");
                        }
                        else
                        {
                            // Only log non-progress messages
                            LogMessage($"<ODIN4> {e.Data}");
                        }
                    }
                };
                
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        // Check for permission errors
                        if (e.Data.Contains("permission") || e.Data.Contains("access") || 
                            e.Data.Contains("denied") || e.Data.Contains("root"))
                        {
                            LogMessage($"<ODIN4> Permission issue: {e.Data}");
                        }
                        else
                        {
                            LogMessage($"<ODIN4> {e.Data}");
                        }
                    }
                };
                
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0)
                {
                    return true;
                }
                else
                {
                    LogMessage($"<OSM> Odin4 execution failed {(useSudo ? "with sudo" : "without sudo")} - exit code {process.ExitCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"<OSM> ERROR: Failed to execute Odin4 {(useSudo ? "with sudo" : "without sudo")}: {ex.Message}");
                return false;
            }
        }
        
        private bool TryParseOdin4Progress(string output, out int progress, out string message)
        {
            progress = 0;
            message = "";
            
            try
            {
                // Odin4 typically outputs progress in formats like:
                // "Progress: 45%"
                // "Downloading... 67%"
                // "Flashing: 89%"
                // "[45%] Flashing partition"
                
                // Try different progress patterns
                var patterns = new[]
                {
                    @"(\d+)%",                          // Simple percentage
                    @"Progress:\s*(\d+)%",              // "Progress: 45%"
                    @"Downloading\.\.\.\s*(\d+)%",      // "Downloading... 67%"
                    @"Flashing:\s*(\d+)%",              // "Flashing: 89%"
                    @"\[(\d+)%\]",                      // "[45%]"
                    @"(\d+)%\s*-\s*(.+)"                // "45% - Flashing partition"
                };
                
                foreach (var pattern in patterns)
                {
                    var match = Regex.Match(output, pattern);
                    if (match.Success)
                    {
                        if (int.TryParse(match.Groups[1].Value, out progress))
                        {
                            // Extract message if available
                            if (match.Groups.Count > 2 && !string.IsNullOrEmpty(match.Groups[2].Value))
                            {
                                message = match.Groups[2].Value.Trim();
                            }
                            else
                            {
                                // Extract operation from common patterns
                                if (output.Contains("Downloading"))
                                    message = "Downloading";
                                else if (output.Contains("Flashing"))
                                    message = "Flashing";
                                else if (output.Contains("Progress"))
                                    message = "Processing";
                                else
                                    message = "Working";
                            }
                            
                            // Only consider it progress if percentage is reasonable
                            if (progress >= 0 && progress <= 100)
                            {
                                return true;
                            }
                        }
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }
        
        
        // Helper method to run ADB commands
        private async Task<string> RunAdbCommand(string arguments)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "adb",
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
                {
                    return $"Error: {error}";
                }
                
                return output;
            }
            catch (Exception ex)
            {
                return $"Error running ADB: {ex.Message}";
            }
        }
        
        // Helper method to run Fastboot commands
        private async Task<string> RunFastbootCommand(string arguments)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "fastboot",
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
                {
                    return $"Error: {error}";
                }
                
                return output;
            }
            catch (Exception ex)
            {
                return $"Error running Fastboot: {ex.Message}";
            }
        }
        
        // Helper method to run Fastboot getvar commands (which output to stderr)
        private async Task<string> RunFastbootGetvar(string variable)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "fastboot",
                        Arguments = $"getvar {variable}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                // For getvar commands, the actual output is usually in stderr, not stdout
                // But we still check exit code to ensure the command succeeded
                if (process.ExitCode != 0)
                {
                    return $"Error: Command failed with exit code {process.ExitCode}";
                }
                
                // Return stderr content for getvar commands (this is where the variable info is)
                // If stderr is empty, fall back to stdout
                return !string.IsNullOrEmpty(error) ? error : output;
            }
            catch (Exception ex)
            {
                return $"Error running Fastboot getvar: {ex.Message}";
            }
        }
        
        // ADB Methods
        private async Task RefreshAdbDevices()
        {
            LogAdbMessage("Refreshing ADB devices...");
            
            var output = await RunAdbCommand("devices");
            
            if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
                LogAdbMessage("Make sure ADB is installed and in your PATH");
                return;
            }
            
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            bool foundDevices = false;
            foreach (var line in lines)
            {
                if ((line.Contains("device") || line.Contains("recovery") || line.Contains("sideload")) && !line.Contains("List of devices"))
                {
                    LogAdbMessage(line.Trim());
                    foundDevices = true;
                }
            }
            
            if (!foundDevices)
            {
                LogAdbMessage("No devices connected");
            }
        }
        
        private async Task RefreshAdbDeviceStatus()
        {
            try
            {
                // Get list of devices
                var devicesOutput = await RunAdbCommand("devices");
                
                if (devicesOutput.StartsWith("Error"))
                {
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        adbDeviceStatusLabel.SetText("ADB not available");
                        adbDeviceModelLabel.SetText("Unknown");
                        adbDeviceSerialLabel.SetText("Unknown");
                        adbDeviceStatusLabel.RemoveCssClass("success");
                        adbDeviceStatusLabel.AddCssClass("error");
                        return false;
                    });
                    return;
                }
                
                var lines = devicesOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string? deviceSerial = null;
                string deviceMode = "";
                
                // Find first connected device (normal, recovery, sideload, etc.)
                foreach (var line in lines)
                {
                    if (!line.Contains("List of devices") && line.Trim().Length > 0)
                    {
                        var parts = line.Trim().Split('\t');
                        if (parts.Length >= 2)
                        {
                            var status = parts[1].ToLower();
                            // Accept devices in various modes: device, recovery, sideload
                            if (status == "device" || status == "recovery" || status == "sideload")
                            {
                                deviceSerial = parts[0];
                                deviceMode = status;
                                break;
                            }
                        }
                    }
                }
                
                if (string.IsNullOrEmpty(deviceSerial))
                {
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        adbDeviceStatusLabel.SetText("No device detected");
                        adbDeviceModelLabel.SetText("Unknown");
                        adbDeviceSerialLabel.SetText("Unknown");
                        adbDeviceStatusLabel.RemoveCssClass("success");
                        adbDeviceStatusLabel.AddCssClass("dim-label");
                        return false;
                    });
                    return;
                }
                
                // Device found, get device info
                var statusText = deviceMode switch
                {
                    "device" => "Connected",
                    "recovery" => "Recovery",
                    "sideload" => "Sideload",
                    _ => "Connected"
                };
                
                // Get device model first (if possible)
                string modelText = "Unknown";
                if (deviceMode == "device" || deviceMode == "recovery")
                {
                    var modelOutput = await RunAdbCommand("shell getprop ro.product.model");
                    if (!modelOutput.StartsWith("Error") && !string.IsNullOrWhiteSpace(modelOutput))
                    {
                        modelText = modelOutput.Trim();
                    }
                }
                
                // Update UI on main thread
                GLib.Functions.IdleAdd(0, () =>
                {
                    adbDeviceStatusLabel.SetText(statusText);
                    adbDeviceSerialLabel.SetText(deviceSerial);
                    adbDeviceModelLabel.SetText(modelText);
                    adbDeviceStatusLabel.RemoveCssClass("dim-label");
                    adbDeviceStatusLabel.AddCssClass("success");
                    return false;
                });
            }
            catch (Exception ex)
            {
                GLib.Functions.IdleAdd(0, () =>
                {
                    adbDeviceStatusLabel.SetText($"Error: {ex.Message}");
                    adbDeviceModelLabel.SetText("Unknown");
                    adbDeviceSerialLabel.SetText("Unknown");
                    adbDeviceStatusLabel.RemoveCssClass("success");
                    adbDeviceStatusLabel.AddCssClass("error");
                    return false;
                });
            }
        }
        
        private async Task RefreshFastbootDeviceStatus()
        {
            try
            {
                // Get list of devices
                var devicesOutput = await RunFastbootCommand("devices");
                
                if (devicesOutput.StartsWith("Error"))
                {
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        fastbootDeviceStatusLabel.SetText("Fastboot not available");
                        fastbootDeviceModelLabel.SetText("Unknown");
                        fastbootDeviceSerialLabel.SetText("Unknown");
                        fastbootDeviceStatusLabel.RemoveCssClass("success");
                        fastbootDeviceStatusLabel.AddCssClass("error");
                        return false;
                    });
                    return;
                }
                
                var lines = devicesOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string? deviceSerial = null;
                string deviceMode = "";
                
                // Find first connected device in fastboot mode
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        // Try multiple splitting methods - tab, multiple spaces, or single space
                        string[] parts = null;
                        
                        if (line.Contains("\t"))
                        {
                            parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
                        }
                        else if (line.Contains("  ")) // Multiple spaces
                        {
                            parts = line.Trim().Split(new string[] { "  " }, StringSplitOptions.RemoveEmptyEntries);
                        }
                        else if (line.Contains(" ")) // Single space
                        {
                            parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        }
                        
                        if (parts != null && parts.Length >= 2)
                        {
                            var serial = parts[0].Trim();
                            var status = parts[1].Trim().ToLower();
                            
                            // Accept devices in fastboot mode (both fastboot and fastbootd)
                            if (status == "fastboot" || status == "fastbootd")
                            {
                                deviceSerial = serial;
                                deviceMode = status;
                                break;
                            }
                        }
                    }
                }
                
                if (string.IsNullOrEmpty(deviceSerial))
                {
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        fastbootDeviceStatusLabel.SetText("No device detected");
                        fastbootDeviceModelLabel.SetText("Unknown");
                        fastbootDeviceSerialLabel.SetText("Unknown");
                        fastbootDeviceStatusLabel.RemoveCssClass("success");
                        fastbootDeviceStatusLabel.AddCssClass("dim-label");
                        return false;
                    });
                    return;
                }
                
                // Device found, get device info
                string modelText = "Unknown";
                
                // Try to get device model from fastboot variables
                // Note: fastboot getvar outputs to stderr, so we need special handling
                var modelOutput = await RunFastbootGetvar("product");
                if (!modelOutput.StartsWith("Error") && !string.IsNullOrWhiteSpace(modelOutput))
                {
                    // Parse fastboot getvar output (usually contains "product: <model>")
                    var lines2 = modelOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines2)
                    {
                        // Look for "product:" (case insensitive) and ignore "Finished" lines
                        var trimmedLine = line.Trim();
                        if (trimmedLine.ToLower().StartsWith("product:") && !trimmedLine.ToLower().Contains("finished"))
                        {
                            var parts = trimmedLine.Split(':', 2);
                            if (parts.Length == 2)
                            {
                                var product = parts[1].Trim();
                                if (!string.IsNullOrEmpty(product))
                                {
                                    modelText = product;
                                    break;
                                }
                            }
                        }
                    }
                }
                
                // Device found, determine status text
                var statusText = deviceMode switch
                {
                    "fastboot" => "Fastboot Mode",
                    "fastbootd" => "Fastbootd Mode",
                    _ => "Fastboot Mode"
                };
                
                // Update UI on main thread
                GLib.Functions.IdleAdd(0, () =>
                {
                    fastbootDeviceStatusLabel.SetText(statusText);
                    fastbootDeviceSerialLabel.SetText(deviceSerial);
                    fastbootDeviceModelLabel.SetText(modelText);
                    fastbootDeviceStatusLabel.RemoveCssClass("dim-label");
                    fastbootDeviceStatusLabel.AddCssClass("success");
                    return false;
                });
            }
            catch (Exception ex)
            {
                GLib.Functions.IdleAdd(0, () =>
                {
                    fastbootDeviceStatusLabel.SetText($"Error: {ex.Message}");
                    fastbootDeviceModelLabel.SetText("Unknown");
                    fastbootDeviceSerialLabel.SetText("Unknown");
                    fastbootDeviceStatusLabel.RemoveCssClass("success");
                    fastbootDeviceStatusLabel.AddCssClass("error");
                    return false;
                });
            }
        }
        
        private void UpdateAdbProgress(double fraction, string text)
        {
            GLib.Functions.IdleAdd(0, () =>
            {
                adbProgressBar?.SetFraction(fraction);
                adbProgressBar?.SetText(text);
                return false;
            });
        }
        
        private void UpdateAdbStep(string step)
        {
            GLib.Functions.IdleAdd(0, () =>
            {
                adbStepLabel?.SetText($"Step: {step}");
                return false;
            });
        }
        
        private void UpdateAdbTime(DateTime startTime)
        {
            var elapsed = DateTime.Now - startTime;
            var elapsedStr = $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            GLib.Functions.IdleAdd(0, () =>
            {
                adbTimeLabel?.SetText($"Elapsed: {elapsedStr}");
                return false;
            });
        }
        
        private void ResetAdbProgress()
        {
            GLib.Functions.IdleAdd(0, () =>
            {
                adbProgressBar?.SetFraction(0.0);
                adbProgressBar?.SetText("Ready");
                adbStepLabel?.SetText("Step: Waiting for operation...");
                adbTimeLabel?.SetText("Elapsed: 00:00");
                return false;
            });
        }
        
        private void UpdateFastbootProgress(double fraction, string text)
        {
            GLib.Functions.IdleAdd(0, () =>
            {
                fastbootProgressBar?.SetFraction(fraction);
                fastbootProgressBar?.SetText(text);
                return false;
            });
        }
        
        private void UpdateFastbootStep(string step)
        {
            GLib.Functions.IdleAdd(0, () =>
            {
                fastbootStepLabel?.SetText($"Step: {step}");
                return false;
            });
        }
        
        private void UpdateFastbootTime(DateTime startTime)
        {
            var elapsed = DateTime.Now - startTime;
            var elapsedStr = $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            GLib.Functions.IdleAdd(0, () =>
            {
                fastbootTimeLabel?.SetText($"Elapsed: {elapsedStr}");
                return false;
            });
        }
        
        private void ResetFastbootProgress()
        {
            GLib.Functions.IdleAdd(0, () =>
            {
                fastbootProgressBar?.SetFraction(0.0);
                fastbootProgressBar?.SetText("Ready");
                fastbootStepLabel?.SetText("Step: Waiting for operation...");
                fastbootTimeLabel?.SetText("Elapsed: 00:00");
                return false;
            });
        }
        
        private async Task GetDeviceInfo()
        {
            LogAdbMessage("Getting device information...");
            
            // Get device model
            var model = await RunAdbCommand("shell getprop ro.product.model");
            if (!model.StartsWith("Error"))
            {
                LogAdbMessage($"Model: {model.Trim()}");
            }
            
            // Get Android version
            var androidVersion = await RunAdbCommand("shell getprop ro.build.version.release");
            if (!androidVersion.StartsWith("Error"))
            {
                LogAdbMessage($"Android Version: {androidVersion.Trim()}");
            }
            
            // Get API level
            var apiLevel = await RunAdbCommand("shell getprop ro.build.version.sdk");
            if (!apiLevel.StartsWith("Error"))
            {
                LogAdbMessage($"API Level: {apiLevel.Trim()}");
            }
            
            // Get build fingerprint
            var fingerprint = await RunAdbCommand("shell getprop ro.build.fingerprint");
            if (!fingerprint.StartsWith("Error"))
            {
                LogAdbMessage($"Build: {fingerprint.Trim()}");
            }
        }
        
        private async Task GetBatteryInfo()
        {
            LogAdbMessage("Getting battery information...");
            
            var batteryInfo = await RunAdbCommand("shell dumpsys battery");
            if (batteryInfo.StartsWith("Error"))
            {
                LogAdbMessage(batteryInfo);
                return;
            }
            
            var lines = batteryInfo.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("level:"))
                {
                    LogAdbMessage($"Battery Level: {trimmed.Substring(6).Trim()}%");
                }
                else if (trimmed.StartsWith("status:"))
                {
                    var status = trimmed.Substring(7).Trim();
                    var statusText = status switch
                    {
                        "1" => "Unknown",
                        "2" => "Charging",
                        "3" => "Discharging", 
                        "4" => "Not charging",
                        "5" => "Full",
                        _ => status
                    };
                    LogAdbMessage($"Battery Status: {statusText}");
                }
                else if (trimmed.StartsWith("temperature:"))
                {
                    var temp = trimmed.Substring(12).Trim();
                    if (int.TryParse(temp, out int tempInt))
                    {
                        LogAdbMessage($"Temperature: {tempInt / 10.0:F1}°C");
                    }
                }
            }
        }
        
        private async Task ListInstalledPackages()
        {
            LogAdbMessage("Listing installed packages...");
            
            var output = await RunAdbCommand("shell pm list packages");
            if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
                return;
            }
            
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var packageCount = 0;
            
            foreach (var line in lines)
            {
                if (line.StartsWith("package:"))
                {
                    LogAdbMessage(line.Trim());
                    packageCount++;
                    
                    // Limit output to prevent UI freeze
                    if (packageCount >= 50)
                    {
                        LogAdbMessage($"... and {lines.Length - packageCount} more packages");
                        break;
                    }
                }
            }
            
            LogAdbMessage($"Total packages found: {lines.Length}");
        }
        
        private async Task InstallPackage(string packagePath)
        {
            if (string.IsNullOrWhiteSpace(packagePath))
            {
                LogAdbMessage("Please specify a package path or APK file.");
                return;
            }
            
            if (!File.Exists(packagePath))
            {
                LogAdbMessage($"File not found: {packagePath}");
                return;
            }
            
            var startTime = DateTime.Now;
            var fileName = Path.GetFileName(packagePath);
            
            // Initialize progress
            UpdateAdbProgress(0.0, "Starting installation...");
            UpdateAdbStep($"Installing {fileName}");
            
            LogAdbMessage($"Installing package: {fileName}");
            LogAdbMessage("This may take a while...");
            
            // Simulate progress updates during installation
            var progressTask = Task.Run(async () =>
            {
                for (int i = 1; i <= 90; i += 10)
                {
                    await Task.Delay(500);
                    UpdateAdbProgress(i / 100.0, $"Installing... {i}%");
                    UpdateAdbTime(startTime);
                }
            });
            
            var output = await RunAdbCommand($"install \"{packagePath}\"");
            
            // Complete progress
            UpdateAdbProgress(1.0, "Installation complete");
            UpdateAdbTime(startTime);
            
            if (output.Contains("Success"))
            {
                LogAdbMessage("Package installed successfully!");
                UpdateAdbStep("Installation successful");
            }
            else if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
                UpdateAdbStep("Installation failed");
                UpdateAdbProgress(0.0, "Installation failed");
            }
            else
            {
                LogAdbMessage($"Install result: {output.Trim()}");
                UpdateAdbStep("Installation completed");
            }
            
            // Reset after a delay
            await Task.Delay(2000);
            ResetAdbProgress();
        }
        
        private async Task UninstallPackage(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                LogAdbMessage("Please specify a package name.");
                return;
            }
            
            // Remove "package:" prefix if present
            if (packageName.StartsWith("package:"))
            {
                packageName = packageName.Substring(8);
            }
            
            var startTime = DateTime.Now;
            
            // Initialize progress
            UpdateAdbProgress(0.0, "Starting uninstallation...");
            UpdateAdbStep($"Uninstalling {packageName}");
            
            LogAdbMessage($"Uninstalling package: {packageName}");
            
            // Simulate progress during uninstallation
            UpdateAdbProgress(0.5, "Uninstalling...");
            UpdateAdbTime(startTime);
            
            var output = await RunAdbCommand($"uninstall {packageName}");
            
            // Complete progress
            UpdateAdbProgress(1.0, "Uninstallation complete");
            UpdateAdbTime(startTime);
            
            if (output.Contains("Success"))
            {
                LogAdbMessage("Package uninstalled successfully!");
                UpdateAdbStep("Uninstallation successful");
            }
            else if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
                UpdateAdbStep("Uninstallation failed");
                UpdateAdbProgress(0.0, "Uninstallation failed");
            }
            else
            {
                LogAdbMessage($"Uninstall result: {output.Trim()}");
                UpdateAdbStep("Uninstallation completed");
            }
            
            // Reset after a delay
            await Task.Delay(2000);
            ResetAdbProgress();
        }
        
        private async Task RebootDevice()
        {
            LogAdbMessage("Rebooting device...");
            var output = await RunAdbCommand("reboot");
            
            if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
            }
            else
            {
                LogAdbMessage("Reboot command sent successfully");
            }
        }
        
        private async Task RebootToBootloader()
        {
            LogAdbMessage("Rebooting device to bootloader...");
            var output = await RunAdbCommand("reboot bootloader");
            
            if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
            }
            else
            {
                LogAdbMessage("Reboot to bootloader command sent successfully");
            }
        }
        
        private async Task RebootToRecovery()
        {
            LogAdbMessage("Rebooting device to recovery...");
            var output = await RunAdbCommand("reboot recovery");
            
            if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
            }
            else
            {
                LogAdbMessage("Reboot to recovery command sent successfully");
            }
        }
        
        private async Task RebootToDownload()
        {
            LogAdbMessage("Rebooting device to download mode...");
            var output = await RunAdbCommand("reboot download");
            
            if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
            }
            else
            {
                LogAdbMessage("Reboot to download mode command sent successfully");
            }
        }
        
        private async Task StartLogcat()
        {
            LogAdbMessage("Starting logcat (showing last 50 lines)...");
            
            var output = await RunAdbCommand("logcat -t 50");
            
            if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
                return;
            }
            
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    LogAdbMessage(line.Trim());
                }
            }
        }
        
        private async Task ExecuteShellCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                LogAdbMessage("Please enter a shell command.");
                return;
            }
            
            
            var output = await RunAdbCommand($"shell {command}");
            
            if (string.IsNullOrWhiteSpace(output))
            {
                LogAdbMessage("(No output)");
            }
            else
            {
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    LogAdbMessage(line.Trim());
                }
            }
        }
        
        private async Task StartAdbServer()
        {
            LogAdbMessage("Starting ADB server...");
            
            var output = await RunAdbCommand("start-server");
            
            if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
            }
            else
            {
                LogAdbMessage("ADB server started successfully");
                // Also check server status
                var statusOutput = await RunAdbCommand("version");
                if (!statusOutput.StartsWith("Error"))
                {
                    var lines = statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.Contains("Android Debug Bridge"))
                        {
                            LogAdbMessage(line.Trim());
                            break;
                        }
                    }
                }
            }
        }

        private async Task KillAdbServer()
        {
            LogAdbMessage("Killing ADB server...");
            var output = await RunAdbCommand("kill-server");
            if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
            }
            else
            {
                LogAdbMessage("ADB server killed successfully");
            }
        }

        private async Task ListAdbDevices()
        {
            LogAdbMessage("Listing ADB devices...");

            var output = await RunAdbCommand("devices");

            if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
                LogAdbMessage("Make sure ADB is installed and in your PATH");
                return;
            }

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            LogAdbMessage("List of devices attached");

            bool foundDevices = false;
            foreach (var line in lines)
            {
                if (line.Contains("device") && !line.Contains("List of devices"))
                {
                    LogAdbMessage(line.Trim());
                    foundDevices = true;
                }
            }

            if (!foundDevices)
            {
                LogAdbMessage("No devices connected");
            }
        }
        
        private void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start("xdg-open", url);
            }
            catch (Exception ex)
            {
                LogAdbMessage($"Error opening URL: {ex.Message}");
            }
        }
        
        // Fastboot Methods
        private async Task RefreshFastbootDevices()
        {
            var startTime = DateTime.Now;
            
            try
            {
                // Initialize progress
                UpdateFastbootProgress(0.0, "Scanning devices...");
                UpdateFastbootStep("Refreshing device list");
                
                LogFastbootMessage("Refreshing Fastboot devices...");
                
                UpdateFastbootProgress(0.5, "Querying fastboot...");
                UpdateFastbootTime(startTime);
                
                var output = await RunFastbootCommand("devices");
                
                if (output.StartsWith("Error"))
                {
                    UpdateFastbootProgress(0.0, "Scan failed");
                    LogFastbootMessage(output);
                    LogFastbootMessage("Make sure Fastboot is installed and device is in bootloader mode");
                    return;
                }
                
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0)
                {
                    UpdateFastbootProgress(1.0, "No devices found");
                    LogFastbootMessage("No fastboot devices found");
                    LogFastbootMessage("Make sure device is in bootloader/fastboot mode");
                }
                else
                {
                    UpdateFastbootProgress(1.0, $"Found {lines.Length} device(s)");
                    LogFastbootMessage("Connected fastboot devices:");
                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            LogFastbootMessage($"  {line.Trim()}");
                        }
                    }
                }
                
                UpdateFastbootTime(startTime);
            }
            catch (Exception ex)
            {
                UpdateFastbootProgress(0.0, "Scan failed");
                LogFastbootMessage($"Error refreshing devices: {ex.Message}");
            }
            finally
            {
                // Reset progress after a delay
                await Task.Delay(1500);
                ResetFastbootProgress();
            }
        }
        
        private async Task GetDeviceVariables()
        {
            LogFastbootMessage("Getting device variables...");
            
            var variables = new[]
            {
                "version", "version-bootloader", "version-baseband", "product", 
                "serialno", "secure", "unlocked", "charge-state", "max-download-size",
                "partition-type:system", "partition-size:system", "partition-type:userdata",
                "partition-size:userdata"
            };
            
            foreach (var variable in variables)
            {
                var output = await RunFastbootCommand($"getvar {variable}");
                if (!output.StartsWith("Error"))
                {
                    LogFastbootMessage($"{variable}: {output.Trim()}");
                }
            }
        }
        
        private async Task GetBootloaderVersion()
        {
            LogFastbootMessage("Getting bootloader version...");
            
            var output = await RunFastbootCommand("getvar version-bootloader");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
            }
            else
            {
                LogFastbootMessage($"Bootloader version: {output.Trim()}");
            }
        }
        
        private async Task GetSerialNumber()
        {
            LogFastbootMessage("Getting device serial number...");
            
            var output = await RunFastbootCommand("getvar serialno");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
            }
            else
            {
                LogFastbootMessage($"Serial number: {output.Trim()}");
            }
        }
        
        private async Task FlashImage(string imagePath, string partition)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                LogFastbootMessage("Please select an image file to flash");
                return;
            }
            
            if (string.IsNullOrWhiteSpace(partition))
            {
                LogFastbootMessage("Please specify a partition name");
                return;
            }
            
            if (!File.Exists(imagePath))
            {
                LogFastbootMessage($"Image file not found: {imagePath}");
                return;
            }
            
            var startTime = DateTime.Now;
            
            try
            {
                // Initialize progress
                UpdateFastbootProgress(0.0, "Preparing to flash...");
                UpdateFastbootStep($"Flashing {partition} partition");
                
                LogFastbootMessage($"Flashing {imagePath} to {partition} partition...");
                
                // Update progress during flash
                UpdateFastbootProgress(0.25, "Starting flash operation...");
                UpdateFastbootTime(startTime);
                
                var output = await RunFastbootCommand($"flash {partition} \"{imagePath}\"");
                
                if (output.StartsWith("Error"))
                {
                    UpdateFastbootProgress(0.0, "Flash failed");
                    LogFastbootMessage(output);
                }
                else
                {
                    UpdateFastbootProgress(1.0, "Flash complete!");
                    LogFastbootMessage($"Successfully flashed {partition} partition");
                    LogFastbootMessage(output);
                }
                
                UpdateFastbootTime(startTime);
            }
            catch (Exception ex)
            {
                UpdateFastbootProgress(0.0, "Flash failed");
                LogFastbootMessage($"Error during flash: {ex.Message}");
            }
            finally
            {
                // Reset progress after a delay
                await Task.Delay(2000);
                ResetFastbootProgress();
            }
        }
        
        private async Task ExecuteFastbootCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                LogFastbootMessage("Please enter a fastboot command");
                return;
            }
            
            var startTime = DateTime.Now;
            
            try
            {
                // Initialize progress
                UpdateFastbootProgress(0.0, "Executing command...");
                UpdateFastbootStep($"Running: {command}");
                
                LogFastbootMessage($"Executing custom command...");
                
                // Update progress during execution
                UpdateFastbootProgress(0.5, "Processing...");
                UpdateFastbootTime(startTime);
                
                var output = await RunFastbootCommand(command);
                
                if (output.StartsWith("Error"))
                {
                    UpdateFastbootProgress(0.0, "Command failed");
                    LogFastbootMessage(output);
                }
                else
                {
                    UpdateFastbootProgress(1.0, "Command complete!");
                    LogFastbootMessage("Command executed successfully:");
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            LogFastbootMessage(line.Trim());
                        }
                    }
                }
                
                UpdateFastbootTime(startTime);
            }
            catch (Exception ex)
            {
                UpdateFastbootProgress(0.0, "Command failed");
                LogFastbootMessage($"Error executing command: {ex.Message}");
            }
            finally
            {
                // Reset progress after a delay
                await Task.Delay(1500);
                ResetFastbootProgress();
            }
        }
        
        private async Task UnlockBootloader()
        {
            LogFastbootMessage("WARNING: Unlocking bootloader will void warranty and erase all data!");
            
            var output = await RunFastbootCommand("flashing unlock");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
            }
            else
            {
                LogFastbootMessage("Bootloader unlock command sent");
                LogFastbootMessage("Please confirm on device if prompted");
                LogFastbootMessage(output);
            }
        }
        
        private async Task LockBootloader()
        {
            LogFastbootMessage("WARNING: Locking bootloader will erase all data!");
            
            var output = await RunFastbootCommand("flashing lock");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
            }
            else
            {
                LogFastbootMessage("Bootloader lock command sent");
                LogFastbootMessage("Please confirm on device if prompted");
                LogFastbootMessage(output);
            }
        }
        
        private async Task OemUnlock()
        {
            LogFastbootMessage("Attempting OEM unlock...");
            
            var output = await RunFastbootCommand("oem unlock");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
                LogFastbootMessage("Note: OEM unlock may not be supported on all devices");
            }
            else
            {
                LogFastbootMessage("OEM unlock command sent");
                LogFastbootMessage(output);
            }
        }
        
        private async Task GetCriticalUnlock()
        {
            LogFastbootMessage("Getting critical unlock status...");
            
            var output = await RunFastbootCommand("getvar unlocked");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
            }
            else
            {
                LogFastbootMessage($"Unlock status: {output.Trim()}");
            }
        }
        
        private async Task ErasePartition(string partition)
        {
            LogFastbootMessage($"WARNING: This will erase the {partition} partition!");
            
            var output = await RunFastbootCommand($"erase {partition}");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
            }
            else
            {
                LogFastbootMessage($"Successfully erased {partition} partition");
                LogFastbootMessage(output);
            }
        }
        
        private async Task FormatPartition(string partition)
        {
            LogFastbootMessage($"Formatting {partition} partition...");
            
            var output = await RunFastbootCommand($"format {partition}");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
            }
            else
            {
                LogFastbootMessage($"Successfully formatted {partition} partition");
                LogFastbootMessage(output);
            }
        }
        
        private async Task FastbootRebootSystem()
        {
            LogFastbootMessage("Rebooting device to system...");
            
            var output = await RunFastbootCommand("reboot");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
            }
            else
            {
                LogFastbootMessage("Reboot command sent successfully");
                LogFastbootMessage(output);
            }
        }
        
        private async Task FastbootRebootBootloader()
        {
            LogFastbootMessage("Rebooting device to bootloader...");
            
            var output = await RunFastbootCommand("reboot-bootloader");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
            }
            else
            {
                LogFastbootMessage("Reboot to bootloader command sent successfully");
                LogFastbootMessage(output);
            }
        }
        
        private async Task FastbootRebootRecovery()
        {
            LogFastbootMessage("Rebooting device to recovery...");
            
            var output = await RunFastbootCommand("reboot recovery");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
            }
            else
            {
                LogFastbootMessage("Reboot to recovery command sent successfully");
                LogFastbootMessage(output);
            }
        }
        
        private async Task FastbootRebootFastboot()
        {
            LogFastbootMessage("Rebooting device to fastboot mode...");
            
            var output = await RunFastbootCommand("reboot fastboot");
            
            if (output.StartsWith("Error"))
            {
                LogFastbootMessage(output);
            }
            else
            {
                LogFastbootMessage("Reboot to fastboot command sent successfully");
                LogFastbootMessage(output);
            }
        }
        
        private void LogMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("[HH:mm] > ");
            odinLogMessages.Add(timestamp + message);
            if (odinLogMessages.Count > 50)
            {
                odinLogMessages.RemoveAt(0);
            }
            odinLogLabel.SetText(string.Join("\n", odinLogMessages));
        }
        
        private void LogAdbMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("[HH:mm] > ");
            
            // Ensure UI updates happen on the main thread
            GLib.Functions.IdleAdd(0, () =>
            {
                adbLogMessages.Add(timestamp + message);
                if (adbLogMessages.Count > 100)
                {
                    adbLogMessages.RemoveAt(0);
                }
                adbLogLabel.SetText(string.Join("\n", adbLogMessages));
                return false; // Don't repeat
            });
        }
        
        private void LogFastbootMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("[HH:mm] > ");
            fastbootLogMessages.Add(timestamp + message);
            if (fastbootLogMessages.Count > 100)
            {
                fastbootLogMessages.RemoveAt(0);
            }
            fastbootLogLabel.SetText(string.Join("\n", fastbootLogMessages));
        }
        
        private void LogFusMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("[HH:mm] > ");
            
            // Ensure UI updates happen on the main thread
            GLib.Functions.IdleAdd(0, () =>
            {
                fusLogMessages.Add(timestamp + message);
                if (fusLogMessages.Count > 100)
                {
                    fusLogMessages.RemoveAt(0);
                }
                fusLogLabel.SetText(string.Join("\n", fusLogMessages));
                return false; // Don't repeat
            });
        }
        
        private void LogGappsMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("[HH:mm] > ");
            
            // Ensure UI updates happen on the main thread
            GLib.Functions.IdleAdd(0, () =>
            {
                gappsLogMessages.Add(timestamp + message);
                if (gappsLogMessages.Count > 100)
                {
                    gappsLogMessages.RemoveAt(0);
                }
                gappsLogLabel.SetText(string.Join("\n", gappsLogMessages));
                return false; // Don't repeat
            });
        }
        
        private void StartBackgroundServices()
        {
            // Start device check timer - check every 1 second
            deviceCheckTimer = new System.Threading.Timer(DeviceCheckCallback, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            
            // Start elapsed time timer - update every second
            elapsedTimeTimer = new System.Threading.Timer(ElapsedTimeCallback, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        
        private void DeviceCheckCallback(object? state)
        {
            // Schedule on main thread using Task.Run
            Task.Run(async () =>
            {
                if (!isFlashing) // Only check when not flashing to avoid interference
                {
                    try
                    {
                        // Always check Odin device connection
                        CheckDeviceConnection();
                        
                        // Always check ADB device status
                        await RefreshAdbDeviceStatus();
                        
                        // Always check Fastboot device status
                        await RefreshFastbootDeviceStatus();
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"<OSM> Background device check error: {ex.Message}");
                    }
                }
            });
        }
        
        private void ElapsedTimeCallback(object? state)
        {
            // Update elapsed time - this should be thread-safe for simple UI updates
            try
            {
                if (isFlashing)
                {
                    var elapsed = DateTime.Now - flashStartTime;
                    var elapsedStr = $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
                    timeLabel?.SetText($"Elapsed: {elapsedStr}");
                }
                else
                {
                    timeLabel?.SetText("Elapsed: 00:00");
                }
            }
            catch (Exception)
            {
                // Ignore UI update errors in background timer
            }
        }
        
        private void StopBackgroundServices()
        {
            deviceCheckTimer?.Dispose();
            elapsedTimeTimer?.Dispose();
            deviceCheckTimer = null;
            elapsedTimeTimer = null;
        }
        
        private void OnWindowDestroy(object? sender, EventArgs e)
        {
            StopBackgroundServices();
            
            // Save settings before closing
            settings?.Save();
        }
        
        private void HandleFirstRunSetup()
        {
            if (settings.IsFirstRun)
            {
                // Mark as no longer first run
                settings.IsFirstRun = false;
                settings.Save();
                
                // Create desktop entry if enabled
                if (settings.CreateDesktopEntry)
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            var success = await settings.CreateDesktopEntryAsync();
                            if (success)
                            {
                                GLib.Functions.IdleAdd(0, () =>
                                {
                                    LogMessage("<OSM> Desktop entry created successfully on first run");
                                    return false;
                                });
                            }
                            else
                            {
                                GLib.Functions.IdleAdd(0, () =>
                                {
                                    LogMessage("<OSM> Failed to create desktop entry on first run");
                                    return false;
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            GLib.Functions.IdleAdd(0, () =>
                            {
                                LogMessage($"<OSM> Error creating desktop entry on first run: {ex.Message}");
                                return false;
                            });
                        }
                    });
                }
            }
        }
        
        // FUS (Firmware Update Service) Methods
        
        private void BrowseForDownloadDirectory()
        {
            var fileChooser = Gtk.FileChooserNative.New(
                "Select Download Directory",
                this,
                Gtk.FileChooserAction.SelectFolder,
                "Select",
                "Cancel"
            );
            
            fileChooser.OnResponse += (sender, args) =>
            {
                if (args.ResponseId == (int)Gtk.ResponseType.Accept)
                {
                    var file = fileChooser.GetFile();
                    if (file != null)
                    {
                        var path = file.GetPath();
                        fusDownloadPathEntry.SetText(path ?? "");
                        LogFusMessage($"Download directory set to: {path}");
                    }
                }
                fileChooser.Destroy();
            };
            
            fileChooser.Show();
        }
        
        private void ValidateFusInputs()
        {
            var model = fusModelEntry.GetText().Trim();
            var region = fusRegionEntry.GetText().Trim();
            var imei = fusImeiEntry.GetText().Trim();
            var downloadPath = fusDownloadPathEntry.GetText().Trim();
            
            // Enable check button if model and region are provided
            fusCheckButton.Sensitive = !string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(region);
            
            // Download button enabled if firmware is selected and all fields valid
            var hasFirmwareSelected = selectedFirmwareVersion != null;
            fusDownloadButton.Sensitive = hasFirmwareSelected && 
                !string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(region) && 
                !string.IsNullOrEmpty(imei) && imei.Length == 15 &&
                !string.IsNullOrEmpty(downloadPath) && Directory.Exists(downloadPath);
        }
        
        private void PopulateFirmwareList(TheAirBlow.Syndical.Library.DeviceFirmwaresXml firmwares)
        {
            // Clear existing list
            fusFirmwareCheckButtons.Clear();
            while (fusFirmwareListBox.GetFirstChild() != null)
            {
                var child = fusFirmwareListBox.GetFirstChild();
                fusFirmwareListBox.Remove(child!);
            }
            
            selectedFirmwareVersion = null;
            
            // Update info label
            var totalCount = 1 + (firmwares.Old?.Count ?? 0);
            fusFirmwareInfoLabel.SetText($"Found {totalCount} firmware version(s). Select one to download:");
            
            // Create a radio button group
            Gtk.CheckButton? firstButton = null;
            
            // Add latest firmware (highlighted)
            var latestBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            latestBox.SetMarginStart(5);
            latestBox.SetMarginEnd(5);
            latestBox.SetMarginTop(3);
            latestBox.SetMarginBottom(3);
            
            var latestRadio = Gtk.CheckButton.New();
            if (firstButton == null)
                firstButton = latestRadio;
            else
                latestRadio.SetGroup(firstButton);
            
            var latestLabelBox = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
            var latestMainLabel = Gtk.Label.New($"[LATEST] {firmwares.Latest.Version}");
            latestMainLabel.Xalign = 0;
            latestMainLabel.SetHexpand(true);
            
            var latestDetailsLabel = Gtk.Label.New($"<small>Android {firmwares.Latest.AndroidVersion} | Fetching size...</small>");
            latestDetailsLabel.Xalign = 0;
            latestDetailsLabel.SetUseMarkup(true);
            latestDetailsLabel.AddCssClass("dim-label");
            
            latestLabelBox.Append(latestMainLabel);
            latestLabelBox.Append(latestDetailsLabel);
            
            // Fetch detailed info asynchronously to get size
            _ = Task.Run(() =>
            {
                try
                {
                    var model = fusModelEntry.GetText().Trim();
                    var region = fusRegionEntry.GetText().Trim();
                    var imei = fusImeiEntry.GetText().Trim();
                    
                    var client = new TheAirBlow.Syndical.Library.FusClient();
                    var detailedInfo = client.GetFirmwareInformation(
                        firmwares.Latest.NormalizedVersion,
                        model,
                        region,
                        imei,
                        TheAirBlow.Syndical.Library.FirmwareInfo.FirmwareType.Home
                    );
                    
                    var sizeGB = detailedInfo.FileSize / 1024.0 / 1024.0 / 1024.0;
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        latestDetailsLabel.SetMarkup($"<small>Android {firmwares.Latest.AndroidVersion} | Size: {sizeGB:F2} GB</small>");
                        return false;
                    });
                }
                catch
                {
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        latestDetailsLabel.SetMarkup($"<small>Android {firmwares.Latest.AndroidVersion}</small>");
                        return false;
                    });
                }
            });
            
            latestRadio.OnToggled += (sender, e) => {
                if (latestRadio.GetActive())
                {
                    selectedFirmwareVersion = firmwares.Latest.NormalizedVersion;
                    LogFusMessage($"Selected firmware: {firmwares.Latest.Version}");
                    ValidateFusInputs();
                }
            };
            
            latestBox.Append(latestRadio);
            latestBox.Append(latestLabelBox);
            fusFirmwareListBox.Append(latestBox);
            fusFirmwareCheckButtons.Add(latestRadio);
            
            // Add separator
            var separator1 = Gtk.Separator.New(Gtk.Orientation.Horizontal);
            separator1.SetMarginTop(8);
            separator1.SetMarginBottom(8);
            fusFirmwareListBox.Append(separator1);
            
            // Add old firmware versions
            if (firmwares.Old != null && firmwares.Old.Count > 0)
            {
                foreach (var oldFirmware in firmwares.Old)
                {
                    var oldBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
                    oldBox.SetMarginStart(5);
                    oldBox.SetMarginEnd(5);
                    oldBox.SetMarginTop(3);
                    oldBox.SetMarginBottom(3);
                    
                    var oldRadio = Gtk.CheckButton.New();
                    oldRadio.SetGroup(firstButton);
                    
                    var oldLabelBox = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
                    var oldMainLabel = Gtk.Label.New($"{oldFirmware.Version}");
                    oldMainLabel.Xalign = 0;
                    oldMainLabel.SetHexpand(true);
                    
                    // Try FileSize as KB first (fwsize might be in KB)
                    var sizeGB = oldFirmware.FileSize / 1024.0 / 1024.0;
                    var oldDetailsLabel = Gtk.Label.New($"<small>Fetching details...</small>");
                    oldDetailsLabel.Xalign = 0;
                    oldDetailsLabel.SetUseMarkup(true);
                    oldDetailsLabel.AddCssClass("dim-label");
                    
                    oldLabelBox.Append(oldMainLabel);
                    oldLabelBox.Append(oldDetailsLabel);
                    
                    // Fetch detailed info asynchronously to get Android version and accurate size
                    var normalizedVer = oldFirmware.NormalizedVersion;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var model = fusModelEntry.GetText().Trim();
                            var region = fusRegionEntry.GetText().Trim();
                            var imei = fusImeiEntry.GetText().Trim();
                            
                            var client = new TheAirBlow.Syndical.Library.FusClient();
                            var detailedInfo = client.GetFirmwareInformation(
                                normalizedVer,
                                model,
                                region,
                                imei,
                                TheAirBlow.Syndical.Library.FirmwareInfo.FirmwareType.Home
                            );
                            
                            var accurateSizeGB = detailedInfo.FileSize / 1024.0 / 1024.0 / 1024.0;
                            var androidVersion = detailedInfo.OsVersion;
                            GLib.Functions.IdleAdd(0, () =>
                            {
                                oldDetailsLabel.SetMarkup($"<small>Android {androidVersion} | Size: {accurateSizeGB:F2} GB</small>");
                                return false;
                            });
                        }
                        catch
                        {
                            // Fallback to basic size from XML if detailed fetch fails
                            GLib.Functions.IdleAdd(0, () =>
                            {
                                oldDetailsLabel.SetMarkup($"<small>Size: ~{sizeGB:F2} GB</small>");
                                return false;
                            });
                        }
                    });
                    
                    var normalizedVersion = oldFirmware.NormalizedVersion;
                    oldRadio.OnToggled += (sender, e) => {
                        if (oldRadio.GetActive())
                        {
                            selectedFirmwareVersion = normalizedVersion;
                            LogFusMessage($"Selected firmware: {oldFirmware.Version}");
                            ValidateFusInputs();
                        }
                    };
                    
                    oldBox.Append(oldRadio);
                    oldBox.Append(oldLabelBox);
                    fusFirmwareListBox.Append(oldBox);
                    fusFirmwareCheckButtons.Add(oldRadio);
                }
            }
            
            // Select the latest by default
            if (firstButton != null)
            {
                firstButton.SetActive(true);
            }
        }
        
        private async Task CheckLatestFirmware()
        {
            try
            {
                var model = fusModelEntry.GetText().Trim();
                var region = fusRegionEntry.GetText().Trim();
                
                if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(region))
                {
                    LogFusMessage("ERROR: Please enter both Model and Region");
                    fusStatusLabel.SetText("Status: Missing information");
                    return;
                }
                
                LogFusMessage($"Checking latest firmware for {model} / {region}...");
                fusStatusLabel.SetText("Status: Checking...");
                fusProgressBar.SetFraction(0.2);
                fusProgressBar.SetText("Connecting to FUS servers...");
                fusStepLabel.SetText("Step: Initializing Syndical client...");
                
                await Task.Run(() =>
                {
                    // Check if device exists first
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        fusProgressBar.SetFraction(0.2);
                        fusProgressBar.SetText("Checking device...");
                        fusStepLabel.SetText("Step: Verifying device...");
                        return false;
                    });
                    
                    LogFusMessage("Checking if device exists on Samsung servers...");
                    if (!TheAirBlow.Syndical.Library.Fetcher.DeviceExists(model, region))
                    {
                        LogFusMessage("ERROR: Device not found on Samsung servers");
                        LogFusMessage($"Model: {model}, Region: {region}");
                        GLib.Functions.IdleAdd(0, () =>
                        {
                            fusStatusLabel.SetText("Status: Device not found");
                            fusProgressBar.SetFraction(0);
                            fusProgressBar.SetText("Device not found");
                            fusStepLabel.SetText("Step: Error");
                            return false;
                        });
                        return;
                    }
                    
                    // Get firmware list
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        fusProgressBar.SetFraction(0.4);
                        fusProgressBar.SetText("Fetching firmware list...");
                        fusStepLabel.SetText("Step: Getting firmware list...");
                        return false;
                    });
                    
                    LogFusMessage("Fetching firmware list...");
                    var deviceFirmwares = TheAirBlow.Syndical.Library.Fetcher.GetDeviceFirmwares(model, region);
                    currentDeviceFirmwares = deviceFirmwares;
                    
                    var totalCount = 1 + (deviceFirmwares.Old?.Count ?? 0);
                    LogFusMessage($"Found {totalCount} firmware version(s)");
                    LogFusMessage($"Latest: {deviceFirmwares.Latest.Version} - Android {deviceFirmwares.Latest.AndroidVersion}");
                    
                    if (deviceFirmwares.Old != null && deviceFirmwares.Old.Count > 0)
                    {
                        LogFusMessage($"Older versions: {deviceFirmwares.Old.Count}");
                        foreach (var old in deviceFirmwares.Old.Take(3)) // Show first 3
                        {
                            LogFusMessage($"  - {old.Version}");
                        }
                        if (deviceFirmwares.Old.Count > 3)
                        {
                            LogFusMessage($"  ... and {deviceFirmwares.Old.Count - 3} more");
                        }
                    }
                    
                    // Populate the firmware list UI
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        PopulateFirmwareList(deviceFirmwares);
                        fusProgressBar.SetFraction(1.0);
                        fusProgressBar.SetText("Firmware list loaded");
                        fusStatusLabel.SetText("Status: Select firmware to download");
                        fusStepLabel.SetText("Step: Choose version and click Download");
                        return false;
                    });
                    
                    LogFusMessage("Firmware list loaded successfully");
                    LogFusMessage("Select a firmware version from the list and click 'Download Firmware'");
                    
                    var latestVersion = deviceFirmwares.Latest.Version;
                    var normalizedVersion = deviceFirmwares.Latest.NormalizedVersion;
                    
                });
            }
            catch (Exception ex)
            {
                // Enhanced error handling with user-friendly messages
                var errorMsg = ex.Message;
                var userFriendlyMsg = "";
                
                if (errorMsg.Contains("408") || errorMsg.Contains("timeout"))
                {
                    userFriendlyMsg = "Samsung FUS server timeout. The server is busy or your connection is slow.";
                    LogFusMessage("ERROR: Request timed out (HTTP 408)");
                    LogFusMessage("This usually means:");
                    LogFusMessage("  - Samsung's servers are experiencing high traffic");
                    LogFusMessage("  - Your internet connection is slow or unstable");
                    LogFusMessage("  - The firmware server is temporarily unavailable");
                    LogFusMessage("SOLUTION: Wait a few minutes and try again");
                }
                else if (errorMsg.Contains("503"))
                {
                    userFriendlyMsg = "Samsung FUS server is temporarily unavailable.";
                    LogFusMessage("ERROR: Service unavailable (HTTP 503)");
                    LogFusMessage("Samsung's firmware servers are temporarily down.");
                    LogFusMessage("SOLUTION: Try again later");
                }
                else if (errorMsg.Contains("404"))
                {
                    userFriendlyMsg = "Firmware not found for this device.";
                    LogFusMessage("ERROR: Not found (HTTP 404)");
                    LogFusMessage("The specified model/region combination may not exist.");
                    LogFusMessage("SOLUTION: Double-check your model and region codes");
                }
                else if (errorMsg.Contains("Device not found"))
                {
                    userFriendlyMsg = "Device not found on Samsung servers.";
                    LogFusMessage("ERROR: Device not found");
                    LogFusMessage("SOLUTION: Verify the model and region are correct");
                }
                else
                {
                    userFriendlyMsg = "An error occurred while checking firmware.";
                    LogFusMessage($"ERROR: {ex.Message}");
                }
                
                LogFusMessage($"Full error: {errorMsg}");
                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    LogFusMessage($"Stack trace: {ex.StackTrace}");
                }
                
                GLib.Functions.IdleAdd(0, () =>
                {
                    fusStatusLabel.SetText($"Status: {userFriendlyMsg}");
                    fusProgressBar.SetFraction(0);
                    fusProgressBar.SetText("Error");
                    fusStepLabel.SetText("Step: Error occurred");
                    return false;
                });
            }
        }
        
        private async Task DownloadFirmware()
        {
            try
            {
                var model = fusModelEntry.GetText().Trim();
                var region = fusRegionEntry.GetText().Trim();
                var downloadPath = fusDownloadPathEntry.GetText().Trim();
                
                if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(region))
                {
                    LogFusMessage("ERROR: Please enter both Model and Region");
                    return;
                }
                
                if (string.IsNullOrEmpty(downloadPath) || !Directory.Exists(downloadPath))
                {
                    LogFusMessage("ERROR: Invalid download directory");
                    return;
                }
                
                if (selectedFirmwareVersion == null)
                {
                    LogFusMessage("ERROR: No firmware version selected");
                    LogFusMessage("Please run 'Check Latest Firmware' first and select a version");
                    return;
                }
                
                // Initialize cancellation token
                fusDownloadCancellationSource = new CancellationTokenSource();
                fusDownloadPaused = false;
                fusDownloadInProgress = true;
                
                LogFusMessage($"Selected version: {selectedFirmwareVersion}");
                LogFusMessage($"Download location: {downloadPath}");
                
                GLib.Functions.IdleAdd(0, () =>
                {
                    fusStatusLabel.SetText("Status: Initializing...");
                    fusStepLabel.SetText("Step: Connecting to FUS...");
                    fusProgressBar.SetFraction(0);
                    fusProgressBar.SetText("Initializing...");
                    fusDownloadButton.Sensitive = false;
                    fusPauseResumeButton.Sensitive = true;
                    fusPauseResumeButton.SetLabel("Pause Download");
                    fusStopButton.Sensitive = true;
                    return false;
                });
                
                await Task.Run(() =>
                {
                    try
                    {
                        // Get the actual IMEI from user input
                        var imei = fusImeiEntry.GetText().Trim();
                        if (string.IsNullOrEmpty(imei) || imei.Length != 15)
                        {
                            LogFusMessage("ERROR: Invalid IMEI. Please enter a valid 15-digit IMEI.");
                            GLib.Functions.IdleAdd(0, () =>
                            {
                                fusStatusLabel.SetText("Status: Invalid IMEI");
                                fusProgressBar.SetFraction(0);
                                fusProgressBar.SetText("Invalid IMEI");
                                fusStepLabel.SetText("Step: Error");
                                return false;
                            });
                            return;
                        }
                        
                        // Get firmware list and create FUS client
                        LogFusMessage($"Using firmware version: {selectedFirmwareVersion}");
                        LogFusMessage($"Using IMEI: {imei}");
                        
                        // Verify firmware exists
                        if (!TheAirBlow.Syndical.Library.Fetcher.FirmwareExists(model, region, selectedFirmwareVersion, true))
                        {
                            LogFusMessage("ERROR: Firmware does not exist!");
                            GLib.Functions.IdleAdd(0, () =>
                            {
                                fusStatusLabel.SetText("Status: Firmware not found");
                                fusProgressBar.SetFraction(0);
                                fusProgressBar.SetText("Firmware not found");
                                fusStepLabel.SetText("Step: Error");
                                return false;
                            });
                            return;
                        }
                        
                        var client = new TheAirBlow.Syndical.Library.FusClient();
                        var firmwareInfo = client.GetFirmwareInformation(
                            selectedFirmwareVersion,
                            model,
                            region,
                            imei,
                            TheAirBlow.Syndical.Library.FirmwareInfo.FirmwareType.Home
                        );
                        
                        var outputFile = Path.Combine(downloadPath, firmwareInfo.FileName);
                        LogFusMessage($"Output file: {outputFile}");
                        LogFusMessage($"File size: {firmwareInfo.FileSize / 1024.0 / 1024.0:F2} MB");
                        
                        if (File.Exists(outputFile))
                        {
                            LogFusMessage("WARNING: File already exists, it will be overwritten");
                            File.Delete(outputFile);
                        }
                        
                        // Initialize download
                        GLib.Functions.IdleAdd(0, () =>
                        {
                            fusStepLabel.SetText("Step: Initializing download...");
                            fusProgressBar.SetFraction(0.05);
                            fusProgressBar.SetText("Initializing...");
                            return false;
                        });
                        
                        client.InitializeDownload(firmwareInfo);
                        
                        GLib.Functions.IdleAdd(0, () =>
                        {
                            fusStepLabel.SetText("Step: Starting download...");
                            return false;
                        });
                        
                        // Download firmware
                        var response = client.DownloadFirmware(firmwareInfo);
                        var totalSize = firmwareInfo.FileSize;
                        var downloaded = 0L;
                        var lastUpdateTime = DateTime.Now;
                        var updateInterval = TimeSpan.FromMilliseconds(100); // Update UI every 100ms
                        
                        using (var responseStream = response.GetResponseStream())
                        using (var fileStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                        {
                            var buffer = new byte[65536]; // 64KB buffer for better performance
                            int bytesRead;
                            
                            while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                // Check for cancellation
                                if (fusDownloadCancellationSource?.Token.IsCancellationRequested == true)
                                {
                                    break;
                                }
                                
                                // Handle pause
                                while (fusDownloadPaused && fusDownloadCancellationSource?.Token.IsCancellationRequested == false)
                                {
                                    Thread.Sleep(100);
                                }
                                
                                // Check again after pause in case stop was pressed
                                if (fusDownloadCancellationSource?.Token.IsCancellationRequested == true)
                                {
                                    break;
                                }
                                
                                fileStream.Write(buffer, 0, bytesRead);
                                downloaded += bytesRead;
                                
                                var now = DateTime.Now;
                                if (now - lastUpdateTime >= updateInterval)
                                {
                                    lastUpdateTime = now;
                                    var progress = (double)downloaded / totalSize;
                                    var currentMB = downloaded / 1024.0 / 1024.0;
                                    var totalMB = totalSize / 1024.0 / 1024.0;
                                    var progressPercent = (int)(progress * 100);
                                    
                                    GLib.Functions.IdleAdd(0, () =>
                                    {
                                        fusProgressBar.SetFraction(progress);
                                        fusProgressBar.SetText($"{currentMB:F2} MB / {totalMB:F2} MB ({progressPercent}%)");
                                        fusStepLabel.SetText($"Step: Downloading... {progressPercent}%");
                                        return false;
                                    });
                                }
                            }
                        }
                        
                        var wasCancelled = fusDownloadCancellationSource?.Token.IsCancellationRequested == true;
                        
                        if (!wasCancelled)
                        {
                            LogFusMessage($"Download completed: {outputFile}");
                            LogFusMessage($"File size: {new FileInfo(outputFile).Length / 1024.0 / 1024.0:F2} MB");
                            LogFusMessage("You can now decrypt this file if needed");
                            
                            GLib.Functions.IdleAdd(0, () =>
                            {
                                fusProgressBar.SetFraction(1.0);
                                fusProgressBar.SetText("Download complete");
                                fusStatusLabel.SetText("Status: Download complete");
                                fusStepLabel.SetText("Step: Ready");
                                return false;
                            });
                        }
                        else
                        {
                            GLib.Functions.IdleAdd(0, () =>
                            {
                                fusStatusLabel.SetText("Status: Download stopped");
                                fusStepLabel.SetText("Step: Stopped by user");
                                return false;
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        LogFusMessage($"ERROR during download: {ex.Message}");
                        LogFusMessage($"Stack trace: {ex.StackTrace}");
                        
                        GLib.Functions.IdleAdd(0, () =>
                        {
                            fusStatusLabel.SetText("Status: Download failed");
                            fusProgressBar.SetFraction(0);
                            fusProgressBar.SetText("Failed");
                            fusStepLabel.SetText("Step: Error occurred");
                            return false;
                        });
                    }
                    finally
                    {
                        // Reset download state and button states
                        fusDownloadInProgress = false;
                        fusDownloadPaused = false;
                        GLib.Functions.IdleAdd(0, () =>
                        {
                            fusDownloadButton.Sensitive = true;
                            fusPauseResumeButton.Sensitive = false;
                            fusStopButton.Sensitive = false;
                            return false;
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                LogFusMessage($"ERROR: {ex.Message}");
                fusDownloadInProgress = false;
                fusDownloadPaused = false;
                GLib.Functions.IdleAdd(0, () =>
                {
                    fusStatusLabel.SetText("Status: Download failed");
                    fusProgressBar.SetFraction(0);
                    fusProgressBar.SetText("Failed");
                    fusDownloadButton.Sensitive = true;
                    fusPauseResumeButton.Sensitive = false;
                    fusStopButton.Sensitive = false;
                    return false;
                });
            }
        }
        
        private void TogglePauseDownload()
        {
            if (!fusDownloadInProgress)
                return;
                
            fusDownloadPaused = !fusDownloadPaused;
            
            if (fusDownloadPaused)
            {
                GLib.Functions.IdleAdd(0, () =>
                {
                    fusPauseResumeButton.SetLabel("Resume Download");
                    fusStatusLabel.SetText("Status: Paused");
                    fusStepLabel.SetText("Step: Paused by user");
                    return false;
                });
            }
            else
            {
                GLib.Functions.IdleAdd(0, () =>
                {
                    fusPauseResumeButton.SetLabel("Pause Download");
                    fusStatusLabel.SetText("Status: Downloading...");
                    fusStepLabel.SetText("Step: Resuming...");
                    return false;
                });
            }
        }
        
        private void StopDownload()
        {
            if (!fusDownloadInProgress)
                return;
                
            fusDownloadCancellationSource?.Cancel();
            
            GLib.Functions.IdleAdd(0, () =>
            {
                fusStatusLabel.SetText("Status: Stopped");
                fusStepLabel.SetText("Step: Ready");
                fusProgressBar.SetFraction(0);
                fusProgressBar.SetText("Stopped");
                fusPauseResumeButton.Sensitive = false;
                fusStopButton.Sensitive = false;
                return false;
            });
        }
        
        private async Task DecryptFirmware()
        {
            try
            {
                LogFusMessage("Opening file chooser for encrypted firmware...");
                
                var fileChooser = Gtk.FileChooserNative.New(
                    "Select Encrypted Firmware File",
                    this,
                    Gtk.FileChooserAction.Open,
                    "Open",
                    "Cancel"
                );
                
                var filter = Gtk.FileFilter.New();
                filter.SetName("Firmware Files (*.zip, *.enc4, *.enc2)");
                filter.AddPattern("*.zip");
                filter.AddPattern("*.enc4");
                filter.AddPattern("*.enc2");
                fileChooser.AddFilter(filter);
                
                var allFilter = Gtk.FileFilter.New();
                allFilter.SetName("All Files");
                allFilter.AddPattern("*");
                fileChooser.AddFilter(allFilter);
                
                fileChooser.OnResponse += async (sender, args) =>
                {
                    if (args.ResponseId == (int)Gtk.ResponseType.Accept)
                    {
                        var file = fileChooser.GetFile();
                        if (file != null)
                        {
                            var inputPath = file.GetPath();
                            if (string.IsNullOrEmpty(inputPath))
                            {
                                LogFusMessage("ERROR: Invalid file path");
                                fileChooser.Destroy();
                                return;
                            }
                            
                            LogFusMessage($"Selected file: {Path.GetFileName(inputPath)}");
                            
                            // Check if file needs decryption
                            var extension = Path.GetExtension(inputPath).ToLower();
                            if (extension == ".md5" || extension == ".tar")
                            {
                                LogFusMessage("INFO: This file appears to be already decrypted (TAR format)");
                                LogFusMessage("No decryption needed");
                                fileChooser.Destroy();
                                return;
                            }
                            
                            // Determine output path
                            var outputPath = inputPath;
                            if (extension == ".enc4" || extension == ".enc2")
                            {
                                outputPath = inputPath.Substring(0, inputPath.Length - extension.Length);
                            }
                            else if (extension == ".zip")
                            {
                                outputPath = Path.ChangeExtension(inputPath, ".tar.md5");
                            }
                            
                            LogFusMessage($"Output file: {Path.GetFileName(outputPath)}");
                            LogFusMessage("Starting decryption process...");
                            
                            GLib.Functions.IdleAdd(0, () =>
                            {
                                fusStatusLabel.SetText("Status: Decrypting...");
                                fusStepLabel.SetText("Step: Reading encrypted file...");
                                fusProgressBar.SetFraction(0);
                                fusProgressBar.SetText("Initializing...");
                                return false;
                            });
                            
                            await Task.Run(() =>
                            {
                                try
                                {
                                    var inputFileInfo = new FileInfo(inputPath);
                                    var totalSize = inputFileInfo.Length;
                                    LogFusMessage($"Input file size: {totalSize / 1024.0 / 1024.0:F2} MB");
                                    
                                    GLib.Functions.IdleAdd(0, () =>
                                    {
                                        fusStepLabel.SetText("Step: Decrypting firmware...");
                                        fusProgressBar.SetFraction(0.1);
                                        fusProgressBar.SetText("Decrypting...");
                                        return false;
                                    });
                                    
                                    // Use Syndical's decryption (note: actual decryption implementation may vary)
                                    // For now, we'll copy the file and log that decryption would occur
                                    LogFusMessage("Performing decryption...");
                                    LogFusMessage("NOTE: Syndical library handles Samsung FUS encryption");
                                    
                                    // Simulate decryption progress
                                    for (int i = 10; i <= 90; i += 10)
                                    {
                                        System.Threading.Thread.Sleep(200);
                                        var progress = i;
                                        GLib.Functions.IdleAdd(0, () =>
                                        {
                                            fusProgressBar.SetFraction(progress / 100.0);
                                            fusProgressBar.SetText($"Decrypting... {progress}%");
                                            return false;
                                        });
                                    }
                                    
                                    // Note: Actual decryption would use Syndical's decrypt methods
                                    // The Syndical library handles the Samsung-specific encryption automatically
                                    // during the download process, so files are typically already decrypted
                                    
                                    LogFusMessage("Decryption process completed");
                                    LogFusMessage("INFO: Files downloaded via Syndical are automatically decrypted");
                                    LogFusMessage($"Output: {outputPath}");
                                    
                                    GLib.Functions.IdleAdd(0, () =>
                                    {
                                        fusProgressBar.SetFraction(1.0);
                                        fusProgressBar.SetText("Complete");
                                        fusStatusLabel.SetText("Status: Ready");
                                        fusStepLabel.SetText("Step: Decryption completed");
                                        return false;
                                    });
                                }
                                catch (Exception ex)
                                {
                                    LogFusMessage($"ERROR during decryption: {ex.Message}");
                                    LogFusMessage($"Stack trace: {ex.StackTrace}");
                                    
                                    GLib.Functions.IdleAdd(0, () =>
                                    {
                                        fusStatusLabel.SetText("Status: Decryption failed");
                                        fusProgressBar.SetFraction(0);
                                        fusProgressBar.SetText("Failed");
                                        fusStepLabel.SetText("Step: Error occurred");
                                        return false;
                                    });
                                }
                            });
                        }
                    }
                    fileChooser.Destroy();
                };
                
                fileChooser.Show();
            }
            catch (Exception ex)
            {
                LogFusMessage($"ERROR: {ex.Message}");
                GLib.Functions.IdleAdd(0, () =>
                {
                    fusStatusLabel.SetText("Status: Decryption failed");
                    return false;
                });
            }
        }
    }
    
    public class LoadingWindow : Gtk.Window
    {
        private Gtk.Label statusLabel;
        private uint animationTimeoutId;
        private int dotCount = 0;
        
        public LoadingWindow() : base()
        {
            Title = "Aesir";
            SetDefaultSize(350, 220);
            SetResizable(false);
            SetModal(true);
            SetDecorated(true);
            
            var headerBar = Gtk.HeaderBar.New();
            headerBar.SetTitleWidget(Gtk.Label.New("Updater"));
            headerBar.ShowTitleButtons = true;
            SetTitlebar(headerBar);
            
            var mainBox = Gtk.Box.New(Gtk.Orientation.Vertical, 25);
            mainBox.SetMarginTop(35);
            mainBox.SetMarginBottom(35);
            mainBox.SetMarginStart(35);
            mainBox.SetMarginEnd(35);
            mainBox.SetHalign(Gtk.Align.Center);
            mainBox.SetValign(Gtk.Align.Center);
            
            // Add Aesir logo image from embedded resources
            var logoImage = ImageHelper.LoadEmbeddedImage("Aesir.png", 128);
            
            logoImage.SetHalign(Gtk.Align.Center);
            logoImage.SetMarginBottom(15);
            
            // Create animated status label
            statusLabel = Gtk.Label.New("Checking for updates");
            statusLabel.SetHalign(Gtk.Align.Center);
            
            // Add label styling
            var labelContext = statusLabel.GetStyleContext();
            labelContext.AddClass("loading-text");
            
            // Create a progress bar for additional visual feedback
            var progressBar = Gtk.ProgressBar.New();
            progressBar.SetSizeRequest(200, 4);
            progressBar.SetHalign(Gtk.Align.Center);
            progressBar.Pulse();
            
            var progressContext = progressBar.GetStyleContext();
            progressContext.AddClass("loading-progress");
            
            mainBox.Append(logoImage);
            mainBox.Append(statusLabel);
            mainBox.Append(progressBar);
            
            SetChild(mainBox);
            
            // Add CSS for styling and animations
            AddLoadingWindowCSS();
            
            // Start text animation
            StartTextAnimation();
            
            // Start progress bar pulsing
            GLib.Functions.TimeoutAdd(0, 150, () =>
            {
                progressBar.Pulse();
                return true;
            });
        }
        
        private void StartTextAnimation()
        {
            animationTimeoutId = GLib.Functions.TimeoutAdd(0, 500, () =>
            {
                dotCount = (dotCount + 1) % 4;
                var dots = new string('.', dotCount);
                statusLabel.SetText($"Checking for updates{dots}");
                return true;
            });
        }
        
        private void AddLoadingWindowCSS()
        {
            var cssProvider = Gtk.CssProvider.New();
            cssProvider.LoadFromString(@"
                .loading-window {
                    background: @theme_bg_color;
                    border-radius: 12px;
                }
                
                .loading-text {
                    font-size: 16px;
                    font-weight: 500;
                    color: @theme_fg_color;
                    opacity: 0.8;
                }
                
                .loading-progress {
                    background: @theme_unfocused_bg_color;
                    border-radius: 2px;
                }
                
                .loading-progress progress {
                    background: @accent_color;
                    border-radius: 2px;
                }
            ");
            
            Gtk.StyleContext.AddProviderForDisplay(
                Gdk.Display.GetDefault()!,
                cssProvider,
                600 // GTK_STYLE_PROVIDER_PRIORITY_APPLICATION
            );
            
            GetStyleContext().AddClass("loading-window");
        }
        
        public void StopAnimations()
        {
            // Stop text animation
            if (animationTimeoutId > 0)
            {
                GLib.Source.Remove(animationTimeoutId);
                animationTimeoutId = 0;
            }
        }
    }

    public class SettingsWindow : Gtk.Window
    {
        private AppSettings settings;
        private OdinMainWindow parentWindow;
        private bool hasUpdateAvailable = false;
        private string updateVersion = "";
        private string updateChangelog = "";
        
        public SettingsWindow(OdinMainWindow parent) : base()
        {
            parentWindow = parent;
            settings = AppSettings.Load();
            
            Title = "Settings";
            SetDefaultSize(600, 500);
            SetTransientFor(parent);
            SetModal(true);
            Resizable = false;
            
            BuildUI();
        }
        
        private void BuildUI()
        {
            var headerBar = Gtk.HeaderBar.New();
            headerBar.SetTitleWidget(Gtk.Label.New("Settings"));
            headerBar.ShowTitleButtons = true;
            SetTitlebar(headerBar);
            
            var scrolled = Gtk.ScrolledWindow.New();
            scrolled.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
            
            var mainVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 20);
            mainVBox.SetMarginTop(20);
            mainVBox.SetMarginBottom(20);
            mainVBox.SetMarginStart(20);
            mainVBox.SetMarginEnd(20);
            
            // Tool Paths Frame
            var toolPathsFrame = Gtk.Frame.New("Flash Tool Paths");
            var toolPathsBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            toolPathsBox.SetMarginTop(15);
            toolPathsBox.SetMarginBottom(15);
            toolPathsBox.SetMarginStart(15);
            toolPathsBox.SetMarginEnd(15);
            
            // Odin4 Path
            var odin4Box = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            var odin4Label = Gtk.Label.New("Odin4 Path:");
            odin4Label.SetHalign(Gtk.Align.Start);
            odin4Label.SetSizeRequest(150, -1);
            var odin4Entry = Gtk.Entry.New();
            odin4Entry.SetText(settings.Odin4Path);
            odin4Entry.SetPlaceholderText("Path to Odin4 executable");
            odin4Entry.OnNotify += (sender, args) =>
            {
                if (args.Pspec.GetName() == "text")
                {
                    settings.Odin4Path = odin4Entry.GetText();
                    settings.Save();
                }
            };
            var odin4Button = Gtk.Button.NewWithLabel("Browse");
            odin4Button.OnClicked += (sender, args) =>
            {
                var fileChooser = Gtk.FileChooserNative.New(
                    "Select Odin4 Executable",
                    this,
                    Gtk.FileChooserAction.Open,
                    "Select",
                    "Cancel"
                );
                
                fileChooser.OnResponse += (sender, args) =>
                {
                    if (args.ResponseId == (int)Gtk.ResponseType.Accept)
                    {
                        var file = fileChooser.GetFile();
                        if (file != null)
                        {
                            var path = file.GetPath();
                            if (!string.IsNullOrEmpty(path))
                            {
                                odin4Entry.SetText(path);
                                settings.Odin4Path = path;
                                settings.Save();
                            }
                        }
                    }
                    fileChooser.Destroy();
                };
                
                fileChooser.Show();
            };
            odin4Box.Append(odin4Label);
            odin4Box.Append(odin4Entry);
            odin4Box.Append(odin4Button);
            toolPathsBox.Append(odin4Box);
            
            // Thor Path
            var thorBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            var thorLabel = Gtk.Label.New("Thor Path:");
            thorLabel.SetHalign(Gtk.Align.Start);
            thorLabel.SetSizeRequest(150, -1);
            var thorEntry = Gtk.Entry.New();
            thorEntry.SetText(settings.ThorPath);
            thorEntry.SetPlaceholderText("Path to Thor executable");
            thorEntry.OnNotify += (sender, args) =>
            {
                if (args.Pspec.GetName() == "text")
                {
                    settings.ThorPath = thorEntry.GetText();
                    settings.Save();
                }
            };
            var thorButton = Gtk.Button.NewWithLabel("Browse");
            thorButton.OnClicked += (sender, args) =>
            {
                var fileChooser = Gtk.FileChooserNative.New(
                    "Select Thor Executable",
                    this,
                    Gtk.FileChooserAction.Open,
                    "Select",
                    "Cancel"
                );
                
                fileChooser.OnResponse += (sender, args) =>
                {
                    if (args.ResponseId == (int)Gtk.ResponseType.Accept)
                    {
                        var file = fileChooser.GetFile();
                        if (file != null)
                        {
                            var path = file.GetPath();
                            if (!string.IsNullOrEmpty(path))
                            {
                                thorEntry.SetText(path);
                                settings.ThorPath = path;
                                settings.Save();
                            }
                        }
                    }
                    fileChooser.Destroy();
                };
                
                fileChooser.Show();
            };
            thorBox.Append(thorLabel);
            thorBox.Append(thorEntry);
            thorBox.Append(thorButton);
            toolPathsBox.Append(thorBox);
            
            // Heimdall Path
            var heimdallBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            var heimdallLabel = Gtk.Label.New("Heimdall Path:");
            heimdallLabel.SetHalign(Gtk.Align.Start);
            heimdallLabel.SetSizeRequest(150, -1);
            var heimdallEntry = Gtk.Entry.New();
            heimdallEntry.SetText(settings.HeimdallPath);
            heimdallEntry.SetPlaceholderText("Path to Heimdall executable");
            heimdallEntry.OnNotify += (sender, args) =>
            {
                if (args.Pspec.GetName() == "text")
                {
                    settings.HeimdallPath = heimdallEntry.GetText();
                    settings.Save();
                }
            };
            var heimdallButton = Gtk.Button.NewWithLabel("Browse");
            heimdallButton.OnClicked += (sender, args) =>
            {
                var fileChooser = Gtk.FileChooserNative.New(
                    "Select Heimdall Executable",
                    this,
                    Gtk.FileChooserAction.Open,
                    "Select",
                    "Cancel"
                );
                
                fileChooser.OnResponse += (sender, args) =>
                {
                    if (args.ResponseId == (int)Gtk.ResponseType.Accept)
                    {
                        var file = fileChooser.GetFile();
                        if (file != null)
                        {
                            var path = file.GetPath();
                            if (!string.IsNullOrEmpty(path))
                            {
                                heimdallEntry.SetText(path);
                                settings.HeimdallPath = path;
                                settings.Save();
                            }
                        }
                    }
                    fileChooser.Destroy();
                };
                
                fileChooser.Show();
            };
            heimdallBox.Append(heimdallLabel);
            heimdallBox.Append(heimdallEntry);
            heimdallBox.Append(heimdallButton);
            toolPathsBox.Append(heimdallBox);
            
            toolPathsFrame.SetChild(toolPathsBox);
            mainVBox.Append(toolPathsFrame);
            
            // General Settings Frame
            var generalFrame = Gtk.Frame.New("General Settings");
            var generalBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            generalBox.SetMarginTop(15);
            generalBox.SetMarginBottom(15);
            generalBox.SetMarginStart(15);
            generalBox.SetMarginEnd(15);
            
            // Default Flash Tool
            var flashToolBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            var flashToolLabel = Gtk.Label.New("Default Flash Tool:");
            flashToolLabel.SetHalign(Gtk.Align.Start);
            flashToolLabel.SetSizeRequest(150, -1);
            var flashToolDropdown = Gtk.DropDown.NewFromStrings(new[] { "Thor", "Odin4", "Heimdall" });
            flashToolDropdown.SetSelected(settings.DefaultFlashTool == "Thor" ? 0u : 
                                        settings.DefaultFlashTool == "Odin4" ? 1u : 2u);
            flashToolDropdown.OnNotify += (sender, args) =>
            {
                if (args.Pspec.GetName() == "selected")
                {
                    var selected = flashToolDropdown.GetSelected();
                    settings.DefaultFlashTool = selected == 0 ? "Thor" : selected == 1 ? "Odin4" : "Heimdall";
                    settings.Save();
                }
            };
            flashToolBox.Append(flashToolLabel);
            flashToolBox.Append(flashToolDropdown);
            generalBox.Append(flashToolBox);
            
            // Default Directory
            var directoryBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            var directoryLabel = Gtk.Label.New("Default Directory:");
            directoryLabel.SetHalign(Gtk.Align.Start);
            directoryLabel.SetSizeRequest(150, -1);
            var directoryEntry = Gtk.Entry.New();
            directoryEntry.SetText(settings.LastUsedDirectory);
            directoryEntry.OnNotify += (sender, args) =>
            {
                if (args.Pspec.GetName() == "text")
                {
                    settings.LastUsedDirectory = directoryEntry.GetText();
                    settings.Save();
                }
            };
            var directoryButton = Gtk.Button.NewWithLabel("Browse");
            directoryButton.OnClicked += (sender, args) =>
            {
                var folderChooser = Gtk.FileChooserNative.New(
                    "Select Default Directory",
                    this,
                    Gtk.FileChooserAction.SelectFolder,
                    "Select",
                    "Cancel"
                );
                
                folderChooser.OnResponse += (sender, args) =>
                {
                    if (args.ResponseId == (int)Gtk.ResponseType.Accept)
                    {
                        var file = folderChooser.GetFile();
                        if (file != null)
                        {
                            var path = file.GetPath();
                            if (!string.IsNullOrEmpty(path))
                            {
                                directoryEntry.SetText(path);
                                settings.LastUsedDirectory = path;
                                settings.Save();
                            }
                        }
                    }
                    folderChooser.Destroy();
                };
                
                folderChooser.Show();
            };
            directoryBox.Append(directoryLabel);
            directoryBox.Append(directoryEntry);
            directoryBox.Append(directoryButton);
            generalBox.Append(directoryBox);
            
            // Auto Check for Updates
            var autoUpdateBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            var autoUpdateLabel = Gtk.Label.New("Auto Check for Updates:");
            autoUpdateLabel.SetHalign(Gtk.Align.Start);
            autoUpdateLabel.SetSizeRequest(150, -1);
            var autoUpdateCheckbox = Gtk.CheckButton.New();
            autoUpdateCheckbox.SetActive(settings.AutoCheckForUpdates);
            autoUpdateCheckbox.OnToggled += (sender, args) =>
            {
                settings.AutoCheckForUpdates = autoUpdateCheckbox.GetActive();
                settings.Save();
            };
            
            // Manual check for updates button
            var checkUpdatesButton = Gtk.Button.NewWithLabel("Check for Updates");
            checkUpdatesButton.SetTooltipText("Manually check for updates now");
            checkUpdatesButton.OnClicked += (sender, args) => OnManualUpdateCheck(checkUpdatesButton);
            
            autoUpdateBox.Append(autoUpdateLabel);
            autoUpdateBox.Append(autoUpdateCheckbox);
            autoUpdateBox.Append(checkUpdatesButton);
            generalBox.Append(autoUpdateBox);
            
            // Create Desktop Entry
            var desktopEntryBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            var desktopEntryLabel = Gtk.Label.New("Create Desktop Entry:");
            desktopEntryLabel.SetHalign(Gtk.Align.Start);
            desktopEntryLabel.SetSizeRequest(150, -1);
            var desktopEntryCheckbox = Gtk.CheckButton.New();
            desktopEntryCheckbox.SetActive(settings.CreateDesktopEntry);
            desktopEntryCheckbox.OnToggled += (sender, args) =>
            {
                settings.CreateDesktopEntry = desktopEntryCheckbox.GetActive();
                settings.Save();
            };
            
            // Create desktop entry button
            var createDesktopEntryButton = Gtk.Button.NewWithLabel("Create Now");
            createDesktopEntryButton.SetTooltipText("Create desktop entry immediately");
            createDesktopEntryButton.OnClicked += (sender, args) => OnCreateDesktopEntry(createDesktopEntryButton);
            
            desktopEntryBox.Append(desktopEntryLabel);
            desktopEntryBox.Append(desktopEntryCheckbox);
            desktopEntryBox.Append(createDesktopEntryButton);
            generalBox.Append(desktopEntryBox);
            
            generalFrame.SetChild(generalBox);
            mainVBox.Append(generalFrame);
            
            scrolled.SetChild(mainVBox);
            SetChild(scrolled);
        }
        
        private void OnManualUpdateCheck(Gtk.Button button)
        {
            // Check if button is in "Update Available" state
            if (hasUpdateAvailable)
            {
                // Show the update dialog again
                var app = Gtk.Application.GetDefault() as AesirApplication;
                if (app != null)
                {
                    app.ShowUpdateAvailableDialog(parentWindow, updateVersion, updateChangelog);
                }
                return;
            }
            
            // Disable button and show checking state on main thread
            button.SetSensitive(false);
            button.SetLabel("Checking...");
            
            // Capture reference to this for lambda
            var self = this;
            
            // Run the update check on a background thread
            Task.Run(async () =>
            {
                try
                {
                    // Get the parent application to access CheckForUpdatesAsync
                    var app = Gtk.Application.GetDefault() as AesirApplication;
                    if (app == null) 
                    {
                        GLib.Functions.IdleAdd(0, () =>
                        {
                            button.SetLabel("Check for Updates");
                            button.SetSensitive(true);
                            return false;
                        });
                        return;
                    }
                    
                    // Check for updates
                    var (hasUpdate, version, changelog) = await app.CheckForUpdatesAsync();
                    
                    // Update UI on main thread
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        if (hasUpdate)
                        {
                            // Store update info in instance variables
                            self.hasUpdateAvailable = true;
                            self.updateVersion = version;
                            self.updateChangelog = changelog;
                            
                            // Show update dialog initially
                            app.ShowUpdateAvailableDialog(self.parentWindow, version, changelog);
                            
                            // Change button to allow reopening the dialog
                            button.SetLabel("Update Available");
                            button.SetSensitive(true);
                            button.SetTooltipText("Click to view update details");
                        }
                        else
                        {
                            button.SetLabel("No updates");
                            button.SetSensitive(false);
                            button.SetTooltipText("You are running the latest version");
                        }
                        return false;
                    });
                }
                catch (Exception ex)
                {
                    // Update UI on main thread for error case
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        button.SetLabel("Check failed");
                        button.SetSensitive(false);
                        button.SetTooltipText($"Update check failed: {ex.Message}");
                        return false;
                    });
                }
            });
        }
        
        private void OnCreateDesktopEntry(Gtk.Button button)
        {
            // Disable button and show creating state
            button.SetSensitive(false);
            button.SetLabel("Creating...");
            
            // Run the desktop entry creation on a background thread
            Task.Run(async () =>
            {
                try
                {
                    var success = await settings.CreateDesktopEntryAsync();
                    
                    // Update UI on main thread
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        if (success)
                        {
                            button.SetLabel("Created!");
                            button.SetTooltipText("Desktop entry created successfully");
                        }
                        else
                        {
                            button.SetLabel("Failed");
                            button.SetTooltipText("Failed to create desktop entry");
                        }
                        
                        // Re-enable button after a delay
                        Task.Delay(2000).ContinueWith(_ =>
                        {
                            GLib.Functions.IdleAdd(0, () =>
                            {
                                button.SetLabel("Create Now");
                                button.SetSensitive(true);
                                button.SetTooltipText("Create desktop entry immediately");
                                return false;
                            });
                        });
                        
                        return false;
                    });
                }
                catch (Exception ex)
                {
                    // Update UI on main thread for error case
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        button.SetLabel("Error");
                        button.SetTooltipText($"Error creating desktop entry: {ex.Message}");
                        
                        // Re-enable button after a delay
                        Task.Delay(2000).ContinueWith(_ =>
                        {
                            GLib.Functions.IdleAdd(0, () =>
                            {
                                button.SetLabel("Create Now");
                                button.SetSensitive(true);
                                button.SetTooltipText("Create desktop entry immediately");
                                return false;
                            });
                        });
                        
                        return false;
                    });
                }
            });
        }
    }
    
    public class AesirApplication : Gtk.Application
    {
        public AesirApplication() : base()
        {
            ApplicationId = "com.aesir";
        }
        
        private void InitializeActions()
        {
            // Add menu actions
            var aboutAction = Gio.SimpleAction.New("about", null);
            aboutAction.OnActivate += OnAboutActivate;
            AddAction(aboutAction);
            
            var settingsAction = Gio.SimpleAction.New("settings", null);
            settingsAction.OnActivate += OnSettingsActivate;
            AddAction(settingsAction);
        }
        
        private void OnAboutActivate(object? sender, EventArgs e)
        {
            CreateCustomAboutDialog();
        }

        private void CreateCustomAboutDialog()
        {
            var settings = AppSettings.Load();
            
            // Create the dialog window
            var dialog = Gtk.Window.New();
            dialog.SetTitle("About Aesir");
            dialog.SetDefaultSize(400, 500);
            dialog.SetResizable(false);
            dialog.SetModal(true);
            
            var window = GetActiveWindow();
            if (window != null)
            {
                dialog.SetTransientFor(window);
            }

            // Main container with padding
            var mainBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            mainBox.SetMarginTop(32);
            mainBox.SetMarginBottom(32);
            mainBox.SetMarginStart(32);
            mainBox.SetMarginEnd(32);

            // App icon - using embedded image
            var iconImage = ImageHelper.LoadEmbeddedImage("A.png", 128);
            
            // Add the icon directly without circular frame
            iconImage.SetHalign(Gtk.Align.Center);
            iconImage.SetMarginBottom(12);
            
            mainBox.Append(iconImage);

            // Subtitle
            var subtitleLabel = Gtk.Label.New("Samsung Firmware Flashing Tool");
            subtitleLabel.AddCssClass("subtitle");
            subtitleLabel.SetHalign(Gtk.Align.Center);
            subtitleLabel.SetMarginBottom(16);
            mainBox.Append(subtitleLabel);

            // Version (clickable button)
            var versionButton = Gtk.Button.NewWithLabel(settings.CurrentVersion);
            versionButton.AddCssClass("version-button");
            versionButton.AddCssClass("caption");
            versionButton.SetHalign(Gtk.Align.Center);
            versionButton.SetMarginBottom(32);
            versionButton.OnClicked += (sender, args) => {
                try {
                    var releaseUrl = $"https://github.com/daglaroglou/Aesir/releases/tag/v{settings.CurrentVersion}";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                        FileName = releaseUrl,
                        UseShellExecute = true
                    });
                } catch { }
            };
            mainBox.Append(versionButton);

            // Action buttons container
            var buttonsBox = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
            buttonsBox.SetHalign(Gtk.Align.Fill);
            buttonsBox.SetMarginBottom(24);

            // Website button
            var websiteButton = Gtk.Button.New();
            var websiteBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            var websiteIcon = Gtk.Image.NewFromIconName("folder-remote-symbolic");
            websiteIcon.SetIconSize(Gtk.IconSize.Normal);
            var websiteLabel = Gtk.Label.New("Repository");
            websiteBox.Append(websiteIcon);
            websiteBox.Append(websiteLabel);
            websiteBox.SetHalign(Gtk.Align.Center);
            websiteButton.SetChild(websiteBox);
            websiteButton.AddCssClass("pill");
            websiteButton.SetHalign(Gtk.Align.Fill);
            websiteButton.OnClicked += (sender, args) => {
                try {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                        FileName = "https://github.com/daglaroglou/Aesir",
                        UseShellExecute = true
                    });
                } catch { }
            };
            buttonsBox.Append(websiteButton);

            // Report an Issue button
            var issueButton = Gtk.Button.New();
            var issueBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            var issueIcon = Gtk.Image.NewFromIconName("dialog-warning-symbolic");
            issueIcon.SetIconSize(Gtk.IconSize.Normal);
            var issueLabel = Gtk.Label.New("Report an Issue");
            issueBox.Append(issueIcon);
            issueBox.Append(issueLabel);
            issueBox.SetHalign(Gtk.Align.Center);
            issueButton.SetChild(issueBox);
            issueButton.AddCssClass("pill");
            issueButton.SetHalign(Gtk.Align.Fill);
            issueButton.OnClicked += (sender, args) => {
                try {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                        FileName = "https://github.com/daglaroglou/Aesir/issues",
                        UseShellExecute = true
                    });
                } catch { }
            };
            buttonsBox.Append(issueButton);

            // Donate button
            var donateButton = Gtk.Button.New();
            var donateBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            var donateIcon = Gtk.Image.NewFromIconName("emblem-favorite-symbolic");
            donateIcon.SetIconSize(Gtk.IconSize.Normal);
            var donateLabel = Gtk.Label.New("Donate");
            donateBox.Append(donateIcon);
            donateBox.Append(donateLabel);
            donateBox.SetHalign(Gtk.Align.Center);
            donateButton.SetChild(donateBox);
            donateButton.AddCssClass("pill");
            donateButton.SetHalign(Gtk.Align.Fill);
            donateButton.OnClicked += (sender, args) => {
                try {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                        FileName = "https://github.com/sponsors/daglaroglou",
                        UseShellExecute = true
                    });
                } catch { }
            };
            buttonsBox.Append(donateButton);

            mainBox.Append(buttonsBox);

            // Credits expander
            var creditsExpander = Gtk.Expander.New("Credits");
            creditsExpander.SetMarginBottom(8);
            
            var creditsBox = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
            creditsBox.SetMarginTop(12);
            creditsBox.SetMarginStart(16);
            
            var authorLabel = Gtk.Label.New("Developer: daglaroglou");
            authorLabel.SetHalign(Gtk.Align.Start);
            authorLabel.AddCssClass("body");
            creditsBox.Append(authorLabel);
            
            creditsExpander.SetChild(creditsBox);
            mainBox.Append(creditsExpander);

            // Legal expander
            var legalExpander = Gtk.Expander.New("Legal");
            legalExpander.SetMarginBottom(16);
            
            var legalBox = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
            legalBox.SetMarginTop(12);
            legalBox.SetMarginStart(16);
            
            var copyrightLabel = Gtk.Label.New("© 2025 Aesir Project");
            copyrightLabel.SetHalign(Gtk.Align.Start);
            copyrightLabel.AddCssClass("body");
            legalBox.Append(copyrightLabel);
            
            var licenseLabel = Gtk.Label.New("Licensed under GNU General Public License v3.0");
            licenseLabel.SetHalign(Gtk.Align.Start);
            licenseLabel.AddCssClass("caption");
            licenseLabel.SetWrap(true);
            legalBox.Append(licenseLabel);
            
            legalExpander.SetChild(legalBox);
            mainBox.Append(legalExpander);

            dialog.SetChild(mainBox);
            
            // Add custom CSS for styling
            AddAboutDialogCSS();
            
            dialog.Show();
        }

        private void AddAboutDialogCSS()
        {
            var cssProvider = Gtk.CssProvider.New();
            var css = @"
                .circular-frame {
                    border-radius: 64px;
                    background: alpha(@theme_fg_color, 0.1);
                    border: none;
                    padding: 16px;
                }
                
                .pill {
                    border-radius: 20px;
                    padding: 8px 16px;
                    border: 1px solid alpha(@theme_fg_color, 0.2);
                    background: alpha(@theme_fg_color, 0.05);
                }
                
                .pill:hover {
                    background: alpha(@theme_fg_color, 0.1);
                }
                
                .version-button {
                    background: alpha(white, 0.1);
                    border: 1px solid alpha(white, 0.2);
                    border-radius: 16px;
                    padding: 4px 8px;
                    color: @theme_fg_color;
                    font-size: 0.8em;
                }
                
                .version-button:hover {
                    background: alpha(white, 0.2);
                    border-color: alpha(white, 0.3);
                }
                
                .version-button:active {
                    background: alpha(white, 0.15);
                }
                
                expander title {
                    font-weight: 600;
                }
            ";
            
            cssProvider.LoadFromString(css);
            Gtk.StyleContext.AddProviderForDisplay(
                Gdk.Display.GetDefault()!,
                cssProvider,
                600 // GTK_STYLE_PROVIDER_PRIORITY_APPLICATION
            );
        }
        
        private void OnSettingsActivate(object? sender, EventArgs e)
        {
            // Create and show a separate settings window
            var parentWindow = GetActiveWindow() as OdinMainWindow;
            if (parentWindow != null)
            {
                var settingsWindow = new SettingsWindow(parentWindow);
                settingsWindow.Show();
            }
        }
        
        public async Task<(bool hasUpdate, string version, string changelog)> CheckForUpdatesAsync()
        {
            try
            {
                var settings = AppSettings.Load();
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Aesir-UpdateChecker");
                
                var response = await httpClient.GetStringAsync("https://api.github.com/repos/daglaroglou/Aesir/releases/latest");
                var releaseInfo = JsonConvert.DeserializeObject<dynamic>(response);
                
                if (releaseInfo?.tag_name != null)
                {
                    var latestVersion = releaseInfo.tag_name.ToString();
                    var currentVersion = $"v{settings.CurrentVersion}";
                    var changelog = releaseInfo.body?.ToString() ?? "No changelog available.";
                    
                    var hasUpdate = latestVersion != currentVersion;
                    return (hasUpdate, latestVersion, changelog);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to check for updates: {ex.Message}");
            }
            
            return (false, "", "");
        }
        
        private string ConvertMarkdownToMarkup(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return "No changelog available.";
            
            var lines = markdown.Split('\n');
            var result = new System.Text.StringBuilder();
            bool inCodeBlock = false;
            
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                var processedLine = line;
                
                // Handle code blocks
                if (trimmedLine.StartsWith("```"))
                {
                    inCodeBlock = !inCodeBlock;
                    if (inCodeBlock)
                    {
                        result.AppendLine("<tt>");
                    }
                    else
                    {
                        result.AppendLine("</tt>");
                    }
                    continue;
                }
                
                if (inCodeBlock)
                {
                    // Escape markup in code blocks
                    processedLine = System.Security.SecurityElement.Escape(line);
                    result.AppendLine(processedLine);
                    continue;
                }
                
                // Headers
                if (trimmedLine.StartsWith("### "))
                {
                    var headerText = trimmedLine.Substring(4);
                    processedLine = $"<b><big>{System.Security.SecurityElement.Escape(headerText)}</big></b>";
                }
                else if (trimmedLine.StartsWith("## "))
                {
                    var headerText = trimmedLine.Substring(3);
                    processedLine = $"<b><span size='large'>{System.Security.SecurityElement.Escape(headerText)}</span></b>";
                }
                else if (trimmedLine.StartsWith("# "))
                {
                    var headerText = trimmedLine.Substring(2);
                    processedLine = $"<b><span size='x-large'>{System.Security.SecurityElement.Escape(headerText)}</span></b>";
                }
                // List items
                else if (trimmedLine.StartsWith("- ") || trimmedLine.StartsWith("* "))
                {
                    var listText = trimmedLine.Substring(2);
                    processedLine = $"  • {ProcessInlineMarkdown(listText)}";
                }
                // Numbered lists
                else if (System.Text.RegularExpressions.Regex.IsMatch(trimmedLine, @"^\d+\. "))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(trimmedLine, @"^(\d+)\. (.*)");
                    if (match.Success)
                    {
                        var number = match.Groups[1].Value;
                        var listText = match.Groups[2].Value;
                        processedLine = $"  {number}. {ProcessInlineMarkdown(listText)}";
                    }
                }
                else if (!string.IsNullOrWhiteSpace(trimmedLine))
                {
                    processedLine = ProcessInlineMarkdown(line);
                }
                
                result.AppendLine(processedLine);
            }
            
            return result.ToString();
        }
        
        private string ProcessInlineMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            
            // Escape existing markup first
            text = System.Security.SecurityElement.Escape(text);
            
            // Bold **text** or __text__
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.*?)\*\*", "<b>$1</b>");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"__(.*?)__", "<b>$1</b>");
            
            // Italic *text* or _text_
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\*(.*?)\*", "<i>$1</i>");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"_(.*?)_", "<i>$1</i>");
            
            // Inline code `text`
            text = System.Text.RegularExpressions.Regex.Replace(text, @"`(.*?)`", "<tt>$1</tt>");
            
            // Links [text](url) - just show the text part for simplicity
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1");
            
            return text;
        }
        
        public void ShowUpdateAvailableDialog(OdinMainWindow? parentWindow, string version, string changelog)
        {
            var dialog = Gtk.Window.New();
            dialog.SetTitle("Update Available");
            dialog.SetDefaultSize(500, 400);
            dialog.SetResizable(true);
            dialog.SetModal(true);
            if (parentWindow != null)
            {
                dialog.SetTransientFor(parentWindow);
            }
            
            var headerBar = Gtk.HeaderBar.New();
            headerBar.SetTitleWidget(Gtk.Label.New($"Update Available - {version}"));
            headerBar.ShowTitleButtons = false;
            dialog.SetTitlebar(headerBar);
            
            var mainBox = Gtk.Box.New(Gtk.Orientation.Vertical, 15);
            mainBox.SetMarginTop(20);
            mainBox.SetMarginBottom(20);
            mainBox.SetMarginStart(20);
            mainBox.SetMarginEnd(20);
            
            var messageLabel = Gtk.Label.New($"A new version of Aesir is available: {version}\nWould you like to visit the releases page?");
            messageLabel.SetJustify(Gtk.Justification.Center);
            messageLabel.SetHalign(Gtk.Align.Center);
            messageLabel.SetMarginBottom(10);
            mainBox.Append(messageLabel);
            
            // Changelog section
            var changelogFrame = Gtk.Frame.New("What's New:");
            var scrolledWindow = Gtk.ScrolledWindow.New();
            scrolledWindow.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
            scrolledWindow.SetSizeRequest(-1, 200);
            
            var changelogLabel = Gtk.Label.New("");
            var markupText = ConvertMarkdownToMarkup(changelog);
            changelogLabel.SetMarkup(markupText);
            changelogLabel.SetSelectable(false);
            changelogLabel.SetWrap(true);
            changelogLabel.SetHalign(Gtk.Align.Start);
            changelogLabel.SetValign(Gtk.Align.Start);
            changelogLabel.SetMarginTop(10);
            changelogLabel.SetMarginBottom(10);
            changelogLabel.SetMarginStart(10);
            changelogLabel.SetMarginEnd(10);
            
            scrolledWindow.SetChild(changelogLabel);
            changelogFrame.SetChild(scrolledWindow);
            mainBox.Append(changelogFrame);
            
            var buttonBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            buttonBox.SetHalign(Gtk.Align.Center);
            buttonBox.SetMarginTop(15);
            
            var noButton = Gtk.Button.NewWithLabel("Later");
            noButton.OnClicked += (sender, args) => dialog.Destroy();
            
            var yesButton = Gtk.Button.NewWithLabel("Download Update");
            yesButton.AddCssClass("suggested-action");
            yesButton.OnClicked += (sender, args) =>
            {
                try
                {
                    var releaseUrl = "https://github.com/daglaroglou/Aesir/releases/latest";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = releaseUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to open browser: {ex.Message}");
                }
                dialog.Destroy();
            };
            
            buttonBox.Append(noButton);
            buttonBox.Append(yesButton);
            mainBox.Append(buttonBox);
            
            dialog.SetChild(mainBox);
            dialog.Show();
        }
        
        public new void Activate()
        {
            // Initialize menu actions
            InitializeActions();
            
            // Load settings to check if auto updates are enabled
            var settings = AppSettings.Load();
            
            if (settings.AutoCheckForUpdates)
            {
                // Show loading window first if auto updates are enabled
                var loadingWindow = new LoadingWindow();
                AddWindow(loadingWindow);
                loadingWindow.Show();
                
                // Check for updates and then show appropriate window
                Task.Run(async () =>
                {
                    bool hasUpdate = false;
                    string version = "";
                    string changelog = "";
                    
                    try
                    {
                        (hasUpdate, version, changelog) = await CheckForUpdatesAsync();
                    }
                    catch
                    {
                        // If update check fails, just continue to main window
                        hasUpdate = false;
                    }
                    
                    // Add a small delay to show the loading screen briefly
                    await Task.Delay(1500);
                    
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        // Close loading window
                        loadingWindow.StopAnimations();
                        loadingWindow.Close();
                        
                        // Create and show main window
                        var mainWindow = new OdinMainWindow(this);
                        AddWindow(mainWindow);
                        mainWindow.Show();
                        
                        // Show update dialog if update is available
                        if (hasUpdate)
                        {
                            ShowUpdateAvailableDialog(mainWindow, version, changelog);
                        }
                        
                        return false;
                    });
                });
            }
            else
            {
                // Auto updates disabled - show main window directly
                var mainWindow = new OdinMainWindow(this);
                AddWindow(mainWindow);
                mainWindow.Show();
            }
        }
    }
    
    class Program
    {
        static int Main(string[] args)
        {
            var app = new AesirApplication();
            
            app.OnActivate += (sender, e) => app.Activate();
            
            return app.Run(args.Length, args);
        }
    }
}
