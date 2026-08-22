using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using TerminalPoolSystem;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Renders the terminal in a font from a file on disk.
    ///
    /// The game ships one monospace TMP asset per locale - SourceCodePro for latin,
    /// JetBrainsMono for cyrillic, Mplus1Code and NotoSansMonoCJK for the CJK sets - and
    /// offers no way to choose between them, let alone to use anything else.
    ///
    /// Fonts are found by file rather than by asking the OS what is installed, because on
    /// Linux that question cannot be answered usefully. Unity's enumeration knows exactly one
    /// directory, /usr/share/fonts, and Steam runs games inside a container whose copy of it
    /// holds six DejaVu faces and nothing else - so every font the player actually has is
    /// invisible to it. Reading a file works the same everywhere, and lets the setting name a
    /// font the machine has rather than one the sandbox happens to expose.
    ///
    /// TMP builds a font asset from a UnityEngine.Font and reloads the face through it every
    /// time it rasterises a glyph it has not seen, so the bytes have to stay reachable from
    /// that object forever, not just at build time. A Font constructed from a path is used as
    /// the identity token, and FontEngine.LoadFontFace is patched to serve our bytes whenever
    /// it is handed one - which keeps TMP's own dynamic atlas machinery intact instead of
    /// reimplementing it.
    ///
    /// Only terminal rows are restyled. Window chrome, the mail client and the desktop keep
    /// the game's own fonts, whose layouts are measured against them. The asset a row arrived
    /// with becomes a TMP fallback, so a glyph the chosen font lacks still renders rather than
    /// showing tofu.
    /// </summary>
    internal static class TerminalFont
    {
        // TMP's own defaults for a runtime asset. The sampling size is the resolution glyphs
        // are rasterised at, not the size text is drawn at.
        private const int SamplingPointSize = 90;
        private const int AtlasPadding = 9;
        private const int AtlasSize = 1024;

        private static TMP_FontAsset _asset;
        private static string _builtFrom;
        private static bool _listed;

        // The Font objects we handed to TMP, and the file bytes each one stands for.
        private static readonly Dictionary<Font, byte[]> Sources = new Dictionary<Font, byte[]>();

        internal static void Apply(Harmony harmony)
        {
            MethodInfo load = AccessTools.Method(
                typeof(FontEngine), "LoadFontFace", new[] { typeof(Font), typeof(int) });

            if (load == null)
            {
                UwUTermPlugin.Log.LogWarning("font: FontEngine.LoadFontFace(Font, int) not found");
                return;
            }

            harmony.Patch(load, prefix: new HarmonyMethod(
                typeof(TerminalFont).GetMethod(nameof(BeforeLoadFontFace),
                    BindingFlags.Static | BindingFlags.NonPublic)));

            EnsureFontFolder();
            ListOnce();
        }

        /// <summary>The folder the setting tells people to copy a font into, created so it is
        /// there to be found rather than being a path they have to trust.</summary>
        private static void EnsureFontFolder()
        {
            string folder = Path.Combine(Paths.BepInExRootPath, "fonts");
            try
            {
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            }
            catch (System.Exception e)
            {
                UwUTermPlugin.Log.LogWarning("font: could not create " + folder + " - " + e.Message);
            }
        }

        /// <summary>
        /// TMP reloads the face from its Font every time it rasterises a glyph it has not seen
        /// before. For our fonts that Font carries no data the engine can read, so the call is
        /// answered from the file we loaded instead. Every other font in the game - the ones
        /// baked into the build - falls through untouched.
        /// </summary>
        private static bool BeforeLoadFontFace(Font font, int pointSize, ref FontEngineError __result)
        {
            if (font == null || !Sources.TryGetValue(font, out byte[] bytes)) return true;

            __result = FontEngine.LoadFontFace(bytes, pointSize);
            return false;
        }

        /// <summary>Drop the built asset so the next request builds against the new setting.
        /// The screen notices the font changing under it and resizes itself.</summary>
        internal static void OnConfigReloaded()
        {
            ListOnce();
            _asset = null;
            _builtFrom = null;
        }

        /// <summary>
        /// The font the terminal should draw in, or null to keep the one it has.
        ///
        /// The screen asks for this directly. It used to be pushed onto the game's rows for
        /// the screen to read back off one of them, which meant a font could only arrive by
        /// way of objects nothing draws any more - a path with no reason to exist once the
        /// rows stopped being the picture.
        /// </summary>
        internal static TMP_FontAsset For(TMP_FontAsset fallback)
        {
            string wanted = UwUTermPlugin.TerminalFontName.Value.Trim();
            if (wanted.Length == 0) return null;

            return Resolve(wanted, fallback);
        }

        // ---- the asset ---------------------------------------------------------------

        /// <summary>One asset serves every terminal. A name that cannot be resolved is
        /// remembered as such, so a typo costs one log line rather than one per row.</summary>
        private static TMP_FontAsset Resolve(string wanted, TMP_FontAsset gameFont)
        {
            string key = wanted + "|" + UwUTermPlugin.TerminalFontFallback.Value;
            if (_builtFrom == key) return _asset;

            _builtFrom = key;
            _asset = Build(wanted, gameFont);
            return _asset;
        }

        private static TMP_FontAsset Build(string wanted, TMP_FontAsset fallback)
        {
            string path = Locate(wanted);
            if (path == null)
            {
                UwUTermPlugin.Log.LogError("font: no font file matching \"" + wanted + "\" was found.");
                LogAvailable();
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (System.Exception e)
            {
                UwUTermPlugin.Log.LogError("font: " + path + " could not be read - " + e.Message);
                return null;
            }

            // The path form of the constructor, so the object stands a chance of loading on
            // its own; the LoadFontFace patch is what actually makes it work.
            var source = new Font(path);
            Sources[source] = bytes;

            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(
                source, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA,
                AtlasSize, AtlasSize, AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: true);

            if (asset == null)
            {
                Sources.Remove(source);
                UwUTermPlugin.Log.LogError("font: no usable face could be loaded from " + path);
                return null;
            }

            asset.name = "UwUTerm " + Path.GetFileNameWithoutExtension(path);

            // A grid is only a grid while every character advances the same distance, and TMP's
            // faux bold does not: with no bold face to switch to it thickens the glyph in place
            // and adds boldSpacing to the advance of every character it covers. That is a
            // fraction of a cell each, invisible on one word and a shove on a whole row - a
            // file tree down the left, whose directory names are bold, walks everything to the
            // right of it out of its column. Zeroing it keeps the weight and drops the shove.
            asset.boldSpacing = 0f;

            if (UwUTermPlugin.TerminalFontFallback.Value && fallback != null)
                asset.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };

            // Nothing in any scene references an asset built at runtime, so the
            // Resources.UnloadUnusedAssets that Unity runs on a scene change would collect it -
            // and its atlas and material - out from under the rows still drawing with it.
            Keep(source);
            Keep(asset);
            Keep(asset.material);
            if (asset.atlasTextures != null)
                foreach (Texture2D atlas in asset.atlasTextures) Keep(atlas);

            string via = UwUTermPlugin.TerminalFontFallback.Value && fallback != null
                ? ", falling back to " + fallback.name
                : "";
            UwUTermPlugin.Log.LogInfo(
                "font: terminal rendering in " + asset.faceInfo.familyName + " from " + path + via);
            return asset;
        }

        private static void Keep(Object asset)
        {
            if (asset != null) asset.hideFlags = HideFlags.DontUnloadUnusedAsset;
        }

        // ---- finding the file ---------------------------------------------------------

        /// <summary>
        /// An absolute path is taken as given. Anything else is matched against the stem of
        /// every font file in the search roots, preferring an exact name and then the regular
        /// weight - so "JetBrains Mono" finds JetBrainsMono-Regular.ttf rather than its
        /// ExtraBoldItalic sibling.
        ///
        /// A path that does not exist is not an error: the directory a font sits in on the
        /// host is usually not the one it is reachable through here, so "fira-mono/FiraMono-
        /// Regular.otf" is retried as the name FiraMono-Regular and found wherever it is.
        /// </summary>
        private static string Locate(string wanted)
        {
            bool looksLikePath = wanted.IndexOf('/') >= 0 || wanted.IndexOf('\\') >= 0;
            if (looksLikePath && File.Exists(wanted)) return wanted;

            string target = Normalize(looksLikePath ? Path.GetFileNameWithoutExtension(wanted) : wanted);
            string best = null;
            int bestRank = int.MaxValue;

            foreach (string file in Available())
            {
                string stem = Normalize(Path.GetFileNameWithoutExtension(file));

                int rank;
                if (stem == target) rank = 0;
                else if (stem == target + "regular") rank = 1;
                else if (stem.StartsWith(target, System.StringComparison.Ordinal))
                    rank = 2 + (stem.Length - target.Length);
                else continue;

                if (rank < bestRank) { bestRank = rank; best = file; }
            }

            return best;
        }

        /// <summary>Names are compared with punctuation and case thrown away, so the family
        /// name a font is known by matches the filename it ships under.</summary>
        private static string Normalize(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));

            return sb.ToString();
        }

        private static IEnumerable<string> Available()
        {
            foreach (string root in Roots())
            {
                string[] files;
                try
                {
                    if (!Directory.Exists(root)) continue;
                    files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories);
                }
                catch (System.Exception)
                {
                    continue;
                }

                foreach (string file in files)
                {
                    string extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension == ".ttf" || extension == ".otf") yield return file;
                }
            }
        }

        /// <summary>
        /// Where to look, nearest first. BepInEx/fonts leads because it is the one directory
        /// guaranteed to be reachable - the game is running out of it - and /run/host is where
        /// Steam's container mounts the real root, which is the only way to reach the machine's
        /// own fonts from inside one.
        /// </summary>
        private static IEnumerable<string> Roots()
        {
            yield return Path.Combine(Paths.BepInExRootPath, "fonts");

            string home = System.Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".fonts");
                yield return Path.Combine(home, ".local/share/fonts");
            }

            yield return "/run/host/usr/share/fonts";
            yield return "/run/host/usr/local/share/fonts";
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";

            string windows = System.Environment.GetEnvironmentVariable("WINDIR");
            if (!string.IsNullOrEmpty(windows)) yield return Path.Combine(windows, "Fonts");
        }

        private static void ListOnce()
        {
            if (_listed || !UwUTermPlugin.ListFonts.Value) return;
            _listed = true;
            LogAvailable();
        }

        /// <summary>
        /// Every root and what it holds, rather than one flat list of names. Which roots exist
        /// is the useful half: it says whether the game can see the machine's fonts at all, and
        /// an absent /run/host is the signal to drop a file into BepInEx/fonts instead.
        /// </summary>
        private static void LogAvailable()
        {
            int total = 0;

            foreach (string root in Roots())
            {
                var names = new List<string>();
                try
                {
                    if (!Directory.Exists(root))
                    {
                        UwUTermPlugin.Log.LogInfo("font: " + root + " - absent");
                        continue;
                    }

                    foreach (string file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
                    {
                        string extension = Path.GetExtension(file).ToLowerInvariant();
                        if (extension == ".ttf" || extension == ".otf")
                            names.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
                catch (System.Exception e)
                {
                    UwUTermPlugin.Log.LogInfo("font: " + root + " - unreadable, " + e.Message);
                    continue;
                }

                total += names.Count;
                names.Sort();
                UwUTermPlugin.Log.LogInfo("font: " + root + " - " + names.Count + " found" +
                    (names.Count == 0 ? "" : ": " + string.Join(", ", names.ToArray())));
            }

            if (total == 0)
                UwUTermPlugin.Log.LogWarning(
                    "font: nothing found anywhere. Put a .ttf or .otf in " +
                    Path.Combine(Paths.BepInExRootPath, "fonts") + " and name it in TerminalFont.");
        }
    }
}
