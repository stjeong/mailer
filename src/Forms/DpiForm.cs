using System.Runtime.InteropServices;
using MailClient.Services;

namespace MailClient.Forms;

/// <summary>
/// 화면 배율(DPI)이 바뀔 때 직접 다시 계산해야 하는 것이 있는 컨트롤 (예: 글꼴로 측정한 크기, 직접 그리는 크기)
/// </summary>
public interface IDpiAware
{
    /// <summary>창의 레이아웃이 <paramref name="newDpi"/> 에 맞게 비례 조정된 뒤 호출됩니다.</summary>
    void OnDpiRescaled(int oldDpi, int newDpi);
}

/// <summary>
/// 모니터별 DPI(PerMonitorV2) 대응 창의 공통 기반.
/// <para>
/// 이 프로그램의 창들은 AutoScaleMode.None 으로 두고, 생성자에서 크기를 직접 DPI 로 변환합니다(S()).
/// WinForms 는 창의 DPI 가 바뀌면(배율이 다른 모니터로 옮기거나 배율 설정을 바꿀 때) 창 크기와 글꼴만 새 DPI 로 바꾸고,
/// AutoScaleMode.None 인 창의 컨트롤 크기·여백은 그대로 둡니다. 그래서 여기서 나머지를 같은 비율로 조정합니다.
/// </para>
/// <para>
/// 창을 처음 열 때도 같은 처리를 합니다. 생성자는 시스템 DPI 로 크기를 계산하는데, 창이 배율이 다른 모니터에서
/// 만들어지면(예: 그 모니터에 있는 주 창 위에 여는 대화 상자) 핸들을 만들 때 DPI 가 달라지기 때문입니다.
/// </para>
/// </summary>
public class DpiForm : Form
{
    /// <summary>현재 컨트롤 크기들이 맞춰져 있는 DPI</summary>
    private int _layoutDpi;

    public DpiForm()
    {
        AutoScaleMode = AutoScaleMode.None;   // 크기는 S() 로 직접 변환
        _layoutDpi = DeviceDpi;               // 핸들이 없을 때는 시스템 DPI
    }

    /// <summary>시스템 DPI (프로세스 시작 시 주 모니터 배율). 글꼴의 포인트 → 픽셀 변환 기준.</summary>
    public static int SystemDpi { get; } = GetSystemDpi();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private static int GetSystemDpi()
    {
        try { return (int)GetDpiForSystem(); }
        catch { return 96; }
    }

    protected override void OnLoad(EventArgs e)
    {
        // 배율이 다른 모니터에서 처음 열린 경우: 생성자에서 계산한 크기를 이 모니터 배율로 조정
        if (DeviceDpi != _layoutDpi)
        {
            int old = _layoutDpi;
            float factor = (float)DeviceDpi / old;
            var splits = CaptureSplits();
            if (WindowState == FormWindowState.Normal)
                ClientSize = ScaleSize(ClientSize, factor);
            MinimumSize = ScaleSize(MinimumSize, factor);
            MaximumSize = ScaleSize(MaximumSize, factor);
            RescaleLayout(old, DeviceDpi, splits);
            // 크기가 바뀌었으므로 가운데 위치를 다시 계산
            if (StartPosition == FormStartPosition.CenterParent && Owner != null) CenterToParent();
            else if (StartPosition is FormStartPosition.CenterScreen or FormStartPosition.CenterParent) CenterToScreen();
        }
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        var min = MinimumSize;
        var max = MaximumSize;
        var splits = CaptureSplits();   // WinForms 의 처리로 바뀌기 전의 분할 위치
        base.OnDpiChanged(e);   // 창 크기(Windows 가 제안한 영역)와 글꼴은 WinForms 가 조정
        if (e.Cancel || DeviceDpi == _layoutDpi) return;

        float factor = (float)DeviceDpi / _layoutDpi;
        // 최소/최대 크기는 WinForms 설정에 따라 이미 조정됐을 수 있음 (그대로면 여기서 조정)
        if (MinimumSize == min) MinimumSize = ScaleSize(min, factor);
        if (MaximumSize == max) MaximumSize = ScaleSize(max, factor);
        RescaleLayout(_layoutDpi, DeviceDpi, splits);
    }

    /// <summary>모든 자식 컨트롤의 위치·크기·여백·표 열/행 크기·분할 위치 등을 비율에 맞게 조정</summary>
    private void RescaleLayout(int oldDpi, int newDpi, List<SplitState> splits)
    {
        _layoutDpi = newDpi;
        float factor = (float)newDpi / oldDpi;
        // Control.Scale 은 내부 상태(RequiredScaling)에 따라 여백·최소 크기를 한 번 또는 두 번 곱해 왕복하면 어긋나므로
        // 필요한 값만 직접 정확히 한 번씩 조정합니다.
        var all = Descendants(this).Where(c => !IsSelfScaling(c)).ToList();
        SuspendLayout();
        foreach (var c in all) c.SuspendLayout();
        try
        {
            Padding = ScalePadding(Padding, factor);
            foreach (var c in all) ScaleOne(c, oldDpi, newDpi);
            // 자동 크기 컨트롤은 자식부터 새 글꼴 기준 크기로 다시 계산
            for (int i = all.Count - 1; i >= 0; i--) RefitAutoSize(all[i]);
        }
        finally
        {
            // 자식 컨테이너부터 레이아웃 (부모 레이아웃이 자식 크기를 바꾸면 그때 다시 배치됨)
            for (int i = all.Count - 1; i >= 0; i--) all[i].ResumeLayout(true);
            ResumeLayout(true);
        }
        FitAutoSizeForm();

        // 글꼴로 측정하거나 직접 그리는 크기는 각자 다시 계산.
        // Windows 는 창의 WM_DPICHANGED 다음에 자식 창들에 WM_DPICHANGED_AFTERPARENT 를 보내고, 이때 기본 컨트롤
        // (예: TreeView 의 줄 높이)이 자기 값을 기본값으로 되돌리므로 그 메시지들이 끝난 뒤에 실행합니다.
        if (IsHandleCreated) BeginInvoke(() => AfterRescale(oldDpi, newDpi, splits));
        else AfterRescale(oldDpi, newDpi, splits);
    }

    private void AfterRescale(int oldDpi, int newDpi, List<SplitState> splits)
    {
        if (IsDisposed || DeviceDpi != newDpi) return;   // 그사이 다시 바뀌었으면 그쪽 처리에 맡김
        RestoreSplits(splits, (float)newDpi / oldDpi);
        foreach (var c in Descendants(this).OfType<IDpiAware>().ToList())
            c.OnDpiRescaled(oldDpi, newDpi);
        OnDpiRescaled(oldDpi, newDpi);
        FitAutoSizeForm();
        Log.Write($"DPI {oldDpi} -> {newDpi}: {GetType().Name} rescaled");
        Invalidate(true);
    }

    /// <summary>DPI 변경 직전의 분할 창 상태</summary>
    private sealed record SplitState(SplitContainer Split, int Distance, int Length);

    private static int SplitLength(SplitContainer s) => s.Orientation == Orientation.Vertical ? s.Width : s.Height;

    private List<SplitState> CaptureSplits() =>
        Descendants(this).OfType<SplitContainer>().Select(s => new SplitState(s, s.SplitterDistance, SplitLength(s))).ToList();

    /// <summary>
    /// 분할 위치를 DPI 변경 직전 값에서 다시 계산합니다. (SplitContainer 는 크기가 바뀔 때와 WinForms 의 DPI 처리 중에
    /// 분할 위치를 스스로 바꾸기도 해서, 중간 값에서 계산하면 어긋남)
    /// 고정 패널이 있으면 그 패널 크기를 비율로, 없으면 전체 대비 비율을 유지합니다.
    /// </summary>
    private static void RestoreSplits(List<SplitState> splits, float factor)
    {
        foreach (var (s, distance, length) in splits)
        {
            if (s.IsDisposed || length <= 0) continue;
            int newLength = SplitLength(s);
            int target = s.FixedPanel switch
            {
                FixedPanel.Panel1 => Sc(distance, factor),
                FixedPanel.Panel2 => newLength - Sc(length - distance, factor),
                _ => (int)Math.Round((double)distance * newLength / length)
            };
            int maxDistance = newLength - s.Panel2MinSize - s.SplitterWidth;
            if (maxDistance < s.Panel1MinSize) continue;
            try { s.SplitterDistance = Math.Clamp(target, s.Panel1MinSize, maxDistance); }
            catch (Exception ex) { Log.Error("DPI splitter", ex); }
        }
    }

    /// <summary>
    /// 레이아웃 조정이 끝난 뒤 호출됩니다. 글꼴로 측정한 크기 등 비례 조정으로 맞지 않는 것을 다시 계산하세요.
    /// (글꼴은 이미 새 DPI 에 맞게 바뀌어 있습니다.)
    /// </summary>
    protected virtual void OnDpiRescaled(int oldDpi, int newDpi) { }

    /// <summary>스스로 DPI 를 처리하는 컨트롤 (메뉴·상태 표시줄 등 ToolStrip 계열): 손대지 않음</summary>
    private static bool IsSelfScaling(Control c)
    {
        for (var x = c; x != null; x = x.Parent)
            if (x is ToolStrip) return true;
        return false;
    }

    private static int Sc(int v, float f) => (int)Math.Round(v * f);

    /// <summary>레이아웃이 늘이는 컨트롤의 처음 크기 (기록한 DPI 기준)</summary>
    private sealed record DesignSize(int Dpi, int Width, int Height)
    {
        public int WidthAt(int dpi) => (int)Math.Round(Width * (double)dpi / Dpi);
        public int HeightAt(int dpi) => (int)Math.Round(Height * (double)dpi / Dpi);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, DesignSize> DesignSizes = new();

    /// <summary>
    /// 값 하나의 기준(처음 기록한 값과 그때의 DPI). 마지막으로 적용한 값과 지금 값이 다르면 프로그램이 직접 바꾼 것이므로
    /// 지금 값을 새 기준으로 삼습니다.
    /// </summary>
    private sealed class Tracked<T> where T : struct
    {
        private T _value, _applied;
        private int _dpi;
        private bool _has;

        public T Resolve(T current, int oldDpi, int newDpi, Func<T, float, T> scale)
        {
            if (!_has || !current.Equals(_applied)) { _value = current; _dpi = oldDpi; _has = true; }
            _applied = scale(_value, (float)newDpi / _dpi);
            return _applied;
        }
    }

    private sealed class Baseline
    {
        public readonly Tracked<Padding> Margin = new(), Padding = new();
        public readonly Tracked<Size> Min = new(), Max = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Baseline> Baselines = new();

    /// <summary>부모의 레이아웃이 폭/높이를 정하는지 (Dock 또는 양쪽 Anchor)</summary>
    private static (bool Width, bool Height) Stretched(Control c) => c.Dock switch
    {
        DockStyle.Fill => (true, true),
        DockStyle.Top or DockStyle.Bottom => (true, false),
        DockStyle.Left or DockStyle.Right => (false, true),
        _ => ((c.Anchor & (AnchorStyles.Left | AnchorStyles.Right)) == (AnchorStyles.Left | AnchorStyles.Right),
              (c.Anchor & (AnchorStyles.Top | AnchorStyles.Bottom)) == (AnchorStyles.Top | AnchorStyles.Bottom))
    };

    /// <summary>컨트롤 하나의 위치·크기·Margin·Padding·최소/최대 크기와 컨트롤 종류별 DPI 의존 값을 조정</summary>
    private static void ScaleOne(Control c, int oldDpi, int newDpi)
    {
        float f = (float)newDpi / oldDpi;
        // 여백·최소/최대 크기는 처음 기록한 값에서 계산 (지금 값에 비율을 곱하면 반올림 오차가 쌓임: 예) 1px → 1 → 2)
        var b = Baselines.GetValue(c, _ => new Baseline());
        c.Margin = b.Margin.Resolve(c.Margin, oldDpi, newDpi, ScalePadding);
        c.Padding = b.Padding.Resolve(c.Padding, oldDpi, newDpi, ScalePadding);
        var min = b.Min.Resolve(c.MinimumSize, oldDpi, newDpi, ScaleSize);
        var max = b.Max.Resolve(c.MaximumSize, oldDpi, newDpi, ScaleSize);
        // 줄일 때는 최소 크기부터, 늘릴 때는 최대 크기부터 (서로 충돌하지 않게)
        if (f < 1) { c.MinimumSize = min; c.MaximumSize = max; }
        else { c.MaximumSize = max; c.MinimumSize = min; }

        // 분할 창 패널·탭 페이지의 위치/크기는 부모가 정함
        if (c is not (SplitterPanel or TabPage))
        {
            if (!c.AutoSize || IsWidthOnlyAutoSize(c))
            {
                var (stretchW, stretchH) = Stretched(c);
                // 레이아웃이 늘이는 방향(Dock=Fill, 양쪽 고정 등)은 지금 크기가 아니라 처음 기록한 크기에서 계산
                // (지금 크기는 레이아웃이 늘인 값이라 비율을 곱해 되돌리면 왕복할 때마다 조금씩 커짐)
                var design = DesignSizes.GetValue(c, x => new DesignSize(oldDpi, x.Width, x.Height));
                int w = stretchW ? design.WidthAt(newDpi) : Sc(c.Width, f);
                int h = stretchH ? design.HeightAt(newDpi) : Sc(c.Height, f);
                c.SetBounds(Sc(c.Left, f), Sc(c.Top, f), Math.Max(1, w), Math.Max(1, h));
            }
            else
                c.Location = new Point(Sc(c.Left, f), Sc(c.Top, f));
        }

        switch (c)
        {
            case TableLayoutPanel tlp:
                foreach (ColumnStyle cs in tlp.ColumnStyles) if (cs.SizeType == SizeType.Absolute) cs.Width *= f;
                foreach (RowStyle rs in tlp.RowStyles) if (rs.SizeType == SizeType.Absolute) rs.Height *= f;
                break;
            case SplitContainer sc:
                // 분할 위치는 RestoreSplits 에서 (여기서는 최소 크기와 분할선 두께만)
                try
                {
                    sc.Panel1MinSize = Math.Max(0, Sc(sc.Panel1MinSize, f));
                    sc.Panel2MinSize = Math.Max(0, Sc(sc.Panel2MinSize, f));
                    sc.SplitterWidth = Math.Max(1, Sc(sc.SplitterWidth, f));
                }
                catch (Exception ex) { Log.Error("DPI splitter", ex); }   // 창이 너무 작으면 범위 오류
                break;
            case ListView lv:
                foreach (ColumnHeader col in lv.Columns)
                    if (col.Width > 0) col.Width = Math.Max(1, Sc(col.Width, f));
                break;
            case TreeView tv:
                tv.ItemHeight = EvenItemHeight(Sc(tv.ItemHeight, f));
                tv.Indent = Math.Max(1, Sc(tv.Indent, f));
                break;
        }
    }

    /// <summary>
    /// TreeView 줄 높이는 짝수로: 홀수 높이는 TreeView 가 창(핸들)을 다시 만들어야 해서 깜박이고 스크롤 위치 등을 잃음
    /// </summary>
    public static int EvenItemHeight(int height) => Math.Max(2, height + height % 2);

    /// <summary>AutoSize 가 높이만 정하는 컨트롤 (폭은 레이아웃이나 코드가 정함)</summary>
    private static bool IsWidthOnlyAutoSize(Control c) =>
        c is TextBoxBase or ComboBox or UpDownBase or DateTimePicker or TrackBar or ListBox;

    /// <summary>
    /// 자동 크기 컨트롤은 비율을 곱하지 않고 새 글꼴로 크기를 다시 계산합니다.
    /// (글꼴 크기와 글자 폭이 정확히 비례하지 않고, 버튼·패널의 GrowOnly 모드는 한번 커지면 줄어들지 않으므로
    /// 먼저 최소 크기로 줄인 뒤 원하는 크기로 맞춤)
    /// </summary>
    private static void RefitAutoSize(Control c)
    {
        if (!c.AutoSize || IsWidthOnlyAutoSize(c) || c is SplitterPanel or TabPage) return;
        c.Size = new Size(Math.Max(1, c.MinimumSize.Width), Math.Max(1, c.MinimumSize.Height));
        c.Size = c.GetPreferredSize(Size.Empty);
    }

    /// <summary>내용에 맞춰 크기가 정해지는 대화 상자(AutoSize)는 새 크기의 내용에 다시 맞춤</summary>
    private void FitAutoSizeForm()
    {
        if (!AutoSize || WindowState != FormWindowState.Normal) return;
        PerformLayout();
        var preferred = GetPreferredSize(Size.Empty);
        if (AutoSizeMode == AutoSizeMode.GrowOnly) preferred = new Size(Math.Max(preferred.Width, Width), Math.Max(preferred.Height, Height));
        Size = preferred;
    }

    internal static IEnumerable<Control> Descendants(Control c) =>
        c.Controls.Cast<Control>().SelectMany(x => Descendants(x).Prepend(x));

    private static Size ScaleSize(Size s, float f) =>
        s.IsEmpty ? s : new Size((int)Math.Round(s.Width * f), (int)Math.Round(s.Height * f));

    private static Padding ScalePadding(Padding p, float f) => new(
        (int)Math.Round(p.Left * f), (int)Math.Round(p.Top * f), (int)Math.Round(p.Right * f), (int)Math.Round(p.Bottom * f));
}
