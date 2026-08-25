using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Integration;

public class ApiLog : BaseEntity
{
    public Guid? ApiClientId { get; set; }
    public string? Endpoint { get; set; }
    public string? Method { get; set; }
    public string? RequestBody { get; set; } // JSON
    public int? ResponseStatus { get; set; }
    public int? ResponseTimeMs { get; set; }
    public string? IpAddress { get; set; }
    public string? ErrorMessage { get; set; }

    // Navigation properties
    public virtual ApiClient? ApiClient { get; set; }
}
