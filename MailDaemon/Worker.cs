using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MailDaemon.Services;  
using MailDaemon.Settings;
using Microsoft.Extensions.Options;

namespace MailDaemon;

public class DaemonWorker : BackgroundService
{
    private readonly ILogger<DaemonWorker> _logger;
    private readonly DaemonService _service;
    private readonly DaemonSettings _settings;

    public DaemonWorker(
        ILogger<DaemonWorker> logger,
        DaemonService service,
        IOptions<DaemonSettings> settings)
    {
        _logger = logger;
        _service = service;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MailDaemon iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _service.ProcesarTareas();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en ciclo principal del daemon");
            }

            await Task.Delay(_settings.IntervalSeconds * 1000, stoppingToken);
        }

        _logger.LogInformation("MailDaemon detenido.");
    }
}
