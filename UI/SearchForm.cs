using System.Diagnostics;
using System.Drawing.Drawing2D;
using DeskLite.Core;

namespace DeskLite.UI;

/// <summary>
/// 桌面搜索窗口。
/// 对应 DeskGo 的「桌面搜索」：支持文件名子串匹配 + 拼音首字母匹配。
/// 已去掉云端搜索、索引开关等联网相关项。
/// </summary>
internal sealed class SearchForm : Form
{
    private readonly IconCache _icons;
    private readonly Func<IReadOnlyList<DiscoveredItem>> _source;

    private readonly TextBox _input;
    private readonly Label _hint;
    private readonly ListView _result;
    private readonly ImageList _imageList;

    private IReadOnlyList<DiscoveredItem> _all = Array.Empty<DiscoveredItem>();

    private static readonly Color BgColor = Color.FromArgb(30, 30, 36);
    private static readonly Color InputColor = Color.FromArgb(46, 46, 56);
    private static readonly Color TextColor = Color.FromArgb(238, 238, 244);
    private static readonly Color HintColor = Color.FromArgb(140, 140, 155);

    public SearchForm(IconCache icons, Func<IReadOnlyList<DiscoveredItem>> source)
    {
        _icons = icons;
        _source = source;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(620, 420);
        BackColor = BgColor;
        ShowInTaskbar = false;
        KeyPreview = true;
        TopMost = true;

        var header = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = BgColor };
        _input = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = InputColor,
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 13F),
            Location = new Point(18, 14),
            Width = 560,
            Height = 30,
        };
        _input.TextChanged += (_, _) => Filter();
        header.Controls.Add(_input);

        _hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = HintColor,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            Text = "  输入名称或拼音首字母（如 wdwj → 我的文件）· Esc 关闭 · Enter 打开",
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _imageList = new ImageList { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };

        _result = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            BorderStyle = BorderStyle.None,
            BackColor = BgColor,
            ForeColor = TextColor,
            SmallImageList = _imageList,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _result.Columns.Add("名称", 190);
        _result.Columns.Add("类型", 90);
        _result.Columns.Add("位置", 310);
        _result.DoubleClick += (_, _) => OpenSelected();
        _result.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; } };
        _result.DrawColumnHeader += (_, e) => { };
        _result.OwnerDraw = false;

        Controls.Add(_result);
        Controls.Add(_hint);
        Controls.Add(header);

        Load += (_, _) =>
        {
            _all = _source();
            Filter();
            _input.Focus();
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = new GraphicsPath();
        int d = 16;
        var r = new Rectangle(0, 0, Width, Height);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    public void Reload() { _all = _source(); Filter(); }

    /// <summary>用指定关键字打开（来自右键菜单「桌面搜索」或全局热键）</summary>
    public void SetKeyword(string? kw)
    {
        _input.Text = kw ?? "";
        _input.SelectionStart = _input.TextLength;
        _input.SelectionLength = 0;
        Filter();
    }

    private void Filter()
    {
        var kw = _input.Text;
        _result.BeginUpdate();
        try
        {
            _result.Items.Clear();
            _imageList.Images.Clear();

            var hits = _all.Where(i => Pinyin.Matches(i.Name, kw))
                           .OrderBy(i => i.Name, StringComparer.CurrentCulture)
                           .Take(300);

            foreach (var it in hits)
            {
                _imageList.Images.Add(_icons.GetIcon(it.Path));
                var lvi = new ListViewItem(it.Name)
                {
                    ImageIndex = _imageList.Images.Count - 1,
                    Tag = it,
                };
                lvi.SubItems.Add(Rules.TypeName(it.Type));
                lvi.SubItems.Add(Path.GetDirectoryName(it.Path) ?? "");
                _result.Items.Add(lvi);
            }
            _hint.Text = $"  命中 {_result.Items.Count} 项 · 输入名称或拼音首字母（如 wdwj → 我的文件）· Esc 关闭";
        }
        finally { _result.EndUpdate(); }
    }

    private void OpenSelected()
    {
        if (_result.SelectedItems.Count == 0) return;
        if (_result.SelectedItems[0].Tag is not DiscoveredItem it) return;
        try { Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true }); } catch { }
    }
}
