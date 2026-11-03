using System.Globalization;
using System.Net;
using System.Text.Json;

namespace GdutNetwork.Windows;

public sealed record RechargeHistoryPeriod(DateOnly Start, DateOnly End)
{
    public static DateOnly ChinaToday => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);
    public static RechargeHistoryPeriod LastSixMonths(DateOnly today) => new(today.AddMonths(-6), today);

    public IReadOnlyList<RechargeHistoryPeriod> Split()
    {
        if (Start > End || End.DayNumber - Start.DayNumber > 366) throw new ArgumentOutOfRangeException(nameof(Start));
        var result = new List<RechargeHistoryPeriod>();
        for (var start = Start; start <= End;)
        {
            var end = start.AddDays(Math.Min(59, End.DayNumber - start.DayNumber));
            result.Add(new(start, end));
            if (end == End) break;
            start = end.AddDays(1);
        }
        return result;
    }
}

public sealed record RechargeHistoryEntry(DateTimeOffset OccurredAt, decimal Amount, string PaymentType, string Terminal, string Remark)
{
    public override string ToString() => "Recharge entry [private content]";
}
public sealed record RechargeHistoryPage(int Total, IReadOnlyList<RechargeHistoryEntry> Entries);
public sealed record RechargeHistorySnapshot(RechargeHistoryPeriod Period, IReadOnlyList<RechargeHistoryEntry> Entries, DateTimeOffset CheckedAt)
{
    public override string ToString() => "Recharge history [private content]";
}

public sealed class RechargeHistoryClient : IDisposable
{
    private const int PageSize = 100;
    private const int MaximumRows = 10000;
    private readonly HttpClient client;

    public RechargeHistoryClient(CookieContainer cookies, HttpMessageHandler? testHandler = null)
    {
        client = new(testHandler ?? new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All });
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.Referrer = new Uri("https://selfmanager.gdut.edu.cn/bill/payMent");
    }

    public static Uri QueryUri(RechargeHistoryPeriod period, int pageNumber)
    {
        if (period.Start > period.End || period.End.DayNumber - period.Start.DayNumber >= 60 || pageNumber < 1 || pageNumber > MaximumRows / PageSize)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        string start = period.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string end = period.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new($"https://selfmanager.gdut.edu.cn/bill/getPayMent?pageSize={PageSize}&pageNumber={pageNumber}&sortName=0&sortOrder=DESC&startTime={start}&endTime={end}");
    }

    public async Task<RechargeHistorySnapshot> LoadAsync(DateOnly today, CancellationToken token)
    {
        var period = RechargeHistoryPeriod.LastSixMonths(today);
        var entries = new List<RechargeHistoryEntry>();
        foreach (var slice in period.Split())
        {
            int? expected = null;
            int received = 0;
            IReadOnlyList<RechargeHistoryEntry>? previous = null;
            for (int pageNumber = 1; ; pageNumber++)
            {
                token.ThrowIfCancellationRequested();
                using var response = await client.GetAsync(QueryUri(slice, pageNumber), HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode is >= 300 and < 400 || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new SelfServiceException("官网会话已失效，请在官方页面登录后刷新充值记录。");
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(2_000_000, token);
                var page = ParsePage(await response.Content.ReadAsStringAsync(token));
                expected ??= page.Total;
                if (expected != page.Total) throw new SelfServiceException("查询期间充值记录发生变化，请刷新重试。");
                if (page.Total > MaximumRows || entries.Count + page.Entries.Count > MaximumRows)
                    throw new SelfServiceException("充值记录较多，未能完整读取，请在官网核对。");
                if (page.Entries.Count > PageSize || received + page.Entries.Count > expected || page.Entries.Count == 0 && received < expected)
                    throw new SelfServiceException("官网充值明细分页未返回完整数据，请刷新或在官网核对。");
                if (previous is not null && page.Entries.Count > 0 && page.Entries.SequenceEqual(previous))
                    throw new SelfServiceException("官网充值明细分页未能推进，请在官网核对。");
                foreach (var entry in page.Entries)
                {
                    var day = DateOnly.FromDateTime(entry.OccurredAt.DateTime);
                    if (day < slice.Start || day > slice.End) throw new SelfServiceException("官网返回了日期范围外的记录，请在官网核对。");
                    entries.Add(entry);
                }
                received += page.Entries.Count;
                if (received == expected) break;
                previous = page.Entries;
            }
        }
        // Identical transactions can be legitimate: preserve duplicates, and avoid overlapping date windows instead.
        return new(period, entries.OrderByDescending(x => x.OccurredAt).ToArray(), DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)));
    }

    public static RechargeHistoryPage ParsePage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array || !root.TryGetProperty("total", out var totalValue))
                throw new FormatException();
            int total;
            if (totalValue.ValueKind == JsonValueKind.Null && rows.GetArrayLength() == 0) total = 0; // Official empty response.
            else if (!totalValue.TryGetInt32(out total) || total < 0) throw new FormatException();
            var entries = new List<RechargeHistoryEntry>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 5 || !row[0].TryGetInt64(out var milliseconds) || !row[2].TryGetDecimal(out var amount)) throw new FormatException();
                var time = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToOffset(TimeSpan.FromHours(8));
                entries.Add(new(time, amount, Text(row[1]), Text(row[3]), Text(row[4])));
            }
            if (entries.Count > total) throw new FormatException();
            return new(total, entries);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new SelfServiceException("官网充值明细格式未能确认，请在官网核对；当前未显示不完整记录。"); }
    }

    private static string Text(JsonElement value)
    {
        string text = value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number => value.GetRawText(), JsonValueKind.Null => "", _ => throw new FormatException() };
        return text.Length <= 8192 ? text : throw new FormatException();
    }
    public void Dispose() => client.Dispose();
}
