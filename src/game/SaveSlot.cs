using Godot;
using System;
using System.IO;
using System.Text;

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
    /// Godot resolves <c>user://</c> to the per-user application data directory.
    /// Writes use a temporary sibling and replace the destination only after the
    /// complete save has been flushed, so a failed write preserves the previous save.
    ///
    /// One slot, deliberately. Multiple saves are a UI feature; having none at all was
    /// the defect. The whole player state round-trips through the same data format the
    /// game's own content uses, so a save is readable by eye.
    /// </remarks>
    public static class SaveSlot
    {
        public const string DefaultPath = "user://savegame.txt";

        /// <summary>Whether there is a game to continue.</summary>
        public static bool Exists => Godot.FileAccess.FileExists(DefaultPath);

        /// <summary>Replaces a save only after writing it successfully.</summary>
        public static bool Save(string text, string path = DefaultPath)
        {
            string destination = ProjectSettings.GlobalizePath(path);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, System.IO.FileAccess.Write))
                {
                    file.Write(Encoding.UTF8.GetBytes(text));
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: true);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                GD.PrintErr($"[save] could not write {path}: {error.Message}");
                return false;
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Reads the save, or null when there is none or it cannot be read.</summary>
        public static string? Load(string path = DefaultPath)
        {
            if (!Godot.FileAccess.FileExists(path))
            {
                return null;
            }

            using Godot.FileAccess? file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (file is null)
            {
                GD.PrintErr($"[save] could not read {path}: {Godot.FileAccess.GetOpenError()}");
                return null;
            }

            return file.GetAsText();
        }

    }
}
