using System.Globalization;
using System.Text;
using GalactiLog.Core.Integrations;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.Services.Telemetry;

/// <summary>
/// The daily sender. Checks once a minute after start, then hourly, and posts at most one report
/// per 24 hours, only from an installed build with telemetry on and an install id. Every failure
/// is logged at Debug and retried on the next tick; nothing escapes the loop.
/// </summary>
public sealed class TelemetryService : BackgroundService
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan MinimumGap = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly BuildInfo _buildInfo;
    private readonly SettingsStore _settings;
    private readonly TelemetryReportBuilder _builder;
    private readonly IntegrationHttpClient _http;
    private readonly ResolverCounters _counters;
    private readonly ErrorTemplateCounter _errors;
    private readonly string _dataRoot;
    private readonly ILogger<TelemetryService> _logger;

    public TelemetryService(
        BuildInfo buildInfo,
        SettingsStore settings,
        TelemetryReportBuilder builder,
        IntegrationHttpClient http,
        ResolverCounters counters,
        ErrorTemplateCounter errors,
        string dataRoot,
        ILogger<TelemetryService> logger)
    {
        _buildInfo = buildInfo;
        _settings = settings;
        _builder = builder;
        _http = http;
        _counters = counters;
        _errors = errors;
        _dataRoot = dataRoot;
        _logger = logger;
    }

    /// <summary>Observable so HostDisposalTests can confirm the container disposed it, which is
    /// what cancels the loop.</summary>
    public bool IsDisposed { get; private set; }

    public override void Dispose()
    {
        IsDisposed = true;
        base.Dispose();
    }

    /// <summary>The send decision: installed, enabled, has an id, and no successful send in the
    /// last 24 hours. An unparsable or future last-sent value counts as never sent.</summary>
    public static bool ShouldSend(GeneralSettings g, bool isInstalled, DateTimeOffset now)
    {
        if (!isInstalled || !g.TelemetryEnabled || g.TelemetryId is null)
        {
            return false;
        }

        return !DateTimeOffset.TryParse(
                g.TelemetryLastSent, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var last)
            || now - last >= MinimumGap
            // A stamp in the future means the clock went back; without this it would stall sends.
            || last > now;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host or container shutting down.
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!ShouldSend(_settings.GetGeneral(), _buildInfo.IsInstalled, DateTimeOffset.UtcNow))
            {
                return;
            }

            var json = _builder.Build(includeCrash: true, indented: false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, TelemetryEndpoints.ReportUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.UserAgent.TryParseAdd("GalactiLog/" + _buildInfo.Version);

            using var response = await _http.Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Telemetry report refused with status {Status}", (int)response.StatusCode);
                return;
            }

            _settings.MutateGeneral(general => general with
            {
                TelemetryLastSent = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            });
            // ponytail: counts recorded between Build and these resets are dropped; at most a
            // second of counts once a day.
            CrashMarker.Delete(_dataRoot);
            _errors.Reset();
            _counters.Reset();
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Telemetry report not sent");
        }
        catch (Exception)
        {
            // Shutting down mid-send; the loop exits on its own.
        }
    }
}
