namespace QMgr.Domain.Enums;

/// <summary>
/// Defines the industry type for the organization, which affects kiosk customization and UI theming.
/// </summary>
public enum IndustryType
{
    /// <summary>
    /// General purpose queue management (default)
    /// </summary>
    General = 0,

    /// <summary>
    /// Hospital/Healthcare facility - Medical services, patient queues
    /// </summary>
    Hospital = 1,

    /// <summary>
    /// Bank/Financial institution - Banking services, financial transactions
    /// </summary>
    Bank = 2,

    /// <summary>
    /// Pharmacy/Drugstore - Prescription pickup, consultation
    /// </summary>
    Pharmacy = 3,

    /// <summary>
    /// Electronics Shop - Sales, repairs, technical support
    /// </summary>
    ElectronicsShop = 4,

    /// <summary>
    /// Government Office - Public services, permits, documentation
    /// </summary>
    Government = 5,

    /// <summary>
    /// Telecom/Service Center - Phone, internet service support
    /// </summary>
    Telecom = 6,

    /// <summary>
    /// Restaurant/Food Service - Table management, order pickup
    /// </summary>
    Restaurant = 7
}
