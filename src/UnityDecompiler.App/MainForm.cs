using System.Diagnostics;
using System.Drawing.Drawing2D;
using UnityDecompiler.Core;

namespace UnityDecompiler.App;

public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(24, 24, 28);
    private static readonly Color Surface = Color.FromArgb(36, 36, 42);
    private static readonly Color SurfaceHover = Color.FromArgb(44, 52, 66);
    private static readonly Color Accent = Color.FromArgb(86, 156, 214);
    private static readonly Color TextColor = Color.FromArgb(230, 230, 235);
    private static readonly Color MutedText = Color.FromArgb(150, 150, 160);

    private readonly DropZone dropZone;
    private readonly TextBox outputBox;
    private readonly CheckBox includeFrameworkBox;
    private readonly CheckBox copyStreamingAssetsBox;
    private readonly ProgressBar progressBar;
    private readonly Label statusLabel;
    private readonly RichTextBox logBox;
    private readonly Button openOutputButton;
    private readonly Button cancelButton;
    private readonly string? startupPath;

    private CancellationTokenSource? cts;
    private string? lastOutputDirectory;

    public MainForm(string? startupPath)
    {
        this.startupPath = startupPath;

        Text = "Unity Decompiler";
        MinimumSize = new Size(640, 600);
        Size = new Size(760, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9.5f);
        AllowDrop = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 7,
            BackColor = Background,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // title
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));  // drop zone
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // output folder
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // options
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // progress
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // log
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons
        Controls.Add(layout);

        var title = new Label
        {
            Text = "Unity Decompiler",
            Font = new Font("Segoe UI Semibold", 18f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(title);

        dropZone = new DropZone { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
        dropZone.Click += (_, _) => BrowseForGame();
        layout.Controls.Add(dropZone);

        // Output folder row.
        var outputRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 6) };
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputRow.Controls.Add(new Label { Text = "Save to:", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = MutedText });
        outputBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = Path.Combine(GameDecompiler.DefaultOutputRoot, "<Game name>"),
        };
        outputRow.Controls.Add(outputBox);
        var browseOutput = MakeButton("Browse...");
        browseOutput.Click += (_, _) => BrowseForOutput();
        outputRow.Controls.Add(browseOutput);
        layout.Controls.Add(outputRow);

        // Options.
        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        includeFrameworkBox = new CheckBox { Text = "Also decompile Unity / System DLLs (slow)", AutoSize = true, ForeColor = MutedText };
        copyStreamingAssetsBox = new CheckBox { Text = "Copy StreamingAssets", AutoSize = true, Checked = true, ForeColor = MutedText };
        options.Controls.Add(includeFrameworkBox);
        options.Controls.Add(copyStreamingAssetsBox);
        layout.Controls.Add(options);

        // Progress.
        var progressPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 8) };
        statusLabel = new Label { Text = "Ready. Drop a Unity game above.", AutoSize = true, ForeColor = MutedText, Margin = new Padding(0, 0, 0, 4) };
        progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 14, Maximum = 1000, Style = ProgressBarStyle.Continuous };
        progressPanel.Controls.Add(statusLabel);
        progressPanel.Controls.Add(progressBar);
        layout.Controls.Add(progressPanel);

        logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Surface,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9.5f),
            WordWrap = true,
        };
        layout.Controls.Add(logBox);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        openOutputButton = MakeButton("Open output folder", primary: true);
        openOutputButton.Enabled = false;
        openOutputButton.Click += (_, _) => OpenFolder(lastOutputDirectory);
        cancelButton = MakeButton("Cancel");
        cancelButton.Enabled = false;
        cancelButton.Click += (_, _) => cts?.Cancel();
        buttons.Controls.Add(openOutputButton);
        buttons.Controls.Add(cancelButton);
        layout.Controls.Add(buttons);

        // The whole window accepts drops, not only the drop zone.
        foreach (Control c in new Control[] { this, dropZone, logBox })
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnter;
            c.DragLeave += (_, _) => dropZone.Highlighted = false;
            c.DragDrop += OnDragDrop;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!string.IsNullOrWhiteSpace(startupPath))
            _ = StartAsync(startupPath!);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (cts == null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            dropZone.Highlighted = true;
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        dropZone.Highlighted = false;
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            _ = StartAsync(paths[0]);
    }

    private void BrowseForGame()
    {
        if (cts != null) return;
        using var dialog = new OpenFileDialog
        {
            Title = "Select the Unity game",
            Filter = "Unity game (*.exe;*.zip;*.dll)|*.exe;*.zip;*.dll|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _ = StartAsync(dialog.FileName);
    }

    private void BrowseForOutput()
    {
        using var dialog = new FolderBrowserDialog { Description = "Where should the decompiled game be saved?", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            outputBox.Text = Path.Combine(dialog.SelectedPath, "Decompiled");
    }

    private async Task StartAsync(string inputPath)
    {
        if (cts != null) return;
        cts = new CancellationTokenSource();
        SetBusy(true);
        logBox.Clear();
        progressBar.Value = 0;
        AppendLog($"Input: {inputPath}", LogLevel.Info);

        var options = new DecompileOptions
        {
            InputPath = inputPath,
            OutputDirectory = string.IsNullOrWhiteSpace(outputBox.Text) ? null : outputBox.Text.Trim(),
            IncludeFrameworkAssemblies = includeFrameworkBox.Checked,
            CopyStreamingAssets = copyStreamingAssetsBox.Checked,
        };

        // Progress<T> marshals reports back onto the UI thread.
        var progress = new Progress<DecompileProgress>(OnProgress);
        var token = cts.Token;
        try
        {
            var result = await Task.Run(() => GameDecompiler.Run(options, progress, token), token);
            lastOutputDirectory = result.OutputDirectory;
            progressBar.Value = progressBar.Maximum;
            statusLabel.Text = result.Game.Backend == ScriptingBackend.IL2CPP
                ? $"{result.Game.DisplayName}: IL2CPP game, see the README in the output folder."
                : $"{result.Game.DisplayName}: {result.AssembliesDecompiled} assemblies decompiled.";
            dropZone.Subtitle = $"Last: {result.Game.DisplayName}. Drop another game to decompile it.";
            OpenFolder(result.OutputDirectory);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.", LogLevel.Warning);
            statusLabel.Text = "Cancelled.";
        }
        catch (UnityGameNotFoundException ex)
        {
            AppendLog(ex.Message, LogLevel.Error);
            statusLabel.Text = "That doesn't look like a Unity game.";
        }
        catch (Exception ex)
        {
            AppendLog("Error: " + ex, LogLevel.Error);
            statusLabel.Text = "Something went wrong, see the log.";
        }
        finally
        {
            cts.Dispose();
            cts = null;
            SetBusy(false);
        }
    }

    private void OnProgress(DecompileProgress p)
    {
        if (p.Fraction is double f)
            progressBar.Value = Math.Clamp((int)(f * progressBar.Maximum), 0, progressBar.Maximum);

        // Per-file counters only update the status line; everything else goes to the log too.
        statusLabel.Text = p.Message.Trim();
        if (!(p.Level == LogLevel.Info && p.Message.EndsWith(" files", StringComparison.Ordinal)))
            AppendLog(p.Message, p.Level);
    }

    private void AppendLog(string message, LogLevel level)
    {
        logBox.SelectionStart = logBox.TextLength;
        logBox.SelectionColor = level switch
        {
            LogLevel.Success => Color.FromArgb(106, 200, 120),
            LogLevel.Warning => Color.FromArgb(230, 190, 90),
            LogLevel.Error => Color.FromArgb(240, 100, 100),
            _ => TextColor,
        };
        logBox.AppendText(message + Environment.NewLine);
        logBox.ScrollToCaret();
    }

    private void SetBusy(bool busy)
    {
        cancelButton.Enabled = busy;
        openOutputButton.Enabled = !busy && lastOutputDirectory != null;
        dropZone.Busy = busy;
    }

    private void OpenFolder(string? path)
    {
        if (path == null || !Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { AppendLog("Could not open folder: " + ex.Message, LogLevel.Warning); }
    }

    private static Button MakeButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Surface,
            ForeColor = primary ? Color.White : TextColor,
            Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(6, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(70, 70, 80);
        return button;
    }

    /// <summary>The big dashed "drop your game here" area.</summary>
    private sealed class DropZone : Control
    {
        private bool highlighted, busy, hover;
        private string subtitle = "The game .exe, the game folder, or a .zip of it. Click to browse.";

        public DropZone()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
        }

        public bool Highlighted { get => highlighted; set { highlighted = value; Invalidate(); } }
        public bool Busy { get => busy; set { busy = value; Cursor = value ? Cursors.WaitCursor : Cursors.Hand; Invalidate(); } }
        public string Subtitle { get => subtitle; set { subtitle = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Background);

            var rect = new Rectangle(1, 1, Width - 3, Height - 3);
            using var path = RoundedRect(rect, 12);
            using (var fill = new SolidBrush(highlighted || hover ? SurfaceHover : Surface))
                g.FillPath(fill, path);
            using (var pen = new Pen(highlighted ? Accent : Color.FromArgb(80, 80, 92), 2) { DashStyle = DashStyle.Dash })
                g.DrawPath(pen, path);

            var heading = busy ? "Decompiling..." : highlighted ? "Release to decompile" : "Drop a Unity game here";
            using var headingFont = new Font("Segoe UI Semibold", 15f);
            using var subFont = new Font("Segoe UI", 9.5f);
            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            var arrowArea = new Rectangle(rect.X, rect.Y + rect.Height / 2 - 70, rect.Width, 50);
            using (var arrowFont = new Font("Segoe UI Symbol", 24f))
            using (var arrowBrush = new SolidBrush(highlighted ? Accent : MutedText))
                g.DrawString(busy ? "⏳" : "⬇", arrowFont, arrowBrush, arrowArea, center);

            using (var textBrush = new SolidBrush(TextColor))
                g.DrawString(heading, headingFont, textBrush, new Rectangle(rect.X, rect.Y + rect.Height / 2 - 18, rect.Width, 36), center);
            using (var mutedBrush = new SolidBrush(MutedText))
                g.DrawString(subtitle, subFont, mutedBrush, new Rectangle(rect.X + 10, rect.Y + rect.Height / 2 + 20, rect.Width - 20, 40), center);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
