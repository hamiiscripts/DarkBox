using System;
using System.Drawing;
using System.Windows.Forms;

// v4.1.2: progress window for the WebView2 download + install, shown by
// the setup bootstrapper. The download runs on a worker thread; this form
// only displays progress and offers Cancel during the download.
internal sealed class SetupProgressForm : Form
{
    private readonly Label _status;
    private readonly ProgressBar _bar;
    private readonly Label _detail;
    private readonly Button _cancel;

    public event EventHandler CancelRequested;

    public SetupProgressForm()
    {
        Text = "DarkBox Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 158);
        _status = new Label
        {
            Left = 20, Top = 16, Width = 420, Height = 38,
            Font = new Font("Segoe UI", 11),
            Text = "Preparing…",
        };
        _bar = new ProgressBar
        {
            Left = 20, Top = 60, Width = 420, Height = 22,
            Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous,
        };
        _detail = new Label
        {
            Left = 20, Top = 90, Width = 420, Height = 20,
            Font = new Font("Segoe UI", 9), ForeColor = Color.Gray,
        };
        _cancel = new Button
        {
            Text = "Cancel", Left = 360, Top = 118, Width = 80, Height = 26,
        };
        _cancel.Click += (s, e) =>
        {
            _cancel.Enabled = false;
            _cancel.Text = "Cancelling…";
            var h = CancelRequested;
            if (h != null) h(this, EventArgs.Empty);
        };
        Controls.Add(_status);
        Controls.Add(_bar);
        Controls.Add(_detail);
        Controls.Add(_cancel);
    }

    // percent < 0 = unknown length (marquee). Safe to call from any thread.
    public void Report(string status, int percent, string detail)
    {
        if (IsDisposed) return;
        try
        {
            if (!IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke((Action)(() => Report(status, percent, detail))); return; }
            if (status != null) _status.Text = status;
            if (percent >= 0)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = Math.Max(0, Math.Min(100, percent));
            }
            else _bar.Style = ProgressBarStyle.Marquee;
            if (detail != null) _detail.Text = detail;
        }
        catch { }
    }

    // Called by the worker when the install phase starts: no cancelling
    // a running installer.
    public void SetInstalling()
    {
        try
        {
            if (!IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke((Action)SetInstalling); return; }
            _cancel.Enabled = false;
        }
        catch { }
    }

    public void Done()
    {
        try
        {
            if (!IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke((Action)Close); return; }
            Close();
        }
        catch { }
    }
}
