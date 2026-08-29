using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// Where a saved game lives on disk, and the only place the view layer touches it.
    /// </summary>
    /// <remarks>
    /// The serialising is <see cref="EndlessSky.Sim.SaveGame"/>'s job and is engine-free
    /// and covered by the sim suite; this is only the file handling around it, which
    /// needs Godot to resolve <c>user://</c>.
    ///
    /// A save is written through Godot's own FileAccess rather than System.IO because
    /// <c>user://</c> is a virtual path — it lands in the per-user application data
    /// directory on every platform, which is where a save belongs and where an exported
    /// build can actually write.
    ///
    /// One slot, deliberately. Multiple saves are a UI feature; having none at all was
    /// the defect. The whole player state round-trips through the same data format the
    /// game's own content uses, so a save is readable by eye.
    /// </remarks>
    public static class SaveSlot
    {
        private const string Path = "user://savegame.txt";

        /// <summary>Whether there is a game to continue.</summary>
        public static bool Exists => FileAccess.FileExists(Path);

        /// <summary>Writes a save, returning false if the file could not be opened.</summary>
        public static bool Save(string text)
        {
            using FileAccess? file = FileAccess.Open(Path, FileAccess.ModeFlags.Write);
            if (file is null)
            {
                GD.PrintErr($"[save] could not write {Path}: {FileAccess.GetOpenError()}");
                return false;
            }

            file.StoreString(text);
            return true;
        }

        /// <summary>Reads the save, or null when there is none or it cannot be read.</summary>
        public static string? Load()
        {
            if (!Exists)
            {
                return null;
            }

            using FileAccess? file = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
            if (file is null)
            {
                GD.PrintErr($"[save] could not read {Path}: {FileAccess.GetOpenError()}");
                return null;
            }

            return file.GetAsText();
        }

        /// <summary>The absolute path, for logging.</summary>
        public static string Where => ProjectSettings.GlobalizePath(Path);
    }
}
