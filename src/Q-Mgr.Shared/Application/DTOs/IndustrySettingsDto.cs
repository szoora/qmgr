using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

/// <summary>
/// Single source of truth for the org's industry selection and its per-industry kiosk feature
/// toggles (Voice Announcements, SMS Notifications, etc.), shared between Q-Mgr.API and
/// Q-Mgr.Web like OrganizationBrandingDto. IndustryType persists on Organization.IndustryType;
/// Features persists as JSON in Organization.Settings (a previously-unused generic settings
/// blob) under the "IndustryFeatures" key.
/// </summary>
public record IndustrySettingsDto
{
    public IndustryType IndustryType { get; set; } = IndustryType.Service;
    public Dictionary<string, bool> Features { get; set; } = new();
}
