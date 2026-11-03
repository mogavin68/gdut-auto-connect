using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GdutNetwork.Core;

namespace GdutNetwork.Windows;

public sealed class DiagnosticService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    public async Task<DiagnosticReport?> RunAsync(CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var started = DateTimeOffset.Now;
            var timer = Stopwatch.StartNew();
            NetworkSnapshot snapshot = await Task.Run(() => new WindowsNetworkReader().Read(), cancellationToken).ConfigureAwait(false);
            var results = await Task.WhenAll(ProbeSpec.Defaults.Select(spec => ProbeAsync(spec, snapshot.Target?.InterfaceIndex, cancellationToken))).ConfigureAwait(false);
            // A network change while probing invalidates conclusions tied to the previous connection.
            var after = await Task.Run(() => new WindowsNetworkReader().Read(), cancellationToken).ConfigureAwait(false);
            bool changed = !snapshot.Wifi.SequenceEqual(after.Wifi);
            var assessment = changed
                ? new Assessment(NetworkState.Unknown, "检测过程中网络发生变化", "本轮结果保留供诊断，下轮重新判断；不执行恢复操作。")
                : NetworkEvaluator.Evaluate(snapshot, results);
            return new(started, timer.ElapsedMilliseconds, "ReadOnly", snapshot, results, assessment, changed);
        }
        finally { gate.Release(); }
    }

    private static async Task<ProbeResult> ProbeAsync(ProbeSpec spec, int? targetIndex, CancellationToken cancellationToken)
    {
        RouteObservation? observation = null;
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
                Exception? last = null;
                foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        observation = WindowsNetworkReader.GetRoute(address, targetIndex, (socket.LocalEndPoint as IPEndPoint)?.Address);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (OperationCanceledException) { socket.Dispose(); throw; }
                    catch (SocketException ex) { socket.Dispose(); last = ex; }
                }
                throw new HttpRequestException("Connection failed", last);
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return await HttpProbe.RunAsync(client, spec, () => observation, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
    }
}
