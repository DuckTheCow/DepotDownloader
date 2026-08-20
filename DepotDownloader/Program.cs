// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotDownloader
{
    class Program
    {
        private static bool[] consumedArgs;

        static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintVersion();
                PrintUsage();

                if (OperatingSystem.IsWindowsVersionAtLeast(5, 0))
                {
                    PlatformUtilities.VerifyConsoleLaunch();
                }

                return 0;
            }

            Ansi.Init();

            DebugLog.Enabled = false;

            AccountSettingsStore.LoadFromFile("account.config");

            #region Common Options

            // Not using HasParameter because it is case insensitive
            if (args.Length == 1 && (args[0] == "-V" || args[0] == "--version"))
            {
                PrintVersion(true);
                return 0;
            }

            consumedArgs = new bool[args.Length];

            if (HasParameter(args, "-debug"))
            {
                PrintVersion(true);

                DebugLog.Enabled = true;
                DebugLog.AddListener((category, message) =>
                {
                    Console.WriteLine("[{0}] {1}", category, message);
                });

                var httpEventListener = new HttpDiagnosticEventListener();
            }

            var username = GetParameter<string>(args, "-username") ?? GetParameter<string>(args, "-user");
            var password = GetParameter<string>(args, "-password") ?? GetParameter<string>(args, "-pass");
            ContentDownloader.Config.RememberPassword = HasParameter(args, "-remember-password");
            ContentDownloader.Config.UseQrCode = HasParameter(args, "-qr");
            ContentDownloader.Config.SkipAppConfirmation = HasParameter(args, "-no-mobile");

            if (username == null)
            {
                if (ContentDownloader.Config.RememberPassword && !ContentDownloader.Config.UseQrCode)
                {
                    Console.WriteLine("Error: -remember-password can not be used without -username or -qr.");
                    return 1;
                }
            }
            else if (ContentDownloader.Config.UseQrCode)
            {
                Console.WriteLine("Error: -qr can not be used with -username.");
                return 1;
            }

            ContentDownloader.Config.DownloadManifestOnly = HasParameter(args, "-manifest-only");

            var cellId = GetParameter(args, "-cellid", -1);
            if (cellId == -1)
            {
                cellId = 0;
            }

            ContentDownloader.Config.CellID = cellId;

            var fileList = GetParameter<string>(args, "-filelist");

            if (fileList != null)
            {
                const string RegexPrefix = "regex:";

                try
                {
                    ContentDownloader.Config.UsingFileList = true;
                    ContentDownloader.Config.FilesToDownload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ContentDownloader.Config.FilesToDownloadRegex = [];

                    var files = await File.ReadAllLinesAsync(fileList);

                    foreach (var fileEntry in files)
                    {
                        if (string.IsNullOrWhiteSpace(fileEntry))
                        {
                            continue;
                        }

                        if (fileEntry.StartsWith(RegexPrefix))
                        {
                            var rgx = new Regex(fileEntry[RegexPrefix.Length..], RegexOptions.Compiled | RegexOptions.IgnoreCase);
                            ContentDownloader.Config.FilesToDownloadRegex.Add(rgx);
                        }
                        else
                        {
                            ContentDownloader.Config.FilesToDownload.Add(fileEntry.Replace('\\', '/'));
                        }
                    }

                    Console.WriteLine("Using filelist: '{0}'.", fileList);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Warning: Unable to load filelist: {0}", ex);
                }
            }

            ContentDownloader.Config.InstallDirectory = GetParameter<string>(args, "-dir");

            ContentDownloader.Config.VerifyAll = HasParameter(args, "-verify-all") || HasParameter(args, "-verify_all") || HasParameter(args, "-validate");

            if (HasParameter(args, "-use-lancache"))
            {
                await Client.DetectLancacheServerAsync();
                if (Client.UseLancacheServer)
                {
                    Console.WriteLine("Detected Lancache server! Downloads will be directed through the Lancache.");

                    // Increasing the number of concurrent downloads when the cache is detected since the downloads will likely
                    // be served much faster than over the internet.  Steam internally has this behavior as well.
                    if (!HasParameter(args, "-max-downloads"))
                    {
                        ContentDownloader.Config.MaxDownloads = 25;
                    }
                }
            }

            ContentDownloader.Config.MaxDownloads = GetParameter(args, "-max-downloads", 8);
            ContentDownloader.Config.LoginID = HasParameter(args, "-loginid") ? GetParameter<uint>(args, "-loginid") : null;

            #endregion

            var appListPath = GetParameter<string>(args, "-applist");
            var appId = GetParameter(args, "-app", ContentDownloader.INVALID_APP_ID);

            List<uint> appIds = null;

            if (appListPath != null)
            {
                if (appId != ContentDownloader.INVALID_APP_ID)
                {
                    Console.WriteLine("Warning: -app is ignored because -applist was specified.");
                }

                appIds = await LoadAppListAsync(appListPath);

                if (appIds.Count == 0)
                {
                    Console.WriteLine("Error: -applist '{0}' did not contain any valid app ids.", appListPath);
                    return 1;
                }

                appId = appIds[0];
            }

            if (appId == ContentDownloader.INVALID_APP_ID)
            {
                Console.WriteLine("Error: -app not specified!");
                return 1;
            }

            var pubFile = GetParameter(args, "-pubfile", ContentDownloader.INVALID_MANIFEST_ID);
            var ugcId = GetParameter(args, "-ugc", ContentDownloader.INVALID_MANIFEST_ID);
            if (pubFile != ContentDownloader.INVALID_MANIFEST_ID)
            {
                #region Pubfile Downloading

                PrintUnconsumedArgs(args);

                if (InitializeSteam(username, password))
                {
                    try
                    {
                        await ContentDownloader.DownloadPubfileAsync(appId, pubFile).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        ex is ContentDownloaderException
                        || ex is OperationCanceledException)
                    {
                        Console.WriteLine(ex.Message);
                        return 1;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Download failed to due to an unhandled exception: {0}", e.Message);
                        throw;
                    }
                    finally
                    {
                        ContentDownloader.ShutdownSteam3();
                    }
                }
                else
                {
                    Console.WriteLine("Error: InitializeSteam failed");
                    return 1;
                }

                #endregion
            }
            else if (ugcId != ContentDownloader.INVALID_MANIFEST_ID)
            {
                #region UGC Downloading

                PrintUnconsumedArgs(args);

                if (InitializeSteam(username, password))
                {
                    try
                    {
                        await ContentDownloader.DownloadUGCAsync(appId, ugcId).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        ex is ContentDownloaderException
                        || ex is OperationCanceledException)
                    {
                        Console.WriteLine(ex.Message);
                        return 1;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Download failed to due to an unhandled exception: {0}", e.Message);
                        throw;
                    }
                    finally
                    {
                        ContentDownloader.ShutdownSteam3();
                    }
                }
                else
                {
                    Console.WriteLine("Error: InitializeSteam failed");
                    return 1;
                }

                #endregion
            }
            else
            {
                #region App downloading

                var branch = GetParameter<string>(args, "-branch") ?? GetParameter<string>(args, "-beta") ?? ContentDownloader.DEFAULT_BRANCH;
                ContentDownloader.Config.BetaPassword = GetParameter<string>(args, "-branchpassword") ?? GetParameter<string>(args, "-betapassword");

                if (!string.IsNullOrEmpty(ContentDownloader.Config.BetaPassword) && string.IsNullOrEmpty(branch))
                {
                    Console.WriteLine("Error: Cannot specify -branchpassword when -branch is not specified.");
                    return 1;
                }

                ContentDownloader.Config.DownloadAllPlatforms = HasParameter(args, "-all-platforms");

                var os = GetParameter<string>(args, "-os");

                if (ContentDownloader.Config.DownloadAllPlatforms && !string.IsNullOrEmpty(os))
                {
                    Console.WriteLine("Error: Cannot specify -os when -all-platforms is specified.");
                    return 1;
                }

                ContentDownloader.Config.DownloadAllArchs = HasParameter(args, "-all-archs");

                var arch = GetParameter<string>(args, "-osarch");

                if (ContentDownloader.Config.DownloadAllArchs && !string.IsNullOrEmpty(arch))
                {
                    Console.WriteLine("Error: Cannot specify -osarch when -all-archs is specified.");
                    return 1;
                }

                ContentDownloader.Config.DownloadAllLanguages = HasParameter(args, "-all-languages");
                var language = GetParameter<string>(args, "-language");

                if (ContentDownloader.Config.DownloadAllLanguages && !string.IsNullOrEmpty(language))
                {
                    Console.WriteLine("Error: Cannot specify -language when -all-languages is specified.");
                    return 1;
                }

                var lv = HasParameter(args, "-lowviolence");

                var depotManifestIds = new List<(uint, ulong)>();
                var isUGC = false;

                var depotIdList = GetParameterList<uint>(args, "-depot");
                var manifestIdList = GetParameterList<ulong>(args, "-manifest");
                if (manifestIdList.Count > 0)
                {
                    if (depotIdList.Count != manifestIdList.Count)
                    {
                        Console.WriteLine("Error: -manifest requires one id for every -depot specified");
                        return 1;
                    }

                    var zippedDepotManifest = depotIdList.Zip(manifestIdList, (depotId, manifestId) => (depotId, manifestId));
                    depotManifestIds.AddRange(zippedDepotManifest);
                }
                else
                {
                    depotManifestIds.AddRange(depotIdList.Select(depotId => (depotId, ContentDownloader.INVALID_MANIFEST_ID)));
                }

                PrintUnconsumedArgs(args);

                if (InitializeSteam(username, password))
                {
                    var appsToProcess = appIds ?? [appId];
                    var isBatch = appsToProcess.Count > 1;
                    var baseInstallDir = string.IsNullOrWhiteSpace(ContentDownloader.Config.InstallDirectory)
                        ? ContentDownloader.DEFAULT_DOWNLOAD_DIR
                        : ContentDownloader.Config.InstallDirectory;
                    var succeededCount = 0;
                    var failedCount = 0;
                    var consecutiveFailures = 0;
                    const int ConsecutiveFailureThrottleThreshold = 5;
                    var stopwatch = isBatch ? Stopwatch.StartNew() : null;

                    // success.txt records apps that have already completed in a previous -applist run, so a resumed
                    // batch can skip them before making any Steam API call at all, instead of only after paying for
                    // RequestAppInfo/RequestDepotKey and finding the manifest already on disk.
                    var successFilePath = Path.Combine(baseInstallDir, "success.txt");
                    var completedAppIds = new HashSet<uint>();

                    // "already completed, skipping" is printed once per app by default, which floods the terminal
                    // when most of the list is already done (e.g. resuming a large batch). Instead, consecutive
                    // skips are collapsed into a single range line, printed once the streak ends.
                    var skipStreakStartIndex = -1;
                    var skipStreakCount = 0;

                    void FlushSkipStreak()
                    {
                        if (skipStreakCount == 0)
                        {
                            return;
                        }

                        if (skipStreakCount == 1)
                        {
                            Console.WriteLine("[{0}/{1}] app {2} - already completed, skipping", skipStreakStartIndex + 1, appsToProcess.Count, appsToProcess[skipStreakStartIndex]);
                        }
                        else
                        {
                            Console.WriteLine("[{0}-{1}/{2}] already completed, skipping ({3} apps)", skipStreakStartIndex + 1, skipStreakStartIndex + skipStreakCount, appsToProcess.Count, skipStreakCount);
                        }

                        skipStreakCount = 0;
                    }

                    // Ctrl+C during a large batch shouldn't kill the process mid-app and lose that app's progress.
                    // Instead it requests a stop after the current app finishes; success.txt already has everything
                    // up to that point, so re-running the same command later picks up right where this left off.
                    using var stopRequested = new CancellationTokenSource();

                    if (isBatch)
                    {
                        Directory.CreateDirectory(baseInstallDir);
                        completedAppIds = await LoadCompletedAppsAsync(successFilePath);

                        Console.CancelKeyPress += (_, e) =>
                        {
                            e.Cancel = true;

                            if (!stopRequested.IsCancellationRequested)
                            {
                                stopRequested.Cancel();
                                Console.WriteLine();
                                Console.WriteLine("Ctrl+C received: finishing the current app, then stopping. Re-run the same command later to continue with the rest of the list.");
                            }
                            else
                            {
                                Console.WriteLine("Ctrl+C received again: forcing immediate exit.");
                                Environment.Exit(1);
                            }
                        };
                    }

                    try
                    {
                        for (var i = 0; i < appsToProcess.Count; i++)
                        {
                            if (isBatch && stopRequested.IsCancellationRequested)
                            {
                                break;
                            }

                            var currentAppId = appsToProcess[i];

                            if (isBatch && completedAppIds.Contains(currentAppId))
                            {
                                if (skipStreakCount == 0)
                                {
                                    skipStreakStartIndex = i;
                                }

                                skipStreakCount++;
                                succeededCount++;
                                continue;
                            }

                            FlushSkipStreak();

                            if (isBatch)
                            {
                                Console.WriteLine("[{0}/{1}] app {2}", i + 1, appsToProcess.Count, currentAppId);
                                ContentDownloader.Config.InstallDirectory = Path.Combine(baseInstallDir, currentAppId.ToString());
                            }

                            try
                            {
                                // DepotConfigStore.Instance is a static singleton that refuses to load twice, so it must be
                                // reset between apps even though everything else in DownloadAppAsync is safe to call repeatedly.
                                DepotConfigStore.Instance = null;

                                await ContentDownloader.DownloadAppAsync(currentAppId, new List<(uint depotId, ulong manifestId)>(depotManifestIds), branch, os, arch, language, lv, isUGC).ConfigureAwait(false);
                                succeededCount++;
                                consecutiveFailures = 0;

                                if (isBatch)
                                {
                                    await File.AppendAllLinesAsync(successFilePath, [currentAppId.ToString()]);
                                }
                            }
                            catch (Exception ex) when (
                                ex is ContentDownloaderException
                                || ex is OperationCanceledException)
                            {
                                if (!isBatch)
                                {
                                    Console.WriteLine(ex.Message);
                                    return 1;
                                }

                                // OperationCanceledException here is always DownloadAppAsync giving up on this app's
                                // depots (e.g. a 401 on one depot, or the per-depot manifest retry cap being hit) via
                                // its own per-call CancellationTokenSource, not a real external cancellation (this
                                // codebase has no Ctrl+C/global token), so batching treats it as a per-app failure
                                // like ContentDownloaderException, not a reason to abort the rest of the list.
                                failedCount++;
                                Console.WriteLine("Skipping app {0}: {1}", currentAppId, ex.Message);

                                // Only a manifest download that exhausted its retry cap on repeated transient errors
                                // (ServiceUnavailable, timeouts) counts as throttling evidence. A clean rejection
                                // (not owned, delisted, region-locked) means Steam answered fine - that's routine and
                                // resets the streak rather than adding to it.
                                if (ex is ManifestRetryExhaustedException)
                                {
                                    consecutiveFailures++;

                                    // A handful of apps failing this way back to back usually isn't bad luck on
                                    // individual apps - it's Steam throttling this session. Stop the same way Ctrl+C
                                    // does (finish cleanly, keep success.txt) instead of burning through the rest of
                                    // the list against a throttle that won't lift mid-run.
                                    if (consecutiveFailures >= ConsecutiveFailureThrottleThreshold && !stopRequested.IsCancellationRequested)
                                    {
                                        Console.WriteLine();
                                        Console.WriteLine("{0} apps in a row failed to download a manifest - this usually means Steam is throttling this session. Stopping here; re-run the same command later to continue.", consecutiveFailures);
                                        stopRequested.Cancel();
                                    }
                                }
                                else
                                {
                                    consecutiveFailures = 0;
                                }
                            }
                        }

                        FlushSkipStreak();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Download failed to due to an unhandled exception: {0}", e.Message);
                        throw;
                    }
                    finally
                    {
                        ContentDownloader.ShutdownSteam3();
                    }

                    if (isBatch)
                    {
                        stopwatch.Stop();

                        if (stopRequested.IsCancellationRequested)
                        {
                            var remaining = appsToProcess.Count - succeededCount - failedCount;
                            Console.WriteLine("Stopped early: {0} succeeded, {1} failed, {2} not yet attempted, in {3}.", succeededCount, failedCount, remaining, stopwatch.Elapsed);
                        }
                        else
                        {
                            Console.WriteLine("Processed {0} apps: {1} succeeded, {2} failed in {3}.", appsToProcess.Count, succeededCount, failedCount, stopwatch.Elapsed);
                        }

                        if (succeededCount == 0)
                        {
                            return 1;
                        }
                    }
                }
                else
                {
                    Console.WriteLine("Error: InitializeSteam failed");
                    return 1;
                }

                #endregion
            }

            return 0;
        }

        static bool InitializeSteam(string username, string password)
        {
            if (!ContentDownloader.Config.UseQrCode)
            {
                if (username != null && password == null && (!ContentDownloader.Config.RememberPassword || !AccountSettingsStore.Instance.LoginTokens.ContainsKey(username)))
                {
                    if (AccountSettingsStore.Instance.LoginTokens.ContainsKey(username))
                    {
                        Console.WriteLine($"Account \"{username}\" has stored credentials. Did you forget to specify -remember-password?");
                    }

                    do
                    {
                        Console.Write("Enter account password for \"{0}\": ", username);
                        if (Console.IsInputRedirected)
                        {
                            password = Console.ReadLine();
                        }
                        else
                        {
                            // Avoid console echoing of password
                            password = Util.ReadPassword();
                        }

                        Console.WriteLine();
                    } while (string.Empty == password);
                }
                else if (username == null)
                {
                    Console.WriteLine("No username given. Using anonymous account with dedicated server subscription.");
                }
            }

            if (!string.IsNullOrEmpty(password))
            {
                const int MAX_PASSWORD_SIZE = 64;

                if (password.Length > MAX_PASSWORD_SIZE)
                {
                    Console.Error.WriteLine($"Warning: Password is longer than {MAX_PASSWORD_SIZE} characters, which is not supported by Steam.");
                }

                if (!password.All(char.IsAscii))
                {
                    Console.Error.WriteLine("Warning: Password contains non-ASCII characters, which is not supported by Steam.");
                }
            }

            return ContentDownloader.InitializeSteam3(username, password);
        }

        static int IndexOfParam(string[] args, string param)
        {
            for (var x = 0; x < args.Length; ++x)
            {
                if (args[x].Equals(param, StringComparison.OrdinalIgnoreCase))
                {
                    consumedArgs[x] = true;
                    return x;
                }
            }

            return -1;
        }

        static bool HasParameter(string[] args, string param)
        {
            return IndexOfParam(args, param) > -1;
        }

        static T GetParameter<T>(string[] args, string param, T defaultValue = default)
        {
            var index = IndexOfParam(args, param);

            if (index == -1 || index == (args.Length - 1))
                return defaultValue;

            var strParam = args[index + 1];

            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter != null)
            {
                consumedArgs[index + 1] = true;
                return (T)converter.ConvertFromString(strParam);
            }

            return default;
        }

        static List<T> GetParameterList<T>(string[] args, string param)
        {
            var list = new List<T>();
            var index = IndexOfParam(args, param);

            if (index == -1 || index == (args.Length - 1))
                return list;

            index++;

            while (index < args.Length)
            {
                var strParam = args[index];

                if (strParam[0] == '-') break;

                var converter = TypeDescriptor.GetConverter(typeof(T));
                if (converter != null)
                {
                    consumedArgs[index] = true;
                    list.Add((T)converter.ConvertFromString(strParam));
                }

                index++;
            }

            return list;
        }

        static async Task<List<uint>> LoadAppListAsync(string path)
        {
            var result = new List<uint>();
            var seen = new HashSet<uint>();
            string[] lines;

            try
            {
                lines = await File.ReadAllLinesAsync(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: Unable to read -applist file '{0}': {1}", path, ex.Message);
                return result;
            }

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var field = line.Split(',')[0].Trim();

                if (uint.TryParse(field, out var id) && seen.Add(id))
                {
                    result.Add(id);
                }
            }

            return result;
        }

        static async Task<HashSet<uint>> LoadCompletedAppsAsync(string path)
        {
            var result = new HashSet<uint>();

            if (!File.Exists(path))
            {
                return result;
            }

            string[] lines;

            try
            {
                lines = await File.ReadAllLinesAsync(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Warning: Unable to read '{0}': {1}", path, ex.Message);
                return result;
            }

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (line.Length > 0 && uint.TryParse(line, out var id))
                {
                    result.Add(id);
                }
            }

            return result;
        }

        static void PrintUnconsumedArgs(string[] args)
        {
            var printError = false;

            for (var index = 0; index < consumedArgs.Length; index++)
            {
                if (!consumedArgs[index])
                {
                    printError = true;
                    Console.Error.WriteLine($"Argument #{index + 1} {args[index]} was not used.");
                }
            }

            if (printError)
            {
                Console.Error.WriteLine("Make sure you specified the arguments correctly. Check --help for correct arguments.");
                Console.Error.WriteLine();
            }
        }

        static void PrintUsage()
        {
            // Do not use tabs to align parameters here because tab size may differ
            Console.WriteLine();
            Console.WriteLine("Usage: downloading one or all depots for an app:");
            Console.WriteLine("       depotdownloader -app <id> [-depot <id> [-manifest <id>]]");
            Console.WriteLine("                       [-username <username> [-password <password>]] [other options]");
            Console.WriteLine();
            Console.WriteLine("Usage: downloading a workshop item using pubfile id");
            Console.WriteLine("       depotdownloader -app <id> -pubfile <id> [-username <username> [-password <password>]]");
            Console.WriteLine("Usage: downloading a workshop item using ugc id");
            Console.WriteLine("       depotdownloader -app <id> -ugc <id> [-username <username> [-password <password>]]");
            Console.WriteLine();
            Console.WriteLine("Parameters:");
            Console.WriteLine("  -app <#>                 - the AppID to download.");
            Console.WriteLine("  -applist <file.txt>      - a file with one AppID per line; logs in once and downloads each app. Overrides -app.");
            Console.WriteLine("  -depot <#>               - the DepotID to download.");
            Console.WriteLine("  -manifest <id>           - manifest id of content to download (requires -depot, default: current for branch).");
            Console.WriteLine($"  -branch <branchname>    - download from specified branch if available (default: {ContentDownloader.DEFAULT_BRANCH}).");
            Console.WriteLine("  -branchpassword <pass>   - branch password if applicable.");
            Console.WriteLine("  -all-platforms           - downloads all platform-specific depots when -app is used.");
            Console.WriteLine("  -all-archs               - download all architecture-specific depots when -app is used.");
            Console.WriteLine("  -os <os>                 - the operating system for which to download the game (windows, macos or linux, default: OS the program is currently running on)");
            Console.WriteLine("  -osarch <arch>           - the architecture for which to download the game (32 or 64, default: the host's architecture)");
            Console.WriteLine("  -all-languages           - download all language-specific depots when -app is used.");
            Console.WriteLine("  -language <lang>         - the language for which to download the game (default: english)");
            Console.WriteLine("  -lowviolence             - download low violence depots when -app is used.");
            Console.WriteLine();
            Console.WriteLine("  -ugc <#>                 - the UGC ID to download.");
            Console.WriteLine("  -pubfile <#>             - the PublishedFileId to download. (Will automatically resolve to UGC id)");
            Console.WriteLine();
            Console.WriteLine("  -username <user>         - the username of the account to login to for restricted content.");
            Console.WriteLine("  -password <pass>         - the password of the account to login to for restricted content.");
            Console.WriteLine("  -remember-password       - if set, remember the password for subsequent logins of this user.");
            Console.WriteLine("                             use -username <username> -remember-password as login credentials.");
            Console.WriteLine("  -qr                      - display a login QR code to be scanned with the Steam mobile app");
            Console.WriteLine("  -no-mobile               - prefer entering a 2FA code instead of prompting to accept in the Steam mobile app");
            Console.WriteLine();
            Console.WriteLine("  -dir <installdir>        - the directory in which to place downloaded files.");
            Console.WriteLine("  -filelist <file.txt>     - the name of a local file that contains a list of files to download (from the manifest).");
            Console.WriteLine("                             prefix file path with `regex:` if you want to match with regex. each file path should be on their own line.");
            Console.WriteLine();
            Console.WriteLine("  -validate                - include checksum verification of files already downloaded");
            Console.WriteLine("  -manifest-only           - downloads a human readable manifest for any depots that would be downloaded.");
            Console.WriteLine("  -cellid <#>              - the overridden CellID of the content server to download from.");
            Console.WriteLine("  -max-downloads <#>       - maximum number of chunks to download concurrently. (default: 8).");
            Console.WriteLine("  -loginid <#>             - a unique 32-bit integer Steam LogonID in decimal, required if running multiple instances of DepotDownloader concurrently.");
            Console.WriteLine("  -use-lancache            - forces downloads over the local network via a Lancache instance.");
            Console.WriteLine();
            Console.WriteLine("  -debug                   - enable verbose debug logging.");
            Console.WriteLine("  -V or --version          - print version and runtime.");
        }

        static void PrintVersion(bool printExtra = false)
        {
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            Console.WriteLine($"DepotDownloader v{version}");

            if (!printExtra)
            {
                return;
            }

            Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription}");
        }
    }
}
