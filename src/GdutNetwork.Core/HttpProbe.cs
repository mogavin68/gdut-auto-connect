using System.Diagnostics;
using System.Net;
using System.Text;

namespace GdutNetwork.Core;

public static class HttpProbe
{
    private const int MaximumBodyBytes = 256 * 1024;

    public static async Task<ProbeResult> RunAsync(HttpClient client, ProbeSpec spec, Func<RouteObservation?> route,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, spec.Endpoint);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (code is >= 300 and < 400)
            {
                string? location = response.Headers.Location is { } value
                    && Uri.TryCreate(spec.Endpoint, value, out var target) ? SafeEndpoint.Display(target) : null;
                return Result(ProbeOutcome.Redirect, code, redirect: location);
            }
            if (!response.IsSuccessStatusCode) return Result(ProbeOutcome.UnexpectedResponse, code);
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] block = new byte[8192];
            while (buffer.Length <= MaximumBodyBytes)
            {
                int read = await stream.ReadAsync(block.AsMemory(0, Math.Min(block.Length, MaximumBodyBytes + 1 - (int)buffer.Length)), budget.Token).ConfigureAwait(false);
                if (read == 0) break;
                buffer.Write(block, 0, read);
                if (buffer.Length > MaximumBodyBytes) return Result(ProbeOutcome.UnexpectedResponse, code, error: "ResponseTooLarge");
            }
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(response.Content.Headers.ContentType?.CharSet?.Trim('"') ?? "utf-8"); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
            string body = encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var evidence = PortalClassifier.Classify(body);
            if (spec.IsPortal) return Result(ProbeOutcome.PortalResponse, code, evidence);
            bool matches = spec.ExactContent is not null ? body.Trim() == spec.ExactContent
                : spec.ContentPrefix is not null && body.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith(spec.ContentPrefix, StringComparison.OrdinalIgnoreCase);
            return Result(matches ? ProbeOutcome.ExpectedResponse : ProbeOutcome.UnexpectedResponse, code,
                evidence.State != PortalState.Unknown ? evidence : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Result(ProbeOutcome.Timeout, error: "Timeout"); }
        catch (HttpRequestException ex) { return Result(ProbeOutcome.Failed, error: ex.HttpRequestError.ToString()); }
        catch (IOException) { return Result(ProbeOutcome.Failed, error: "ReadError"); }

        ProbeResult Result(ProbeOutcome outcome, int? status = null, PortalEvidence? evidence = null, string? redirect = null, string? error = null)
            => new(spec.Name, SafeEndpoint.Display(spec.Endpoint), outcome, status, evidence, redirect, route(), error, timer.ElapsedMilliseconds);
    }
}
