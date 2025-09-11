using Gtk;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace Aesir
{
    public class OdinMainWindow : Gtk.ApplicationWindow
    {
        // Flash tool selection
        public enum FlashTool
        {
            Odin4,
            Heimdall,
            Thor
        }
        
        private FlashTool selectedFlashTool = FlashTool.Odin4;
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
        
        private Gtk.CheckButton autoRebootCheck = null!;
        private Gtk.CheckButton resetTimeCheck = null!;
        private Gtk.CheckButton rePartitionCheck = null!;
        
        private Gtk.Label odinLogLabel = null!;
        private Gtk.Button startButton = null!;
        private Gtk.Button resetButton = null!;
        private Gtk.Label deviceStatusLabel = null!;
        private Gtk.Label comPortLabel = null!;
        
        // ADB tab controls
        private Gtk.Label adbLogLabel = null!;
        private Gtk.Entry shellCommandEntry = null!;
        private Gtk.Entry packageNameEntry = null!;
        private Gtk.Entry packagePathEntry = null!;
        
        // Log storage
        private List<string> odinLogMessages = new List<string>();
        private List<string> adbLogMessages = new List<string>();

        public OdinMainWindow(Gtk.Application application) : base()
        {
            Application = application;
            Title = "Aesir - Firmware Flash Tool";
            SetDefaultSize(1000, 700);
            Resizable = true;
            
            BuildUI();
            ConnectSignals();
            
            // Initialize Odin log
            LogMessage("Aesir - Firmware Flash Tool");
            LogMessage($"<OSM> Default flash tool: {selectedFlashTool}");
            LogMessage("");
            LogMessage("<OSM> Check device driver installation and device connection.");
            LogMessage("<OSM> Waiting for device...");
            LogMessage("<OSM> WARNING: This tool can modify your device firmware!");
            LogMessage("<OSM> Use at your own risk and ensure you have proper backups.");
            
            // Initialize ADB log
            LogAdbMessage("ADB interface initialized");
            LogAdbMessage("Ready for ADB commands...");
            LogAdbMessage("Make sure ADB is installed and device has USB debugging enabled.");
        }
        
        private void BuildUI()
        {
            // Create main notebook for tabs
            var notebook = Gtk.Notebook.New();
            
            // Create Odin Tab
            var odinTab = CreateOdinTab();
            notebook.AppendPage(odinTab, Gtk.Label.New("Odin"));

            // Create ADB Tab
            var adbTab = CreateAdbTab();
            notebook.AppendPage(adbTab, Gtk.Label.New("ADB"));
            
            // Create GAPPS Tab
            var gappsTab = CreateGappsTab();
            notebook.AppendPage(gappsTab, Gtk.Label.New("GAPPS"));
            
            // Create Other Tab
            var otherTab = CreateOtherTab();
            notebook.AppendPage(otherTab, Gtk.Label.New("Other"));
            
            Child = notebook;
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
            var optionsFrame = Gtk.Frame.New("Options");
            var optionsVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
            optionsVBox.SetMarginTop(10);
            optionsVBox.SetMarginBottom(10);
            optionsVBox.SetMarginStart(10);
            optionsVBox.SetMarginEnd(10);
            
            autoRebootCheck = Gtk.CheckButton.NewWithLabel("Auto Reboot");
            autoRebootCheck.Active = true;
            optionsVBox.Append(autoRebootCheck);
            
            resetTimeCheck = Gtk.CheckButton.NewWithLabel("Reset Time");
            resetTimeCheck.Active = true;
            optionsVBox.Append(resetTimeCheck);
            
            rePartitionCheck = Gtk.CheckButton.NewWithLabel("Re-Partition");
            rePartitionCheck.Active = false;
            optionsVBox.Append(rePartitionCheck);
            
            optionsFrame.Child = optionsVBox;
            middleHBox.Append(optionsFrame);
            
            // Middle - Progress and Status  
            var progressFrame = Gtk.Frame.New("Progress");
            var progressBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            progressBox.SetMarginTop(10);
            progressBox.SetMarginBottom(10);
            progressBox.SetMarginStart(10);
            progressBox.SetMarginEnd(10);
            
            var progressBar = Gtk.ProgressBar.New();
            progressBar.SetShowText(true);
            progressBar.SetText("Ready");
            progressBox.Append(progressBar);
            
            var stepLabel = Gtk.Label.New("Step: Waiting for device...");
            stepLabel.Xalign = 0;
            progressBox.Append(stepLabel);
            
            var timeLabel = Gtk.Label.New("Elapsed: 00:00");
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
            flashToolDropDown.SetSelected(0); // Default to Odin4
            flashToolDropDown.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "selected")
                {
                    selectedFlashTool = (FlashTool)flashToolDropDown.GetSelected();
                    UpdateFlashToolLabel();
                    LogMessage($"<OSM> Flash tool changed to: {selectedFlashTool}");
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
            
            // Middle section - Three frames side by side (matching Odin structure)
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
            
            return mainVBox;
        }
        
        private Gtk.Widget CreateGappsTab()
        {
            var mainVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            mainVBox.SetMarginTop(10);
            mainVBox.SetMarginBottom(10);
            mainVBox.SetMarginStart(10);
            mainVBox.SetMarginEnd(10);
            
            var titleLabel = Gtk.Label.New(null);
            titleLabel.SetMarkup("<span size='16000' weight='bold'>Google Apps (GAPPS) Management</span>");
            titleLabel.SetMarginBottom(20);
            mainVBox.Append(titleLabel);
            
            var descLabel = Gtk.Label.New("This section provides links and information for downloading Google Apps packages.");
            descLabel.SetWrapMode(Pango.WrapMode.Word);
            descLabel.SetMarginBottom(15);
            mainVBox.Append(descLabel);
            
            // GAPPS Download Links
            var linksFrame = Gtk.Frame.New("GAPPS Download Sources");
            var linksBox = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            linksBox.SetMarginTop(10);
            linksBox.SetMarginBottom(10);
            linksBox.SetMarginStart(10);
            linksBox.SetMarginEnd(10);
            
            var sources = new[]
            {
                ("OpenGApps", "https://opengapps.org/", "Comprehensive Google Apps packages"),
                ("BiTGApps", "https://bitgapps.github.io/", "Lightweight Google Apps"),
                ("MindTheGapps", "https://wiki.lineageos.org/gapps.html", "LineageOS recommended GAPPS"),
                ("NikGApps", "https://nikgapps.com/", "Customizable Google Apps packages")
            };
            
            foreach (var (name, url, desc) in sources)
            {
                var sourceBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
                
                var infoBox = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
                var nameLabel = Gtk.Label.New(null);
                nameLabel.SetMarkup($"<span weight='bold'>{name}</span>");
                nameLabel.Xalign = 0;
                var descLabel2 = Gtk.Label.New(desc);
                descLabel2.Xalign = 0;
                descLabel2.AddCssClass("dim-label");
                infoBox.Append(nameLabel);
                infoBox.Append(descLabel2);
                
                var openButton = Gtk.Button.NewWithLabel("Open");
                openButton.OnClicked += (s, e) => OpenUrl(url);
                
                sourceBox.Append(infoBox);
                sourceBox.Append(openButton);
                linksBox.Append(sourceBox);
            }
            
            linksFrame.Child = linksBox;
            mainVBox.Append(linksFrame);
            
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
            
            // Entry change events using notify::text property
            apFileEntry.OnNotify += (sender, e) => {
                if (e.Pspec.GetName() == "text")
                    CheckStartButtonState();
            };
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
            filter.SetName("Firmware Files (*.tar.md5, *.img, *.bin)");
            filter.AddPattern("*.tar.md5");
            filter.AddPattern("*.img");
            filter.AddPattern("*.bin");
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
                        LogMessage($"<OSM> {partition} file selected: {Path.GetFileName(path ?? "")}");
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
        
        private void CheckStartButtonState()
        {
            // Enable start button if at least AP file is selected
            bool canStart = !string.IsNullOrEmpty(apFileEntry.GetText());
            startButton.Sensitive = canStart;
            
            if (canStart)
            {
                LogMessage("<OSM> Ready to flash! Click Start to begin.");
            }
        }
        
        private void OnStartClicked(object? sender, EventArgs e)
        {
            LogMessage("");
            LogMessage($"<OSM> Firmware flashing started using {selectedFlashTool}...");
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
            
            LogMessage($"<OSM> Checking device connection for {selectedFlashTool}...");
            deviceStatusLabel.SetText("Device Status: Checking connection...");
            
            // Check for device connection using selected flash tool
            CheckDeviceConnection();
        }
        
        private async void CheckDeviceConnection()
        {
            try
            {
                // Try to detect devices in download mode
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
                    LogMessage($"<OSM> Samsung device detected for {selectedFlashTool}!");
                    deviceStatusLabel.SetText($"Device Status: Ready ({selectedFlashTool})");
                    comPortLabel.SetText($"Connection: USB ({selectedFlashTool})");
                    LogMessage($"<OSM> Ready to flash firmware using {selectedFlashTool}. WARNING: This will modify your device!");
                }
                else
                {
                    LogMessage($"<OSM> No Samsung device detected for {selectedFlashTool}.");
                    LogMessage($"<OSM> Please ensure device is in download mode and connected via USB for {selectedFlashTool}.");
                    deviceStatusLabel.SetText("Device Status: Not detected");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"<OSM> Error checking device connection: {ex.Message}");
                deviceStatusLabel.SetText("Device Status: Error");
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
            
            // Reset options
            autoRebootCheck.Active = true;
            resetTimeCheck.Active = true;
            rePartitionCheck.Active = false;
            
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
        
        // ADB Methods
        private async Task RefreshAdbDevices()
        {
            LogAdbMessage("Refreshing ADB devices...");
            LogAdbMessage("$ adb devices");
            
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
            
            LogAdbMessage($"Installing package: {Path.GetFileName(packagePath)}");
            LogAdbMessage("This may take a while...");
            
            var output = await RunAdbCommand($"install \"{packagePath}\"");
            
            if (output.Contains("Success"))
            {
                LogAdbMessage("Package installed successfully!");
            }
            else if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
            }
            else
            {
                LogAdbMessage($"Install result: {output.Trim()}");
            }
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
            
            LogAdbMessage($"Uninstalling package: {packageName}");
            
            var output = await RunAdbCommand($"uninstall {packageName}");
            
            if (output.Contains("Success"))
            {
                LogAdbMessage("Package uninstalled successfully!");
            }
            else if (output.StartsWith("Error"))
            {
                LogAdbMessage(output);
            }
            else
            {
                LogAdbMessage($"Uninstall result: {output.Trim()}");
            }
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
            
            LogAdbMessage($"$ adb shell {command}");
            
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
            LogAdbMessage("$ adb devices");

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
            adbLogMessages.Add(timestamp + message);
            if (adbLogMessages.Count > 100)
            {
                adbLogMessages.RemoveAt(0);
            }
            adbLogLabel.SetText(string.Join("\n", adbLogMessages));
        }
    }
    
    public class AesirApplication : Gtk.Application
    {
        public AesirApplication() : base()
        {
            ApplicationId = "com.aesir";
        }
        
        public new void Activate()
        {
            var window = new OdinMainWindow(this);
            AddWindow(window);
            window.Show();
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
