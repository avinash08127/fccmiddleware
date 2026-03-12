using DPPMiddleware.IRepository;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DppMiddleWareService
{
    public class Attendant
    {
        public int FpId { get; set; }
        public int MaxLimit { get; set; }
        public int CurrentCount { get; set; }
        public string? Status { get; set; }
        public bool IsAllowed { get; set; }

        public Attendant(
            int fpId,
            int maxLimit,
            int currentCount,
            string? status,
            bool isAllowed)
        {
            FpId = fpId;
            MaxLimit = maxLimit;
            CurrentCount = currentCount;
            Status = status;
            IsAllowed = isAllowed;
        }
    }
    public class AttendantMonitorWindow : Form
    {
        private NotifyIcon _trayIcon;
        private bool _allowClose = false;
        private TableLayoutPanel _table;
        private Panel _scrollPanel;
        //private Panel _loaderPanel;
        //private Label _loaderLabel;
        private Dictionary<string, CheckBox> _checkMap = new();
        private Dictionary<string, int> _limitMap = new();
        private Dictionary<string, (CheckBox Check, NumericUpDown Input)> _controlMap = new();
        private Dictionary<int, CheckBox> _allowedMap = new();
        // public event Action<List<string>> OnUnblockClicked;
        public event Action<int, bool>? OnIsAllowedChanged;
        public static AttendantMonitorWindow? Popup;
        private Label _errorLabel;
        private readonly Dictionary<int, AttendantRow> _rows = new();

        protected override CreateParams CreateParams
        {
            get
            {
                const int CP_NOCLOSE_BUTTON = 0x200;
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CP_NOCLOSE_BUTTON;
                return cp;
            }
        }
        public AttendantMonitorWindow()
        {
            Text = "PUMA FCC - Unblock Confirmation";
            Size = new Size(1000, 600);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;

            // SCROLL PANEL
            _scrollPanel = new Panel()
            {
                Dock = DockStyle.Top,
                AutoScroll = true,
                Height = 480,
                BorderStyle = BorderStyle.FixedSingle
            };
            _errorLabel = new Label()
            {
                // Set any properties, for example:
                AutoSize = true,
                ForeColor = Color.Red,
                Visible = false  // Initially hidden
            };

            // TABLE
            _table = new TableLayoutPanel()
            {
                Dock = DockStyle.Top,
                ColumnCount = 5,   // ID | Name | Limit | Input | Allow
                AutoSize = true,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
            };
            _scrollPanel.Controls.Add(_errorLabel);

            _scrollPanel.Controls.Add(_table);
            //_loaderPanel = new Panel
            //{
            //    Dock = DockStyle.Fill,
            //    BackColor = Color.FromArgb(180, Color.White), // semi-transparent
            //    Visible = false
            //};

            //_loaderLabel = new Label
            //{
            //    Text = "Loading...",
            //    AutoSize = true,
            //    Font = new Font("Segoe UI", 14, FontStyle.Bold),
            //    ForeColor = Color.Gray
            //};

            //_loaderPanel.Controls.Add(_loaderLabel);
            //Controls.Add(_loaderPanel);

            //// center label
            //_loaderPanel.Resize += (s, e) =>
            //{
            //    _loaderLabel.Left = (_loaderPanel.Width - _loaderLabel.Width) / 2;
            //    _loaderLabel.Top = (_loaderPanel.Height - _loaderLabel.Height) / 2;
            //};
            // UNBLOCK BUTTON
            //var btn = new Button()
            //{
            //    Text = "UNBLOCK SELECTED",
            //    Dock = DockStyle.Bottom,
            //    Height = 50,
            //    BackColor = Color.SteelBlue,
            //    ForeColor = Color.White,
            //    Font = new Font("Segoe UI", 10, FontStyle.Bold)
            //};

            //btn.Click += UnblockClicked;

            Controls.Add(_scrollPanel);
            //Controls.Add(btn);


            _trayIcon = new NotifyIcon()
            {
                Icon = SystemIcons.Application,   // you can change icon later
                Visible = true,
                Text = "PUMA FCC - Unblock Confirmation"
            };
            _trayIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
            };

            _errorLabel = new Label()
            {
                // Set any properties, for example:
                AutoSize = true,
                ForeColor = Color.Red,
                Visible = false  // Initially hidden
            };

            //Controls.Add(_errorLabel);
        }

        public void ShowErrorMessage(string message)
        {
            _errorLabel.Text = message;
            _errorLabel.Visible = true;

            // Auto-hide after 3 seconds
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 3000;
            timer.Tick += (s, e) =>
            {
                _errorLabel.Visible = false;
                timer.Stop();
                timer.Dispose();
            };
            timer.Start();
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                this.Hide();                      // HIDE but keep application running
                _trayIcon.ShowBalloonTip(500,
                    "Running in Background",
                    "Attendant Monitor is still active.",
                    ToolTipIcon.Info);
            }
            base.OnFormClosing(e);
        }
        //public void LoadAttendants(List<Attendant> attendants)
        //{
        //    if (_rows.Count > 0)
        //    {
        //        UpdateAttendants(attendants);
        //        return;
        //    }

        //    // 🔹 Build UI once
        //    _table.Controls.Clear();
        //    _rows.Clear();

        //    int row = 1;

        //    foreach (var att in attendants)
        //    {
        //        AddCell(att.FpId.ToString(), 0, row);

        //        var lblMax = AddCell(att.MaxLimit.ToString(), 1, row);
        //        var lblCur = AddCell(att.CurrentCount.ToString(), 2, row);
        //        var lblStatus = AddCell(att.Status ?? "", 3, row);

        //        var toggle = new CheckBox
        //        {
        //            Checked = att.IsAllowed,
        //            Anchor = AnchorStyles.None,
        //            Tag = att.FpId
        //        };

        //        toggle.CheckedChanged += Toggle_IsAllowed_Changed;
        //        _table.Controls.Add(toggle, 4, row);

        //        _rows[att.FpId] = new AttendantRow
        //        {
        //            MaxLimit = lblMax,
        //            CurrentCount = lblCur,
        //            Status = lblStatus,
        //            IsAllowed = toggle
        //        };

        //        row++;
        //    }
        //}

        public void LoadAttendants(List<Attendant> attendants)
        {
            _table.Controls.Clear();
            _allowedMap.Clear();

            _table.RowCount = attendants.Count + 1;
            _table.ColumnCount = 5;

            _table.ColumnStyles.Clear();
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); // FP Id
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); // MaxLimit
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); // CurrentCount
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); // Status
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); // Toggle

            // Headers
            AddHeader("FP ID", 0);
            AddHeader("Max Limit", 1);
            AddHeader("Current Count", 2);
            AddHeader("Status", 3);
            AddHeader("Allowed", 4);

            _table.RowStyles.Clear();
            for (int i = 0; i <= attendants.Count; i++)
                _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));

            int row = 1;

            foreach (var att in attendants)
            {
                // FP ID
                AddCell(att.FpId.ToString(), 0, row);

                // MaxLimit
                AddCell(att.MaxLimit.ToString(), 1, row);

                // CurrentCount
                AddCell(att.CurrentCount.ToString(), 2, row);

                // Status
                var statusLabel = new Label
                {
                    Text = att.Status ?? "N/A",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = att.Status?.Equals("unavailable", StringComparison.OrdinalIgnoreCase) == true
                                ? Color.Red
                                : Color.DarkGreen
                };
                _table.Controls.Add(statusLabel, 3, row);

                // IsAllowed Toggle
                var toggle = new CheckBox
                {
                    Checked = att.IsAllowed,
                    Anchor = AnchorStyles.None,
                    Enabled = true,
                    Tag = att.FpId // 👈 store FPId
                };

                toggle.CheckedChanged += Toggle_IsAllowed_Changed;


                _table.Controls.Add(toggle, 4, row);
                _allowedMap[att.FpId] = toggle;

                row++;
            }
        }
        private void Toggle_IsAllowed_Changed(object? sender, EventArgs e)
        {
            if (sender is not CheckBox cb) return;

            int fpId = (int)cb.Tag;
            bool isAllowed = cb.Checked;

            OnIsAllowedChanged?.Invoke(fpId, isAllowed);
        }
        //public void ShowLoader()
        //{
        //    _loaderPanel.BringToFront();
        //    _loaderPanel.Visible = true;
        //}

        //public void HideLoader()
        //{
        //    _loaderPanel.Visible = false;
        //}
        private Label AddCell(string text, int col, int row)
        {
            var lbl = new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _table.Controls.Add(lbl, col, row);
            return lbl;
        }
        private void UpdateAttendants(List<Attendant> attendants)
        {
            foreach (var att in attendants)
            {
                if (!_rows.TryGetValue(att.FpId, out var row))
                    continue;

                // MaxLimit
                if (row.MaxLimit.Text != att.MaxLimit.ToString())
                    row.MaxLimit.Text = att.MaxLimit.ToString();

                // CurrentCount
                if (row.CurrentCount.Text != att.CurrentCount.ToString())
                    row.CurrentCount.Text = att.CurrentCount.ToString();

                // Status
                if (row.Status.Text != (att.Status ?? ""))
                {
                    row.Status.Text = att.Status ?? "";
                    row.Status.ForeColor =
                        att.Status?.Equals("unavailable", StringComparison.OrdinalIgnoreCase) == true
                            ? Color.Red
                            : Color.Green;
                }

                // IsAllowed
                if (row.IsAllowed.Checked != att.IsAllowed)
                    row.IsAllowed.Checked = att.IsAllowed;
            }
        }

        //private void AddCell(string text, int column, int row)
        //{
        //    _table.Controls.Add(new Label
        //    {
        //        Text = text,
        //        Dock = DockStyle.Fill,
        //        TextAlign = ContentAlignment.MiddleCenter,
        //        AutoSize = false,
        //        Margin = new Padding(4)
        //    }, column, row);
        //}

        //public void LoadAttendants(List<Attendant> attendants)
        //{
        //    _table.Controls.Clear();
        //    _checkMap.Clear();
        //    _limitMap.Clear();

        //    _table.RowCount = attendants.Count + 1;
        //    _table.ColumnCount = 3;

        //    // _table.ColumnCount = 2;   // must match 5 controls per row

        //    _table.ColumnStyles.Clear();
        //    _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));  // Name
        //    //_table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));   // Max limit
        //    _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));  // Input
        //    //_table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));   // Allow
        //    _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));   // Checkbox

        //    // HEADER
        //    // HEADER (row 0)
        //    AddHeader("FuelPump Id", 0);
        //    //AddHeader("Max Limit", 1);
        //    AddHeader("N+N Allowed", 1);
        //    //AddHeader("Action", 3);
        //    AddHeader("Choose", 2);
        //    _table.RowStyles.Clear();

        //    for (int i = 0; i < attendants.Count + 1; i++)
        //        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        //    int row = 1;

        //    foreach (var att in attendants)
        //    {
        //        // Column 0 → Name
        //        _table.Controls.Add(new Label()
        //        {
        //            Text = $"{att.FpId}",
        //            AutoSize = false,
        //            TextAlign = ContentAlignment.MiddleCenter,
        //            Dock = DockStyle.Fill,
        //            Margin = new Padding(6)
        //        }, 0, row);

        //        // Column 1 → Max Limit
        //        //_table.Controls.Add(new Label()
        //        //{
        //        //    Text = att.MaxLimit.ToString(),
        //        //    AutoSize = false,
        //        //    TextAlign = ContentAlignment.MiddleCenter,
        //        //    Dock = DockStyle.Fill,
        //        //    Margin = new Padding(4)
        //        //}, 1, row);

        //        // Column 2 → Textbox (Input)
        //        var inputBox = new NumericUpDown()
        //        {
        //            Name = $"input_{att.FpId}",
        //            Dock = DockStyle.Fill,
        //            Margin = new Padding(4),
        //            Minimum = 0,
        //            Maximum = int.MaxValue,
        //            Value = 0
        //        };
        //        _table.Controls.Add(inputBox, 1, row);

        //        //// Column 3 → Allow button
        //        //var allowBtn = new Button()
        //        //{
        //        //    Text = "Allow",
        //        //    Tag = att.Id,
        //        //    Dock = DockStyle.Fill,
        //        //    Margin = new Padding(4)
        //        //};
        //        //allowBtn.Click += (s, e) =>
        //        //{
        //        //    var txt = _table.Controls[$"input_{att.Id}"] as TextBox;
        //        //    MessageBox.Show($"Allow {att.Id} - Value: {txt?.Text}");
        //        //};
        //        //_table.Controls.Add(allowBtn, 3, row);

        //        // Column 4 → CheckBox
        //        var cb = new CheckBox()
        //        {
        //            Anchor = AnchorStyles.None,
        //            //Dock = DockStyle.Fill,
        //            Margin = new Padding(6)
        //        };
        //        _table.Controls.Add(cb, 2, row);

        //        // _checkMap[att.Id] = cb;
        //        _controlMap[Convert.ToString(att.FpId)] = (cb, inputBox);

        //        row++;
        //    }
        //}

        private void AddHeader(string title, int column)
        {
            var lbl = new Label()
            {
                Text = title,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                AutoSize = false,
                ForeColor = Color.Black,
                Margin = new Padding(4),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill
            };

            _table.Controls.Add(lbl, column, 0);   // always row 0, given column
        }

        //private void UnblockClicked(object? sender, EventArgs e)
        //{
        //    var selected = new List<UnblockRequest>();

        //    foreach (var kvp in _controlMap)
        //    {
        //        var fpId = kvp.Key;
        //        var check = kvp.Value.Check;
        //        var input = kvp.Value.Input;

        //        if (check.Checked)
        //        {
        //            if (input.Value <= 0)
        //            {
        //                ShowErrorMessage($"Value must be greater than 0 for FP {fpId}");
        //                return; // STOP
        //            }

        //            selected.Add(new UnblockRequest
        //            {
        //                FpId = fpId,
        //                Limit = (int)input.Value
        //            });
        //        }
        //    }

        //    if (!selected.Any())
        //    {
        //        ShowErrorMessage("Please select at least one FP.");
        //        return;
        //    }

        //    OnUnblockClicked?.Invoke(selected);
        //}

        //private void UnblockClicked(object? sender, EventArgs e)
        //{
        //    var selectedIds = _checkMap
        //        .Where(kvp => kvp.Value.Checked)
        //        .Select(kvp => kvp.Key)
        //        .ToList();

        //    OnUnblockClicked?.Invoke(selectedIds);
        //}

        // Worker calls this when someone hits limit
        public void HighlightAttendant(string attendantId)
        {
            if (_controlMap.ContainsKey(attendantId))
            {
                _controlMap[attendantId].Check.Checked = true;
                _controlMap[attendantId].Check.BackColor = Color.LightPink;
            }
        }

        private class AttendantRow
        {
            public Label MaxLimit { get; set; }
            public Label CurrentCount { get; set; }
            public Label Status { get; set; }
            public CheckBox IsAllowed { get; set; }
        }
    }

    public class UnblockRequest
    {
        public string FpId { get; set; } = "";
        public bool IsAllowed { get; set; }
        public int Limit { get; set; }
    }

}
