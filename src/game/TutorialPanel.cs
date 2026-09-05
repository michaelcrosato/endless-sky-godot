using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// The tutorial's one piece of screen: what to do next, and what just happened.
    /// </summary>
    /// <remarks>
    /// Its own <see cref="CanvasLayer"/> at a layer above the landed overlay, on
    /// purpose. Two of the four steps happen at a port and two happen in flight, and
    /// those are different scenes; threading the tutorial through both would put half
    /// its logic in <see cref="LandedOverlay"/> and leave the prompt able to disagree
    /// with itself across a takeoff. The shell hides this hint while a menu is open.
    /// </remarks>
    public partial class TutorialPanel : CanvasLayer
    {
        /// <summary>Above the landed overlay, so the prompt survives a landing.</summary>
        private const int AbovePort = 20;

        private Label _prompt = null!;
        private Label _confirmation = null!;
        private PanelContainer _panel = null!;

        /// <summary>Frames a step's confirmation stays up before the next prompt takes over.</summary>
        private const int ConfirmationFrames = 260;

        private int _confirmationLeft;

        public override void _Ready()
        {
            Layer = AbovePort;

            _panel = new PanelContainer();
            _panel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
            _panel.GrowHorizontal = Control.GrowDirection.Both;
            _panel.GrowVertical = Control.GrowDirection.Begin;
            _panel.Position = new Vector2(0, -96);
            _panel.AddThemeStyleboxOverride("panel", Style());
            AddChild(_panel);

            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 3);
            _panel.AddChild(column);

            _confirmation = new Label { HorizontalAlignment = HorizontalAlignment.Center };
            _confirmation.AddThemeFontSizeOverride("font_size", 15);
            _confirmation.AddThemeColorOverride("font_color", new Color(0.62f, 0.88f, 0.70f));
            column.AddChild(_confirmation);

            _prompt = new Label { HorizontalAlignment = HorizontalAlignment.Center };
            _prompt.AddThemeFontSizeOverride("font_size", 16);
            _prompt.AddThemeColorOverride("font_color", new Color(0.90f, 0.94f, 0.99f));
            _prompt.AddThemeConstantOverride("outline_size", 3);
            _prompt.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
            column.AddChild(_prompt);

            var dismiss = new Label
            {
                Text = "F3 to dismiss",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            dismiss.AddThemeFontSizeOverride("font_size", 11);
            dismiss.AddThemeColorOverride("font_color", new Color(0.40f, 0.48f, 0.56f));
            column.AddChild(dismiss);
        }

        private static StyleBoxFlat Style() => new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.07f, 0.11f, 0.93f),
            BorderColor = new Color(0.35f, 0.55f, 0.75f, 0.8f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 18,
            ContentMarginRight = 18,
            ContentMarginTop = 9,
            ContentMarginBottom = 9,
        };

        /// <summary>Show where the tutorial has got to, or hide once it is finished.</summary>
        public void Show(Tutorial tutorial, string? justFinished, bool landed = false, bool modal = false)
        {
            // Ports already carry their own control legend. Use that lower space so
            // the prompt does not cover the port's credit balance and controls at 720p.
            _panel.Position += new Vector2(0, (landed ? -16 : -96) - _panel.OffsetBottom);
            if (!string.IsNullOrEmpty(justFinished))
            {
                _confirmation.Text = justFinished;
                _confirmationLeft = ConfirmationFrames;
            }
            if (modal)
            {
                Visible = false;
                return;
            }
            if (tutorial.IsDismissed ||
                (tutorial.IsComplete && _confirmationLeft <= 0 && string.IsNullOrEmpty(justFinished)))
            {
                // The final "good flying" earns its moment on screen before the panel
                // goes; hiding the instant the last step completes means the player
                // never sees the game acknowledge what they did.
                Visible = false;
                return;
            }

            Visible = true;

            if (string.IsNullOrEmpty(justFinished) && --_confirmationLeft <= 0)
            {
                _confirmation.Text = string.Empty;
            }

            _confirmation.Visible = !string.IsNullOrEmpty(_confirmation.Text);
            _prompt.Text = tutorial.Prompt;
            _prompt.Visible = !string.IsNullOrEmpty(_prompt.Text);
        }
    }
}
