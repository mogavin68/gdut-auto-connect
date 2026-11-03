using GdutNetwork.Windows;

namespace GdutNetwork.App;

internal sealed class RechargeHistoryPanel : UserControl
{
    private readonly Label range = new() { Dock = DockStyle.Fill };
    private readonly Label state = new() { Dock = DockStyle.Fill, Text = "登录官网后，打开此页查询最近六个月的充值明细。", ForeColor = Color.DimGray };
    private readonly Button refresh = new() { Text = "刷新充值记录", AutoSize = true, Enabled = false };
    private readonly DataGridView grid = new();
    internal event Func<Task>? RefreshRequested;
    internal event Action? OpenOfficialRequested;

    internal RechargeHistoryPanel(bool preview)
    {
        Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 5 };
        foreach (int height in new[] { 32, 42, 48 }) root.RowStyles.Add(new(SizeType.Absolute, height));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 48));
        SetRange(RechargeHistoryPeriod.LastSixMonths(RechargeHistoryPeriod.ChinaToday));
        root.Controls.Add(range, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var official = new Button { Text = "打开官网充值明细", AutoSize = true };
        actions.Controls.AddRange([refresh, official]); root.Controls.Add(actions, 0, 1); root.Controls.Add(state, 0, 2);
        grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells; grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        foreach (var title in new[] { "交费时间", "金额（元）", "交费类型", "受理终端", "备注" }) grid.Columns.Add(title, title);
        grid.Columns[0].ValueType = typeof(DateTime); grid.Columns[0].DefaultCellStyle.Format = "yyyy-MM-dd HH:mm:ss"; grid.Columns[0].FillWeight = 170; grid.Columns[0].MinimumWidth = 165;
        grid.Columns[1].ValueType = typeof(decimal); grid.Columns[1].DefaultCellStyle.Format = "0.00"; grid.Columns[1].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; grid.Columns[1].FillWeight = 90;
        grid.Columns[2].FillWeight = 145; grid.Columns[3].FillWeight = 90; grid.Columns[4].FillWeight = 240;
        root.Controls.Add(grid, 0, 3);
        root.Controls.Add(new Label { Text = "来源：广工自助服务“充值明细”。日期按北京时间；待支付订单不等于已充值。记录仅在当前窗口显示。", Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 4);
        Controls.Add(root);
        refresh.Click += async (_, _) => { if (!preview && RefreshRequested is { } handler) await handler(); };
        official.Click += (_, _) => { if (!preview) OpenOfficialRequested?.Invoke(); };
        if (preview) state.Text = "界面预览 · 充值记录来自官网，按交费时间倒序显示。";
    }

    internal void SetReady(bool ready) => refresh.Enabled = ready;
    internal void Loading(RechargeHistoryPeriod period)
    {
        SetRange(period); grid.Rows.Clear(); refresh.Enabled = false;
        state.Text = "正在分段查询官网，并读取所有分页…";
    }
    internal void ShowSnapshot(RechargeHistorySnapshot snapshot)
    {
        SetRange(snapshot.Period); grid.Rows.Clear();
        foreach (var item in snapshot.Entries) grid.Rows.Add(item.OccurredAt.DateTime, item.Amount, item.PaymentType, item.Terminal, item.Remark);
        state.Text = snapshot.Entries.Count == 0
            ? $"该日期范围内暂无官网充值明细 · 最近查询 {snapshot.CheckedAt:HH:mm:ss}"
            : $"共 {snapshot.Entries.Count} 条 · 已读取所有分段及分页 · 最近查询 {snapshot.CheckedAt:HH:mm:ss}";
        refresh.Enabled = true;
    }
    internal void ShowError(string message) { grid.Rows.Clear(); state.Text = message; refresh.Enabled = true; }
    private void SetRange(RechargeHistoryPeriod period) => range.Text = $"最近半年：{period.Start:yyyy-MM-dd} 至 {period.End:yyyy-MM-dd}（含起止日期）";
}
