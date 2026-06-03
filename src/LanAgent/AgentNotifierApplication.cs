using System.Diagnostics;
using System.Drawing;
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
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(1);
    private readonly System.Windows.Forms.Timer _timer;
    private ShutdownReminderForm? _activeReminder;

    public AgentNotifierContext()
    {
        _timer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromSeconds(30).TotalMilliseconds
        };
        _timer.Tick += (_, _) => EvaluateReminder();
        _timer.Start();
        EvaluateReminder();
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

        var reminderState = AgentReminderStateStore.Load();
        if (reminderState.SnoozeUntil.HasValue && reminderState.SnoozeUntil.Value > now)
        {
            return;
        }

        if (reminderState.LastShownAt.HasValue && now - reminderState.LastShownAt.Value < ReminderInterval)
        {
            return;
        }

        AgentReminderStateStore.Save(reminderState with
        {
            LastShownAt = now,
            SnoozeUntil = null
        });

        _activeReminder = new ShutdownReminderForm(
            state,
            onAcknowledge: () => _activeReminder = null,
            onSnooze: () =>
            {
                AgentReminderStateStore.Save(new AgentReminderState(now, now.Add(ReminderInterval)));
                _activeReminder = null;
            });
        _activeReminder.FormClosed += (_, _) => _activeReminder = null;
        _activeReminder.Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _activeReminder?.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class ShutdownReminderForm : Form
{
    private readonly Action _onAcknowledge;
    private readonly Action _onSnooze;

    public ShutdownReminderForm(AgentNotifierState state, Action onAcknowledge, Action onSnooze)
    {
        _onAcknowledge = onAcknowledge;
        _onSnooze = onSnooze;

        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Color.White;
        ClientSize = new Size(420, 220);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;

        var borderPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(18)
        };
        borderPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(220, 38, 38), 2);
            var bounds = borderPanel.ClientRectangle;
            bounds.Width -= 1;
            bounds.Height -= 1;
            e.Graphics.DrawRectangle(pen, bounds);
        };

        var iconBox = new PictureBox
        {
            Image = SystemIcons.Warning.ToBitmap(),
            Location = new Point(18, 18),
            Size = new Size(48, 48),
            SizeMode = PictureBoxSizeMode.StretchImage
        };

        var titleLabel = new Label
        {
            AutoSize = false,
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            ForeColor = Color.FromArgb(185, 28, 28),
            Location = new Point(80, 18),
            Size = new Size(320, 30),
            Text = $"电脑已运行超过 {state.ShutdownThresholdDays} 天"
        };

        var detailLabel = new Label
        {
            AutoSize = false,
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(127, 29, 29),
            Location = new Point(80, 56),
            Size = new Size(320, 90),
            Text = BuildDetailText(state)
        };

        var acknowledgeButton = BuildButton("知道了", new Point(208, 162), filled: false);
        acknowledgeButton.Click += (_, _) =>
        {
            _onAcknowledge();
            Close();
        };

        var snoozeButton = BuildButton("1小时后提醒", new Point(76, 162), filled: true);
        snoozeButton.Click += (_, _) =>
        {
            _onSnooze();
            Close();
        };

        borderPanel.Controls.Add(iconBox);
        borderPanel.Controls.Add(titleLabel);
        borderPanel.Controls.Add(detailLabel);
        borderPanel.Controls.Add(snoozeButton);
        borderPanel.Controls.Add(acknowledgeButton);
        Controls.Add(borderPanel);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(this).WorkingArea;
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    private static Button BuildButton(string text, Point location, bool filled)
    {
        return new Button
        {
            BackColor = filled ? Color.FromArgb(220, 38, 38) : Color.White,
            FlatAppearance =
            {
                BorderColor = Color.FromArgb(220, 38, 38),
                BorderSize = 1
            },
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            ForeColor = filled ? Color.White : Color.FromArgb(220, 38, 38),
            Location = location,
            Size = new Size(120, 36),
            Text = text,
            UseVisualStyleBackColor = false
        };
    }

    private static string BuildDetailText(AgentNotifierState state)
    {
        return $"已运行：{AgentNotifierFormatting.FormatUptime(state.UptimeSeconds)}\r\n请及时关机重启电脑，避免长时间运行造成卡顿";
    }
}
