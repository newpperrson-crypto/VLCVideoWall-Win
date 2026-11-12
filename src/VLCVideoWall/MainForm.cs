using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VLCVideoWall;

public class MainForm : Form
{
    private readonly string _dir;
    private readonly string _confPath;
    private readonly string _settingsPath;

    // VW-08: одна операция со стеной за раз — кнопки, трей и таймеры не должны наслаиваться
    private readonly SemaphoreSlim _opLock = new SemaphoreSlim(1, 1);
    private readonly WallController _wall;
    private readonly BindingList<Camera> _cameras = new();

    private AppSettings _app = new();
    private WallConfig _cfg = new();
    private bool _loading;
    private bool _busy;
    private bool _exiting;
    private bool _forceExit;
    private bool _stopDone;

    // --- верхняя панель статуса
    private Panel _dot;
    private Label _statusText;
    private Label _detail;
    private Button _btnStart, _btnStop, _btnRestart;

    // --- вкладка «Стена»
    private WallPreview _preview;
    private Label _wallInfo;

    // --- звук
    // --- звук: общая поддержка и отдельный процесс VLC на каждую включённую камеру
    private readonly AudioController _audio = new AudioController();
    private readonly WallAudioBadges _wallBadges = new WallAudioBadges();
    private System.Windows.Forms.Timer _audioTimer;
    private CheckBox _chkAudio;
    private NumericUpDown _numAudioVolume;
    private Label _audioHint;

    // --- вкладка «Камеры и сетка»
    private ComboBox _preset;
    private NumericUpDown _cols, _rows;
    private Label _info;
    private ComboBox _canvasPreset;
    private NumericUpDown _canvasW, _canvasH;
    private Label _canvasInfo;
    private DataGridView _grid;
    private Button _btnAdd, _btnDel, _btnUp, _btnDown, _btnProbe;

    // --- вкладка «Журнал»
    private TextBox _logDirBox;
    private NumericUpDown _keepDays;
    private CheckBox _chkLogAuto;
    private TextBox _logView;
    private Label _logPathLabel;

    // --- вкладка «Общие»
    private CheckBox _chkAutoApp, _chkAutoWall, _chkTrayMin, _chkTrayClose, _chkTopMost;
    private CheckBox _chkWindowed;   // стена в окне размером с полотно
    private TabControl _tabs;
    private TextBox _vlcBox;
    private Label _vlcHint;

    // --- трей и таймеры
    private NotifyIcon _tray;
    private ToolStripMenuItem _miStart, _miStop;
    private System.Windows.Forms.Timer _statusTimer, _logTimer;
    private Icon _appIcon;

    private static readonly string[] Presets = { "1x1", "2x1", "1x2", "2x2", "3x2", "2x3", "3x3", "4x3", "4x4", "Другая" };

    private const int MaxTiles = 16;

    private static readonly (string Name, int W, int H)[] CanvasPresets =
    {
        ("HD 1280×720", 1280, 720),
        ("Full HD 1920×1080", 1920, 1080),
        ("2K 2560×1440", 2560, 1440),
        ("4K 3840×2160", 3840, 2160),
        ("SXGA 1280×1024 (5:4)", 1280, 1024),
    };

    public MainForm()
    {
        _dir = AppContext.BaseDirectory.TrimEnd('\\');
        _confPath = Path.Combine(_dir, "Mosaic.conf");
        _settingsPath = Path.Combine(_dir, "VLCVideoWall.settings.json");
        // переезд с прежнего имени: настройки пользователя не теряются при переименовании
        var legacySettings = Path.Combine(_dir, "VideoWall.settings.json");
        if (!File.Exists(_settingsPath) && File.Exists(legacySettings)) _settingsPath = legacySettings;
        _wall = new WallController(_dir);

        Text = "Видеостена — управление";
        Icon = BuildAppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(1020, 740);
        Font = new Font("Segoe UI", 9f);

        BuildUi();
        LoadAll();
        BuildTray();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += (s, e) => UpdateStatus();
        _statusTimer.Start();

        // значки звука поверх плиток: отдельные окна, потому что VLC рисует мозаику полотном
        _wallBadges.Toggled += pos => ToggleAudioByIndex(pos);
        _audio.Changed += RefreshAudioUi;
        _audioTimer = new System.Windows.Forms.Timer { Interval = 700 };
        _audioTimer.Tick += (s, e) => { UpdateWallBadges(); UpdateAudioHint(); };
        _audioTimer.Start();

        _logTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _logTimer.Tick += (s, e) => { if (_chkLogAuto.Checked) LoadLog(false); };

        Shown += OnShown;
    }

    // ============================== интерфейс ==============================

    private void BuildUi()
    {
        // ---------- панель статуса ----------
        var statusPanel = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Color.FromArgb(244, 247, 251) };

        var flLeft = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(14, 16, 0, 0),
            BackColor = Color.Transparent
        };
        _dot = new Panel { Size = new Size(14, 14), BackColor = Color.Gray, Margin = new Padding(2, 4, 10, 0) };
        var lblWall = new Label { Text = "Стена:", AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
        _statusText = new Label { Text = "проверяю…", AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(6, 1, 0, 0) };
        _detail = new Label { Text = "", AutoSize = true, ForeColor = Color.FromArgb(93, 107, 127), Margin = new Padding(18, 4, 0, 0) };
        flLeft.Controls.AddRange(new Control[] { _dot, lblWall, _statusText, _detail });

        var flRight = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 13, 8, 0),
            BackColor = Color.Transparent
        };
        _btnStart = MakeButton("▶  Запустить", 130, true);
        _btnStop = MakeButton("■  Остановить", 130, false);
        _btnRestart = MakeButton("⟳  Перезапустить", 160, false);
        _btnStart.Click += async (s, e) => await StartWall();
        _btnStop.Click += async (s, e) => await StopWall();
        _btnRestart.Click += async (s, e) => await RestartWall();
        flRight.Controls.AddRange(new Control[] { _btnStart, _btnStop, _btnRestart });

        statusPanel.Controls.Add(flLeft);
        statusPanel.Controls.Add(flRight);

        // ---------- вкладки ----------
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
        _tabs = tabs;
        tabs.TabPages.Add(BuildTabWall());
        tabs.TabPages.Add(BuildTabCameras());
        tabs.TabPages.Add(BuildTabLog());
        tabs.TabPages.Add(BuildTabGeneral());
        tabs.TabPages.Add(BuildTabAbout());
        tabs.SelectedIndex = 0;

        // ---------- нижняя панель ----------
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Color.FromArgb(244, 247, 251) };
        var flBottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 13, 8, 0),
            BackColor = Color.Transparent
        };
        var btnClose = MakeButton("Закрыть", 110, false);
        var btnSaveRestart = MakeButton("Сохранить и перезапустить", 220, true);
        var btnSave = MakeButton("Сохранить", 120, false);
        btnClose.Click += (s, e) => Close();
        btnSave.Click += async (s, e) => await SaveAndMaybeRestart(false);
        btnSaveRestart.Click += async (s, e) => await SaveAndMaybeRestart(true);
        flBottom.Controls.AddRange(new Control[] { btnClose, btnSaveRestart, btnSave });

        var lblHintBottom = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Padding = new Padding(14, 20, 0, 0),
            ForeColor = Color.FromArgb(93, 107, 127),
            Text = "Правки — после «Сохранить и перезапустить»."
        };
        bottom.Controls.Add(flBottom);
        bottom.Controls.Add(lblHintBottom);

        Controls.Add(tabs);
        Controls.Add(bottom);
        Controls.Add(statusPanel);
    }

    private TabPage BuildTabWall()
    {
        var page = new TabPage("Стена") { Padding = new Padding(12) };
        _preview = new WallPreview { Dock = DockStyle.Fill };
        _preview.TileClicked += OnTileClicked;
        _wallInfo = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 66,
            ForeColor = Color.FromArgb(93, 107, 127),
            Text = ""
        };
        page.Controls.Add(_preview);
        page.Controls.Add(_wallInfo);
        return page;
    }

    private void UpdatePreview()
    {
        if (_preview == null || _cols == null) return;

        int cols = (int)_cols.Value;
        int rows = (int)_rows.Value;
        int cw = _canvasW != null ? (int)_canvasW.Value : 1920;
        int chh = _canvasH != null ? (int)_canvasH.Value : 1080;
        var names = _cameras.Where(c => c.Enabled).Select(c => c.Name).ToList();

        var cells = new string[cols * rows];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = i < names.Count ? names[i] : null;

        _preview.Cols = cols;
        _preview.Rows = rows;
        _preview.CanvasW = cw;
        _preview.CanvasH = chh;
        _preview.Cells = cells;
        _preview.Invalidate();
        if (_preview != null)
        {
            var namesNow = _cameras.Where(c => c.Enabled).Select(c => c.Name).ToList();
            _preview.AudioSupport = _audio.Enabled;
            var on = new bool[namesNow.Count];
            var track = new AudioTrackState[namesNow.Count];
            var busy = new bool[namesNow.Count];
            for (int i = 0; i < namesNow.Count; i++)
            {
                on[i] = _audio.IsOn(namesNow[i]);
                track[i] = _audio.TrackState(namesNow[i]);
                busy[i] = _audio.IsBusy(namesNow[i]);
            }
            _preview.AudioOn = on;
            _preview.AudioTrack = track;
            _preview.AudioBusy = busy;
        }

        int used = Math.Min(names.Count, cells.Length);
        var text = string.Format("Сетка {0}×{1} · полотно {2}×{3} · занято {4} из {5}",
            cols, rows, cw, chh, used, cols * rows);
        if (names.Count > cells.Length) text += "   ·   часть камер не помещается в сетку";
        if (_cameras.Count == 0) text += "   ·   камеры не добавлены";
        else if (names.Count == 0) text += "   ·   камеры не включены";
        text += Environment.NewLine + "Порядок окон: слева-направо, сверху-вниз.";
        _wallInfo.Text = text;
    }

    private TabPage BuildTabCameras()
    {
        var page = new TabPage("Камеры и сетка") { Padding = new Padding(12) };

        var top = new Panel { Dock = DockStyle.Top, Height = 124 };

        var rowGrid = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        rowGrid.Controls.Add(new Label { Text = "Сетка:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        _preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Margin = new Padding(0, 2, 14, 0) };
        _preset.Items.AddRange(Presets);
        _preset.SelectedIndexChanged += (s, e) => { if (!_loading) OnPresetChanged(); };
        rowGrid.Controls.Add(_preset);
        rowGrid.Controls.Add(new Label { Text = "колонок:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        _cols = new NumericUpDown { Minimum = 1, Maximum = MaxTiles, Width = 52, Margin = new Padding(0, 2, 10, 0) };
        _cols.ValueChanged += (s, e) => { if (!_loading) ClampTiles(true); };
        rowGrid.Controls.Add(_cols);
        rowGrid.Controls.Add(new Label { Text = "строк:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        _rows = new NumericUpDown { Minimum = 1, Maximum = MaxTiles, Width = 52, Margin = new Padding(0, 2, 16, 0) };
        _rows.ValueChanged += (s, e) => { if (!_loading) ClampTiles(false); };
        rowGrid.Controls.Add(_rows);
        _info = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 0), ForeColor = Color.FromArgb(93, 107, 127) };
        rowGrid.Controls.Add(_info);

        var rowCanvas = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        rowCanvas.Controls.Add(new Label { Text = "Полотно:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        _canvasPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, Margin = new Padding(0, 2, 14, 0) };
        _canvasPreset.Items.AddRange(CanvasPresetNames());
        _canvasPreset.SelectedIndexChanged += (s, e) => { if (!_loading) OnCanvasPresetChanged(); };
        rowCanvas.Controls.Add(_canvasPreset);
        _canvasW = new NumericUpDown { Minimum = 640, Maximum = 3840, Increment = 2, Width = 74, Margin = new Padding(0, 2, 6, 0) };
        _canvasW.ValueChanged += (s, e) => { if (!_loading) OnCanvasSizeEdited(); };
        rowCanvas.Controls.Add(_canvasW);
        rowCanvas.Controls.Add(new Label { Text = "×", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        _canvasH = new NumericUpDown { Minimum = 480, Maximum = 2160, Increment = 2, Width = 74, Margin = new Padding(0, 2, 16, 0) };
        _canvasH.ValueChanged += (s, e) => { if (!_loading) OnCanvasSizeEdited(); };
        rowCanvas.Controls.Add(_canvasH);

        // стена в окне: размер окна равен разрешению полотна (по умолчанию выключено)
        _chkWindowed = new CheckBox
        {
            Text = "в окне (размер = полотну)",
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0)
        };
        _chkWindowed.CheckedChanged += (s, e) => { if (!_loading) UpdateCanvasInfo(); };
        rowCanvas.Controls.Add(_chkWindowed);


        // подпись к полотну — отдельной строкой: справа от полей она не влезает
        var rowCanvasInfo = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, WrapContents = false, Padding = new Padding(2, 0, 0, 0) };
        _canvasInfo = new Label { AutoSize = true, Margin = new Padding(0, 4, 0, 0), ForeColor = Color.FromArgb(93, 107, 127) };
        rowCanvasInfo.Controls.Add(_canvasInfo);

        top.Controls.Add(rowCanvasInfo);
        top.Controls.Add(rowCanvas);
        top.Controls.Add(rowGrid);

        var bar = new Panel { Dock = DockStyle.Top, Height = 44 };
        var fl2 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        _btnAdd = MakeButton("+ Камера", 110, false);
        _btnDel = MakeButton("− Удалить", 110, false);
        _btnUp = MakeButton("↑", 44, false);
        _btnDown = MakeButton("↓", 44, false);
        _btnProbe = MakeButton("Проверить связь", 150, false);
        _btnAdd.Click += (s, e) => AddCamera();
        _btnDel.Click += (s, e) => RemoveCamera();
        _btnUp.Click += (s, e) => MoveCamera(-1);
        _btnDown.Click += (s, e) => MoveCamera(1);
        _btnProbe.Click += async (s, e) => await ProbeCameras();
        fl2.Controls.AddRange(new Control[] { _btnAdd, _btnDel, _btnUp, _btnDown, _btnProbe });
        bar.Controls.Add(fl2);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Enabled", HeaderText = "Вкл", Width = 44, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Канал", Width = 82, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Url", HeaderText = "RTSP-адрес", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Check", HeaderText = "Проверка", Width = 230, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.DataSource = _cameras;
        _grid.CellEndEdit += (s, e) => { UpdateInfo(); RefreshAudioUi(); };
        _grid.DataBindingComplete += (s, e) => { UpdateInfo(); RefreshAudioUi(); };

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            ForeColor = Color.FromArgb(93, 107, 127),
            Text = "Порядок строк = порядок окон: слева-направо, сверху-вниз.\r\n" +
                   "Например для 3x3: ch1, ch2, ch3 — первая строка; ch4, ch5, ch6 — вторая."
        };

        page.Controls.Add(_grid);
        page.Controls.Add(hint);
        page.Controls.Add(bar);
        page.Controls.Add(top);
        return page;
    }

    private TabPage BuildTabLog()
    {
        var page = new TabPage("Журнал") { Padding = new Padding(12) };

        var top = new Panel { Dock = DockStyle.Top, Height = 92 };
        var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        row1.Controls.Add(new Label { Text = "Папка журнала:", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
        _logDirBox = new TextBox { Width = 420, Margin = new Padding(0, 3, 6, 0) };
        var btnBrowse = MakeButton("…", 40, false);
        var btnOpen = MakeButton("Открыть папку", 130, false);
        btnBrowse.Click += (s, e) => BrowseLogDir();
        btnOpen.Click += (s, e) => OpenLogFolder();
        row1.Controls.AddRange(new Control[] { _logDirBox, btnBrowse, btnOpen });

        var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        row2.Controls.Add(new Label { Text = "Хранить, дней:", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
        _keepDays = new NumericUpDown { Minimum = 0, Maximum = 3650, Width = 70, Margin = new Padding(0, 3, 14, 0) };
        _chkLogAuto = new CheckBox { Text = "обновлять автоматически", AutoSize = true, Margin = new Padding(0, 5, 14, 0) };
        var btnRefresh = MakeButton("Обновить", 110, false);
        btnRefresh.Click += (s, e) => LoadLog(true);
        _logPathLabel = new Label
        {
            AutoSize = false,
            Width = 340,
            Height = 22,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(93, 107, 127),
            Margin = new Padding(0, 7, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };
        row2.Controls.AddRange(new Control[] { _keepDays, _chkLogAuto, btnRefresh, _logPathLabel });

        top.Controls.Add(row2);
        top.Controls.Add(row1);

        _logView = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(250, 251, 253),
            BorderStyle = BorderStyle.FixedSingle
        };

        page.Controls.Add(_logView);
        page.Controls.Add(top);
        return page;
    }

    private TabPage BuildTabGeneral()
    {
        var page = new TabPage("Общие") { Padding = new Padding(16) };

        var fl = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };

        // --- VLC: где установлен
        var vlcPanel = new Panel { Width = 820, Height = 92, Margin = new Padding(0, 0, 0, 18) };
        var lblVlc = new Label { Text = "Программа VLC:", AutoSize = true, Left = 0, Top = 11 };
        _vlcBox = new TextBox { Left = 124, Top = 8, Width = 500, Anchor = AnchorStyles.Left | AnchorStyles.Top };
        var bBrowse = new Button { Text = "Обзор…", Left = 634, Top = 6, Width = 92, Height = 28, FlatStyle = FlatStyle.System };
        var bFind = new Button { Text = "Найти", Left = 732, Top = 6, Width = 86, Height = 28, FlatStyle = FlatStyle.System };
        bBrowse.Click += (s, e) => BrowseVlc();
        bFind.Click += (s, e) => AutoDetectVlc();
        _vlcHint = new Label { Left = 124, Top = 40, Width = 690, Height = 44, ForeColor = Color.FromArgb(93, 107, 127) };
        _vlcBox.TextChanged += (s, e) => UpdateVlcHint();
        vlcPanel.Controls.AddRange(new Control[] { lblVlc, _vlcBox, bBrowse, bFind, _vlcHint });

        _chkAutoApp = new CheckBox
        {
            Text = "Запускать приложение при входе в Windows",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 2)
        };
        _chkAutoWall = new CheckBox
        {
            Text = "Запускать стену при запуске приложения",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 18)
        };
        _chkTrayMin = new CheckBox
        {
            Text = "Сворачивать в трей (а не на панель задач) и прятать окно после запуска стены",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 2)
        };
        _chkTrayClose = new CheckBox
        {
            Text = "При закрытии окна скрывать в трей, а не выходить",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 2)
        };
        _chkTopMost = new CheckBox
        {
            Text = "Окно поверх всех окон (видно поверх стены)",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 18)
        };

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = Color.FromArgb(93, 107, 127),
            Text = "Полный выход (меню значка в трее → «Выход») сначала останавливает стену, потом закрывает приложение.\r\n" +
                   "Отключать VLC в этот момент вручную не нужно."
        };

        // поддержка звука: общий выключатель, по умолчанию выключен
        var audioRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        _chkAudio = new CheckBox
        {
            Text = "Поддержка звука",
            AutoSize = true,
            Margin = new Padding(0, 4, 14, 0)
        };
        _chkAudio.CheckedChanged += (s, e) => OnAudioSupportChanged();
        audioRow.Controls.Add(_chkAudio);

        audioRow.Controls.Add(new Label { Text = "громкость", AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
        _numAudioVolume = new NumericUpDown
        {
            Minimum = 0, Maximum = 100, Increment = 5, Width = 62,
            Margin = new Padding(0, 4, 4, 0)
        };
        _numAudioVolume.ValueChanged += (s, e) =>
        {
            if (_loading) return;
            _audio.Volume = (int)_numAudioVolume.Value;
            _audio.ApplyVolumeAll();
            SaveAudioState();
        };
        audioRow.Controls.Add(_numAudioVolume);
        audioRow.Controls.Add(new Label { Text = "%", AutoSize = true, Margin = new Padding(2, 8, 0, 0) });

        _audioHint = new Label { AutoSize = true, ForeColor = Color.FromArgb(93, 107, 127), Margin = new Padding(18, 8, 0, 0) };
        audioRow.Controls.Add(_audioHint);

        fl.Controls.AddRange(new Control[] { vlcPanel, _chkAutoApp, _chkAutoWall, _chkTrayMin, _chkTrayClose, _chkTopMost, audioRow, hint });
        page.Controls.Add(fl);
        return page;
    }

    // ============================== VLC ==============================

    private static string DetectVlc()
    {
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VideoLAN\VLC"))
            {
                var dir = k != null ? k.GetValue("InstallDir") as string : null;
                if (!string.IsNullOrEmpty(dir))
                {
                    var p = Path.Combine(dir, "vlc.exe");
                    if (File.Exists(p)) return p;
                }
            }
        }
        catch { }

        foreach (var p in new[] { @"C:\Program Files\VideoLAN\VLC\vlc.exe", @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe" })
            if (File.Exists(p)) return p;

        return null;
    }

    private void BrowseVlc()
    {
        using (var dlg = new OpenFileDialog())
        {
            dlg.Title = "Укажите vlc.exe";
            dlg.Filter = "VLC (vlc.exe)|vlc.exe|Программы (*.exe)|*.exe";
            var cur = _vlcBox.Text.Trim();
            if (File.Exists(cur)) dlg.FileName = cur;
            if (dlg.ShowDialog(this) == DialogResult.OK) _vlcBox.Text = dlg.FileName;
        }
    }

    private void AutoDetectVlc()
    {
        var p = DetectVlc();
        if (p == null)
        {
            Ask( "Установленный VLC не найден.\r\nУкажите путь к vlc.exe вручную кнопкой «Обзор…».",
                "Видеостена", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _vlcBox.Text = p;
    }

    private void UpdateVlcHint()
    {
        if (_vlcHint == null) return;
        var p = (_vlcBox.Text ?? "").Trim();
        if (p.Length == 0)
        {
            _vlcHint.Text = "Пусто — сторож найдёт VLC сам (реестр, Program Files).";
            _vlcHint.ForeColor = Color.FromArgb(93, 107, 127);
        }
        else if (File.Exists(p))
        {
            _vlcHint.Text = "Файл найден.";
            _vlcHint.ForeColor = Color.FromArgb(21, 128, 61);
        }
        else
        {
            _vlcHint.Text = "По этому пути файла нет — стена не запустится.";
            _vlcHint.ForeColor = Color.FromArgb(180, 83, 9);
        }
    }

    private string EffectiveVlcPath()
    {
        if (_vlcBox == null) return "";
        var p = (_vlcBox.Text ?? "").Trim();
        if (p.Length > 0 && File.Exists(p)) return p;
        return DetectVlc() ?? "";
    }

    private static Button MakeButton(string text, int width, bool accent)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 32,
            Margin = new Padding(0, 0, 8, 0),
            FlatStyle = FlatStyle.System,
            UseVisualStyleBackColor = true
        };
        if (accent) b.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        return b;
    }

    // ============================== данные ==============================

    private void LoadAll()
    {
        _loading = true;
        try
        {
            _cfg = WallConfig.Load(_confPath);
            _app = AppSettings.Load(_settingsPath);

            _cameras.Clear();
            foreach (var c in _cfg.Cameras) _cameras.Add(c);

            _preset.SelectedItem = Presets.Contains(_cfg.Layout) ? _cfg.Layout : "Другая";
            _cols.Value = Math.Clamp(_cfg.Cols, 1, MaxTiles);
            _rows.Value = Math.Clamp(_cfg.Rows, 1, MaxTiles);
            _preset.SelectedItem = Presets.Contains(_cfg.Layout) ? _cfg.Layout : "Другая";

            var stdCanvas = CanvasPresets.FirstOrDefault(p => p.W == _cfg.CanvasW && p.H == _cfg.CanvasH);
            _canvasPreset.SelectedItem = string.IsNullOrEmpty(stdCanvas.Name) ? "Другое…" : stdCanvas.Name;
            _canvasW.Value = Math.Clamp(_cfg.CanvasW, (int)_canvasW.Minimum, (int)_canvasW.Maximum);
            _chkWindowed.Checked = _cfg.Windowed;
            _canvasH.Value = Math.Clamp(_cfg.CanvasH, (int)_canvasH.Minimum, (int)_canvasH.Maximum);
            _canvasW.Enabled = _canvasH.Enabled = _canvasPreset.SelectedItem.ToString().StartsWith("Другое");
            UpdateCanvasInfo();

            _logDirBox.Text = string.IsNullOrWhiteSpace(_cfg.LogDir) ? _dir : _cfg.LogDir;
            _keepDays.Value = Math.Clamp(_cfg.LogKeepDays, 0, 3650);

            _vlcBox.Text = string.IsNullOrWhiteSpace(_cfg.VlcPath) ? (DetectVlc() ?? "") : _cfg.VlcPath;
            UpdateVlcHint();

            _chkAutoApp.Checked = _app.AutostartApp;
            _chkAutoWall.Checked = _app.AutostartWall;
            _chkTrayMin.Checked = _app.MinimizeToTray;
            _chkTrayClose.Checked = _app.CloseToTray;
            _chkTopMost.Checked = _app.TopMost;
            TopMost = _app.TopMost;

            _chkAudio.Checked = _app.AudioEnabled;
            if (_numAudioVolume != null) _numAudioVolume.Value = Math.Clamp(_app.AudioVolume, 0, 100);
            _audio.Enabled = _app.AudioEnabled;
            _audio.Volume = Math.Clamp(_app.AudioVolume, 0, 100);
            _audio.SetCameras(_cameras);
            _audio.RestoreStates(_app.AudioChannels);
            _chkLogAuto.Checked = _app.LogAutoRefresh;

            if (_app.Width > 400 && _app.Height > 400) Size = new Size(_app.Width, _app.Height);
            if (_app.Left > -10000 && _app.Top > -10000 && _app.Left != -1) { StartPosition = FormStartPosition.Manual; Location = new Point(_app.Left, _app.Top); }
        }
        finally { _loading = false; }

        OnPresetChanged();
        UpdateInfo();
        LoadLog(false);
        _wall.VlcPath = EffectiveVlcPath();
        UpdateStatus();
    }

    private void OnPresetChanged()
    {
        if (_preset.SelectedItem == null) return;
        var s = _preset.SelectedItem.ToString();
        bool custom = s == "Другая";
        _cols.Enabled = custom;
        _rows.Enabled = custom;

        if (!custom)
        {
            var p = s.Split('x');
            int c, r;
            if (int.TryParse(p[0], out c) && int.TryParse(p[1], out r))
            {
                _loading = true;
                _cols.Value = Math.Clamp(c, 1, MaxTiles);
                _rows.Value = Math.Clamp(r, 1, MaxTiles);
                _loading = false;
            }
        }
        UpdateInfo();
    }

    private void ClampTiles(bool colsChanged)
    {
        int cols = (int)_cols.Value;
        int rows = (int)_rows.Value;
        if (cols * rows > MaxTiles)
        {
            _loading = true;
            if (colsChanged) _rows.Value = Math.Max(1, MaxTiles / cols);
            else _cols.Value = Math.Max(1, MaxTiles / rows);
            _loading = false;
        }
        _preset.SelectedItem = "Другая";
        UpdateCanvasInfo();
        UpdateInfo();
    }

    // ---------- полотно ----------

    private static string[] CanvasPresetNames()
    {
        var list = CanvasPresets.Select(p => p.Name).ToList();
        list.Add("Другое…");
        return list.ToArray();
    }

    private static bool IsStandardCanvas(int w, int h)
    {
        return CanvasPresets.Any(p => p.W == w && p.H == h);
    }

    private static string BackgroundFileName(int w, int h)
    {
        return IsStandardCanvas(w, h) ? ("bg_" + w + "x" + h + ".jpg") : "bg_custom.jpg";
    }

    private void OnCanvasPresetChanged()
    {
        var s = _canvasPreset.SelectedItem != null ? _canvasPreset.SelectedItem.ToString() : "";
        bool custom = s.StartsWith("Другое");
        _canvasW.Enabled = custom;
        _canvasH.Enabled = custom;

        if (!custom)
        {
            var p = CanvasPresets.FirstOrDefault(x => x.Name == s);
            if (p.Name != null)
            {
                _loading = true;
                _canvasW.Value = Math.Clamp(p.W, (int)_canvasW.Minimum, (int)_canvasW.Maximum);
                _canvasH.Value = Math.Clamp(p.H, (int)_canvasH.Minimum, (int)_canvasH.Maximum);
                _loading = false;
            }
        }
        UpdateCanvasInfo();
        UpdatePreview();
    }

    private void OnCanvasSizeEdited()
    {
        _canvasPreset.SelectedItem = "Другое…";
        UpdateCanvasInfo();
        UpdatePreview();
    }

    private void UpdateCanvasInfo()
    {
        if (_canvasInfo == null || _canvasW == null) return;
        bool windowed = _chkWindowed != null && _chkWindowed.Checked;
        int w = (int)_canvasW.Value;
        int h = (int)_canvasH.Value;
        int cols = Math.Max(1, (int)_cols.Value);
        int rows = Math.Max(1, (int)_rows.Value);
        _canvasInfo.Text = string.Format("{0} · полотно {1}×{2}, фон {3} · плитка ≈ {4}×{5} · {6}",
            IsStandardCanvas(w, h) ? "стандартное" : "своё", w, h,
            BackgroundFileName(w, h), w / cols, h / rows,
            windowed ? "в окне " + w + "×" + h : "на весь экран");
    }

    /// <summary>Клик по плитке: включить или выключить звук этой камеры.</summary>
    private void OnTileClicked(int index)
    {
        ToggleAudioByIndex(index);
    }

    /// <summary>Переключить звук плитки. Недоступно, если поддержка звука выключена.</summary>
    private void ToggleAudioByIndex(int index)
    {
        if (!_audio.Enabled) return;
        var names = _cameras.Where(c => c.Enabled).Select(c => c.Name).ToList();
        if (index < 0 || index >= names.Count) return;

        if (!_audio.Toggle(names[index])) { UpdateAudioHint(); return; }

        SaveAudioState();
        RefreshAudioUi();          // сразу показываем «принято» — значок становится Busy
    }

    /// <summary>Поддержка звука переключена в «Общих настройках».</summary>
    private void OnAudioSupportChanged()
    {
        if (_loading) return;
        _audio.Enabled = _chkAudio.Checked;
        _audio.SetCameras(_cameras);

        if (!_audio.Enabled) _audio.StopAll();
        else
        {
            if (_wall.IsRunning) _audio.StartEnabled();
            _audio.ProbeTracks();
        }

        SaveAudioState();
        RefreshAudioUi();
        UpdatePreview();
    }

    private void SaveAudioState()
    {
        _app.AudioEnabled = _audio.Enabled;
        _app.AudioVolume = _audio.Volume;
        _app.AudioChannels = _audio.EnabledChannels();
        try { _app.Save(_settingsPath); } catch { }
    }

    /// <summary>Обновить всё, что показывает звук: макет, значки на стене, подпись.</summary>
    private void RefreshAudioUi()
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(RefreshAudioUi)); } catch { } return; }
        UpdatePreview();
        UpdateWallBadges();
        UpdateAudioHint();
    }

    private void UpdateAudioHint()
    {
        if (_audioHint == null) return;
        if (!_audio.Enabled) { _audioHint.Text = "значки звука скрыты"; return; }

        int on = _audio.EnabledChannels().Count;
        int silent = _cameras.Count(c => c.Enabled && _audio.TrackState(c.Name) == AudioTrackState.Absent);
        var text = on == 0 ? "звук выключен у всех окон" : "звук включён: " + on + " из " + _cameras.Count(c => c.Enabled);
        if (silent > 0) text += " · без звуковой дорожки: " + silent;
        _audioHint.Text = text;
    }

    /// <summary>Значки звука над плитками живой стены.</summary>
    private void UpdateWallBadges()
    {
        var empty = new Dictionary<int, Rectangle>();
        if (!_audio.Enabled || !_wall.IsRunning)
        {
            _wallBadges.Sync(IntPtr.Zero, empty, p => AudioBadgeState.Off, p => "");
            return;
        }

        IntPtr hwnd;
        Rectangle client;
        if (!WallWindow.TryGetVideoWindow(_wall.OwnVlcPid(), out hwnd, out client))
        {
            _wallBadges.Sync(IntPtr.Zero, empty, p => AudioBadgeState.Off, p => "");
            return;
        }

        var names = _cameras.Where(c => c.Enabled).Select(c => c.Name).ToList();
        int cols = Math.Max(1, _cfg.Cols);
        int rows = Math.Max(1, _cfg.Rows);
        int canvasW = Math.Max(1, _cfg.CanvasW);
        int canvasH = Math.Max(1, _cfg.CanvasH);

        // полотно вписано в клиентскую область с сохранением пропорций
        double scale = Math.Min((double)client.Width / canvasW, (double)client.Height / canvasH);
        int drawW = Math.Max(1, (int)(canvasW * scale));
        int drawH = Math.Max(1, (int)(canvasH * scale));
        int left = (client.Width - drawW) / 2;
        int top = (client.Height - drawH) / 2;
        int tileW = drawW / cols;
        int tileH = drawH / rows;

        // значки — отдельные окна, поэтому координаты экранные
        var tiles = new Dictionary<int, Rectangle>();
        int count = Math.Min(names.Count, cols * rows);
        for (int i = 0; i < count; i++)
        {
            int c = i % cols, r = i / cols;
            tiles[i] = new Rectangle(client.X + left + c * tileW, client.Y + top + r * tileH, tileW, tileH);
        }

        _wallBadges.Sync(hwnd, tiles,
            pos => AudioStateFor(pos, names),
            pos => pos < names.Count ? names[pos] : "",
            WallWindow.IsActive(hwnd));
    }

    private AudioBadgeState AudioStateFor(int pos, List<string> names)
    {
        if (pos < 0 || pos >= names.Count) return AudioBadgeState.Off;
        var name = names[pos];
        if (_audio.IsBusy(name)) return AudioBadgeState.Busy;
        if (_audio.TrackState(name) == AudioTrackState.Absent) return AudioBadgeState.NoTrack;
        return _audio.IsOn(name) ? AudioBadgeState.On : AudioBadgeState.Off;
    }

    private void LogUi(string message)
    {
        try { _detail.Text = message; } catch { }
    }

    private void UpdateInfo()
    {
        int w = (int)_cols.Value * (int)_rows.Value;
        int cams = _cameras.Count(c => c.Enabled);
        var text = "окон: " + w + " · камер включено: " + cams;
        if (cams > w) text += "  ⚠ камер больше, чем окон — лишние не покажутся";
        if (cams == 0) text += "  ⚠ нет ни одной включённой камеры";
        _info.Text = text;
        _info.ForeColor = (cams > w || cams == 0) ? Color.FromArgb(180, 83, 9) : Color.FromArgb(93, 107, 127);
        UpdatePreview();
    }

    private bool _statusProbe;      // проба состояния уже идёт
    private bool _statusPending;    // пока проба шла, состояние изменили — повторим позже
    private int _statusGen;                                  // поколение снимка: старые результаты не применяем
    private WallState _lastState = WallState.Stopped;         // последний применённый снимок
    private bool _exitRunning;                                // процедура выхода уже идёт
    private readonly CancellationTokenSource _closing = new CancellationTokenSource();

    private sealed class StatusSnapshot
    {
        public WallState State;
        public int Playing;
    }

    /// <summary>
    /// Обновление статуса без блокировки интерфейса: TCP-пробы (до ~700 мс на вызов)
    /// выполняются в фоне, окно не подвисает и кнопки остаются отзывчивыми.
    /// </summary>
    private void UpdateStatus()
    {
        if (_busy || IsDisposed || Disposing || _closing.IsCancellationRequested) return;
        if (_statusProbe) { _statusPending = true; return; }

        int gen = _statusGen;
        _statusProbe = true;
        Task.Run(() =>
        {
            var st = _wall.State;     // читает файлы и делает TCP-пробы — вне UI-потока
            return new StatusSnapshot
            {
                State = st,
                Playing = st == WallState.Running ? _wall.PlayingChannels() : 0
            };
        }).ContinueWith(t =>
        {
            _statusProbe = false;
            if (IsDisposed || Disposing || _closing.IsCancellationRequested) return;
            if (t.IsCompletedSuccessfully && gen == _statusGen) ApplyStatus(t.Result);
            if (_statusPending) { _statusPending = false; UpdateStatus(); }
        }, _closing.Token, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ApplyStatus(StatusSnapshot s)
    {
        if (IsDisposed || _busy) return;

        var state = s.State;
        _lastState = state;
        bool working = state == WallState.Running;
        bool canStart = state == WallState.Stopped;   // FUP2-03: запускать можно только «с нуля»
        bool canStop = state != WallState.Stopped;    // ...а останавливать — если есть что

        string text;
        Color color;
        switch (state)
        {
            case WallState.Running:
                text = "работает";
                color = Color.FromArgb(21, 128, 61);
                break;
            case WallState.Starting:
                text = "запускается…";
                color = Color.FromArgb(234, 179, 8);
                break;
            case WallState.GuardOnly:
                text = "сторож работает, VLC не отвечает";
                color = Color.FromArgb(185, 28, 28);
                break;
            case WallState.VlcWithoutGuard:
                text = "VLC без сторожа — нажмите «Остановить»";
                color = Color.FromArgb(185, 28, 28);
                break;
            default:
                text = "остановлена";
                color = Color.FromArgb(93, 107, 127);
                break;
        }

        _dot.BackColor = color;
        _statusText.Text = text;
        _statusText.ForeColor = color;

        int cams = _cameras.Count(c => c.Enabled);
        _detail.Text = "каналов: " + cams + " · сетка " + _cols.Value + "×" + _rows.Value;
        if (working && s.Playing > 0) _detail.Text += " · играют " + s.Playing;

        _btnStart.Enabled = canStart;
        _btnStop.Enabled = canStop;
        _btnRestart.Enabled = true;          // перезапуск = остановить (если есть что) + запустить

        if (_tray != null)
        {
            _tray.Text = "Видеостена — " + (working ? "работает" : canStop ? "не отвечает" : "остановлена");
            if (_miStart != null) _miStart.Enabled = canStart;
            if (_miStop != null) _miStop.Enabled = canStop;
        }
    }

    // ============================== камеры ==============================

    private void AddCamera()
    {
        var cam = new Camera { Name = "ch" + (_cameras.Count + 1), Url = "" };
        _cameras.Add(cam);
        if (_grid.Rows.Count > 0)
        {
            var row = _grid.Rows[_grid.Rows.Count - 1];
            _grid.CurrentCell = row.Cells[2];
            _grid.BeginEdit(true);
        }
        UpdateInfo();
    }

    private void RemoveCamera()
    {
        var idx = SelectedIndex();
        if (idx < 0) return;
        _cameras.RemoveAt(idx);
        UpdateInfo();
    }

    private void MoveCamera(int delta)
    {
        var idx = SelectedIndex();
        if (idx < 0) return;
        var target = idx + delta;
        if (target < 0 || target >= _cameras.Count) return;
        var item = _cameras[idx];
        _cameras.RemoveAt(idx);
        _cameras.Insert(target, item);
        _grid.ClearSelection();
        if (target < _grid.Rows.Count) _grid.Rows[target].Selected = true;
        UpdateInfo();
    }

    private int SelectedIndex()
    {
        if (_grid.CurrentRow != null && _grid.CurrentRow.Index >= 0) return _grid.CurrentRow.Index;
        return _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Index : -1;
    }

    private async Task ProbeCameras()
    {
        var rows = Enumerable.Range(0, _cameras.Count).ToList();

        foreach (var i in rows) _cameras[i].Check = "проверяю…";
        _cameras.ResetBindings();

        foreach (var i in rows)
        {
            if (i >= _cameras.Count) continue;
            var url = _cameras[i].Url;
            var result = await Task.Run(() => RtspProbe.Check(url));
            if (i < _cameras.Count) _cameras[i].Check = result;
            _cameras.ResetBindings();
        }
    }

    // ============================== о программе ==============================

    private TabPage BuildTabAbout()
    {
        var page = new TabPage("О программе") { Padding = new Padding(24) };

        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

        var title = new Label
        {
            Text = "VLCVideoWall",
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0)
        };
        var subtitle = new Label
        {
            Text = "Видеостена для Windows",
            Font = new Font("Segoe UI", 11f),
            ForeColor = Color.FromArgb(93, 107, 127),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 16)
        };
        var descr = new Label
        {
            Text = "Собирает RTSP-камеры в одну полноэкранную мозаику на плеере VLC и сама " +
                   "переподключает пропавшие каналы: следит за состоянием потоков через " +
                   "HTTP-интерфейс VLC, проверяет камеру перед повтором и перезапускает только " +
                   "неисправную плитку. До 16 окон, сетки 1x1 … 4x4, полотно HD … 4K, " +
                   "управление из трея, журнал с ротацией.",
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            Margin = new Padding(0, 0, 0, 16)
        };
        var meta = new Label
        {
            Text = "Версия " + ver + Environment.NewLine +
                   "Год " + DateTime.Now.Year + Environment.NewLine +
                   "Лицензия MIT",
            AutoSize = true,
            ForeColor = Color.FromArgb(93, 107, 127),
            Margin = new Padding(0, 0, 0, 16)
        };
        const string repoUrl = "https://github.com/newpperrson-crypto/VLCVideoWall-Win";
        var repo = new LinkLabel
        {
            Text = repoUrl,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        repo.LinkClicked += (s, e) => OpenUrl(repoUrl);

        var copy = new Button
        {
            Text = "Скопировать ссылку",
            AutoSize = true,
            FlatStyle = FlatStyle.System,
            Margin = new Padding(0, 0, 0, 0)
        };
        copy.Click += (s, e) =>
        {
            try
            {
                Clipboard.SetText(repoUrl);
                copy.Text = "Ссылка скопирована";
            }
            catch
            {
                copy.Text = "Не удалось скопировать";
            }
        };

        var fl = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        fl.Controls.AddRange(new Control[] { title, subtitle, descr, meta, repo, copy });
        page.Controls.Add(fl);
        return page;
    }

    // ============================== стена ==============================

    /// <summary>
    /// FUP3-03: только элементы интерфейса. Никаких проверок процессов, файлов и портов —
    /// иначе поток UI подвисает на каждом обновлении статуса.
    /// Кнопки после снятия флага приводит в порядок ближайший снимок состояния.
    /// </summary>
    private void SetBusy(bool busy, string text = null)
    {
        _busy = busy;
        _btnStart.Enabled = !busy;
        _btnStop.Enabled = !busy;
        _btnRestart.Enabled = !busy;
        if (busy)
        {
            _statusText.Text = string.IsNullOrEmpty(text) ? "запускаю…" : text;
            _statusText.ForeColor = Color.FromArgb(93, 107, 127);
            _dot.BackColor = Color.FromArgb(234, 179, 8);
        }
        else
        {
            _statusGen++;   // снимки, начатые до операции, уже не применяем
        }
    }

    private async Task StartWall()
    {
        if (_exiting) return;                  // закрытие уже началось — не запускаем заново
        if (!_opLock.Wait(0)) { WarnBusy(); return; }   // уже идёт запуск или остановка
        try { await StartWallCore(); }
        finally { _opLock.Release(); }
    }

    private async Task StartWallCore()
    {
        if (!_cameras.Any(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Url)))
        {
            Ask(
                "Не задано ни одного адреса RTSP.\r\n" +
                "Добавьте камеры на вкладке «Камеры и сетка» и нажмите «Сохранить».",
                "Видеостена", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            SetBusy(true);
            await _wall.StartAsync();

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 6000 && !_wall.IsGuardAlive()) await Task.Delay(200);

            SetBusy(false);
            UpdateStatus();

            if (!_wall.IsGuardAlive())
                Ask( "Сторож стены не запустился.\r\nОткройте вкладку «Журнал» — там будет причина.",
                    "Видеостена", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (_chkTrayMin.Checked)
                HideToTray();   // стена на весь экран — окно убираем в трей

            // звук: поднимаем процессы включённых камер и уточняем дорожки
            if (_audio.Enabled)
            {
                _audio.SetCameras(_cameras);
                _audio.StartEnabled();
                _audio.ProbeTracks();
            }
            RefreshAudioUi();
        }
        catch (Exception ex)
        {
            SetBusy(false);
            Ask( "Не удалось запустить стену:\r\n" + ex.Message, "Видеостена",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Остановка с захватом блокировки (кнопка, меню трея).</summary>
    private async Task<bool> StopWall()
    {
        if (!_opLock.Wait(0)) { WarnBusy(); return false; }
        try { return await StopWallCore(); }
        finally { _opLock.Release(); }
    }

    /// <summary>FUP3-02: остановка без захвата блокировки — вызывающий уже держит её.</summary>
    private async Task<bool> StopWallCore()
    {
        bool ok = false;
        try
        {
            SetBusy(true, "останавливаю…");
            var progress = StartStopProgress();
            StopResult result;
            try { result = await _wall.StopAsync(); }
            finally { progress.Dispose(); }

            ok = result.Stopped;
            if (!ok)
                Ask(
                    "Стена не остановилась полностью." + Environment.NewLine + result.Note +
                    Environment.NewLine + Environment.NewLine + "Проверьте журнал.",
                    "Видеостена", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            Ask("Ошибка остановки:" + Environment.NewLine + ex.Message, "Видеостена",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _audio.StopAll();
            SetBusy(false);
            UpdateStatus();
            UpdateWallBadges();
            if (_chkLogAuto.Checked) LoadLog(false);
        }
        return ok;
    }

    /// <summary>FUP3-03: таймер прогресса меняет только текст статуса.</summary>
    private System.Windows.Forms.Timer StartStopProgress()
    {
        var sw = Stopwatch.StartNew();
        var tick = new System.Windows.Forms.Timer { Interval = 500 };
        tick.Tick += (s, e) =>
        {
            if (IsDisposed || Disposing) return;
            _statusText.Text = "останавливаю… " + (int)sw.Elapsed.TotalSeconds + " с";
        };
        tick.Start();
        return tick;
    }

    private async Task RestartWall()
    {
        if (_exiting) return;
        // FUP3-02: остановка и запуск идут под одной блокировкой — между ними
        // не может вклиниться другая операция или выход
        if (!_opLock.Wait(0)) { WarnBusy(); return; }
        try
        {
            // VW-08: не запускаем новую стену, пока не подтверждено, что старая остановилась
            bool stopped = await StopWallCore();
            if (!stopped)
            {
                Ask(
                    "Стена не остановилась — перезапуск отменён." + Environment.NewLine +
                    "Иначе поверх старой копии поднялся бы второй сторож. Подробности в журнале.",
                    "Видеостена", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            await Task.Delay(500);
            await StartWallCore();
        }
        finally { _opLock.Release(); }
    }

    // ============================== сохранение ==============================

    private async Task SaveAndMaybeRestart(bool restart)
    {
        int w = (int)_cols.Value * (int)_rows.Value;
        int cams = _cameras.Count(c => c.Enabled);

        if (cams == 0)
        {
            if (Ask( "Ни одна камера не включена — стена будет пустой. Сохранить?",
                    "Видеостена", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }
        else if (cams > w && Ask(
                     "Включённых камер (" + cams + ") больше, чем окон в сетке (" + w + ").\r\nЛишние камеры не будут показаны. Продолжить?",
                     "Видеостена", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        _cfg.Layout = _cols.Value + "x" + _rows.Value;
        _cfg.Windowed = _chkWindowed.Checked;
        _cfg.CanvasW = (int)_canvasW.Value;
        _cfg.CanvasH = (int)_canvasH.Value;
        _cfg.LogKeepDays = (int)_keepDays.Value;
        _cfg.VlcPath = (_vlcBox.Text ?? "").Trim();
        _wall.VlcPath = EffectiveVlcPath();
        var logDir = _logDirBox.Text.Trim();
        _cfg.LogDir = (logDir.Length == 0 || string.Equals(logDir, _dir, StringComparison.OrdinalIgnoreCase)) ? "" : logDir;
        _cfg.Cameras = _cameras.ToList();

        try
        {
            _cfg.Save(_confPath);
        }
        catch (Exception ex)
        {
            Ask( "Не удалось записать Mosaic.conf:\r\n" + ex.Message, "Видеостена",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _app.AutostartApp = _chkAutoApp.Checked;
        _app.AutostartWall = _chkAutoWall.Checked;
        _app.AudioEnabled = _audio.Enabled;
        _app.AudioVolume = _audio.Volume;
        _app.AudioChannels = _audio.EnabledChannels();
        _app.MinimizeToTray = _chkTrayMin.Checked;
        _app.CloseToTray = _chkTrayClose.Checked;
        _app.TopMost = _chkTopMost.Checked;
        TopMost = _app.TopMost;
        _app.LogAutoRefresh = _chkLogAuto.Checked;
        _app.Save(_settingsPath);

        ApplyAutostart();
        CleanupOldLogs();

        if (restart)
        {
            await RestartWall();
        }
        else
        {
            Ask( "Сохранено.\r\nЧтобы изменения применились, нажмите «Перезапустить» (или «Сохранить и перезапустить»).",
                "Видеостена", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        LoadLog(false);
    }

    private void ApplyAutostart()
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string name = "VLCVideoWall";
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(runKey, true) ?? Registry.CurrentUser.CreateSubKey(runKey))
            {
                if (key == null) return;
                if (_chkAutoApp.Checked)
                    key.SetValue(name, "\"" + Application.ExecutablePath + "\"");
                else if (key.GetValue(name) != null)
                    key.DeleteValue(name, false);

                // подчищаем запись от прежнего имени программы, иначе автозапуск сработает дважды
                if (key.GetValue("VideoWall") != null) key.DeleteValue("VideoWall", false);
            }
        }
        catch (Exception ex)
        {
            Ask( "Не удалось изменить автозапуск в реестре:\r\n" + ex.Message, "Видеостена",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ============================== журнал ==============================

    private string EffectiveLogDir
    {
        get
        {
            var t = _logDirBox.Text.Trim();
            return string.IsNullOrEmpty(t) ? _dir : t;
        }
    }

    private string LogFile { get { return Path.Combine(EffectiveLogDir, "mosaic_watchdog.log"); } }

    private void BrowseLogDir()
    {
        using (var dlg = new FolderBrowserDialog())
        {
            dlg.Description = "Папка для журнала";
            dlg.SelectedPath = Directory.Exists(EffectiveLogDir) ? EffectiveLogDir : _dir;
            if (dlg.ShowDialog(this) == DialogResult.OK) { _logDirBox.Text = dlg.SelectedPath; LoadLog(true); }
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            var dir = EffectiveLogDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }
        catch (Exception ex)
        {
            Ask( "Не удалось открыть папку:\r\n" + ex.Message, "Видеостена",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadLog(bool verbose)
    {
        _logPathLabel.Text = LogFile;
        try
        {
            if (!File.Exists(LogFile))
            {
                _logView.Text = "Журнала пока нет: " + LogFile;
                return;
            }
            var lines = File.ReadLines(LogFile, Encoding.UTF8).TakeLast(600).ToList();
            bool atBottom = _logView.SelectionStart >= _logView.TextLength - 1 || verbose;
            _logView.Text = string.Join("\r\n", lines);
            if (atBottom) { _logView.SelectionStart = _logView.TextLength; _logView.ScrollToCaret(); }
        }
        catch (Exception ex)
        {
            _logView.Text = "Не удалось прочитать журнал: " + ex.Message;
        }
    }

    private void CleanupOldLogs()
    {
        int days = (int)_keepDays.Value;
        if (days <= 0) return;
        try
        {
            var dir = EffectiveLogDir;
            if (!Directory.Exists(dir)) return;
            var limit = DateTime.Now.AddDays(-days);
            foreach (var f in Directory.GetFiles(dir, "mosaic_watchdog.log*"))
            {
                try { if (File.GetLastWriteTime(f) < limit) File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    // ============================== трей ==============================

    private void BuildTray()
    {
        _appIcon = BuildAppIcon(SystemInformation.SmallIconSize);
        var menu = new ContextMenuStrip();
        var miOpen = new ToolStripMenuItem("Открыть окно", null, (s, e) => ShowFromTray()) { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _miStart = new ToolStripMenuItem("Запустить стену", null, async (s, e) => await StartWall());
        _miStop = new ToolStripMenuItem("Остановить стену", null, async (s, e) => await StopWall());
        var miLog = new ToolStripMenuItem("Открыть журнал", null, (s, e) => ShowLogTab());
        var miExit = new ToolStripMenuItem("Выход", null, (s, e) => ExitApp());
        menu.Items.Add(miOpen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miStart);
        menu.Items.Add(_miStop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miLog);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);

        _tray = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "Видеостена",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (s, e) => ShowFromTray();

        // VW: при полноэкранной стене панель задач может оказаться поверх меню —
        // поднимаем меню в TOPMOST и при необходимости сдвигаем в рабочую область
        menu.Opened += (s, e) => RaiseMenuAboveTaskbar(menu);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HwndTopMost = new IntPtr(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    private void RaiseMenuAboveTaskbar(ToolStripDropDown menu)
    {
        try
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            var b = menu.Bounds;
            int x = b.Left;
            int y = b.Top;

            if (y + b.Height > wa.Bottom) y = Math.Max(wa.Top, wa.Bottom - b.Height);
            if (y < wa.Top) y = wa.Top;
            if (x + b.Width > wa.Right) x = Math.Max(wa.Left, wa.Right - b.Width);
            if (x < wa.Left) x = wa.Left;

            SetWindowPos(menu.Handle, HwndTopMost, x, y, 0, 0, SwpNoSize | SwpNoActivate);
        }
        catch { }
    }

    private static Icon BuildAppIcon(Size? size = null)
    {
        try
        {
            var asm = typeof(MainForm).Assembly;
            using (var s = asm.GetManifestResourceStream("VLCVideoWall.app.ico"))
            {
                if (s == null) return SystemIcons.Application;
                return size.HasValue ? new Icon(s, size.Value) : new Icon(s);
            }
        }
        catch { return SystemIcons.Application; }
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch
        {
            try { Process.Start("explorer.exe", url); } catch { }
        }
    }

    /// <summary>Показать окно из трея (в т.ч. по повторному запуску exe).</summary>
    public void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        if (TopMost) TopMost = true;   // поднимаем над стеной, если включено «поверх всех окон»
    }

    /// <summary>Показать окно на вкладке «Журнал».</summary>
    private void ShowLogTab()
    {
        ShowFromTray();
        if (_tabs != null)
            for (int i = 0; i < _tabs.TabPages.Count; i++)
                if (_tabs.TabPages[i].Text == "Журнал") { _tabs.SelectedIndex = i; break; }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    /// <summary>
    /// Модальное окно поверх «окна поверх всех окон». У TopMost-формы MessageBox может
    /// оказаться ПОД ней: окно заблокировано, а сообщения не видно — выглядит как зависание.
    /// </summary>
    private DialogResult Ask(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        bool top = TopMost;
        if (top) TopMost = false;
        try { return MessageBox.Show(this, text, caption, buttons, icon); }
        finally { if (top && !IsDisposed) TopMost = true; }
    }

    /// <summary>Понятное сообщение вместо молчаливого «ничего не произошло».</summary>
    private void WarnBusy()
    {
        Ask("Дождитесь завершения текущей операции со стеной." + Environment.NewLine +
            "Обычно это несколько секунд, при аварийной остановке — до 20.",
            "Видеостена", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExitApp()
    {
        _forceExit = true;
        Close();
    }

    // ============================== события формы ==============================

    private void OnShown(object sender, EventArgs e)
    {
        UpdateStatus();
        if (_chkLogAuto.Checked) _logTimer.Start();

        if (_chkAutoWall.Checked && !_wall.IsRunning
            && _cameras.Any(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Url)))
        {
            var t = new System.Windows.Forms.Timer { Interval = 800 };
            t.Tick += async (s2, e2) =>
            {
                t.Stop();
                t.Dispose();
                await StartWall();
            };
            t.Start();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _chkTrayMin.Checked) HideToTray();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveWindowState();

        bool userClosing = e.CloseReason == CloseReason.UserClosing;

        // закрытие окна при галочке «скрывать в трей» — это не выход
        if (!_forceExit && !_exiting && _chkTrayClose != null && _chkTrayClose.Checked && userClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // полный выход: сначала автоматически гасим стену.
        // FUP2-02: останавливать надо при ЛЮБОМ живом компоненте, а не только в состоянии Running.
        // FUP3-02: флаги ставим ДО первого await и до вызова асинхронной процедуры —
        // повторные нажатия «Выход» не запускают вторую остановку.
        // Решение «есть ли что останавливать» принимает StopAsync: дешёвая проверка файлов
        // пропускала момент, когда сторож уже запущен, а vlc.pid ещё не создан.
        if (!_stopDone)
        {
            e.Cancel = true;
            if (_exitRunning) return;        // процедура уже идёт
            _exitRunning = true;
            _exiting = true;                 // новые запуски стены запрещены
            StopWallThenClose();
            return;
        }

        if (_tray != null) _tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { _closing.Cancel(); } catch { }   // фоновые проверки больше не трогают UI
        base.OnFormClosed(e);
    }

    private void SaveWindowState()
    {
        if (_chkAutoWall == null) return;
        _app.Width = Width;
        _app.Height = Height;
        _app.Left = Left;
        _app.Top = Top;
        _app.Save(_settingsPath);
    }

    /// <summary>
    /// FUP3-02: выход под той же блокировкой, что и остальные операции со стеной.
    /// Вызывается только из OnFormClosing, который уже выставил _exitRunning и _exiting.
    /// </summary>
    private async void StopWallThenClose()
    {
        bool locked = false;
        try
        {
            // ждём, пока завершится запуск/остановка, начатые пользователем
            locked = await _opLock.WaitAsync(TimeSpan.FromMilliseconds(25000));
            if (!locked)
            {
                Ask("Не удалось приступить к остановке: другая операция со стеной не завершилась." +
                    Environment.NewLine + "Проверьте журнал и попробуйте выйти ещё раз.",
                    "Видеостена", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                StayInProgram();
                return;
            }

            SetBusy(true, "останавливаю стену…");
            var progress = StartStopProgress();

            StopResult result = null;
            try { result = await _wall.StopAsync(30000); }
            catch { result = null; }
            finally { progress.Dispose(); }

            // FUP2-01/FUP2-04: успех — только подтверждённое завершение всех наших компонентов
            if (result == null || !result.Stopped)
            {
                string why = result != null && !string.IsNullOrEmpty(result.Note)
                    ? Environment.NewLine + result.Note
                    : "";

                var answer = Ask(
                    "Стену не удалось остановить полностью." + why + Environment.NewLine + Environment.NewLine +
                    "«Да» — попробовать ещё раз" + Environment.NewLine +
                    "«Нет» — выйти всё равно (процессы стены останутся в памяти)" + Environment.NewLine +
                    "«Отмена» — остаться в программе",
                    "Видеостена", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);

                if (answer == DialogResult.Cancel)
                {
                    StayInProgram();
                    return;
                }

                if (answer == DialogResult.Yes)
                {
                    var progress2 = StartStopProgress();
                    try { result = await _wall.StopAsync(30000); }
                    catch { result = null; }
                    finally { progress2.Dispose(); }

                    if (result == null || !result.Stopped)
                    {
                        Ask(
                            "Остановить не удалось и со второй попытки." +
                            (result != null && !string.IsNullOrEmpty(result.Note)
                                ? Environment.NewLine + result.Note : "") +
                            Environment.NewLine + "Проверьте журнал: возможно, занят порт 4212 или процесс не отвечает.",
                            "Видеостена", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        StayInProgram();
                        return;
                    }
                }
            }

            _stopDone = true;
            if (_tray != null) _tray.Visible = false;
            try { _closing.Cancel(); } catch { }
            Close();
        }
        catch (Exception ex)
        {
            Ask("Ошибка при выходе:" + Environment.NewLine + ex.Message, "Видеостена",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            StayInProgram();
        }
        finally
        {
            if (locked) _opLock.Release();
            if (!_stopDone) SetBusy(false);
            _exitRunning = false;
        }
    }

    /// <summary>Возврат в рабочее состояние после отмены выхода.</summary>
    private void StayInProgram()
    {
        _stopDone = false;
        _exiting = false;
        _forceExit = false;
        _exitRunning = false;
        SetBusy(false);
        UpdateStatus();
        if (!Visible) ShowFromTray();
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (_audioTimer != null) { _audioTimer.Stop(); _audioTimer.Dispose(); } } catch { }
            try { _audio.Dispose(); } catch { }
            try { _wallBadges.Dispose(); } catch { }
        }

        if (disposing)
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_statusTimer != null) _statusTimer.Dispose();
            if (_logTimer != null) _logTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
