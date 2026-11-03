namespace GdutNetwork.App;

// Scripts read only explicitly labelled fields and the official controls. No host objects or background order submission.
internal static class SelfServiceScripts
{
    internal const string ReadRecharge = """
    (() => {
      if(location.origin !== 'https://selfmanager.gdut.edu.cn' || !location.pathname.startsWith('/service/selfRechargeAction')) return null;
      const clean = s => (s||'').replace(/\s/g,'').replace(/[：:]$/,'');
      const rows = [...document.querySelectorAll('.form-group,tr')];
      const find = name => rows.filter(r => [...r.querySelectorAll('label,th,td')].some(e=>clean(e.innerText)===name));
      const monthRows=find('充值月数');
      const selects=monthRows.flatMap(r=>[...r.querySelectorAll('select')]);
      const all=[...document.querySelectorAll('select')];
      const amountRows=find('总金额');
      const amounts=amountRows.flatMap(r=>[...r.querySelectorAll('input')]);
      const monthly=find('月租');
      const buttons=[...document.querySelectorAll('button,input[type=submit],input[type=button],a.btn')].filter(e=>clean(e.innerText||e.value)==='充值' && !e.disabled);
      if(selects.length!==1 || amounts.length!==1 || buttons.length!==1) return null;
      const select=selects[0];
      const accountRows=find('充值账号');
      const account=accountRows.length===1?accountRows[0].querySelector('label').nextElementSibling.innerText.trim():'';
      return {index:all.indexOf(select), name:select.name, selectedValue:select.value, account, options:[...select.options].filter(o=>/^[1-9][0-9]*$/.test(o.value) && !o.disabled).map(o=>({value:o.value,label:o.text})), amount:amounts[0].value, rate:monthly.length===1?monthly[0].innerText:''};
    })()
    """;

    internal static string SelectMonth(int index, string name, string value) => """
    (()=>{
      if(location.origin!=='https://selfmanager.gdut.edu.cn'||!location.pathname.startsWith('/service/selfRechargeAction'))return false;
      const config=CONFIG;
      const e=[...document.querySelectorAll('select')][config.index];
      if(!e||e.name!==config.name||![...e.options].some(o=>o.value===config.value&&!o.disabled))return false;
      e.value=config.value;e.dispatchEvent(new Event('change',{bubbles:true}));return true;
    })()
    """.Replace("CONFIG", System.Text.Json.JsonSerializer.Serialize(new { index, name, value }));

    internal static string SubmitRecharge(string value, string account, decimal total) => """
    (()=>{
      if(location.origin!=='https://selfmanager.gdut.edu.cn'||!location.pathname.startsWith('/service/selfRechargeAction'))return false;
      const clean=s=>(s||'').replace(/\s/g,'');
      const config=CONFIG;
      const f=document.querySelector('form#form1');
      if(!f||new URL(f.action).origin!==location.origin||new URL(f.action).pathname!=='/service/selfRechargeMoney'||f.method.toLowerCase()!=='post')return false;
      const month=f.querySelector('select[name=rechargeMonth]'), amount=f.querySelector('input[name=totalMoney]');
      const accountLabel=[...f.querySelectorAll('label')].filter(e=>e.innerText.replace(/\s/g,'')==='充值账号');
      if(!month||month.value!==config.value||!amount||Number(amount.value)!==config.total||accountLabel.length!==1||accountLabel[0].nextElementSibling.innerText.trim()!==config.account)return false;
      const buttons=[...document.querySelectorAll('button,input[type=submit],input[type=button],a.btn')].filter(e=>clean(e.innerText||e.value)==='充值'&&!e.disabled);
      if(buttons.length!==1)return false;
      buttons[0].click();return true;
    })()
    """.Replace("CONFIG",System.Text.Json.JsonSerializer.Serialize(new { value, account, total }));

    internal const string ReadPayment = """
    (()=>{
      if(location.origin!=='https://tyzf.gdut.edu.cn'||!/^\/zhifu\/(payAccept|PayMain)\.aspx$/i.test(location.pathname))return null;
      const text=id=>(document.getElementById(id)?.innerText||'').trim();
      const order=text('lb_ddh'),paymentOrder=text('lb_wxddh'),amount=text('lb_amount');
      const e=document.getElementById('PayImg');
      const candidates=e&&e.complete&&e.naturalWidth>=100?[e]:[];
      let qr='';
      if(candidates.length===1)try {const e=candidates[0];if(e.tagName==='CANVAS')qr=e.toDataURL('image/png');else{const c=document.createElement('canvas');c.width=e.naturalWidth;c.height=e.naturalHeight;c.getContext('2d').drawImage(e,0,0);qr=c.toDataURL('image/png');}}catch{}
      const expired=document.getElementById('div_layer');
      return {order,paymentOrder,amount,qr,expired:!!expired&&getComputedStyle(expired).display!=='none'};
    })()
    """;

    internal static string RequestQr(decimal total) => """
    (()=>{
      if(location.origin!=='https://tyzf.gdut.edu.cn'||!/^\/zhifu\/(payAccept|PayMain)\.aspx$/i.test(location.pathname))return false;
      const amount=document.getElementById('lb_amount'),button=document.getElementById('btn_wx');
      if(!amount||Number(amount.innerText)!==EXPECTED_TOTAL||!button||button.disabled||button.value!=='确认支付'||button.getBoundingClientRect().height===0)return false;
      button.click();return true;
    })()
    """.Replace("EXPECTED_TOTAL",total.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
