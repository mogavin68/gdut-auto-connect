namespace GdutNetwork.Core;

public static class NetworkEvaluator
{
    public static Assessment Evaluate(NetworkSnapshot snapshot, IReadOnlyList<ProbeResult> probes)
    {
        var external = probes.Where(p => p.Name is "Microsoft 联网探测" or "百度 HTTPS 探测").ToArray();
        var success = external.Where(p => p.Outcome == ProbeOutcome.ExpectedResponse).ToArray();
        var portal = probes.Where(p => p.Name is "主要认证入口" or "辅助认证入口").ToArray();
        bool captive = portal.Any(p => p.Portal?.State == PortalState.CaptchaRequired);
        bool session = portal.Any(p => p.Portal?.State == PortalState.SessionIndicated);
        bool login = portal.Any(p => p.Portal?.State == PortalState.LoginRequired);

        if (success.Length > 0)
        {
            if (snapshot.Target is null)
                return new(NetworkState.InternetReachable, "当前网络可访问外网探测目标", "尚未证明 gdut 校园网的连通性；保持现有连接。单个目标可达不代表所有网站均可访问。");
            if (success.Any(p => p.Route?.MatchesTarget != true))
                return new(NetworkState.InternetRouteUncertain, "外网探测成功，校园网路径待确认", "探测经过其他网卡或路径未能确认，不能据此认定 gdut 本身已恢复。本版本不改变路由。");
            return new(NetworkState.InternetReachable, "gdut 路径的外网探测成功", "当前探测目标可达；不重复认证。此结论仅覆盖本轮探测目标。");
        }
        if (snapshot.Warning is not null || snapshot.Wifi.Any(w => w.ErrorCode is not null))
            return new(NetworkState.Unknown, "网络状态尚不能可靠判断", "Wi-Fi 信息读取失败或权限受限；请查看诊断详情。本版本不会据此切换网络或提交密码。");
        if (snapshot.Wifi.Count == 0)
            return new(NetworkState.WifiUnavailable, "未读取到无线网卡", "可能未安装、被禁用或无线服务不可用；本版本不修改网卡状态。");
        if (snapshot.Target is null)
            return snapshot.Wifi.Any(w => w.State == "Connected")
                ? new(NetworkState.OtherWifi, "当前连接的不是 gdut，外网探测未成功", "尚不能证明全网断开。本版本只检测，不切网，也不向当前网络提交学校凭据。")
                : new(NetworkState.WifiDisconnected, "Wi-Fi 当前未连接", "本版本只检测；尚未启用自动重连。");
        if (captive)
            return new(NetworkState.NeedsAttention, "认证页面需要人工验证", "检测到验证码相关表单；后续自动登录功能必须暂停并提醒。");
        if (session && login)
            return new(NetworkState.Unknown, "两个认证入口的状态不一致", "一个入口显示会话，另一个显示登录表单；应复查，不重复提交凭据。");
        if (session)
            return new(NetworkState.SessionWithoutInternet, "认证页显示已登录，外网探测未成功", "保持会话，不自动注销；探测目标故障、代理或校园网出口问题仍需区分。");
        if (login)
            return new(NetworkState.NeedsAuthentication, "发现校园网登录表单", "本版本仅识别表单，不读取或提交账号密码；认证接口仍需核实。");
        return new(NetworkState.Unknown, "已连接 gdut，联网状态待确认", "未取得足够证据区分认证失效、入口故障和探测失败；本版本不执行恢复操作。");
    }
}
