using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UwUTerm.Nvim
{
    /// <summary>
    /// A session of the player's own for every window, started by the one they already have.
    ///
    /// One neovim has one screen. Two UIs on the same socket are two views of it - the same
    /// window, the same cursor, and the grid shrunk to fit the smaller of them - so a second
    /// editor window cannot share a session with the first and be its own editor. Until now the
    /// second window ran a neovim inside the game's container instead, which has the mod's bare
    /// config and none of the player's plugins, language servers or tools.
    ///
    /// A session outside the game can start another, and its children are on the machine with
    /// it: one <c>jobstart</c> down the connection we already hold gives a fresh neovim on a
    /// socket of its own, with the same config the player would get in a terminal. So the
    /// session the daemon script starts becomes the one that hands out sessions, and each
    /// window gets a real one.
    ///
    /// One is kept warming at all times, because a window opens faster than a neovim starts and
    /// a window that has to wait for one is a window that shows nothing for a second. Nothing
    /// here is required: with no daemon listening there is nothing to ask, and the editor falls
    /// back to running one in the game as it always did.
    /// </summary>
    internal static class NvimSessions
    {
        /// <summary>How long to give a session to start before giving up on it.</summary>
        private const float StartingSeconds = 10f;

        /// <summary>How long to wait before asking again for a daemon that was not there.</summary>
        private const float RetrySeconds = 5f;

        private static RpcClient _factory;
        private static float _askAgainAt;
        private static int _made;

        private static Warming _warming;
        private static readonly Dictionary<string, int> Jobs = new Dictionary<string, int>();

        /// <summary>How often to ask a session on its way up whether it is listening yet.</summary>
        private const float ProbeSeconds = 0.25f;

        /// <summary>A session on its way up: where it will listen, when it was asked for, and
        /// whether it has answered.</summary>
        private sealed class Warming
        {
            internal string Path;
            internal float Asked;
            internal float ProbeAt;
            internal RpcClient Asking;
            internal volatile bool Ready;
        }

        /// <summary>Whether a session is on its way and worth waiting a moment for, rather than
        /// starting one in the game straight away.</summary>
        internal static bool Coming => _warming != null && !Late(_warming);

        /// <summary>
        /// A session that is up and listening, if one is ready.
        ///
        /// Handed out once: the caller owns it from here, and the next one is started in its
        /// place so the window after this does not wait either.
        /// </summary>
        internal static string Take()
        {
            if (_warming == null || !_warming.Ready) return null;

            string path = _warming.Path;
            Done(_warming);
            _warming = null;

            UwUTermPlugin.Log.LogInfo("nvim: taking the session on " + path);
            return path;
        }

        /// <summary>Keep one warming, and give up on one that never came up.</summary>
        internal static void Tick()
        {
            if (!UwUTermPlugin.NvimSessionPerWindow.Value) return;

            if (_warming != null)
            {
                if (!_warming.Ready) Probe(_warming);
                if (_warming.Ready || !Late(_warming)) return;

                UwUTermPlugin.Log.LogWarning(
                    $"nvim: no session came up on {_warming.Path} - running editors in the game instead");
                Stop(_warming.Path);
                Done(_warming);
                _warming = null;
            }

            Start();
        }

        /// <summary>The window that had this session has gone, and so should the session. They
        /// die with the daemon regardless; this is for a game left running all day.</summary>
        internal static void Release(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            Stop(path);
        }

        internal static void Shutdown()
        {
            foreach (string path in new List<string>(Jobs.Keys)) Stop(path);

            if (_warming != null) Done(_warming);
            _warming = null;
            _factory?.Dispose();
            _factory = null;
        }

        /// <summary>
        /// Ask the session whether it is ready, which is not the same as whether it is there.
        ///
        /// Neovim binds its socket early and sources the player's config afterwards, so a
        /// session answers the door before it has any of their plugins in it. Handing one over
        /// at that point means the filetype we set arrives before whatever would highlight it
        /// exists, and the script sits unhighlighted until something fires FileType again -
        /// which is what opening another file does, and why the first window looked fine and
        /// the second did not.
        ///
        /// So the question asked is v:vim_did_enter: the session has started, in its own
        /// opinion rather than in the operating system's.
        /// </summary>
        private static void Probe(Warming warming)
        {
            if (Time.realtimeSinceStartup < warming.ProbeAt) return;

            warming.ProbeAt = Time.realtimeSinceStartup + ProbeSeconds;

            if (warming.Asking == null || !warming.Asking.Running)
            {
                warming.Asking?.Dispose();
                warming.Asking = new RpcClient();

                if (!warming.Asking.Connect(warming.Path))
                {
                    warming.Asking.Dispose();
                    warming.Asking = null;
                    return;
                }
            }

            warming.Asking.Request("nvim_eval", new object[] { "v:vim_did_enter" },
                (error, result) =>
                {
                    if (error != null || !Truthy(result)) return;

                    warming.Ready = true;
                    UwUTermPlugin.Log.LogInfo(
                        $"nvim: a session is ready on {warming.Path} " +
                        $"({Time.realtimeSinceStartup - warming.Asked:F1}s after asking)");
                });
        }

        private static bool Truthy(object answer)
        {
            try { return answer != null && Convert.ToInt64(answer) != 0; }
            catch (Exception) { return false; }
        }

        private static void Done(Warming warming)
        {
            warming.Asking?.Dispose();
            warming.Asking = null;
        }

        // ---- the session that starts sessions ----------------------------------------------

        private static void Start()
        {
            RpcClient factory = Factory();
            if (factory == null) return;

            // Beside the daemon's own socket, which is short: a unix socket path is limited to
            // about 108 characters and neovim keeps running quietly when it cannot bind one, so
            // a long path buys a session that never listens and never says why.
            string path = Beside(NvimInstall.Address, "uwuterm-" + (++_made) + ".sock");
            if (path == null) return;

            // Started by the session on the machine, so it is on the machine too - same config,
            // same plugins, same tools. Not detached: these belong to the daemon and should go
            // when it does rather than outlive it as strays.
            const string lua =
                "local path = ...\n" +
                "pcall(vim.fn.delete, path)\n" +
                "return vim.fn.jobstart({'nvim', '--headless', '--listen', path})";

            _warming = new Warming { Path = path, Asked = Time.realtimeSinceStartup };

            factory.Request("nvim_exec_lua", new object[] { lua, new object[] { path } },
                (error, result) =>
                {
                    if (error != null)
                    {
                        UwUTermPlugin.Log.LogWarning("nvim: the session could not be started - " + error);
                        return;
                    }

                    try { Jobs[path] = Convert.ToInt32(result); }
                    catch (Exception) { }
                });
        }

        private static void Stop(string path)
        {
            if (!Jobs.TryGetValue(path, out int job)) return;

            Jobs.Remove(path);
            Factory()?.Request("nvim_exec_lua",
                new object[] { "pcall(vim.fn.jobstop, ...)", new object[] { job } });
        }

        /// <summary>
        /// The connection sessions are asked for through.
        ///
        /// Kept rather than opened per request, and retried on a slow tick: the daemon can be
        /// started after the game, and a player who starts one halfway through an evening should
        /// get sessions from then on without restarting anything.
        /// </summary>
        private static RpcClient Factory()
        {
            if (_factory != null && _factory.Running) return _factory;

            if (_factory != null) { _factory.Dispose(); _factory = null; }

            string address = NvimInstall.Address;
            if (address == null || Time.realtimeSinceStartup < _askAgainAt) return null;

            _askAgainAt = Time.realtimeSinceStartup + RetrySeconds;

            var client = new RpcClient();
            if (!client.Connect(address)) { client.Dispose(); return null; }

            UwUTermPlugin.Log.LogInfo("nvim: sessions will be started by the one on " + address);
            _factory = client;
            return _factory;
        }

        /// <summary>A path in the same place as the daemon's own socket. Whatever directory that
        /// is, both sides of the sandbox can already see it - the game reaches the daemon
        /// through it.</summary>
        private static string Beside(string address, string name)
        {
            if (string.IsNullOrEmpty(address)) return null;

            try
            {
                string directory = Path.GetDirectoryName(address);
                return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
            }
            catch (ArgumentException) { return null; }
        }

        private static bool Late(Warming warming) =>
            Time.realtimeSinceStartup - warming.Asked > StartingSeconds;
    }
}
