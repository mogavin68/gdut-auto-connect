using System.Text.Json;
using GdutNetwork.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GdutNetwork.App;

internal sealed class SelfServiceForm : Form
{
    private readonly bool preview;
    private readonly CredentialStore vault;
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill };
    private readonly WebView2 balanceBrowser = new() { Visible = false, Size = new(1,1) };
    private readonly List<WebView2> paymentViews = [];
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly Label paid = new() { Dock = DockStyle.Fill, Text = "待查询", Font = new("Microsoft YaHei UI", 25, FontStyle.Bold) };
    private readonly Label gift = new() { Dock = DockStyle.Fill, Text = "待查询", Font = new("Microsoft YaHei UI", 25, FontStyle.Bold) };
    private readonly Label status = new() { Dock = DockStyle.Fill, Text = "正在准备官网连接…" };
    private readonly Label rate = new() { AutoSize = true, Text = "月租：以官网当前报价为准" };
    private readonly ComboBox months = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly Label amount = new() { AutoSize = true, Text = "总金额：待选择月份", Padding = new(12, 6, 0, 0) };
    private readonly Button generate = new() { AutoSize = true, Text = "生成充值订单", Enabled = false };
    private readonly TextBox order = new() { Dock = DockStyle.Fill, Text = "尚未生成订单", Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical };
    private readonly PictureBox qr = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Label address = new() { Dock = DockStyle.Top, Height = 32, Text = "官网连接尚未建立", AutoEllipsis = true };
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 1500 };
    private CoreWebView2Environment? environment;
    private RechargeState? recharge;
    private bool changingMonths, reading, submitted, initializing, closed, loginAttempted;
    private string? loginNotice;
    private decimal? quotedAmount;
    private decimal? orderTotal;
    private bool qrRequested;
    private bool qrPresented;
    private string? lastQr;
    private string? quotedValue;

    internal SelfServiceForm(CredentialStore vault, bool preview = false)
    {
        this.preview = preview;
        this.vault = vault;
        Text = "套餐余量与自助缴费"; Icon = BrandAssets.LoadIcon();
        Font = new("Microsoft YaHei UI", 10); ClientSize = new(1040, 760); MinimumSize = new(940, 700);
        StartPosition = FormStartPosition.CenterParent; BackColor = Color.FromArgb(245, 247, 251);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), RowCount = 3, ColumnCount = 1 };
        shell.RowStyles.Add(new(SizeType.Absolute, 130)); shell.RowStyles.Add(new(SizeType.Absolute, 62)); shell.RowStyles.Add(new(SizeType.Percent, 100));
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        cards.ColumnStyles.Add(new(SizeType.Percent, 50)); cards.ColumnStyles.Add(new(SizeType.Percent, 50));
        cards.Controls.Add(Card("账号剩余月数", paid), 0, 0); cards.Controls.Add(Card("还没使用赠送月数", gift), 1, 0);
        shell.Controls.Add(cards, 0, 0); shell.Controls.Add(status, 0, 1);
        var native = new TabPage("自助缴费") { Padding = new(18), BackColor = BackColor };
        var official = new TabPage("官方页面") { Padding = new(6) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        foreach(int h in new[]{34,52,44,58}) layout.RowStyles.Add(new(SizeType.Absolute,h));
        layout.RowStyles.Add(new(SizeType.Percent,100)); layout.RowStyles.Add(new(SizeType.Absolute,44));
        layout.Controls.Add(rate,0,0);
        var choose = new FlowLayoutPanel { Dock = DockStyle.Fill };
        choose.Controls.Add(new Label { Text = "充值月数", AutoSize = true, Padding = new(0,6,12,0) }); choose.Controls.Add(months); choose.Controls.Add(amount);
        layout.Controls.Add(choose,0,1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var refresh = new Button { Text = "刷新余量", AutoSize = true };
        var officialButton = new Button { Text = "在官方页面操作", AutoSize = true };
        buttons.Controls.AddRange([generate,refresh,officialButton]); layout.Controls.Add(buttons,0,2);
        layout.Controls.Add(order,0,3); layout.Controls.Add(qr,0,4);
        layout.Controls.Add(new Label { Text = "付款由你使用微信扫码完成。支付结果以官方查询为准；已扣款请勿重复缴费。", Dock = DockStyle.Fill, ForeColor = Color.DimGray },0,5);
        native.Controls.Add(layout); official.Controls.Add(browser); official.Controls.Add(address);
        tabs.TabPages.AddRange([native,official]); shell.Controls.Add(tabs,0,2); Controls.Add(shell); Controls.Add(balanceBrowser);
        officialButton.Click += (_,_) =>
        {
            var payment=paymentViews.LastOrDefault(v=>!v.IsDisposed)?.FindForm();
            if(payment is not null){payment.Show(this);payment.Activate();}
            else tabs.SelectedIndex=1;
        };
        refresh.Click += async (_,_) => await RefreshBalanceAsync();
        months.SelectedIndexChanged += async (_,_) => await ChangeMonthAsync();
        generate.Click += async (_,_) => await GenerateAsync();
        poll.Tick += async (_,_) => { await ReadBrowserAsync(); foreach(var view in paymentViews.ToArray())if(!view.IsDisposed)await ReadBrowserAsync(view); };
        Shown += async (_,_) => { if(!preview) await InitializeAsync(); };
        FormClosed += (_,_) => { closed=true; lifetime.Cancel(); poll.Stop(); poll.Dispose(); browser.Dispose(); balanceBrowser.Dispose(); qr.Image?.Dispose(); lifetime.Dispose(); };
        if(preview)
        {
            paid.Text="待查询";gift.Text="待查询";status.Text="界面预览 · 月数、金额、订单和付款码均由官网提供。";
            order.Text="选择官网提供的充值月数后，点击生成订单。付款二维码将在此处显示。";
        }
    }

    private static Control Card(string title, Label value)
    {
        var panel = new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new(16), BackColor=Color.White, RowCount=2, Margin=new(0,0,14,12) };
        panel.RowStyles.Add(new(SizeType.Absolute,30)); panel.RowStyles.Add(new(SizeType.Percent,100));
        panel.Controls.Add(new Label {Text=title,AutoSize=true,ForeColor=Color.DimGray},0,0);panel.Controls.Add(value,0,1);return panel;
    }

    private async Task InitializeAsync()
    {
        if(initializing) return; initializing=true;
        try
        {
            var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GDUTAutoNetwork","WebView2");
            environment=await CoreWebView2Environment.CreateAsync(null,folder);
            var options=environment.CreateCoreWebView2ControllerOptions(); options.ProfileName="OfficialSelfService"; options.IsInPrivateModeEnabled=true;
            await browser.EnsureCoreWebView2Async(environment,options);
            Configure(browser);
            browser.CoreWebView2.Navigate("https://selfmanager.gdut.edu.cn/dashboard");
            poll.Start();
        }
        catch(Exception ex) when (ex is not OutOfMemoryException)
        { if(!closed) status.Text=$"官网连接未完成（{ex.GetType().Name}）。请检查网络与 WebView2 Runtime。"; }
        finally { initializing=false; }
    }

    private void Configure(WebView2 view)
    {
        var core=view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed=false;core.Settings.IsWebMessageEnabled=false;
        core.Settings.AreDevToolsEnabled=false;core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
        core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
        core.DownloadStarting+=(_,e)=>e.Cancel=true;
        core.NavigationStarting+=(_,e)=>
        {
            if(!SelfServiceNavigation.IsOfficial(e.Uri)) { e.Cancel=true;status.Text="已阻止跳转至未支持的地址，请在浏览器核实官方网站。"; }
            if(view==browser){generate.Enabled=false;quotedAmount=null;}
            if(new Uri(e.Uri).AbsolutePath=="/service/selfRechargeMoney"){submitted=true;months.Enabled=false;}
        };
        core.FrameNavigationStarting+=(_,e)=>{if(e.Uri!="about:blank"&&!SelfServiceNavigation.IsOfficial(e.Uri))e.Cancel=true;};
        core.SourceChanged+=(_,_)=>
        {
            if(closed)return;
            if(view==browser)address.Text="官方页面："+new Uri(core.Source).GetLeftPart(UriPartial.Path);
            else if(view!=balanceBrowser&&view.FindForm() is {} popup)popup.Text="广工官方支付页面 · "+new Uri(core.Source).Host;
        };
        core.NavigationCompleted+=async (_,e)=>{if(!closed&&e.IsSuccess)await PageReadyAsync(view);};
        core.NewWindowRequested+=async (_,e)=>
        {
            using var deferral=e.GetDeferral();e.Handled=true;
            if(environment is null||!SelfServiceNavigation.IsOfficial(e.Uri)) { status.Text="官网请求了未支持的新窗口，请在浏览器中完成。";return; }
            var popup=new Form {Text="广工官方支付页面",Icon=BrandAssets.LoadIcon(),Size=new(1100,800),StartPosition=FormStartPosition.CenterParent};
            var payment=new WebView2 {Dock=DockStyle.Fill};popup.Controls.Add(payment);
            try
            {
                popup.Show(this);
                var options=environment.CreateCoreWebView2ControllerOptions();options.ProfileName="OfficialSelfService";options.IsInPrivateModeEnabled=true;
                await payment.EnsureCoreWebView2Async(environment,options);Configure(payment);e.NewWindow=payment.CoreWebView2;paymentViews.Add(payment);
                popup.FormClosed+=(_,_)=>{paymentViews.Remove(payment);payment.Dispose();};
            }
            catch(Exception ex) when(ex is not OutOfMemoryException) {popup.Dispose();status.Text="支付窗口未能打开，请勿重复生成订单。";}
        };
    }

    private static bool IsSelfService(WebView2 view)=>SelfServiceNavigation.IsSelfService(view.CoreWebView2.Source);

    private async Task PageReadyAsync(WebView2 view)
    {
        try
        {
            if(IsSelfService(view))
            {
                string? html=JsonSerializer.Deserialize<string>(await view.ExecuteScriptAsync("document.documentElement.outerHTML.slice(0,2000000)"));
                if(closed||!IsSelfService(view)||html is null)return;
                if(SelfServiceParser.IsLogin(html))
                {
                    if(view!=browser||loginAttempted){loginNotice=SelfServiceParser.LoginError(html);status.Text=loginNotice;tabs.SelectedIndex=1;return;}
                    // Execute the real, current official frontend once; never emulate its password transformations.
                    loginAttempted=true;SelfServiceParser.ValidateLoginForm(html,new Uri(view.CoreWebView2.Source));
                    var credential=vault.Read()??throw new SelfServiceException("请先在账号与设置中保存校园网账号密码。");
                    string script="""
                    (()=>{
                      if(location.origin!=='https://selfmanager.gdut.edu.cn')return false;
                      const f=document.querySelector('form#loginform');
                      if(!f||new URL(f.action).origin!==location.origin||!/^\/login\/verify(?:;jsessionid=[A-Za-z0-9]+)?$/.test(new URL(f.action).pathname))return false;
                      const captcha=document.querySelector('#randomDiv');if(captcha&&captcha.getBoundingClientRect().height>0)return false;
                      const credentials=CREDENTIALS;
                      f.querySelector('#account').value=credentials.user;f.querySelector('#password').value=credentials.password;
                      const b=f.querySelector('button[type=submit]');if(!b)return false;b.click();return true;
                    })()
                    """.Replace("CREDENTIALS",JsonSerializer.Serialize(new {user=credential.UserName,password=credential.Password}));
                    if(await view.ExecuteScriptAsync(script)!="true")throw new SelfServiceException("官网需要人工验证，请切换到官方页面登录。");
                    status.Text="正在通过官网前端登录…";return;
                }
                loginNotice=null;
                ShowBalance(SelfServiceParser.Balance(html));
                if(view==browser&&new Uri(view.CoreWebView2.Source).AbsolutePath=="/dashboard")
                    browser.CoreWebView2.Navigate("https://selfmanager.gdut.edu.cn/service/selfRechargeAction");
            }
            if(view!=balanceBrowser)await ReadBrowserAsync(view);
        }
        catch(SelfServiceException ex){if(!closed){loginNotice=ex.Message;status.Text=ex.Message;tabs.SelectedIndex=1;}}
        catch(Exception ex) when(ex is not OutOfMemoryException){if(!closed)status.Text=$"官网读取未完成（{ex.GetType().Name}），请使用官方页面。";}
    }

    private async Task RefreshBalanceAsync()
    {
        if(preview||initializing||browser.CoreWebView2 is null)return;
        try
        {
            if(environment is null)return;
            if(balanceBrowser.CoreWebView2 is null)
            {
                var options=environment.CreateCoreWebView2ControllerOptions();options.ProfileName="OfficialSelfService";options.IsInPrivateModeEnabled=true;
                await balanceBrowser.EnsureCoreWebView2Async(environment,options);Configure(balanceBrowser);
            }
            balanceBrowser.CoreWebView2!.Navigate("https://selfmanager.gdut.edu.cn/dashboard");
        }
        catch(Exception ex) when(ex is not OutOfMemoryException){if(!closed)status.Text=$"余量刷新未完成（{ex.GetType().Name}），请在官方页面核对。";}
    }
    private void ShowBalance(SubscriptionBalance value)
    {
        paid.Text=value.AccountMonths is {} p?$"{p:0.##} 个月":"官网字段待确认";
        gift.Text=value.GiftMonths is {} g?$"{g:0.##} 个月":"官网字段待确认";
        status.Text=$"最近查询 {value.CheckedAt:HH:mm:ss} · "+(value.AccountMonths is null||value.GiftMonths is null?"部分月数字段尚未确认，请在官方页面核对。":"数据来自广工自助服务官网。");
    }

    private async Task ReadBrowserAsync(WebView2? view=null)
    {
        if(closed||reading||preview)return;view??=browser;if(view.CoreWebView2 is null)return;reading=true;
        try
        {
            if(!SelfServiceNavigation.IsOfficial(view.CoreWebView2.Source))return;
            if(view==browser&&IsSelfService(view))
            {
                string result=await view.ExecuteScriptAsync(SelfServiceScripts.ReadRecharge);
                if(!IsSelfService(view))return;
                var data=JsonSerializer.Deserialize<RechargeState>(result,JsonOptions);
                if(data is not null&&data.Options.Length>0)
                {
                    bool changed=recharge is null||!recharge.Options.SequenceEqual(data.Options);
                    recharge=data;
                    if(changed)
                    {
                        changingMonths=true;months.Items.Clear();months.Items.AddRange(data.Options);months.SelectedIndex=-1;changingMonths=false;
                        rate.Text=data.Rate.Replace("\r","").Replace("\n"," ").Trim();
                        status.Text="已读取官网充值选项，请选择充值月份。余量可点击刷新查询。";
                    }
                    if(months.SelectedItem is MonthOption selected&&selected.Value==quotedValue&&data.SelectedValue==quotedValue&&data.Account.Length>0&&TryAmount(data.Amount,out decimal total))
                    {quotedAmount=total;amount.Text=$"总金额：{total:0.00} 元";generate.Enabled=!submitted;}
                }
                else if(!submitted)
                { generate.Enabled=false; if(!initializing)status.Text=loginNotice??"原生充值表单尚未就绪；可切换官方页面登录或完成缴费。"; }
            }
            if(submitted)
            {
                var result=await view.ExecuteScriptAsync(SelfServiceScripts.ReadPayment);
                if(!SelfServiceNavigation.IsOfficial(view.CoreWebView2.Source))return;
                var payment=JsonSerializer.Deserialize<PaymentState>(result,JsonOptions);
                if(payment is not null)
                {
                    if(payment.Order.Length>0)order.Text=$"官方订单号：{payment.Order}   ·   微信支付订单号：{payment.PaymentOrder}\n订单金额：{payment.Amount} 元 · 支付结果请在官网核对";
                    if(!qrRequested&&orderTotal is {} expected&&TryAmount(payment.Amount,out var payAmount)&&payAmount==expected)
                    {qrRequested=true;await view.ExecuteScriptAsync(SelfServiceScripts.RequestQr(expected));}
                    if(payment.Expired){var old=qr.Image;qr.Image=null;lastQr=null;old?.Dispose();status.Text="付款码已失效，请在官方页面核对或刷新付款码。请勿重复生成充值订单。";}
                    else if(payment.Qr!=lastQr&&payment.Order.Length>0&&payment.PaymentOrder.Length>0&&orderTotal is {} expectedTotal&&TryAmount(payment.Amount,out var actualTotal)&&actualTotal==expectedTotal&&payment.Qr.StartsWith("data:image/png;base64,",StringComparison.Ordinal)&&payment.Qr.Length<2_000_000)
                    {
                        using var stream=new MemoryStream(Convert.FromBase64String(payment.Qr[22..]));using var decoded=Image.FromStream(stream);
                        if(decoded.Width>2048||decoded.Height>2048)return;
                        var old=qr.Image;qr.Image=new Bitmap(decoded);old?.Dispose();lastQr=payment.Qr;
                        if(!qrPresented)
                        {
                            qrPresented=true;tabs.SelectedIndex=0;
                            if(view!=browser)view.FindForm()?.Hide();
                            Activate();
                        }
                        status.Text="官方付款码已读取。请用微信扫码支付；扣款后点击刷新余量，并在官网核对支付结果。";
                    }
                }
            }
        }
        catch(Exception ex) when(ex is not OutOfMemoryException) {if(!closed){generate.Enabled=false;status.Text="官网页面格式暂不支持原生读取，请使用官方页面。";}}
        finally{reading=false;}
    }

    private async Task ChangeMonthAsync()
    {
        if(preview||changingMonths||recharge is null||months.SelectedItem is not MonthOption choice||!IsSelfService(browser))return;
        quotedAmount=null;quotedValue=choice.Value;generate.Enabled=false;amount.Text="正在读取官网报价…";
        try
        {
            var ok=await browser.ExecuteScriptAsync(SelfServiceScripts.SelectMonth(recharge.Index,recharge.Name,choice.Value));
            if(ok!="true"){status.Text="官网月份选项已变化，请使用官方页面。";return;}
            await ReadBrowserAsync();
        }
        catch(Exception ex) when(ex is not OutOfMemoryException){status.Text="官网报价读取未完成，请使用官方页面。";}
    }

    private async Task GenerateAsync()
    {
        if(preview||submitted||recharge is null||months.SelectedItem is not MonthOption choice||quotedAmount is not {} total||!IsSelfService(browser))return;
        string account=recharge.Account;
        if(MessageBox.Show(this,$"充值账号：{account}\n充值月份：{choice.Label}\n官网报价：{total:0.00} 元\n\n确认生成待支付订单？之后由你扫码付款。","确认充值信息",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return;
        generate.Enabled=false;submitted=true;months.Enabled=false;orderTotal=total;
        try
        {
            if(await browser.ExecuteScriptAsync(SelfServiceScripts.SubmitRecharge(choice.Value,account,total))!="true")
            {submitted=false;months.Enabled=true;status.Text="未提交：官网充值按钮已变化，请使用官方页面。";return;}
            tabs.SelectedIndex=1;order.Text="已向官网提交订单请求，请在官方页面查看订单与付款码。";
            status.Text="请在官方页面完成支付方式选择；能读取的订单号和二维码会同步到原生界面。查询或关闭不会重复下单。";
        }
        catch(Exception ex) when(ex is not OutOfMemoryException){status.Text="订单请求结果尚未确认。请先在官网核对订单，请勿重复生成。";}
    }

    private static bool TryAmount(string value,out decimal total)=>decimal.TryParse(value,System.Globalization.NumberStyles.AllowDecimalPoint,System.Globalization.CultureInfo.InvariantCulture,out total)&&total>0&&total<=100000;
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    private sealed record MonthOption(string Value,string Label){public override string ToString()=>Label;}
    private sealed record RechargeState(int Index,string Name,string SelectedValue,string Account,MonthOption[] Options,string Amount,string Rate);
    private sealed record PaymentState(string Order,string PaymentOrder,string Amount,string Qr,bool Expired);
}
