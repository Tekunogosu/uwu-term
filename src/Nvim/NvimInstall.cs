using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace UwUTerm.Nvim
{
    /// <summary>
    /// Finds the neovim the editor runs, and fetches one if asked.
    ///
    /// It lives in the game folder rather than being taken from the machine. On Linux the game
    /// usually runs inside a container with its own /usr, so a system install is both invisible
    /// and, being built against a newer libc, unloadable even when it is not. A copy alongside
    /// the mod is on the filesystem the game already has, and the same arrangement works on
    /// Windows without a special case.
    ///
    /// Downloading is off unless asked for. A plugin should not reach onto the network because
    /// it started; the address is in the setting's description either way, so fetching it by
    /// hand and unpacking it is always an option.
    /// </summary>
    internal static class NvimInstall
    {
        private const string Release = "https://github.com/neovim/neovim/releases/latest/download/";

        private static bool _fetching;

        /// <summary>Where the editor is expected, unless the setting names somewhere else.</summary>
        internal static string Location
        {
            get
            {
                string configured = UwUTermPlugin.NvimPath.Value.Trim();
                if (configured.Length > 0) return configured;

                string root = Path.Combine(BepInEx.Paths.BepInExRootPath, "nvim");
                return IsWindows
                    ? Path.Combine(root, "bin", "nvim.exe")
                    : Path.Combine(root, "bin", "nvim");
            }
        }

        internal static bool Available => File.Exists(Location);

        /// <summary>
        /// An editor already running to join, or null to start one per window.
        ///
        /// A path is a unix socket, host:port is tcp. What is on the other end is a neovim
        /// started with --listen, which for anyone whose game runs in a container is the way
        /// to get an editor that has the machine's own git, compilers and language servers -
        /// the container has none of them, and no way to install any.
        /// </summary>
        internal static string Address
        {
            get
            {
                string configured = UwUTermPlugin.NvimAddress.Value.Trim();
                return configured.Length > 0 ? configured : null;
            }
        }

        /// <summary>Whether there is an editor to be had at all - one to join, or one to run.</summary>
        internal static bool Usable => Address != null || Available;

        /// <summary>
        /// The directory the editor starts in, made if it is not there.
        ///
        /// This is a folder on the machine, and it is not where the game keeps anything. A
        /// script being edited lives on the server and reaches the editor as text, so it is
        /// never a file here - this is somewhere to keep notes, drafts and whatever else is
        /// worth having on hand, and somewhere for :e to open into that is not the middle of a
        /// mod install.
        /// </summary>
        internal static string Workspace
        {
            get
            {
                string configured = UwUTermPlugin.NvimWorkspace.Value.Trim();
                string path = configured.Length > 0
                    ? configured
                    : Path.Combine(BepInEx.Paths.BepInExRootPath, "workspace");

                try
                {
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                }
                catch (Exception e)
                {
                    UwUTermPlugin.Log.LogWarning($"nvim: could not make {path} - {e.Message}");
                    return null;
                }

                return path;
            }
        }

        /// <summary>
        /// Where the editor keeps its config, its plugins and its state.
        ///
        /// Inherited from the game, XDG_CONFIG_HOME and XDG_DATA_HOME point somewhere nobody
        /// chose. Steam under flatpak hands out ~/.var/app/com.valvesoftware.Steam/config, the
        /// machine's own ~/.config is not mounted, and there is no git or compiler in there to
        /// install anything with. Naming the directories here instead gives the same answer on
        /// every platform and launcher, keeps a config that ships with the mod next to the mod,
        /// and leaves whatever neovim the player has for their own work untouched.
        ///
        /// Null means hand the inherited directories back, which is what "system" asks for.
        /// </summary>
        internal static string ConfigRoot
        {
            get
            {
                string configured = UwUTermPlugin.NvimConfig.Value.Trim();
                if (string.Equals(configured, "system", StringComparison.OrdinalIgnoreCase)) return null;
                if (configured.Length > 0) return configured;

                return Path.Combine(BepInEx.Paths.BepInExRootPath, "nvim-config");
            }
        }

        /// <summary>The XDG variables the editor is started with, and the directories they
        /// name, made if they are not there. Empty when the inherited ones are being kept.</summary>
        internal static IDictionary<string, string> ChildEnvironment
        {
            get
            {
                var variables = new Dictionary<string, string>();
                string root = ConfigRoot;
                if (root == null) return variables;

                foreach (var pair in new[]
                {
                    new[] { "XDG_CONFIG_HOME", "config" },
                    new[] { "XDG_DATA_HOME", "data" },
                    new[] { "XDG_STATE_HOME", "state" },
                    new[] { "XDG_CACHE_HOME", "cache" },
                })
                {
                    string path = Path.Combine(root, pair[1]);

                    try
                    {
                        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    }
                    catch (Exception e)
                    {
                        UwUTermPlugin.Log.LogWarning($"nvim: could not make {path} - {e.Message}");
                        continue;
                    }

                    variables[pair[0]] = path;
                }

                return variables;
            }
        }

        internal static bool IsWindows =>
            Environment.OSVersion.Platform != PlatformID.Unix &&
            Environment.OSVersion.Platform != PlatformID.MacOSX;

        /// <summary>The build for this machine. Named here so the setting can print it and
        /// anyone who would rather not have the mod download things can do it themselves.</summary>
        internal static string Archive =>
            IsWindows ? Release + "nvim-win64.zip" : Release + "nvim-linux-x86_64.tar.gz";

        /// <summary>
        /// Fetch and unpack, once, on a thread of its own - the download is tens of megabytes
        /// and the game should not stop for it.
        /// </summary>
        internal static void FetchInBackground()
        {
            if (_fetching || Available) return;
            _fetching = true;

            var worker = new System.Threading.Thread(Fetch) { IsBackground = true, Name = "UwUTerm.NvimFetch" };
            worker.Start();
        }

        private static void Fetch()
        {
            string root = Path.Combine(BepInEx.Paths.BepInExRootPath, "nvim");
            string archive = Path.Combine(BepInEx.Paths.BepInExRootPath, IsWindows ? "nvim.zip" : "nvim.tar.gz");

            try
            {
                UwUTermPlugin.Log.LogInfo("nvim: downloading " + Archive);

                // Github redirects to a CDN over TLS 1.2, which this framework does not pick by
                // default on every runtime.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                using (var web = new WebClient()) web.DownloadFile(Archive, archive);

                UwUTermPlugin.Log.LogInfo($"nvim: unpacking into {root}");
                Directory.CreateDirectory(root);

                if (IsWindows) Unzip(archive, root);
                else Untar(archive, root);

                // Both archives wrap everything in a directory named for the build -
                // nvim-linux-x86_64, nvim-win64 - so what comes out is lifted up a level and
                // the binary ends up where it is looked for.
                Flatten(root);

                File.Delete(archive);

                UwUTermPlugin.Log.LogInfo(Available
                    ? "nvim: ready at " + Location
                    : "nvim: unpacked but no binary at " + Location + " - check the layout");
            }
            catch (Exception e)
            {
                UwUTermPlugin.Log.LogError($"nvim: could not fetch - {e.Message}");
                UwUTermPlugin.Log.LogError("nvim: download it yourself from " + Archive +
                                           " and unpack it so the binary sits at " + Location);
            }
            finally
            {
                _fetching = false;
            }
        }

        private static void Unzip(string archive, string into) =>
            System.IO.Compression.ZipFile.ExtractToDirectory(archive, into);

        /// <summary>
        /// Unpacked with tar rather than by hand.
        ///
        /// The archive carries symlinks and file modes, and the binary has to come out
        /// executable - reproducing all of that from a tar reader is work that the tar already
        /// present on every machine this runs on does correctly.
        /// </summary>
        private static void Untar(string archive, string into)
        {
            var info = new ProcessStartInfo("tar", $"-xzf \"{archive}\" -C \"{into}\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using (Process child = Process.Start(info))
            {
                string failure = child.StandardError.ReadToEnd();
                child.WaitForExit(120000);

                if (child.ExitCode != 0)
                    throw new Exception("tar failed: " + failure.Trim());
            }
        }

        /// <summary>
        /// Lift a single wrapping directory's contents up a level.
        ///
        /// Stripping it during extraction would do the same thing, but only for the archives
        /// that happen to have exactly one - doing it afterwards means looking at what actually
        /// came out, and leaves an already-correct layout alone.
        /// </summary>
        private static void Flatten(string into)
        {
            if (Directory.Exists(Path.Combine(into, "bin"))) return;

            string[] nested = Directory.GetDirectories(into);
            if (nested.Length != 1) return;

            foreach (string entry in Directory.GetFileSystemEntries(nested[0]))
            {
                string target = Path.Combine(into, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, target);
                else File.Move(entry, target);
            }

            Directory.Delete(nested[0], true);
        }
    }
}
