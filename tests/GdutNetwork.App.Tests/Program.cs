using System.Text;
using System.Text.Json;
using GdutNetwork.App;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    private static int failures, count;
    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var host=new Form {ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-20000,-20000),Size=new(900,700)};
        var view=new WebView2 {Dock=DockStyle.Fill};host.Controls.Add(view);
        host.Shown+=async (_,_)=>
        {
            try
            {
                var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(AppContext.BaseDirectory,"synthetic-browser-tests"));
                var options=environment.CreateCoreWebView2ControllerOptions();options.IsInPrivateModeEnabled=true;
                await view.EnsureCoreWebView2Async(environment,options);
                // All requests are intercepted. This suite has no access to the real portal, vault or payment service.
                view.CoreWebView2.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                string fixture=Recharge;
                view.CoreWebView2.WebResourceRequested+=(_,e)=>e.Response=environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(fixture)),200,"OK","Content-Type: text/html; charset=utf-8");
                async Task Navigate(string url)
                {
                    var ready=new TaskCompletionSource<bool>();
                    void Done(object? sender,CoreWebView2NavigationCompletedEventArgs e)=>ready.TrySetResult(e.IsSuccess);
                    view.CoreWebView2.NavigationCompleted+=Done;
                    try {view.CoreWebView2.Navigate(url);if(!await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)))throw new Exception("navigation");}
                    finally {view.CoreWebView2.NavigationCompleted-=Done;}
                }
                async Task Test(string name,Func<Task<bool>> action)
                {
                    count++;
                    try {if(!await action())throw new Exception("Assertion");Console.WriteLine("PASS "+name);}
                    catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex.GetType().Name);}
                }
                await Navigate("https://selfmanager.gdut.edu.cn/service/selfRechargeAction");
                await Test("Native choices exclude the official placeholder",async()=>
                {
                    using var doc=JsonDocument.Parse(await view.ExecuteScriptAsync(SelfServiceScripts.ReadRecharge));
                    return doc.RootElement.GetProperty("options").GetArrayLength()==2;
                });
                await Test("Month selection triggers the official change event and price",async()=>
                {
                    if(await view.ExecuteScriptAsync(SelfServiceScripts.SelectMonth(0,"rechargeMonth","5"))!="true")return false;
                    using var doc=JsonDocument.Parse(await view.ExecuteScriptAsync(SelfServiceScripts.ReadRecharge));
                    return doc.RootElement.GetProperty("amount").GetString()=="150"&&doc.RootElement.GetProperty("selectedValue").GetString()=="5";
                });
                await Test("Changed quotes stop before any order request",async()=>await view.ExecuteScriptAsync(SelfServiceScripts.SubmitRecharge("5","TEST-ACCOUNT",30))=="false");
                await Test("A different recharge account stops before any order request",async()=>await view.ExecuteScriptAsync(SelfServiceScripts.SubmitRecharge("5","DIFFERENT-ACCOUNT",150))=="false");
                await Test("A stale month selection stops before any order request",async()=>await view.ExecuteScriptAsync(SelfServiceScripts.SubmitRecharge("1","TEST-ACCOUNT",150))=="false");
                await Test("Matching month account and quote submit exactly one synthetic order",async()=>
                    await view.ExecuteScriptAsync(SelfServiceScripts.SubmitRecharge("5","TEST-ACCOUNT",150))=="true"&&await view.ExecuteScriptAsync("window.syntheticOrders")=="1");
                await Test("Changed form destination prevents submission",async()=>
                {
                    await view.ExecuteScriptAsync("document.querySelector('form').action='https://evil.test/order'");
                    return await view.ExecuteScriptAsync(SelfServiceScripts.SubmitRecharge("5","TEST-ACCOUNT",150))=="false";
                });
                await Navigate("https://evil.test/service/selfRechargeAction");
                await Test("A lookalike external page cannot receive native orders",async()=>await view.ExecuteScriptAsync(SelfServiceScripts.SubmitRecharge("5","TEST-ACCOUNT",150))=="false"&&await view.ExecuteScriptAsync(SelfServiceScripts.ReadRecharge)=="null");
                fixture=Payment;
                await Navigate("https://tyzf.gdut.edu.cn/zhifu/PayMain.aspx");
                await Test("Payment extraction preserves both official order identifiers",async()=>
                {
                    using var doc=JsonDocument.Parse(await view.ExecuteScriptAsync(SelfServiceScripts.ReadPayment));
                    return doc.RootElement.GetProperty("order").GetString()=="TEST-ORDER"&&doc.RootElement.GetProperty("paymentOrder").GetString()=="TEST-WX-ORDER";
                });
                await Test("QR generation cannot use a mismatched payment amount",async()=>await view.ExecuteScriptAsync(SelfServiceScripts.RequestQr(150))=="false"&&await view.ExecuteScriptAsync("window.syntheticQrRequests")=="0");
                await Test("Matching payment amount requests the official QR without marking payment complete",async()=>await view.ExecuteScriptAsync(SelfServiceScripts.RequestQr(30))=="true"&&await view.ExecuteScriptAsync("window.syntheticQrRequests")=="1");
                await Test("Expired official QR is reported for removal from native UI",async()=>
                {
                    await view.ExecuteScriptAsync("document.getElementById('div_layer').style.display='block'");
                    using var doc=JsonDocument.Parse(await view.ExecuteScriptAsync(SelfServiceScripts.ReadPayment));
                    return doc.RootElement.GetProperty("expired").GetBoolean();
                });
                await Navigate("https://selfmanager.gdut.edu.cn/zhifu/PayMain.aspx");
                await Test("Payment fields are accepted only on the verified payment origin",async()=>await view.ExecuteScriptAsync(SelfServiceScripts.ReadPayment)=="null"&&await view.ExecuteScriptAsync(SelfServiceScripts.RequestQr(30))=="false");
            }
            catch(Exception ex){failures++;Console.WriteLine("SETUP FAIL "+ex.GetType().Name);}
            finally{Console.WriteLine($"{count-failures}/{count} passed");host.Close();}
        };
        Application.Run(host);return failures==0?0:1;
    }
    private const string Recharge="""
    <!doctype html><form id="form1" action="/service/selfRechargeMoney" method="post">
      <div class="form-group"><label>月租</label><div>30/元</div></div>
      <div class="form-group"><label>充值账号</label><div>TEST-ACCOUNT</div></div>
      <div class="form-group"><label>充值月数</label><div><select name="rechargeMonth"><option value="0"></option><option value="1">1</option><option value="5">5</option></select></div></div>
      <div class="form-group"><label>总金额</label><div><input name="totalMoney" readonly></div></div><button type="submit">充值</button>
    </form><script>window.syntheticOrders=0;document.querySelector('select').onchange=function(){document.querySelector('input').value=Number(this.value)*30};document.querySelector('form').onsubmit=function(e){e.preventDefault();window.syntheticOrders++};</script>
    """;
    private const string Payment="""
    <!doctype html><span id="lb_ddh">TEST-ORDER</span><span id="lb_wxddh">TEST-WX-ORDER</span><span id="lb_amount">30.00</span>
    <input type="button" id="btn_wx" value="确认支付" onclick="window.syntheticQrRequests++"><div id="div_layer" style="display:none"></div>
    <script>window.syntheticQrRequests=0;</script>
    """;
}
