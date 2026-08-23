using System;
using System.IO;

namespace UwUTerm
{
    /// <summary>
    /// Where the mod keeps its things.
    ///
    /// One folder under BepInEx rather than four beside it. Nothing here is a setting - these
    /// are the places the mod owns, and the one setting that can move any of them
    /// (<c>NvimWorkspace</c>) reads its default from here rather than the other way round, so
    /// this stays a plain statement of the layout with no configuration in it.
    ///
    /// The settings file itself is not here. BepInEx decides where a plugin's config lives and
    /// every other plugin's is beside it; moving ours would be tidying our own folder by making
    /// somebody else's untidy.
    /// </summary>
    internal static class Home
    {
        internal static string Root => Path.Combine(BepInEx.Paths.BepInExRootPath, "UwUTerm");

        /// <summary>Scripts written out of the editor, and where a save goes by default.</summary>
        internal static string Workspace => Path.Combine(Root, "workspace");

        /// <summary>The neovim binary, if the mod is the one supplying it.</summary>
        internal static string Nvim => Path.Combine(Root, "nvim");

        /// <summary>The editor's own config, plugins and state - the XDG directories a child
        /// neovim is started with.</summary>
        internal static string NvimConfig => Path.Combine(Root, "nvim-config");

        /// <summary>Fonts to look in before the ones the system has.</summary>
        internal static string Fonts => Path.Combine(Root, "fonts");

        /// <summary>
        /// Command history, kept beside the scripts rather than beside the settings.
        ///
        /// It is what was typed, not how the mod is set up, and a shell keeps its history in the
        /// directory it calls home. Fixed to the workspace the mod owns even when the editor has
        /// been pointed somewhere else - history following a setting about where to save files
        /// would be a surprise, and a path that moves is a path that loses what was in it.
        /// </summary>
        internal static string History => Path.Combine(Workspace, ".history");

        /// <summary>
        /// Bring an older install's folders in, once.
        ///
        /// They used to sit directly under BepInEx, and a player who has been using the mod has
        /// scripts in one of them and a neovim install in another. Moved rather than copied, so
        /// there is one of each afterwards and no question later about which is being read.
        ///
        /// Anything already in the new place wins and the old one is left alone: two folders of
        /// the same name are the player's to merge, and this is not the moment to guess.
        /// </summary>
        internal static void Settle()
        {
            string old = BepInEx.Paths.BepInExRootPath;

            Folder(Path.Combine(old, "workspace"), Workspace);
            Folder(Path.Combine(old, "nvim"), Nvim);
            Folder(Path.Combine(old, "nvim-config"), NvimConfig);
            Folder(Path.Combine(old, "fonts"), Fonts);

            File(Path.Combine(BepInEx.Paths.ConfigPath, UwUTermPlugin.Guid + ".history"), History);
        }

        private static void Folder(string from, string to)
        {
            if (!Directory.Exists(from) || Directory.Exists(to)) return;

            Move(from, to, () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                Directory.Move(from, to);
            });
        }

        private static void File(string from, string to)
        {
            if (!System.IO.File.Exists(from) || System.IO.File.Exists(to)) return;

            Move(from, to, () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                System.IO.File.Move(from, to);
            });
        }

        private static void Move(string from, string to, Action move)
        {
            try
            {
                move();
                UwUTermPlugin.Log.LogInfo($"moved {from} to {to}");
            }
            catch (Exception e)
            {
                // Said rather than worked around. The mod carries on with an empty new folder,
                // and a player told which two paths to look at can put one inside the other; a
                // fallback that quietly reads the old place would leave both in use for good.
                UwUTermPlugin.Log.LogError(
                    $"could not move {from} to {to} - {e.Message}. Move it by hand to keep it.");
            }
        }
    }
}
