using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using LanAdmin.Contracts;

namespace LanAgent;

internal static class AgentNotifierApplication
{
    public static void Run()
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            name: $@"Local\LanAdmin.AgentNotifier.{Process.GetCurrentProcess().SessionId}",
            createdNew: out var createdNew);

        if (!createdNew)
        {
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new AgentNotifierContext());
    }
}

internal sealed class AgentNotifierContext : ApplicationContext
{
    private static readonly TimeSpan AutomaticReminderInterval = TimeSpan.FromDays(1);
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Control _dispatcher;
    private readonly EventWaitHandle _manualReminderSignal;
    private readonly RegisteredWaitHandle _manualReminderRegistration;
    private DateTimeOffset? _lastAutomaticReminderShownAt;
    private ShutdownReminderForm? _activeReminder;

    public AgentNotifierContext()
    {
        _lastAutomaticReminderShownAt = AgentAutomaticReminderStateStore.Load().LastShownAt;
        _dispatcher = new Control();
        _dispatcher.CreateControl();
        _manualReminderSignal = AgentManualReminderSignal.OpenOrCreate();
        _manualReminderRegistration = ThreadPool.RegisterWaitForSingleObject(
            _manualReminderSignal,
            (_, _) =>
            {
                try
                {
                    if (_dispatcher.IsHandleCreated)
                    {
                        _dispatcher.BeginInvoke(new Action(EvaluateManualReminder));
                    }
                }
                catch
                {
                    // The notifier is shutting down.
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        _timer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromSeconds(30).TotalMilliseconds
        };
        _timer.Tick += (_, _) =>
        {
            EvaluateManualReminder();
            EvaluateReminder();
        };
        _timer.Start();
        EvaluateManualReminder();
        EvaluateReminder();
    }

    private void EvaluateManualReminder()
    {
        if (_activeReminder is not null && !_activeReminder.IsDisposed)
        {
            return;
        }

        var request = AgentManualReminderRequestStore.Load();
        if (request is null)
        {
            return;
        }

        var receiptState = AgentManualReminderReceiptStateStore.Load();
        if (string.Equals(receiptState.LastHandledCommandId, request.CommandId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var state = AgentNotifierStateStore.Load();
        if (state is null)
        {
            return;
        }

        AgentManualReminderReceiptStateStore.Save(new AgentManualReminderReceiptState(
            request.CommandId,
            DateTimeOffset.UtcNow));
        AgentManualReminderRequestStore.Clear();
        ShowReminder(state);
    }

    private void EvaluateReminder()
    {
        if (_activeReminder is not null && !_activeReminder.IsDisposed)
        {
            return;
        }

        var state = AgentNotifierStateStore.Load();
        if (state is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (state.ShutdownThresholdDays < ShutdownThresholdDefaults.MinDays)
        {
            return;
        }

        var thresholdSeconds = (long)TimeSpan.FromDays(state.ShutdownThresholdDays).TotalSeconds;
        if (state.UptimeSeconds < thresholdSeconds)
        {
            return;
        }

        var lastShownAt = _lastAutomaticReminderShownAt ?? AgentAutomaticReminderStateStore.Load().LastShownAt;
        if (lastShownAt.HasValue && now - lastShownAt.Value < AutomaticReminderInterval)
        {
            return;
        }

        _lastAutomaticReminderShownAt = now;
        AgentAutomaticReminderStateStore.Save(new AgentAutomaticReminderState(now));
        ShowReminder(state);
    }

    private void ShowReminder(AgentNotifierState state)
    {
        var styleState = AgentReminderStyleStore.Load();
        _activeReminder = new ShutdownReminderForm(
            state,
            styleState,
            onAcknowledge: () => _activeReminder = null);
        _activeReminder.FormClosed += (_, _) => _activeReminder = null;
        _activeReminder.Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _manualReminderRegistration.Unregister(null);
            _manualReminderSignal.Dispose();
            _dispatcher.Dispose();
            _activeReminder?.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class ShutdownReminderForm : Form
{
    private readonly Action _onAcknowledge;
    private readonly ReminderStyleDto _style;
    private readonly AgentNotifierState _state;
    private Image? _backgroundImage;
    private Image? _iconImage;

    public ShutdownReminderForm(AgentNotifierState state, AgentReminderStyleState styleState, Action onAcknowledge)
    {
        _onAcknowledge = onAcknowledge;
        _state = state;
        _style = styleState.Style;

        AutoScaleMode = AutoScaleMode.Font;
        BackColor = ParseColor(_style.BackgroundColor, Color.White);
        ClientSize = new Size(
            Bound(_style.Width, ReminderStyleDefaults.MinWidth, ReminderStyleDefaults.MaxWidth),
            Bound(_style.Height, ReminderStyleDefaults.MinHeight, ReminderStyleDefaults.MaxHeight));
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = _style.TopMost;

        var borderPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            Padding = new Padding(18)
        };

        _backgroundImage = LoadBackgroundImage(styleState.BackgroundImagePath);
        if (_backgroundImage is not null)
        {
            borderPanel.BackgroundImage = _backgroundImage;
            borderPanel.BackgroundImageLayout = ParseImageLayout(_style.BackgroundImageLayout);
        }

        borderPanel.Paint += (_, e) =>
        {
            var borderWidth = Bound(_style.BorderWidth, ReminderStyleDefaults.MinBorderWidth, ReminderStyleDefaults.MaxBorderWidth);
            if (borderWidth <= 0)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(ParseColor(_style.BorderColor, Color.FromArgb(220, 38, 38)), borderWidth);
            var radius = Bound(_style.CornerRadius, ReminderStyleDefaults.MinCornerRadius, ReminderStyleDefaults.MaxCornerRadius);
            var bounds = Rectangle.Inflate(borderPanel.ClientRectangle, -borderWidth / 2, -borderWidth / 2);
            bounds.Width -= 1;
            bounds.Height -= 1;

            if (radius <= 0)
            {
                e.Graphics.DrawRectangle(pen, bounds);
                return;
            }

            using var path = CreateRoundedRectanglePath(bounds, radius);
            e.Graphics.DrawPath(pen, path);
        };

        var showIcon = !string.Equals(_style.IconType, "None", StringComparison.OrdinalIgnoreCase);
        var contentLeft = showIcon ? 80 : 18;
        var contentWidth = Math.Max(120, ClientSize.Width - contentLeft - 20);
        var buttonTop = Math.Max(120, ClientSize.Height - 58);
        var detailTop = 74;
        var detailHeight = Math.Max(42, buttonTop - detailTop - 10);

        PictureBox? iconBox = null;
        _iconImage = showIcon ? BuildIconImage(_style.IconType) : null;
        if (_iconImage is not null)
        {
            iconBox = new PictureBox
            {
                Image = _iconImage,
                Location = new Point(18, 18),
                Size = new Size(48, 48),
                SizeMode = PictureBoxSizeMode.StretchImage
            };
        }

        var titleLabel = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = BuildFont(_style.TitleFontFamily, _style.TitleFontSize, _style.TitleFontStyle, 14F, FontStyle.Bold),
            ForeColor = ParseColor(_style.TitleColor, Color.FromArgb(185, 28, 28)),
            Location = new Point(contentLeft, 18),
            Size = new Size(contentWidth, 52),
            Text = _style.Title,
            UseMnemonic = false
        };

        var detailLabel = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = BuildFont(_style.ContentFontFamily, _style.ContentFontSize, _style.ContentFontStyle, 10.5F, FontStyle.Regular),
            ForeColor = ParseColor(_style.ContentColor, Color.FromArgb(127, 29, 29)),
            Location = new Point(contentLeft, detailTop),
            Size = new Size(contentWidth, detailHeight),
            Text = BuildDetailText(),
            UseMnemonic = false
        };

        var acknowledgeButton = BuildButton();
        acknowledgeButton.Location = new Point((ClientSize.Width - acknowledgeButton.Width) / 2, buttonTop);
        acknowledgeButton.Click += (_, _) =>
        {
            _onAcknowledge();
            Close();
        };

        if (iconBox is not null)
        {
            borderPanel.Controls.Add(iconBox);
        }

        borderPanel.Controls.Add(titleLabel);
        borderPanel.Controls.Add(detailLabel);
        borderPanel.Controls.Add(acknowledgeButton);
        Controls.Add(borderPanel);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyWindowRegion();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(this).WorkingArea;
        Location = _style.Position.Trim().ToUpperInvariant() switch
        {
            "BOTTOMLEFT" => new Point(area.Left + 16, area.Bottom - Height - 16),
            "TOPRIGHT" => new Point(area.Right - Width - 16, area.Top + 16),
            "TOPLEFT" => new Point(area.Left + 16, area.Top + 16),
            "CENTER" => new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2),
            _ => new Point(area.Right - Width - 16, area.Bottom - Height - 16)
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _backgroundImage?.Dispose();
            _iconImage?.Dispose();
            Region?.Dispose();
        }

        base.Dispose(disposing);
    }

    private Button BuildButton()
    {
        return new Button
        {
            BackColor = ParseColor(_style.ButtonBackgroundColor, Color.FromArgb(220, 38, 38)),
            FlatAppearance =
            {
                BorderColor = ParseColor(_style.BorderColor, Color.FromArgb(220, 38, 38)),
                BorderSize = 1
            },
            FlatStyle = FlatStyle.Flat,
            Font = BuildFont(_style.ButtonFontFamily, _style.ButtonFontSize, _style.ButtonFontStyle, 9.5F, FontStyle.Bold),
            ForeColor = ParseColor(_style.ButtonTextColor, Color.White),
            Size = new Size(120, 36),
            Text = _style.ButtonText,
            UseVisualStyleBackColor = false
        };
    }

    private string BuildDetailText()
    {
        return (_style.ContentTemplate ?? ReminderStyleDefaults.DefaultContentTemplate)
            .Replace("{uptime}", AgentNotifierFormatting.FormatUptime(_state.UptimeSeconds), StringComparison.OrdinalIgnoreCase)
            .Replace("{hostName}", _state.HostName, StringComparison.OrdinalIgnoreCase)
            .Replace("{currentUser}", _state.CurrentUser, StringComparison.OrdinalIgnoreCase)
            .Replace("{thresholdDays}", _state.ShutdownThresholdDays.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyWindowRegion()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var radius = Bound(_style.CornerRadius, ReminderStyleDefaults.MinCornerRadius, ReminderStyleDefaults.MaxCornerRadius);
        Region?.Dispose();

        if (radius <= 0)
        {
            Region = null;
            return;
        }

        using var path = CreateRoundedRectanglePath(new Rectangle(0, 0, Width, Height), radius);
        Region = new Region(path);
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
    {
        var diameter = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Font BuildFont(string? family, double size, string? style, float fallbackSize, FontStyle fallbackStyle)
    {
        var fontStyle = ParseFontStyle(style, fallbackStyle);
        var fontSize = (float)Bound(size, ReminderStyleDefaults.MinFontSize, ReminderStyleDefaults.MaxFontSize);
        var fontFamily = string.IsNullOrWhiteSpace(family) ? ReminderStyleDefaults.DefaultFontFamily : family.Trim();

        try
        {
            return new Font(fontFamily, fontSize, fontStyle);
        }
        catch
        {
            return new Font(ReminderStyleDefaults.DefaultFontFamily, fallbackSize, fallbackStyle);
        }
    }

    private static FontStyle ParseFontStyle(string? value, FontStyle fallback)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "REGULAR" => FontStyle.Regular,
            "BOLD" => FontStyle.Bold,
            "ITALIC" => FontStyle.Italic,
            "BOLDITALIC" => FontStyle.Bold | FontStyle.Italic,
            _ => fallback
        };
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Length != 7 ||
            normalized[0] != '#' ||
            !int.TryParse(normalized[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return fallback;
        }

        return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    private static Image? LoadBackgroundImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var source = Image.FromFile(path);
            return new Bitmap(source);
        }
        catch
        {
            return null;
        }
    }

    private static Image BuildIconImage(string? iconType)
    {
        return iconType?.Trim().ToUpperInvariant() switch
        {
            "INFORMATION" => SystemIcons.Information.ToBitmap(),
            "ERROR" => SystemIcons.Error.ToBitmap(),
            "SUCCESS" => BuildSuccessIcon(),
            _ => SystemIcons.Warning.ToBitmap()
        };
    }

    private static Image BuildSuccessIcon()
    {
        var bitmap = new Bitmap(48, 48);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var fillBrush = new SolidBrush(Color.FromArgb(22, 163, 74));
        graphics.FillEllipse(fillBrush, 2, 2, 44, 44);
        using var pen = new Pen(Color.White, 5)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLines(pen, new[] { new Point(14, 25), new Point(22, 33), new Point(35, 16) });
        return bitmap;
    }

    private static ImageLayout ParseImageLayout(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "STRETCH" => ImageLayout.Stretch,
            "CENTER" => ImageLayout.Center,
            "TILE" => ImageLayout.Tile,
            "NONE" => ImageLayout.None,
            _ => ImageLayout.Zoom
        };
    }

    private static int Bound(int value, int min, int max)
    {
        return Math.Min(Math.Max(value, min), max);
    }

    private static double Bound(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return min;
        }

        return Math.Min(Math.Max(value, min), max);
    }
}
