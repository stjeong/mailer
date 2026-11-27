using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MailClient.Forms;

/// <summary>
/// 메일 목록용 ListView.
/// <list type="bullet">
/// <item><b>빈 목록 문구</b>: 항목이 없을 때 안내 문구 표시. Windows 목록 컨트롤의 "빈 목록 문구" 알림
///   (LVN_GETEMPTYMARKUP, Vista 이상)에 응답하므로 컨트롤이 직접 그립니다 (덮는 컨트롤 없음).</item>
/// <item><b>구획 머리글 색</b>: 구획(그룹) 머리글을 회색 띠 + 굵은 글씨로 그립니다. 목록 컨트롤의 그리기 알림
///   (NM_CUSTOMDRAW)에서 구획을 다 그린 직후 머리글 영역만 덮어 그리고, 메일 행은 기존대로 WinForms 가 그립니다.
///   접기 화살표도 Windows 가 두는 자리(오른쪽 끝)에 그리므로 클릭해서 접고 펴는 동작은 Windows 기본 동작 그대로입니다.</item>
/// </list>
/// </summary>
public class MailListView : ListView
{
    public MailListView()
    {
        // 이중 버퍼링 (LVS_EX_DOUBLEBUFFER): 다시 그릴 때 배경을 먼저 지웠다 그리는 과정이 보이지 않게 (깜빡임 방지)
        DoubleBuffered = true;
    }

    // =====================================================================
    // 빈 목록 문구
    // =====================================================================

    private string _emptyText = "";

    [DefaultValue("")]
    [Description("항목이 없을 때 표시할 문구")]
    public string EmptyText
    {
        get => _emptyText;
        set
        {
            value ??= "";
            if (_emptyText == value) return;
            _emptyText = value;
            if (IsHandleCreated)
            {
                // 컨트롤은 한 번 받은 문구를 기억하므로 다시 요청하게 함
                SendMessage(Handle, LVM_RESETEMPTYTEXT, IntPtr.Zero, IntPtr.Zero);
                if (Items.Count == 0) Invalidate();
            }
        }
    }

    /// <summary>true: 목록 가운데, false(기본): 머리글 아래 왼쪽 위</summary>
    [DefaultValue(false)]
    public bool EmptyTextCentered { get; set; }

    // =====================================================================
    // 구획 머리글
    // =====================================================================

    /// <summary>구획 머리글 띠 색 (기본: 연한 회색)</summary>
    [Description("구획 머리글 배경색")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GroupHeaderBackColor { get; set; } = Color.FromArgb(0xE6, 0xE6, 0xE6);

    /// <summary>구획 머리글 글자색 (기본: 진한 회색)</summary>
    [Description("구획 머리글 글자색")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GroupHeaderForeColor { get; set; } = Color.FromArgb(0x33, 0x33, 0x33);

    private Font? _groupFont;
    private Font GroupFont => _groupFont is { } f && f.Size == Font.Size && f.FontFamily.Name == Font.FontFamily.Name
        ? f
        : (_groupFont = new Font(Font, FontStyle.Bold));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _groupFont?.Dispose();
        base.Dispose(disposing);
    }

    private static readonly PropertyInfo? GroupIdProperty =
        typeof(ListViewGroup).GetProperty("ID", BindingFlags.Instance | BindingFlags.NonPublic);

    private ListViewGroup? FindGroup(int id) =>
        Groups.Cast<ListViewGroup>().FirstOrDefault(g => GroupIdProperty?.GetValue(g) is int gid && gid == id);

    /// <summary>머리글 하나를 그림. 직접 그렸으면 true</summary>
    private bool DrawGroupHeader(IntPtr hdc, int groupId)
    {
        var group = FindGroup(groupId);
        if (group == null) return false;

        var r = new RECT { top = LVGGR_HEADER };
        if (SendMessage(Handle, LVM_GETGROUPRECT, (IntPtr)groupId, ref r) == IntPtr.Zero) return false;
        var rect = Rectangle.FromLTRB(0, r.top, ClientSize.Width, r.bottom);   // 목록 폭 전체를 띠로
        if (rect.Height <= 0) return false;
        bool collapsed = ((int)SendMessage(Handle, LVM_GETGROUPSTATE, (IntPtr)groupId, (IntPtr)LVGS_COLLAPSED) & LVGS_COLLAPSED) != 0;

        using var g = Graphics.FromHdc(hdc);
        using (var back = new SolidBrush(GroupHeaderBackColor)) g.FillRectangle(back, rect);
        using (var line = new Pen(ControlPaint.Dark(GroupHeaderBackColor, 0.05f))) g.DrawLine(line, rect.Left, rect.Bottom - 1, rect.Right, rect.Bottom - 1);

        int pad = LogicalToDeviceUnits(8);
        var textRect = new Rectangle(rect.Left + pad, rect.Top, rect.Width - pad * 2 - LogicalToDeviceUnits(24), rect.Height);
        TextRenderer.DrawText(g, group.Header, GroupFont, textRect, GroupHeaderForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // 접기 화살표 (펼침: ⌃, 접힘: ⌄) - Windows 접기 버튼 자리인 오른쪽 끝
        if (group.CollapsedState != ListViewGroupCollapsedState.Default)
        {
            int cx = rect.Right - LogicalToDeviceUnits(14), cy = rect.Top + rect.Height / 2;
            int w = LogicalToDeviceUnits(4), h = LogicalToDeviceUnits(2);
            var g2 = g;
            g2.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(GroupHeaderForeColor, Math.Max(1.5f, LogicalToDeviceUnits(15) / 10f));
            if (collapsed) g2.DrawLines(pen, [new Point(cx - w, cy - h), new Point(cx, cy + h), new Point(cx + w, cy - h)]);
            else g2.DrawLines(pen, [new Point(cx - w, cy + h), new Point(cx, cy - h), new Point(cx + w, cy + h)]);
        }
        return true;
    }

    // =====================================================================
    // 키보드 이동: 구획 머리글 건너뛰기
    // =====================================================================
    // Windows 기본 동작은 위/아래 화살표로 구획 머리글에도 포커스가 가고, 그때 그 구획의 메일이 모두 선택됩니다.
    // 구획을 쓸 때는 이동 키를 직접 처리해 화면에 보이는 메일 순서대로만 이동합니다.

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);   // 다른 처리기(삭제 키 등)가 먼저
        if (e.Handled || !ShowGroups || Groups.Count == 0 || View != View.Details) return;
        if (e.KeyCode is not (Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown) || e.Alt) return;

        var order = VisualOrder();
        e.Handled = true;           // 기본 동작(머리글로 이동)을 막음
        e.SuppressKeyPress = true;
        if (order.Count == 0) return;

        var current = FocusedItem is { } f && order.Contains(f) ? f : (SelectedItems.Count > 0 ? SelectedItems[0] : null);
        int pos = current != null ? order.IndexOf(current) : -1;
        int page = Math.Max(1, ClientSize.Height / Math.Max(1, RowHeight()) - 1);
        int target = e.KeyCode switch
        {
            Keys.Up => pos < 0 ? 0 : pos - 1,
            Keys.Down => pos < 0 ? 0 : pos + 1,
            Keys.Home => 0,
            Keys.End => order.Count - 1,
            Keys.PageUp => pos < 0 ? 0 : pos - page,
            _ => pos < 0 ? 0 : pos + page,
        };
        target = Math.Clamp(target, 0, order.Count - 1);
        MoveTo(order, target, extend: e.Shift, focusOnly: e.Control && !e.Shift);
    }

    /// <summary>화면에 보이는 순서의 메일 (구획 순서 → 구획 안 순서, 접힌 구획은 제외)</summary>
    internal List<ListViewItem> VisualOrder()
    {
        var byGroup = Items.Cast<ListViewItem>().GroupBy(i => i.Group).ToDictionary(g => g.Key ?? DefaultGroupKey, g => g.OrderBy(i => i.Index).ToList());
        var order = new List<ListViewItem>();
        foreach (ListViewGroup g in Groups)
            if (g.CollapsedState != ListViewGroupCollapsedState.Collapsed && byGroup.TryGetValue(g, out var items)) order.AddRange(items);
        if (byGroup.TryGetValue(DefaultGroupKey, out var ungrouped)) order.AddRange(ungrouped);   // 구획이 없는 항목은 맨 아래
        return order;
    }

    private static readonly ListViewGroup DefaultGroupKey = new("(default)");

    private int _anchor = -1;   // Shift 범위 선택의 기준 항목 (Items 인덱스)

    private void MoveTo(List<ListViewItem> order, int target, bool extend, bool focusOnly)
    {
        var item = order[target];
        // BeginUpdate/EndUpdate 로 감싸지 않음: EndUpdate 는 목록 전체를 지우고 다시 그려 키를 누를 때마다 깜빡임.
        // 선택이 바뀐 행만 컨트롤이 다시 그림.
        {
            if (extend)
            {
                // 기준 항목 ~ 대상 항목 사이(화면 순서)만 선택
                var anchorItem = _anchor >= 0 && _anchor < Items.Count && order.Contains(Items[_anchor]) ? Items[_anchor]
                    : FocusedItem is { } fi && order.Contains(fi) ? fi : item;
                _anchor = anchorItem.Index;
                int a = order.IndexOf(anchorItem);
                int lo = Math.Min(a, target), hi = Math.Max(a, target);
                for (int i = 0; i < order.Count; i++)
                {
                    bool sel = i >= lo && i <= hi;
                    if (order[i].Selected != sel) order[i].Selected = sel;
                }
                foreach (ListViewItem hidden in Items)   // 접힌 구획의 항목은 선택 해제
                    if (hidden.Selected && !order.Contains(hidden)) hidden.Selected = false;
            }
            else if (!focusOnly)
            {
                foreach (ListViewItem s in SelectedItems.Cast<ListViewItem>().ToList())
                    if (s != item) s.Selected = false;
                item.Selected = true;
                _anchor = item.Index;
            }
            item.Focused = true;
        }
        item.EnsureVisible();
        if (order.IndexOf(item) == 0 || item.Group != order[order.IndexOf(item) - 1].Group) EnsureGroupHeaderVisible(item.Group);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // 마우스로 고른 항목을 Shift 범위 선택의 기준으로 (Shift/Ctrl 클릭이 아닐 때)
        if ((ModifierKeys & (Keys.Shift | Keys.Control)) == 0 && GetItemAt(e.X, e.Y) is { } hit) _anchor = hit.Index;
    }

    /// <summary>구획의 첫 메일로 올라왔을 때 그 구획 머리글도 보이게 스크롤</summary>
    private void EnsureGroupHeaderVisible(ListViewGroup? group)
    {
        if (group == null || GroupIdProperty?.GetValue(group) is not int id) return;
        var r = new RECT { top = LVGGR_HEADER };
        if (SendMessage(Handle, LVM_GETGROUPRECT, (IntPtr)id, ref r) == IntPtr.Zero) return;
        int headerTop = HeaderBottom();
        if (r.top < headerTop) SendMessage(Handle, LVM_SCROLL, IntPtr.Zero, (IntPtr)(r.top - headerTop));
    }

    /// <summary>열 머리글 아래쪽 (목록 영역 시작 y)</summary>
    private int HeaderBottom()
    {
        var header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header == IntPtr.Zero || !GetWindowRect(header, out var hr)) return 0;
        var pt = PointToClient(new Point(hr.left, hr.bottom));
        return Math.Max(0, pt.Y);
    }

    private int RowHeight() => Items.Count > 0 ? Items[0].Bounds.Height : Font.Height;

    // =====================================================================
    // Win32
    // =====================================================================

    private const int LVM_SCROLL = LVM_FIRST + 20;
    private const int LVM_GETHEADER = LVM_FIRST + 31;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    private const int LVM_FIRST = 0x1000;
    private const int LVM_RESETEMPTYTEXT = LVM_FIRST + 84;
    private const int LVM_GETGROUPSTATE = LVM_FIRST + 92;
    private const int LVM_GETGROUPRECT = LVM_FIRST + 98;
    private const int LVGGR_HEADER = 1;
    private const int LVGS_COLLAPSED = 0x1;

    private const int WM_REFLECT_NOTIFY = 0x2000 + 0x004E;   // WM_REFLECT | WM_NOTIFY (WinForms 가 부모로 온 알림을 되돌려 줌)
    private const int LVN_GETEMPTYMARKUP = -100 - 87;          // LVN_FIRST - 87
    private const int NM_CUSTOMDRAW = -12;
    private const int CDDS_POSTPAINT = 0x00000002;
    private const int LVCDI_GROUP = 1;
    private const uint EMF_CENTERED = 0x1;
    private const int L_MAX_URL_LENGTH = 2048 + 32 + 4;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NMHDR
    {
        public IntPtr hwndFrom;
        public UIntPtr idFrom;
        public int code;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NMCUSTOMDRAW
    {
        public NMHDR hdr;
        public int dwDrawStage;
        public IntPtr hdc;
        public RECT rc;
        public UIntPtr dwItemSpec;
        public uint uItemState;
        public IntPtr lItemlParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NMLVCUSTOMDRAW
    {
        public NMCUSTOMDRAW nmcd;
        public int clrText;
        public int clrTextBk;
        public int iSubItem;
        public int dwItemType;
        public int clrFace;
        public int iIconEffect;
        public int iIconPhase;
        public int iPartId;
        public int iStateId;
        public RECT rcText;
        public uint uAlign;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NMLVEMPTYMARKUP
    {
        public NMHDR hdr;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = L_MAX_URL_LENGTH)]
        public string szMarkup;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_REFLECT_NOTIFY)
        {
            var hdr = Marshal.PtrToStructure<NMHDR>(m.LParam);
            if (hdr.code == LVN_GETEMPTYMARKUP && _emptyText.Length > 0)
            {
                var markup = new NMLVEMPTYMARKUP { hdr = hdr, dwFlags = EmptyTextCentered ? EMF_CENTERED : 0, szMarkup = _emptyText };
                Marshal.StructureToPtr(markup, m.LParam, false);
                m.Result = 1;   // TRUE: 문구를 제공함
                return;
            }
            if (hdr.code == NM_CUSTOMDRAW && ShowGroups)
            {
                // 구획은 CDDS_PREPAINT/POSTPAINT 단계에 dwItemType = LVCDI_GROUP, dwItemSpec = 구획 ID 로 옴.
                // 구획 그리기를 건너뛰면 그 안의 메일 행까지 빠질 수 있으므로, Windows 가 다 그린 뒤(POSTPAINT)
                // 머리글 영역만 덮어 그림. 메일 행과 나머지 단계는 WinForms 가 그대로 처리 (굵은 글씨 등).
                var cd = Marshal.PtrToStructure<NMLVCUSTOMDRAW>(m.LParam);
                if (cd.dwItemType == LVCDI_GROUP && cd.nmcd.dwDrawStage == CDDS_POSTPAINT)
                {
                    DrawGroupHeader(cd.nmcd.hdc, (int)cd.nmcd.dwItemSpec);
                    m.Result = IntPtr.Zero;   // CDRF_DODEFAULT
                    return;
                }
            }
        }
        base.WndProc(ref m);
    }
}
