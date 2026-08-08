using Force.Crc32;
using Serilog;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using UWPHook.Properties;
using UWPHook.SteamGridDb;
using VDFParser;
using VDFParser.Models;

namespace UWPHook
{
    /// <summary>
    /// Interaction logic for GamesWindow.xaml
    /// </summary>
    public partial class GamesWindow : Window
    {
        AppEntryModel Apps;
        BackgroundWorker bwrLoad;
        static LoggingLevelSwitch levelSwitch = new LoggingLevelSwitch();

        public GamesWindow()
        {
            InitializeComponent();
            Log.Debug("Init GamesWindow");
            Apps = new AppEntryModel();
            var args = Environment.GetCommandLineArgs();

            // Init log file to AppData\Roaming\Briano\UWPHook directory with size rotation on 10Mb with max 5 files
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
            string loggerFilePath = String.Join("\\", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), fvi.CompanyName, fvi.ProductName, "application.log");

            Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .WriteTo.File(path: loggerFilePath, rollOnFileSizeLimit: true, fileSizeLimitBytes: 10485760, retainedFileCountLimit: 5)
            .WriteTo.Console()
            .CreateLogger();

            // Switch to Info by default to inform logger level in log file and switch to the correct log level
            levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Information;
            SetLogLevel();

            // If null or 1, the app was launched normally
            if (args?.Length > 1)
            {
                if (args.Any(arg => arg.Equals("--sync-xbox", StringComparison.OrdinalIgnoreCase)))
                {
                    _ = SyncXboxGamesAsync(false, true);
                }
                else
                {
                    // When length is 1, the only argument is the path where the app is installed
                    _ = LauncherAsync(args); // Launches the requested game
                }
            }
            else
            {
                autoSyncXboxGamesCheckBox.IsChecked = Settings.Default.AutoSyncXboxGames;

                if (Settings.Default.AutoSyncXboxGames)
                {
                    _ = SyncXboxGamesAsync(false, false);
                }
                else
                {
                    //auto refresh on load
                    LoadButton_Click(null, null);
                }
            }
        }

        /// <summary>
        /// We have to wait a little untill Steam catches up, otherwise it will stream a black screen
        /// </summary>
        /// <returns></returns>
        async Task LaunchDelay()
        {
            await Task.Delay(10000);
        }

        /// <summary>
        /// Main task that launches a game
        /// Usually invoked by steam
        /// </summary>
        /// <param name="args">launch args received from the program execution</param>
        /// <returns></returns>
        private async Task LauncherAsync(string[] args)
        {
            FullScreenLauncher launcher = null;
            //So, for some reason, Steam is now stopping in-home streaming if the launched app is minimized, so not hiding UWPHook's window is doing the trick for now
            if (Settings.Default.StreamMode)
            {
                this.Hide();
                launcher = new FullScreenLauncher();
                launcher.Show();

                await LaunchDelay();

                launcher.Close();
            }
            else
            {
                this.Title = "UWPHook: Playing a game";
                this.Hide();
            }

            //Hide the window so the app is launched seamless making UWPHook run in the background without bothering the user
            string currentLanguage = CultureInfo.CurrentCulture.ToString();

            try
            {
                //Some apps have their language locked to the UI language of the system, so overriding it might change the language of the game
                //I my self couldn't get this to work on neither Forza Horizon 3 or Halo 5 Forge, @AbGedreht reported it works tho
                if (Settings.Default.ChangeLanguage && !String.IsNullOrEmpty(Settings.Default.TargetLanguage))
                {
                    ScriptManager.RunScript("Set-WinUILanguageOverride " + Properties.Settings.Default.TargetLanguage);
                }

                if (Settings.Default.ChangeResolution && !String.IsNullOrEmpty(Settings.Default.TargetResolution))
                {
                    var targetResolution = ExtractDimensions(Settings.Default.TargetResolution);
                    ScriptManager.RunScript("Set-DisplayResolution -Width " + targetResolution.Width + " - Height " + targetResolution.Height + " -Force");
                }

                //The only other parameter Steam will send is the app AUMID
                AppManager.LaunchUWPApp(args);

                //While the current launched app is running, sleep for n seconds and then check again
                while (AppManager.IsRunning())
                {
                    Thread.Sleep(Settings.Default.Seconds * 1000);
                }
            }
            catch (Exception e)
            {
                this.Show();
                MessageBox.Show(e.Message, "UWPHook", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (Settings.Default.ChangeLanguage && !String.IsNullOrEmpty(Settings.Default.TargetLanguage))
                {
                    ScriptManager.RunScript("Set-WinUILanguageOverride " + currentLanguage);
                }

                //The user has probably finished using the app, so let's close UWPHook to keep the experience clean 
                this.Close();
            }
        }

        static (int Width, int Height) ExtractDimensions(string resolution)
        {
            var parts = resolution.Split('x');
            if (parts.Length == 2)
            {
                if (int.TryParse(parts[0].Trim(), out int width) && int.TryParse(parts[1].Trim(), out int height))
                {
                    return (width, height);
                }
            }
            throw new FormatException("Invalid resolution format.");
        }
        
        /// <summary>
        /// Generates a CRC32 hash expected by Steam to link an image with a game in the library
        /// See https://blog.yo1.dog/calculate-id-for-non-steam-games-js/ for an example
        /// </summary>
        /// <param name="appName">The name of the executable to be displayed</param>
        /// <param name="appTarget">The executable target path</param>
        /// <returns></returns>
        private UInt64 GenerateSteamGridAppId(string appName, string appTarget)
        {
            byte[] nameTargetBytes = Encoding.UTF8.GetBytes(appTarget + appName + "");
            UInt64 crc = Crc32Algorithm.Compute(nameTargetBytes);
            UInt64 gameId = crc | 0x80000000;

            return gameId;
        }

        /// <summary>
        /// Task responsible for triggering the export, blocks the UI, and shows a message
        /// once the task is finished, unlocking the UI
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            grid.IsEnabled = false;
            progressBar.Visibility = Visibility.Visible;

            bool result = false;
            string msg = String.Empty;

            try
            {
                result = await ExportGames(Apps.Entries.Where(app => app.Selected));
                RefreshSteamStatuses();

                msg = "Your apps were successfuly exported!";
                if (result)
                {
                    msg += " Steam data and collections were updated.";
                }

            }
            catch (TaskCanceledException exception)
            {
                Log.Error(exception.Message);
                msg = exception.Message;
            }

            grid.IsEnabled = true;
            progressBar.Visibility = Visibility.Collapsed;

            MessageBox.Show(msg, "UWPHook", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void SyncXboxGamesButton_Click(object sender, RoutedEventArgs e)
        {
            await SyncXboxGamesAsync(true, false);
        }

        private void AutoSyncXboxGamesCheckBox_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.AutoSyncXboxGames = autoSyncXboxGamesCheckBox.IsChecked == true;
            Settings.Default.Save();
        }

        /// <summary>
        /// Downloads the given image in the url to a given path in a given format
        /// </summary>
        /// <param name="imageUrl">The url for the image</param>
        /// <param name="destinationFilename">Path to store the image</param>
        /// <param name="format"></param>
        /// <returns></returns>
        private async Task SaveImage(string imageUrl, string destinationFilename, ImageFormat format)
        {
            await Task.Run(() =>
            {
                WebClient client = new WebClient();
                Stream stream = null;
                try
                {
                    stream = client.OpenRead(imageUrl);
                }
                catch (Exception exception)
                {
                    Log.Error(exception.Message);
                    //Image with error?
                    //Skip for now
                }

                if (stream != null)
                {
                    Bitmap bitmap; bitmap = new Bitmap(stream);
                    bitmap.Save(destinationFilename, format);
                    stream.Flush();
                    stream.Close();
                    client.Dispose();
                }
            });
        }

        /// <summary>
        /// Copies all temporary images to the given user
        /// </summary>
        /// <param name="user">The user path to copy images to</param>
        private void CopyTempGridImagesToSteamUser(string user)
        {
            string tmpGridDirectory = Path.GetTempPath() + "UWPHook\\tmp_grid\\";
            string userGridDirectory = user + "\\config\\grid\\";

            // No images were downloaded, maybe the key is invalid or no app had an image
            if (!Directory.Exists(tmpGridDirectory))
            {
                return;
            }

            string[] images = Directory.GetFiles(tmpGridDirectory);

            if (!Directory.Exists(userGridDirectory))
            {
                Directory.CreateDirectory(userGridDirectory);
            }

            foreach (string image in images)
            {
                string destFile = userGridDirectory + Path.GetFileName(image);
                File.Copy(image, destFile, true);
            }
        }

        private void RemoveTempGridImages()
        {
            string tmpGridDirectory = Path.GetTempPath() + "UWPHook\\tmp_grid\\";
            if (Directory.Exists(tmpGridDirectory))
            {
                Directory.Delete(tmpGridDirectory, true);
            }
        }

        /// <summary>
        /// Task responsible for downloading grid images to a temporary location,
        /// generates the steam ID for the game based in the receiving parameters,
        /// Throws TaskCanceledException if cannot communicate with SteamGridDB properly
        /// </summary>
        /// <param name="appName">The name of the app</param>
        /// <param name="appTarget">The target path of the executable</param>
        /// <returns></returns>
        private async Task DownloadTempGridImages(string appName, string appTarget)
        {
            SteamGridDbApi api = new SteamGridDbApi(Properties.Settings.Default.SteamGridDbApiKey);
            string tmpGridDirectory = Path.GetTempPath() + "UWPHook\\tmp_grid\\";
            GameResponse[] games = null;

            try
            {
                games = await api.SearchGame(appName);
            }
            catch (TaskCanceledException exception)
            {
                Log.Error(exception.Message);
            }

            if (games != null && games.Length > 0)
            {
                var game = games.FirstOrDefault(candidate =>
                    String.Equals(candidate.Name, appName, StringComparison.OrdinalIgnoreCase)) ?? games[0];
                Log.Verbose("Detected Game: " + game.ToString());
                UInt64 gameId = GenerateSteamGridAppId(appName, appTarget);

                SafellyCreateTmpGridDirectory(tmpGridDirectory);

                var gameGridsVertical = api.GetGameGrids(game.Id, "600x900,342x482,660x930");
                var gameGridsHorizontal = api.GetGameGrids(game.Id, "460x215,920x430");
                var gameHeroes = api.GetGameHeroes(game.Id);
                var gameLogos = api.GetGameLogos(game.Id);

                Log.Verbose("Game ID: " + game.Id);

                await Task.WhenAll(
                    gameGridsVertical,
                    gameGridsHorizontal,
                    gameHeroes,
                    gameLogos
                );

                var gridsVertical = await gameGridsVertical;
                var gridsHorizontal = await gameGridsHorizontal;
                var heroes = await gameHeroes;
                var logos = await gameLogos;

                List<Task> saveImagesTasks = new List<Task>();

                if (gridsHorizontal != null && gridsHorizontal.Length > 0)
                {
                    var grid = gridsHorizontal[0];
                    saveImagesTasks.Add(SaveImage(grid.Url, $"{tmpGridDirectory}\\{gameId}.png", ImageFormat.Png));
                }

                if (gridsVertical != null && gridsVertical.Length > 0)
                {
                    var grid = gridsVertical[0];
                    saveImagesTasks.Add(SaveImage(grid.Url, $"{tmpGridDirectory}\\{gameId}p.png", ImageFormat.Png));
                }

                if (heroes != null && heroes.Length > 0)
                {
                    var hero = heroes[0];
                    saveImagesTasks.Add(SaveImage(hero.Url, $"{tmpGridDirectory}\\{gameId}_hero.png", ImageFormat.Png));
                }

                if (logos != null && logos.Length > 0)
                {
                    var logo = logos[0];
                    saveImagesTasks.Add(SaveImage(logo.Url, $"{tmpGridDirectory}\\{gameId}_logo.png", ImageFormat.Png));
                }

                await Task.WhenAll(saveImagesTasks);
            }
        }

        private void SafellyCreateTmpGridDirectory(string tmpGridDirectory)
        {
            if (!Directory.Exists(tmpGridDirectory))
            {
                try
                {
                    Directory.CreateDirectory(tmpGridDirectory);
                    Log.Information("Created directory: " + tmpGridDirectory);
                }
                catch (Exception exception)
                {
                    Log.Error(exception.Message);
                    Log.Error(exception.StackTrace);
                    throw new Exception("UWPHook was not able to create the tmp directory for some reason." + Environment.NewLine +
                        "You may solve this by manually creating the following folder:" + Environment.NewLine +
                        tmpGridDirectory);
                }
            }
        }

        /// <summary>
        /// Main Task to export the selected games to steam
        /// </summary>
        /// <returns></returns>
        private async Task<bool> ExportGames(IEnumerable<AppEntry> appsToExport, ISet<string>? installedAppAumids = null)
        {
            string[] tags = SteamCollectionManager.ParseCollectionNames(Settings.Default.Tags);
            string steam_folder = SteamManager.GetSteamFolder();
            bool shortcutsChanged = false;

            if (Directory.Exists(steam_folder))
            {
                var users = SteamManager.GetUsers(steam_folder);
                var selected_apps = appsToExport.ToList();
                var processModule = Process.GetCurrentProcess().MainModule;
                var exePath = processModule?.FileName;
                var exeDir = Path.GetDirectoryName(exePath);

                List<Task> gridImagesDownloadTasks = new List<Task>();
                bool downloadGridImages = !String.IsNullOrEmpty(Properties.Settings.Default.SteamGridDbApiKey);
                //To make things faster, decide icons and download grid images before looping users
                Log.Verbose("downloadGridImages: " + (downloadGridImages));

                foreach (var app in selected_apps)
                {
                    app.Icon = app.widestSquareIcon();

                    if (downloadGridImages)
                    {
                        Log.Verbose("Downloading grid images for app " + app.Name);

                        gridImagesDownloadTasks.Add(DownloadTempGridImages(app.Name, exePath));
                    }
                }

                await Task.WhenAll(gridImagesDownloadTasks);

                string? steamExeToRestart = await StopSteam();
                try
                {
                    // Export the selected apps and the downloaded images to each user
                    // while Steam is stopped so its in-memory state cannot overwrite them.
                    foreach (var user in users)
                    {
                        try
                        {
                            VDFEntry[] shortcuts = new VDFEntry[0];
                            try
                            {
                                shortcuts = SteamManager.ReadShortcuts(user);
                            }
                            catch (Exception ex)
                            {
                                //If it's a short VDF, let's just overwrite it
                                if (ex.GetType() != typeof(VDFTooShortException))
                                {
                                    Log.Error("Error: Program failed to load existing Steam shortcuts." + Environment.NewLine + ex.Message);
                                    throw new Exception("Error: Program failed to load existing Steam shortcuts." + Environment.NewLine + ex.Message);
                                }
                            }

                            if (shortcuts != null)
                            {
                            VDFEntry[] originalShortcuts = shortcuts.ToArray();
                            bool userShortcutsChanged = false;

                            if (installedAppAumids != null)
                            {
                                var retainedShortcuts = shortcuts
                                    .Where(shortcut => !IsUninstalledUWPHookApp(shortcut, exePath, installedAppAumids))
                                    .ToArray();

                                if (retainedShortcuts.Length != shortcuts.Length)
                                {
                                    Log.Information($"Removing {shortcuts.Length - retainedShortcuts.Length} uninstalled app shortcut(s) for Steam user {user}.");
                                    shortcuts = retainedShortcuts;

                                    for (int i = 0; i < shortcuts.Length; i++)
                                    {
                                        shortcuts[i].Index = i;
                                    }

                                    userShortcutsChanged = true;
                                }
                            }

                            foreach (var app in selected_apps)
                            {
                                try
                                {
                                    app.Icon = PersistAppIcon(app);
                                    Log.Verbose("Defaulting to app.Icon for app " + app.Name);
                                }
                                catch (System.IO.IOException)
                                {
                                    Log.Verbose("Using backup icon for app " + app.Name);

                                    await Task.Run(() =>
                                    {
                                        string tmpGridDirectory = Path.GetTempPath() + "UWPHook\\tmp_grid\\";

                                        SafellyCreateTmpGridDirectory(tmpGridDirectory);

                                        string[] images = Directory.GetFiles(tmpGridDirectory);

                                        UInt64 gameId = GenerateSteamGridAppId(app.Name, exePath);
                                        app.Icon = PersistAppIcon(app, tmpGridDirectory + gameId + "_logo.png");
                                    });
                                }

                                VDFEntry newApp = new VDFEntry()
                                {
                                    appid = ((int)GenerateSteamGridAppId(app.Name, exePath)),
                                    AppName = app.Name,
                                    Exe = exePath,
                                    StartDir = exeDir,
                                    LaunchOptions = app.Aumid + " " + app.Executable,
                                    AllowDesktopConfig = 1,
                                    AllowOverlay = 1,
                                    Icon = app.Icon,
                                    Index = shortcuts.Length,
                                    IsHidden = 0,
                                    OpenVR = 0,
                                    ShortcutPath = "",
                                    Tags = tags,
                                    Devkit = 0,
                                    DevkitGameID = "",
                                    LastPlayTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                };
                                Boolean shortcutAlreadyExists = false;
                                for (int i = 0; i < shortcuts.Length; i++)
                                {
                                    Log.Verbose(shortcuts[i].ToString());

                                    if (shortcuts[i].AppName == app.Name && shortcuts[i].Exe == exePath)
                                    {
                                        shortcutAlreadyExists = true;
                                        newApp.Index = shortcuts[i].Index;

                                        if (!IsSameSteamShortcut(shortcuts[i], newApp))
                                        {
                                            Log.Verbose(app.Name + " already added to Steam. Updating existing shortcut.");
                                            shortcuts[i] = newApp;
                                            userShortcutsChanged = true;
                                        }
                                    }
                                }

                                if (!shortcutAlreadyExists)
                                {
                                    //Resize this array so it fits the new entries
                                    Array.Resize(ref shortcuts, shortcuts.Length + 1);
                                    shortcuts[shortcuts.Length - 1] = newApp;
                                    userShortcutsChanged = true;
                                }
                            }

                            var previousUWPHookShortcuts = originalShortcuts
                                .Where(shortcut => IsUWPHookShortcut(shortcut, exePath))
                                .ToArray();
                            var currentUWPHookShortcuts = shortcuts
                                .Where(shortcut => IsUWPHookShortcut(shortcut, exePath))
                                .ToArray();

                            if (userShortcutsChanged)
                            {
                                try
                                {
                                    if (!Directory.Exists(user + @"\\config\\"))
                                    {
                                        Directory.CreateDirectory(user + @"\\config\\");
                                    }

                                    BackupShortcutsVDF(user, @"\\config\\shortcuts.vdf");

                                    //Write the file with all the shortcuts
                                    File.WriteAllBytes(user + @"\\config\\shortcuts.vdf", VDFSerializer.Serialize(shortcuts));
                                    Log.Debug("Shortcuts written to " + user + @"\\config\\shortcuts.vdf");
                                }
                                catch (Exception ex)
                                {
                                    Log.Error("Error: Program failed while trying to write your Steam shortcuts" + Environment.NewLine + ex.InnerException + ex.StackTrace);
                                    throw new Exception("Error: Program failed while trying to write your Steam shortcuts" + Environment.NewLine + ex.Message);
                                }
                            }

                            bool collectionsChanged = SteamCollectionManager.Synchronize(
                                user,
                                tags,
                                currentUWPHookShortcuts.Select(shortcut => shortcut.appid),
                                previousUWPHookShortcuts.Select(shortcut => shortcut.appid),
                                previousUWPHookShortcuts.SelectMany(shortcut => shortcut.Tags ?? Array.Empty<string>()));

                            shortcutsChanged |= userShortcutsChanged || collectionsChanged;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Error: Program failed exporting your games:" + Environment.NewLine + ex.Message + ex.StackTrace);
                            MessageBox.Show("Error: Program failed exporting your games:" + Environment.NewLine + ex.Message + ex.StackTrace);
                        }
                    }

                    if (gridImagesDownloadTasks.Count > 0)
                    {
                        await Task.WhenAll(gridImagesDownloadTasks);

                        await Task.Run(() =>
                        {
                            foreach (var user in users)
                            {
                                CopyTempGridImagesToSteamUser(user);
                            }

                            RemoveTempGridImages();
                        });
                    }
                }
                finally
                {
                    StartSteam(steamExeToRestart);
                }
            }

            return shortcutsChanged;
        }

        private bool IsUninstalledUWPHookApp(VDFEntry shortcut, string uwpHookExePath, ISet<string> installedAppAumids)
        {
            if (!String.Equals(shortcut.Exe, uwpHookExePath, StringComparison.OrdinalIgnoreCase)
                || String.IsNullOrWhiteSpace(shortcut.LaunchOptions))
            {
                return false;
            }

            string? aumid = shortcut.LaunchOptions
                .Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim('"');

            return !String.IsNullOrWhiteSpace(aumid)
                && aumid.Contains("!")
                && !installedAppAumids.Contains(aumid);
        }

        private bool IsUWPHookShortcut(VDFEntry shortcut, string uwpHookExePath)
        {
            return String.Equals(shortcut.Exe?.Trim('"'), uwpHookExePath.Trim('"'), StringComparison.OrdinalIgnoreCase);
        }

        private bool IsSameSteamShortcut(VDFEntry existing, VDFEntry updated)
        {
            return existing.appid == updated.appid
                && existing.AppName == updated.AppName
                && existing.Exe == updated.Exe
                && existing.StartDir == updated.StartDir
                && existing.LaunchOptions == updated.LaunchOptions
                && existing.AllowDesktopConfig == updated.AllowDesktopConfig
                && existing.AllowOverlay == updated.AllowOverlay
                && existing.Icon == updated.Icon
                && existing.Index == updated.Index
                && existing.IsHidden == updated.IsHidden
                && existing.OpenVR == updated.OpenVR
                && existing.ShortcutPath == updated.ShortcutPath
                && existing.Devkit == updated.Devkit
                && existing.DevkitGameID == updated.DevkitGameID
                && Enumerable.SequenceEqual(existing.Tags ?? new string[0], updated.Tags ?? new string[0]);
        }

        private void BackupShortcutsVDF(string userPath, string vdfSubPath)
        {
            string backupFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Briano", "UWPHook", "backups");
            if (!Directory.Exists(backupFolder))
            {
                Directory.CreateDirectory(backupFolder);
                Log.Debug("Created backup folder: " + backupFolder);
            }

            string user_id = userPath.Split('\\').Last();

            string sourceFilePath = Path.Combine(userPath, "config", "shortcuts.vdf");
            string destinationFileName = Path.Combine(backupFolder, $"{user_id}_{DateTime.Now.ToString("yyyyMMddHHmmss")}_shortcuts.vdf");

            if (!File.Exists(sourceFilePath))
            {
               Log.Information("No VDF file found for user: " + sourceFilePath);
               return;
            }

            File.Copy(sourceFilePath, destinationFileName);
            Log.Debug("Backup created: " + destinationFileName);
        }

        /// <summary>
        /// Copies an apps icon to a intermediate location
        /// Due to some apps changing the icon location when they update, which causes icons to be "lost"
        /// </summary>
        /// <param name="app">App to copy the icon to</param>
        /// <param name="forcedIcon">Overwrites the app.icon to be copied</param>
        /// <returns>string, the path to the usable and persisted icon</returns>
        private string PersistAppIcon(AppEntry app, string forcedIcon = "")
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string icons_path = path + @"\Briano\UWPHook\icons\";

            // If we do not have an specific icon to copy, copy app.icon, if we do, copy the specified icon
            string icon_to_copy = String.IsNullOrEmpty(forcedIcon) ? app.Icon : forcedIcon;

            if (!Directory.Exists(icons_path))
            {
                Directory.CreateDirectory(icons_path);
            }

            string dest_file = String.Join(String.Empty, icons_path, app.Aumid + Path.GetFileName(icon_to_copy));
            try
            {
                if (File.Exists(icon_to_copy))
                {
                    File.Copy(icon_to_copy, dest_file, true);
                }
                else
                {
                    dest_file = app.Icon;
                }
            }
            catch (System.IO.IOException e)
            {
                Log.Warning(e, "Could not copy icon " + app.Icon);
                throw e;
            }

            return dest_file;
        }

        /// <summary>
        /// Gracefully stops Steam before editing its on-disk state.
        /// </summary>
        /// <returns>The Steam executable to restart, or null when Steam was not running.</returns>
        private async Task<string?> StopSteam()
        {
            Func<Process?> getSteam = () => Process.GetProcessesByName("steam").FirstOrDefault();
            Process steam = getSteam();

            if (steam == null)
            {
                Log.Debug("Steam instance not found; no restart will be needed.");
                return null;
            }

            string steamExe = steam.MainModule.FileName;

            Log.Debug("Requesting Steam shutdown before updating shortcuts and collections");
            Process.Start(steamExe, "-exitsteam");

            Stopwatch watch = Stopwatch.StartNew();
            const int waitSeconds = 15;
            while (watch.Elapsed.TotalSeconds < waitSeconds)
            {
                if (getSteam() == null)
                {
                    Log.Debug("Steam stopped successfully");
                    return steamExe;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            throw new TaskCanceledException("Steam did not close within 15 seconds. No shortcuts or collections were changed.");
        }

        private void StartSteam(string? steamExe)
        {
            if (String.IsNullOrWhiteSpace(steamExe))
            {
                return;
            }

            try
            {
                Log.Debug("Restarting Steam");
                Process.Start(steamExe);
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Steam could not be restarted");
                MessageBox.Show("Steam data was updated, but Steam could not be restarted. Please launch it manually.", "UWPHook", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public static void ClearAllShortcuts()
        {
            Log.Debug("Clearing all elements in shortcuts.vdf");
            string[] tags = Settings.Default.Tags.Split(',');
            string steam_folder = SteamManager.GetSteamFolder();

            if (Directory.Exists(steam_folder))
            {
                var users = SteamManager.GetUsers(steam_folder);
                var exePath = @"""" + System.Reflection.Assembly.GetExecutingAssembly().Location + @"""";
                var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

                foreach (var user in users)
                {
                    try
                    {
                        VDFEntry[] shortcuts = new VDFEntry[0];

                        try
                        {
                            if (!Directory.Exists(user + @"\\config\\"))
                            {
                                Directory.CreateDirectory(user + @"\\config\\");
                            }
                            //Write the file with all the shortcuts
                            File.WriteAllBytes(user + @"\\config\\shortcuts.vdf", VDFSerializer.Serialize(shortcuts));
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Error: Program failed while trying to write your Steam shortcuts" + Environment.NewLine + ex.Message);
                            throw new Exception("Error: Program failed while trying to write your Steam shortcuts" + Environment.NewLine + ex.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Error: Program failed while trying to clear your Steam shortcuts:" + Environment.NewLine + ex.Message + ex.StackTrace);
                        MessageBox.Show("Error: Program failed while trying to clear your Steam shortcuts:" + Environment.NewLine + ex.Message + ex.StackTrace);
                    }
                }
                MessageBox.Show("All non-Steam shortcuts has been cleared.");
            }
        }

        private async Task SyncXboxGamesAsync(bool showCompletion, bool closeWhenFinished)
        {
            grid.IsEnabled = false;
            label.Content = "Syncing Xbox games to Steam";
            progressBar.Visibility = Visibility.Visible;

            try
            {
                var entries = await Task.Run(() => GetInstalledAppEntries());
                Apps.Entries = new System.Collections.ObjectModel.ObservableCollection<AppEntry>(entries);
                listGames.ItemsSource = Apps.Entries;

                var xboxGames = Apps.Entries.Where(app => app.IsXboxGame).ToList();
                var installedAppAumids = new HashSet<string>(
                    Apps.Entries.Select(app => app.Aumid),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var app in Apps.Entries)
                {
                    app.Selected = app.IsXboxGame;
                }

                bool changed = await ExportGames(xboxGames, installedAppAumids);
                RefreshSteamStatuses();

                if (showCompletion)
                {
                    string message = changed
                        ? $"Xbox games were synced to Steam. {xboxGames.Count} installed game(s) found."
                        : xboxGames.Count == 0
                            ? "No installed Xbox games were found, and Steam has no stale UWPHook shortcuts."
                            : "Xbox games are already synced to Steam.";
                    MessageBox.Show(message, "UWPHook", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (TaskCanceledException exception)
            {
                Log.Error(exception.Message);
                if (showCompletion)
                {
                    MessageBox.Show(exception.Message, "UWPHook", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error syncing Xbox games:" + Environment.NewLine + ex.Message + ex.StackTrace);
                MessageBox.Show("Error syncing Xbox games:" + Environment.NewLine + ex.Message, "UWPHook", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                grid.IsEnabled = true;
                progressBar.Visibility = Visibility.Collapsed;
                label.Content = "Installed Apps";

                if (closeWhenFinished)
                {
                    Close();
                }
            }
        }

        /// <summary>
        /// Fires the Bwr_DoWork, to load the apps installed at the machine
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            bwrLoad = new BackgroundWorker();
            bwrLoad.DoWork += Bwr_DoWork;
            bwrLoad.RunWorkerCompleted += Bwr_RunWorkerCompleted;

            grid.IsEnabled = false;
            label.Content = "Loading your installed apps";

            progressBar.Visibility = Visibility.Visible;
            Apps.Entries = new System.Collections.ObjectModel.ObservableCollection<AppEntry>();

            bwrLoad.RunWorkerAsync();
        }

        /// <summary>
        /// Callback for restoring the grid list interactivity
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Bwr_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                Log.Error(e.Error.Message);
                MessageBox.Show(e.Error.Message, "UWPHook", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else if (e.Result is IEnumerable<AppEntry> entries)
            {
                Apps.Entries = new System.Collections.ObjectModel.ObservableCollection<AppEntry>(entries);
            }

            listGames.ItemsSource = Apps.Entries;

            grid.IsEnabled = true;
            progressBar.Visibility = Visibility.Collapsed;
            label.Content = "Installed Apps";
        }

        /// <summary>
        /// Worker responsible for loading the apps installed in the machine
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Bwr_DoWork(object sender, DoWorkEventArgs e)
        {
            e.Result = GetInstalledAppEntries();
        }

        private List<AppEntry> GetInstalledAppEntries()
        {
            try
            {
                //For some reason I need to enforce Set-ExecutionPolicy none
                ScriptManager.RunScript("Set-ExecutionPolicy RemoteSigned -Scope Process -Force");

                //Get all installed apps on the system excluding frameworks
                List<String> installedApps = AppManager.GetInstalledApps();

                //Alfabetic sort
                installedApps.Sort();

                //Split every app that we couldn't resolve the app name
                var nameNotFound = (from s in installedApps where s.Contains("double click") select s).ToList<String>();

                //Remove them from the original list
                installedApps.RemoveAll(item => item.Contains("double click"));

                //Rejoin them in the original list, but putting them into last
                installedApps = installedApps.Union(nameNotFound).ToList<String>();

                HashSet<string> steamAppAumids = GetSteamAppAumids();
                List<AppEntry> entries = new List<AppEntry>();

                foreach (var app in installedApps)
                {
                    //Remove end lines from the String and split both values, I split the appname and the AUMID using |
                    //I hope no apps have that in their name. Ever.
                    var values = app.Replace("\r\n", "").Split('|');

                    if (values.Length >= 3 && AppManager.IsKnownApp(values[2], out string readableName))
                    {
                        values[0] = readableName;
                    }

                    if (!String.IsNullOrWhiteSpace(values[0]))
                    {
                        //We get the default square tile to find where the app stores it's icons, then we resolve which one is the widest
                        string logosPath = Path.GetDirectoryName(values[1]);
                        bool isXboxGame = values.Length >= 5 && bool.TryParse(values[4], out bool parsedIsXboxGame) && parsedIsXboxGame;
                        var entry = new AppEntry()
                        {
                            Name = values[0],
                            Executable = values[3],
                            IconPath = logosPath,
                            Aumid = values[2],
                            Selected = false,
                            IsXboxGame = isXboxGame,
                            AddedToSteam = steamAppAumids.Contains(values[2])
                        };
                        entry.Icon = entry.widestSquareIcon();
                        entries.Add(entry);
                    }
                }

                return entries;
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                throw;
            }
        }

        private HashSet<string> GetSteamAppAumids()
        {
            var aumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? steamFolder = SteamManager.GetSteamFolder();

            if (String.IsNullOrWhiteSpace(steamFolder)
                || !Directory.Exists(steamFolder)
                || !Directory.Exists(Path.Combine(steamFolder, "userdata")))
            {
                return aumids;
            }

            foreach (string user in SteamManager.GetUsers(steamFolder))
            {
                try
                {
                    foreach (VDFEntry shortcut in SteamManager.ReadShortcuts(user))
                    {
                        string? aumid = GetShortcutAumid(shortcut);
                        if (!String.IsNullOrWhiteSpace(aumid))
                        {
                            aumids.Add(aumid);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not read Steam shortcuts for {SteamUser}", user);
                }
            }

            return aumids;
        }

        private void RefreshSteamStatuses()
        {
            HashSet<string> steamAppAumids = GetSteamAppAumids();
            foreach (AppEntry app in Apps.Entries)
            {
                app.AddedToSteam = steamAppAumids.Contains(app.Aumid);
            }
        }

        private static string? GetShortcutAumid(VDFEntry shortcut)
        {
            if (String.IsNullOrWhiteSpace(shortcut.LaunchOptions))
            {
                return null;
            }

            string? aumid = shortcut.LaunchOptions
                .Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim('"');

            return !String.IsNullOrWhiteSpace(aumid) && aumid.Contains("!")
                ? aumid
                : null;
        }

        private void ListGames_AutoGeneratingColumn(object sender, System.Windows.Controls.DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.PropertyName == nameof(AppEntry.Icon) || e.PropertyName == nameof(AppEntry.IconPath))
            {
                e.Cancel = true;
            }
            else if (e.PropertyName == nameof(AppEntry.Selected))
            {
                e.Column.DisplayIndex = 0;
            }
            else if (e.PropertyName == nameof(AppEntry.Executable) || e.PropertyName == nameof(AppEntry.Aumid))
            {
                e.Column.IsReadOnly = true;
            }
            else if (e.PropertyName == nameof(AppEntry.AddedToSteam))
            {
                e.Cancel = true;
            }
            else if (e.PropertyName == nameof(AppEntry.SteamStatus))
            {
                e.Column.Header = "Added to Steam";
                e.Column.IsReadOnly = true;
                e.Column.DisplayIndex = 3;
            }
        }

        private void ListGames_LoadingRow(object sender, System.Windows.Controls.DataGridRowEventArgs e)
        {
            e.Row.Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (System.Windows.Controls.TextBlock text in FindVisualChildren<System.Windows.Controls.TextBlock>(e.Row))
                {
                    text.Padding = new Thickness(0, 7, 0, 0);
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent)
            where T : System.Windows.DependencyObject
        {
            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                System.Windows.DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    yield return match;
                }

                foreach (T descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        private void textBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (Apps.Entries != null)
            {
                if (!String.IsNullOrEmpty(textBox.Text) && Apps.Entries.Count > 0)
                {
                    listGames.Items.Filter = new Predicate<object>(Contains);
                }
                else
                {
                    listGames.Items.Filter = null;
                }
            }
        }

        public bool Contains(object o)
        {
            AppEntry appEntry = o as AppEntry;
            return (appEntry.Aumid.ToLower().Contains(textBox.Text.ToLower()) || appEntry.Name.ToLower().Contains(textBox.Text.ToLower()));
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow window = new SettingsWindow();
            window.ShowDialog();
        }

        /// <summary>
        /// Function that executes when the Games Window is loaded
        /// Will inform the user of the possibility of using the SteamGridDB API
        /// redirecting him to the settings page if he wishes to use the functionality
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!Settings.Default.OfferedSteamGridDB)
            {
                Settings.Default.SteamGridDbApiKey = "";
                Settings.Default.OfferedSteamGridDB = true;
                Settings.Default.Save();

                var boxResult = MessageBox.Show("Do you want to automatically import grid images for imported games?", "UWPHook", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (boxResult == MessageBoxResult.Yes)
                {
                    SettingsButton_Click(this, null);
                }
            }
        }

        public static void SetLogLevel()
        {
            int logLevel_index;
            int.TryParse(Settings.Default.SelectedLogLevel, out logLevel_index);

            switch (logLevel_index)
            {
                case 1:
                    Log.Information("Init log with DEBUG level.");
                    levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;
                    break;
                case 2:
                    Log.Information("Init log with TRACE level.");
                    levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Verbose;
                    break;
                default:
                    Log.Information("Init log with ERROR level.");
                    levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Error;
                    break;
            }
        }
    }
}
