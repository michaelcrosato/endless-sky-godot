using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// One place for the look of every panel, so the shell reads as one interface
    /// rather than as a pile of separately-styled screens.
    /// </summary>
    /// <remarks>
    /// The palette is the landed screen's, which was here first: a very dark blue
    /// ground, a cool steel border, and text that steps down in brightness as it
    /// steps down in importance. Keeping the numbers in one file is what stops the
    /// fifth panel drifting a shade away from the first.
    /// </remarks>
    public static class UiTheme
    {
        public static readonly Color Ground = new Color(0.05f, 0.07f, 0.11f, 0.95f);
        public static readonly Color Border = new Color(0.35f, 0.55f, 0.75f, 0.8f);
        public static readonly Color Dim = new Color(0f, 0f, 0f, 0.62f);

        /// <summary>Headings and the selected row.</summary>
        public static readonly Color Bright = new Color(0.92f, 0.95f, 0.98f);

        /// <summary>Ordinary body text.</summary>
        public static readonly Color Body = new Color(0.82f, 0.87f, 0.92f);

        /// <summary>Labels, hints and anything secondary.</summary>
        public static readonly Color Muted = new Color(0.55f, 0.65f, 0.75f);

        /// <summary>Warnings and destructive choices.</summary>
        public static readonly Color Warn = new Color(0.92f, 0.66f, 0.42f);

        /// <summary>Good news: money in, objective met.</summary>
        public static readonly Color Good = new Color(0.55f, 0.85f, 0.62f);

        /// <summary>A full-screen dimmer, so the world reads as paused behind a panel.</summary>
        public static ColorRect Dimmer()
        {
            var dim = new ColorRect { Color = Dim };
            // Anchors AND offsets: a Control under a CanvasLayer is not laid out by a
            // parent, so anchors alone leave it sized zero and it draws nothing.
            dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            return dim;
        }

        /// <summary>The standard bordered panel.</summary>
        public static PanelContainer Panel()
        {
            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = Ground,
                BorderColor = Border,
                BorderWidthLeft = 1, BorderWidthRight = 1,
                BorderWidthTop = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
                ContentMarginLeft = 24, ContentMarginRight = 24,
                ContentMarginTop = 18, ContentMarginBottom = 18,
            });
            return panel;
        }

        /// <summary>
        /// Centres a panel on screen.
        /// </summary>
        /// <remarks>
        /// A CenterContainer rather than LayoutPreset.Center on the panel itself: that
        /// preset captures offsets from the control's size at the moment it is applied,
        /// which is before its contents have given it one, so the panel ends up pinned
        /// to a corner. That bug cost a capture cycle on the landed screen.
        /// </remarks>
        public static CenterContainer Centred()
        {
            var centre = new CenterContainer();
            centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            return centre;
        }

        public static Label Text(string text, int size = 14, Color? colour = null)
        {
            var label = new Label { Text = text };
            label.AddThemeFontSizeOverride("font_size", size);
            label.AddThemeColorOverride("font_color", colour ?? Body);
            return label;
        }

        public static Label Title(string text) => Text(text, 22, Bright);

        public static Label Heading(string text) => Text(text, 12, Muted);

        /// <summary>A fixed-width column pair, for the many label/value lists here.</summary>
        public static string Row(string label, string value, int width = 22) =>
            label.PadRight(width) + value;
    }
}
