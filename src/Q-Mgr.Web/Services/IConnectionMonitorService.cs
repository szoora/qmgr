namespace QMgr.Web.Services;

public enum ConnectionStatus
{
    Connected,
    Connecting,
    Disconnected,
    Reconnecting
}

public interface IConnectionMonitorService : IDisposable
{
    ConnectionStatus Status { get; }
    string StatusMessage { get; }
    DateTime? LastConnectedAt { get; }
    int ReconnectAttempts { get; }

    event Action? OnStatusChanged;

    Task StartMonitoringAsync();
    Task StopMonitoringAsync();
    Task<bool> CheckConnectionAsync();
}

public class ConnectionMonitorService : IConnectionMonitorService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ConnectionMonitorService> _logger;
    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private string _statusMessage = "Checking connection...";
    private DateTime? _lastConnectedAt;
    private int _reconnectAttempts;
    private CancellationTokenSource? _monitoringCts;
    private bool _isMonitoring;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    public ConnectionStatus Status => _status;
    public string StatusMessage => _statusMessage;
    public DateTime? LastConnectedAt => _lastConnectedAt;
    public int ReconnectAttempts => _reconnectAttempts;

    public event Action? OnStatusChanged;

    public ConnectionMonitorService(HttpClient httpClient, ILogger<ConnectionMonitorService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task StartMonitoringAsync()
    {
        if (_isMonitoring) return;

        _isMonitoring = true;
        _monitoringCts = new CancellationTokenSource();

        // Initial check
        await CheckConnectionAsync();

        // Start background monitoring
        _ = MonitorConnectionAsync(_monitoringCts.Token);
    }

    public Task StopMonitoringAsync()
    {
        _isMonitoring = false;
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
        _monitoringCts = null;
        return Task.CompletedTask;
    }

    public async Task<bool> CheckConnectionAsync()
    {
        if (!await _checkLock.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            return _status == ConnectionStatus.Connected;
        }

        try
        {
            var previousStatus = _status;

            if (_status == ConnectionStatus.Disconnected)
            {
                SetStatus(ConnectionStatus.Connecting, "Connecting to server...");
            }
            else if (_status == ConnectionStatus.Reconnecting)
            {
                SetStatus(ConnectionStatus.Reconnecting, $"Reconnecting... (Attempt {_reconnectAttempts})");
            }

            try
            {
                // Use a health check endpoint or simple API call
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = await _httpClient.GetAsync("api/v1/health", cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    _lastConnectedAt = DateTime.Now;
                    _reconnectAttempts = 0;
                    SetStatus(ConnectionStatus.Connected, "Connected to server");
                    return true;
                }
                else
                {
                    HandleDisconnection("Server returned an error");
                    return false;
                }
            }
            catch (TaskCanceledException)
            {
                HandleDisconnection("Connection timed out");
                return false;
            }
            catch (HttpRequestException ex)
            {
                HandleDisconnection($"Cannot reach server: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection check failed");
                HandleDisconnection("Connection error occurred");
                return false;
            }
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private void HandleDisconnection(string message)
    {
        _reconnectAttempts++;

        if (_reconnectAttempts <= 3)
        {
            SetStatus(ConnectionStatus.Reconnecting, $"{message}. Retrying...");
        }
        else
        {
            SetStatus(ConnectionStatus.Disconnected, message);
        }
    }

    private async Task MonitorConnectionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _isMonitoring)
        {
            try
            {
                // Check more frequently when disconnected
                var delay = _status == ConnectionStatus.Connected
                    ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromSeconds(5);

                await Task.Delay(delay, cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                {
                    await CheckConnectionAsync();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in connection monitoring loop");
            }
        }
    }

    private void SetStatus(ConnectionStatus status, string message)
    {
        if (_status != status || _statusMessage != message)
        {
            _status = status;
            _statusMessage = message;

            _logger.LogInformation("Connection status changed: {Status} - {Message}", status, message);

            try
            {
                OnStatusChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invoking OnStatusChanged");
            }
        }
    }

    public void Dispose()
    {
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
        _checkLock.Dispose();
    }
}
