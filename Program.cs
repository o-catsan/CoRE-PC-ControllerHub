using System;
using System.Drawing;
using System.Windows.Forms;

namespace ControllerHubNative;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatalError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ShowFatalError(ex);
        };

        try
        {
            using var selector = new StartupModeForm(InputConfig.Load().Mode);
            if (selector.ShowDialog() != DialogResult.OK)
                return;

            Application.Run(new MainForm(selector.SelectedMode));
        }
        catch (Exception ex)
        {
            ShowFatalError(ex);
        }
    }

    private static void ShowFatalError(Exception ex)
    {
        try
        {
            MessageBox.Show(
                "Controller Hub の起動中にエラーが発生しました。\n\n" + ex,
                "Controller Hub - 起動エラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch { }
    }
}

/// <summary>
/// EXE起動時に Controller / Keyboard + Mouse を選択する画面。
/// </summary>
internal sealed class StartupModeForm : Form
{
    public InputMode SelectedMode { get; private set; }

    private readonly Button controllerButton = new();
    private readonly Button keyboardMouseButton = new();

    private static readonly Color Bg = Color.FromArgb(7, 10, 15);
    private static readonly Color Panel = Color.FromArgb(16, 21, 29);
    private static readonly Color TextColor = Color.FromArgb(225, 233, 243);
    private static readonly Color Muted = Color.FromArgb(125, 143, 166);
    private static readonly Color Blue = Color.FromArgb(42, 137, 208);

    public StartupModeForm(InputMode initialMode)
    {
        SelectedMode = initialMode;
        Text = "Controller Hub - Input Mode";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 300);
        MinimumSize = new Size(620, 300);
        MaximumSize = new Size(620, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Bg;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            Text = "CONTROLLER HUB",
            AutoSize = true,
            Font = new Font("Segoe UI", 20F, FontStyle.Bold),
            ForeColor = TextColor,
            Location = new Point(28, 24)
        };
        Controls.Add(title);

        var subtitle = new Label
        {
            Text = "SELECT INPUT MODE",
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Muted,
            Location = new Point(30, 70)
        };
        Controls.Add(subtitle);

        SetupButton(controllerButton, "CONTROLLER\nGamepad / XInput", 30, 110, InputMode.Controller);
        SetupButton(keyboardMouseButton, "KEYBOARD + MOUSE\nWASD / Mouse", 320, 110, InputMode.KeyboardMouse);

        Shown += (_, _) => UpdateSelectionVisual();
    }

    private void SetupButton(Button button, string text, int x, int y, InputMode mode)
    {
        button.Text = text;
        button.Location = new Point(x, y);
        button.Size = new Size(270, 110);
        button.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        button.ForeColor = TextColor;
        button.BackColor = Panel;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(45, 75, 100);
        button.Click += (_, _) =>
        {
            SelectedMode = mode;
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(button);
    }

    private void UpdateSelectionVisual()
    {
        controllerButton.BackColor = SelectedMode == InputMode.Controller ? Blue : Panel;
        keyboardMouseButton.BackColor = SelectedMode == InputMode.KeyboardMouse ? Blue : Panel;
    }
}
