using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Web.Services;

/// <summary>
/// Client for the appointments API. Split into two halves on purpose:
/// <list type="bullet">
/// <item>the staff methods hit <c>api/v1/branches/{branchId}/appointments</c> and ride the
/// circuit's authenticated HttpClient;</item>
/// <item>the <c>Public*</c> methods hit the anonymous <c>.../appointments/public</c> sub-path and
/// work with or without a signed-in user, which is what the public <c>/book/{branchId}</c> page
/// needs (the shared HttpClient simply sends no bearer token when nobody is signed in).</item>
/// </list>
/// </summary>
public interface IAppointmentApiService
{
    /// <summary>Appointments whose scheduled time falls in [from, to). Both are UTC.</summary>
    Task<List<AppointmentDto>> GetAppointmentsAsync(Guid branchId, DateTime from, DateTime to, AppointmentStatus? status = null, Guid? serviceTypeId = null);

    Task<AppointmentDto?> GetAppointmentAsync(Guid branchId, Guid appointmentId);

    /// <summary>Bookable slots for one service on one day (yyyy-MM-dd, the branch's local calendar).</summary>
    Task<AppointmentAvailabilityDto?> GetAvailabilityAsync(Guid branchId, Guid serviceTypeId, DateOnly date);

    /// <summary>Throws <see cref="InvalidOperationException"/> carrying the API's own message on failure, so the caller can toast it.</summary>
    Task<AppointmentDto> CreateAppointmentAsync(Guid branchId, CreateAppointmentRequest request);

    Task<AppointmentDto> RescheduleAppointmentAsync(Guid branchId, Guid appointmentId, RescheduleAppointmentRequest request);
    Task<AppointmentDto> CancelAppointmentAsync(Guid branchId, Guid appointmentId, string? reason);
    Task<AppointmentDto> MarkNoShowAsync(Guid branchId, Guid appointmentId);

    /// <summary>Converts the booking into a live queue token. Returns the updated booking plus the issued ticket.</summary>
    Task<AppointmentCheckInResponse> CheckInAsync(Guid branchId, Guid appointmentId);

    // ---- Anonymous (public booking page) --------------------------------------------------
    Task<List<BookableServiceTypeDto>> GetPublicServiceTypesAsync(Guid branchId);
    Task<AppointmentAvailabilityDto?> GetPublicAvailabilityAsync(Guid branchId, Guid serviceTypeId, DateOnly date);
    Task<PublicBookingResult> BookPublicAsync(Guid branchId, PublicBookAppointmentRequest request);
}

/// <summary>Mirrors the API's <c>AppointmentCheckInResult</c> wire shape.</summary>
public record AppointmentCheckInResponse
{
    public AppointmentDto? Appointment { get; init; }
    public Guid? TokenId { get; init; }
    public string? TokenDisplayNumber { get; init; }
    public bool AlreadyCheckedIn { get; init; }
}

/// <summary>
/// Booking outcome for the public page. Deliberately not an exception-throwing API: the page is
/// used by members of the public on phones, and "that slot has just gone" is an ordinary answer
/// to render inline, not an error to swallow.
/// </summary>
public record PublicBookingResult(bool Success, PublicAppointmentConfirmationDto? Confirmation, string? ErrorCode, string? Message)
{
    public static PublicBookingResult Ok(PublicAppointmentConfirmationDto confirmation) => new(true, confirmation, null, null);
    public static PublicBookingResult Fail(string code, string message) => new(false, null, code, message);
}

public class AppointmentApiService : IAppointmentApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AppointmentApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public AppointmentApiService(HttpClient httpClient, ILogger<AppointmentApiService> logger, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    private static string Base(Guid branchId) => $"api/v1/branches/{branchId}/appointments";

    public async Task<List<AppointmentDto>> GetAppointmentsAsync(
        Guid branchId, DateTime from, DateTime to, AppointmentStatus? status = null, Guid? serviceTypeId = null)
    {
        try
        {
            var url = $"{Base(branchId)}?from={Uri.EscapeDataString(from.ToUniversalTime().ToString("o"))}&to={Uri.EscapeDataString(to.ToUniversalTime().ToString("o"))}";
            if (status.HasValue) url += $"&status={status.Value}";
            if (serviceTypeId.HasValue) url += $"&serviceTypeId={serviceTypeId.Value}";

            return await _httpClient.GetFromJsonAsync<List<AppointmentDto>>(url, _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load appointments for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<AppointmentDto?> GetAppointmentAsync(Guid branchId, Guid appointmentId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<AppointmentDto>($"{Base(branchId)}/{appointmentId}", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load appointment {AppointmentId}", appointmentId);
            return null;
        }
    }

    public async Task<AppointmentAvailabilityDto?> GetAvailabilityAsync(Guid branchId, Guid serviceTypeId, DateOnly date)
    {
        try
        {
            var url = $"{Base(branchId)}/availability?serviceTypeId={serviceTypeId}&date={date:yyyy-MM-dd}";
            return await _httpClient.GetFromJsonAsync<AppointmentAvailabilityDto>(url, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load availability for branch {BranchId}", branchId);
            return null;
        }
    }

    public async Task<AppointmentDto> CreateAppointmentAsync(Guid branchId, CreateAppointmentRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync(Base(branchId), request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<AppointmentDto>(_jsonOptions))!;
    }

    public async Task<AppointmentDto> RescheduleAppointmentAsync(Guid branchId, Guid appointmentId, RescheduleAppointmentRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"{Base(branchId)}/{appointmentId}/reschedule", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<AppointmentDto>(_jsonOptions))!;
    }

    public async Task<AppointmentDto> CancelAppointmentAsync(Guid branchId, Guid appointmentId, string? reason)
    {
        var response = await _httpClient.PostAsJsonAsync($"{Base(branchId)}/{appointmentId}/cancel",
            new CancelAppointmentRequest { Reason = reason }, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<AppointmentDto>(_jsonOptions))!;
    }

    public async Task<AppointmentDto> MarkNoShowAsync(Guid branchId, Guid appointmentId)
    {
        var response = await _httpClient.PostAsync($"{Base(branchId)}/{appointmentId}/no-show", null);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<AppointmentDto>(_jsonOptions))!;
    }

    public async Task<AppointmentCheckInResponse> CheckInAsync(Guid branchId, Guid appointmentId)
    {
        var response = await _httpClient.PostAsync($"{Base(branchId)}/{appointmentId}/check-in", null);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<AppointmentCheckInResponse>(_jsonOptions))!;
    }

    // ---- Anonymous ------------------------------------------------------------------------

    public async Task<List<BookableServiceTypeDto>> GetPublicServiceTypesAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<BookableServiceTypeDto>>(
                $"{Base(branchId)}/public/service-types", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load bookable services for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<AppointmentAvailabilityDto?> GetPublicAvailabilityAsync(Guid branchId, Guid serviceTypeId, DateOnly date)
    {
        try
        {
            var url = $"{Base(branchId)}/public/availability?serviceTypeId={serviceTypeId}&date={date:yyyy-MM-dd}";
            return await _httpClient.GetFromJsonAsync<AppointmentAvailabilityDto>(url, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load public availability for branch {BranchId}", branchId);
            return null;
        }
    }

    public async Task<PublicBookingResult> BookPublicAsync(Guid branchId, PublicBookAppointmentRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{Base(branchId)}/public", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
            {
                var confirmation = await response.Content.ReadFromJsonAsync<PublicAppointmentConfirmationDto>(_jsonOptions);
                return confirmation == null
                    ? PublicBookingResult.Fail("EMPTY_RESPONSE", "The booking could not be confirmed. Please try again.")
                    : PublicBookingResult.Ok(confirmation);
            }

            var code = response.StatusCode switch
            {
                HttpStatusCode.Conflict => "SLOT_FULL",
                HttpStatusCode.TooManyRequests => "RATE_LIMITED",
                HttpStatusCode.NotFound => "UNAVAILABLE",
                _ => "BOOKING_FAILED"
            };
            return PublicBookingResult.Fail(code, await ApiErrorService.GetErrorMessageAsync(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Public booking failed for branch {BranchId}", branchId);
            return PublicBookingResult.Fail("NETWORK", "We couldn't reach the booking service. Please check your connection and try again.");
        }
    }
}
