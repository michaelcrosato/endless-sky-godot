using System.Linq;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// Graphics settings, remembered between runs.
    /// </summary>
    /// <remarks>
    /// A settings menu that forgets everything on quit is half a feature: a player who
    /// turns on fullscreen expects it to still be fullscreen tomorrow, and having to
    /// set it every launch is worse than not offering it.
    ///
    /// Stored under <c>user://</c>, which Godot maps to the per-user application data
    /// directory — NOT next to the executable. That matters for a game folder the
    /// player may have dropped somewhere read-only, and it means a settings file
    /// survives replacing the build.
    ///
    /// Loading is deliberately forgiving. A missing file is the normal first-run case,
    /// and a corrupt one should cost the player their preferences, not their game — so
    /// anything unreadable falls back to defaults rather than throwing.
    /// </remarks>
    public static class GameSettings
    {
        private const string Path = "user://settings.cfg";
        private const string Section = "graphics";

        /// <summary>Reads the saved settings and applies them to the running game.</summary>
        public static void Apply()
        {
            var config = new ConfigFile();
            if (config.Load(Path) != Error.Ok)
                return;

            var mode = (DisplayServer.WindowMode)(int)config.GetValue(
                Section, "window_mode", (int)DisplayServer.WindowMode.Windowed);

            // Explicit engine launch options take precedence over remembered window
            // settings. This also makes a requested capture resolution reproducible.
            // Godot removes recognized engine options from OS.GetCmdlineArgs().
            string[] args = System.Environment.GetCommandLineArgs().Skip(1)
                .TakeWhile(arg => arg is not "--" and not "++").ToArray();
            bool explicitMode = args.Any(arg => arg is "--windowed" or "-w"
                or "--fullscreen" or "-f" or "--maximized" or "-m");
            if (explicitMode)
                mode = DisplayServer.WindowGetMode();

            // Size before mode: setting a size while fullscreen is ignored, and the
            // window would come back windowed at whatever it happened to be.
            if (mode == DisplayServer.WindowMode.Windowed && !args.Contains("--resolution"))
            {
                var size = new Vector2I(
                    (int)config.GetValue(Section, "width", 1600),
                    (int)config.GetValue(Section, "height", 900));

                if (size.X > 0 && size.Y > 0)
                    DisplayServer.WindowSetSize(size);
            }

            if (!explicitMode)
                DisplayServer.WindowSetMode(mode);

            DisplayServer.WindowSetVsyncMode((bool)config.GetValue(Section, "vsync", true)
                ? DisplayServer.VSyncMode.Enabled
                : DisplayServer.VSyncMode.Disabled);

            Engine.MaxFps = (int)config.GetValue(Section, "frame_cap", 0);

            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root != null)
                tree.Root.Msaa3D = (Viewport.Msaa)(int)config.GetValue(
                    Section, "msaa", (int)Viewport.Msaa.Disabled);
        }

        /// <summary>Writes the current settings out.</summary>
        public static void Save(bool glow)
        {
            var config = new ConfigFile();
            var tree = Engine.GetMainLoop() as SceneTree;

            config.SetValue(Section, "window_mode", (int)DisplayServer.WindowGetMode());

            Vector2I size = DisplayServer.WindowGetSize();
            config.SetValue(Section, "width", size.X);
            config.SetValue(Section, "height", size.Y);

            config.SetValue(Section, "vsync",
                DisplayServer.WindowGetVsyncMode() != DisplayServer.VSyncMode.Disabled);
            config.SetValue(Section, "frame_cap", Engine.MaxFps);
            config.SetValue(Section, "msaa", (int)(tree?.Root.Msaa3D ?? Viewport.Msaa.Disabled));
            config.SetValue(Section, "glow", glow);

            config.Save(Path);
        }

        /// <summary>Whether glow was on last time, for a setting the environment owns.</summary>
        public static bool GlowPreference(bool fallback)
        {
            var config = new ConfigFile();
            if (config.Load(Path) != Error.Ok)
                return fallback;

            return (bool)config.GetValue(Section, "glow", fallback);
        }

        /// <summary>Where the file lives, for a player who wants to delete it.</summary>
        public static string Location => ProjectSettings.GlobalizePath(Path);
    }
}
