using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Command history that survives closing a terminal, or the game.
    ///
    /// Terminal.historialComandos is only ever appended to - nothing clears or reassigns
    /// it anywhere in the game - so every terminal can be pointed at one shared list. That
    /// gives cross-window history for free: a command typed in one terminal is immediately
    /// reachable with Up in another, and there is only ever one file to persist.
    ///
    /// indiceHistorial has to be set to the entry count when a terminal opens, because the
    /// game's Up handler requires indiceHistorial > 0 before it will step back - a fresh
    /// terminal left at 0 could never reach loaded history at all.
    /// </summary>
    internal static class History
    {
        private static readonly List<string> Entries = new List<string>();
        private static bool _loaded;
        private static Regex _ignore;
        private static string _ignoreSource;

        private static string Path =>
            System.IO.Path.Combine(BepInEx.Paths.ConfigPath, UwUTermPlugin.Guid + ".history");

        internal static void Apply(Harmony harmony)
        {
            var awake = AccessTools.Method(typeof(Terminal), "Awake");
            if (awake != null)
                harmony.Patch(awake, postfix: new HarmonyMethod(
                    typeof(History).GetMethod(nameof(OnTerminalAwake),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            else
                UwUTermPlugin.Log.LogWarning("history: Terminal.Awake not found");

            var processed = AccessTools.Method(typeof(Terminal), "ProcesaLinea", new[] { typeof(UnityEngine.KeyCode) });
            if (processed != null)
                harmony.Patch(processed, postfix: new HarmonyMethod(
                    typeof(History).GetMethod(nameof(OnLineProcessed),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            else
                UwUTermPlugin.Log.LogWarning("history: Terminal.ProcesaLinea not found");
        }

        private static void OnTerminalAwake(Terminal __instance)
        {
            if (!UwUTermPlugin.PersistHistory.Value) return;

            Load();
            __instance.historialComandos = Entries;
            __instance.indiceHistorial = Entries.Count;
        }

        /// <summary>
        /// The game appends to the list inline, so there is no add to hook - instead this
        /// runs afterwards and inspects whatever turned up on the end.
        /// </summary>
        private static void OnLineProcessed(Terminal __instance)
        {
            if (!UwUTermPlugin.PersistHistory.Value) return;
            if (!ReferenceEquals(__instance.historialComandos, Entries)) return;
            if (Entries.Count == 0) return;

            string command = Entries[Entries.Count - 1];

            if (Rejected(command))
            {
                Entries.RemoveAt(Entries.Count - 1);
                __instance.indiceHistorial = Entries.Count;
                return;
            }

            int limit = Math.Max(1, UwUTermPlugin.HistoryLimit.Value);
            while (Entries.Count > limit) Entries.RemoveAt(0);

            Save();
        }

        private static bool Rejected(string command)
        {
            if (string.IsNullOrEmpty(command)) return true;

            // bash's ignorespace: a leading space keeps a command out of history, which is
            // the standard way to type something with a password in it and not keep it.
            if (UwUTermPlugin.HistoryIgnoreSpacePrefix.Value && command[0] == ' ') return true;

            if (UwUTermPlugin.HistoryIgnoreDuplicates.Value &&
                Entries.Count > 1 && Entries[Entries.Count - 2] == command) return true;

            string pattern = UwUTermPlugin.HistoryIgnorePattern.Value;
            if (!string.IsNullOrEmpty(pattern))
            {
                if (pattern != _ignoreSource)
                {
                    _ignoreSource = pattern;
                    try { _ignore = new Regex(pattern, RegexOptions.IgnoreCase); }
                    catch (Exception e)
                    {
                        _ignore = null;
                        UwUTermPlugin.Log.LogWarning("history: bad HistoryIgnorePattern - " + e.Message);
                    }
                }
                if (_ignore != null && _ignore.IsMatch(command)) return true;
            }

            return false;
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(Path)) return;

                foreach (string line in File.ReadAllLines(Path))
                    if (line.Length > 0) Entries.Add(line);

                int limit = Math.Max(1, UwUTermPlugin.HistoryLimit.Value);
                while (Entries.Count > limit) Entries.RemoveAt(0);

                UwUTermPlugin.Log.LogInfo($"history: loaded {Entries.Count} commands");
            }
            catch (Exception e)
            {
                UwUTermPlugin.Log.LogWarning("history: could not read - " + e.Message);
            }
        }

        /// <summary>Rewrites the whole file rather than appending, so trimming and rejection
        /// need no special case. A few hundred short lines is nothing.</summary>
        private static void Save()
        {
            try
            {
                File.WriteAllLines(Path, Entries.ToArray());
            }
            catch (Exception e)
            {
                UwUTermPlugin.Log.LogWarning("history: could not write - " + e.Message);
            }
        }
    }
}
