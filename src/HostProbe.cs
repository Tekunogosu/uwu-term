using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UwUTerm
{
    /// <summary>
    /// Report what the game process can actually reach.
    ///
    /// Steam runs the game inside a container with its own filesystem - the same one that
    /// leaves /usr/share/fonts holding six DejaVu faces and nothing of the machine's own. Any
    /// plan that runs a real editor depends on whether a binary can be found and started from
    /// in here, and none of that can be read from decompiled code or from outside the sandbox.
    /// </summary>
    internal static class HostProbe
    {
        private static bool _done;

        internal static void Tick()
        {
            if (_done || !UwUTermPlugin.ProbeHost.Value) return;
            _done = true;

            var sb = new StringBuilder(4096);
            sb.Append("\n=== UwUTerm host probe ===\n");

            Environment(sb);
            Paths(sb);
            Editors(sb);
            Spawning(sb);
            Bundled(sb);

            UwUTermPlugin.Log.LogInfo(sb.ToString());
        }

        private static void Environment(StringBuilder sb)
        {
            sb.Append("\n-- environment --\n");
            foreach (string name in new[] { "HOME", "PATH", "USER", "SHELL", "TMPDIR",
                                            "STEAM_RUNTIME", "container", "XDG_RUNTIME_DIR" })
                sb.Append($"  {name} = {System.Environment.GetEnvironmentVariable(name) ?? "(unset)"}\n");

            sb.Append($"  OS = {System.Environment.OSVersion}\n");
            sb.Append($"  cwd = {Directory.GetCurrentDirectory()}\n");
        }

        /// <summary>Whether the machine's own filesystem is reachable, and where.</summary>
        private static void Paths(StringBuilder sb)
        {
            sb.Append("\n-- paths --\n");

            string home = System.Environment.GetEnvironmentVariable("HOME") ?? "";
            foreach (string path in new[]
                     {
                         "/run/host", "/run/host/usr/bin", "/run/host/etc",
                         "/usr/bin", "/usr/local/bin", "/bin", "/opt",
                         "/tmp", "/var/tmp", home, home + "/.config", home + "/.local/bin",
                     })
            {
                if (path.Length == 0) continue;

                bool exists = Directory.Exists(path);
                int count = 0;
                if (exists)
                {
                    try { count = Directory.GetFileSystemEntries(path).Length; }
                    catch (Exception) { count = -1; }
                }

                sb.Append($"  {path,-28} {(exists ? "present" : "absent")}" +
                          (exists ? $"  entries={(count < 0 ? "unreadable" : count.ToString())}" : "") + "\n");
            }

            sb.Append($"  writable here: {Writable(Path.Combine(BepInEx.Paths.BepInExRootPath, "probe.tmp"))}\n");
            sb.Append($"  writable /tmp: {Writable("/tmp/uwuterm-probe.tmp")}\n");
        }

        /// <summary>Anything that could serve as a real editor, wherever it might be.</summary>
        private static void Editors(StringBuilder sb)
        {
            sb.Append("\n-- editors --\n");

            var roots = new List<string> { "/usr/bin", "/usr/local/bin", "/bin", "/run/host/usr/bin" };
            string home = System.Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home)) roots.Add(home + "/.local/bin");

            foreach (string name in new[] { "nvim", "vim", "vi", "nano", "hx", "kak", "emacs", "code", "codium" })
            {
                var found = new List<string>();
                foreach (string root in roots)
                {
                    try
                    {
                        string candidate = Path.Combine(root, name);
                        if (File.Exists(candidate)) found.Add(candidate);
                    }
                    catch (Exception) { }
                }

                sb.Append($"  {name,-8} {(found.Count == 0 ? "not found" : string.Join(", ", found.ToArray()))}\n");
            }
        }

        /// <summary>
        /// Whether a child process can be started at all. Everything that runs a real editor -
        /// as an embedded UI over a pipe or as a window of its own - needs this to work first.
        /// </summary>
        private static void Spawning(StringBuilder sb)
        {
            sb.Append("\n-- spawning --\n");

            try
            {
                var info = new ProcessStartInfo("/bin/sh", "-c \"echo alive; uname -a; command -v nvim || true\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using (Process child = Process.Start(info))
                {
                    string output = child.StandardOutput.ReadToEnd();
                    child.WaitForExit(4000);

                    sb.Append("  Process.Start: worked\n");
                    foreach (string line in output.Split('\n'))
                        if (line.Trim().Length > 0) sb.Append("    ").Append(line.Trim()).Append('\n');
                }
            }
            catch (Exception e)
            {
                sb.Append($"  Process.Start: FAILED - {e.GetType().Name}: {e.Message}\n");
            }
        }

        /// <summary>
        /// Whether an editor shipped with the mod can actually run in here.
        ///
        /// The machine's own nvim is no use: the sandbox has its own /usr, and a binary built
        /// against the host's newer glibc would not load even if it were visible. One that
        /// carries its libraries with it, sitting in the game folder, is on the filesystem the
        /// game already has - so this asks it for its version and prints whatever comes back,
        /// including the loader's complaint if it cannot start.
        /// </summary>
        private static void Bundled(StringBuilder sb)
        {
            sb.Append("\n-- bundled editor --\n");

            string root = Home.Nvim;
            foreach (string candidate in new[]
                     {
                         Path.Combine(root, "bin", "nvim"),
                         Path.Combine(root, "nvim"),
                         Path.Combine(root, "AppRun"),
                     })
            {
                if (!File.Exists(candidate)) { sb.Append($"  {candidate} - absent\n"); continue; }

                sb.Append($"  {candidate} - present, asking for --version\n");
                Run(sb, candidate, "--version");
                return;
            }

            sb.Append($"  nothing to try. Unpack neovim into {root} to test it.\n");
        }

        private static void Run(StringBuilder sb, string program, string arguments)
        {
            try
            {
                var info = new ProcessStartInfo(program, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using (Process child = Process.Start(info))
                {
                    string output = child.StandardOutput.ReadToEnd();
                    string failure = child.StandardError.ReadToEnd();
                    child.WaitForExit(5000);

                    foreach (string line in (output + failure).Split('\n'))
                        if (line.Trim().Length > 0) sb.Append("    ").Append(line.Trim()).Append('\n');
                }
            }
            catch (Exception e)
            {
                sb.Append($"    FAILED - {e.GetType().Name}: {e.Message}\n");
            }
        }

        private static string Writable(string path)
        {
            try
            {
                File.WriteAllText(path, "probe");
                File.Delete(path);
                return "yes";
            }
            catch (Exception e)
            {
                return "no (" + e.GetType().Name + ")";
            }
        }
    }
}
