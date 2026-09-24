namespace QMgr.Application.Branding;

/// <summary>
/// WHAT THIS PRODUCT IS CALLED, AND THE ONE HOME FOR IT (2026-09-24, plan
/// <c>docs/plans/SACC_DASHBOARD_REBRAND.md</c>).
///
/// <para>Until the rebrand the name was typed about 170 times across seventy files, and three of the
/// places that looked like a home for it — <c>BrandContext</c>, <c>TenantHostContext</c>,
/// <c>EmailTemplates.AppName</c> — each held their own copy. A rebrand now edits THIS FILE and the
/// files under <c>wwwroot/brand/</c>, and nothing else. <c>scripts/e2e/brand-literal-check.mjs</c> fails
/// the build on a product name typed anywhere else.</para>
///
/// <para><b>Two names, and they are different things.</b> <see cref="Name"/> is OURS — "SACC Dashboard".
/// A white-labelled school's app carries its <c>Organization.BrandName</c> exactly as typed, which may be
/// "MARYHILL Dashboard", "Dashboard" or anything else: we suggest a pattern and never force one
/// (<see cref="SuggestFor"/>). <see cref="NameFor"/> is the only thing that chooses between them.</para>
///
/// <para><b>What is NOT here, on purpose:</b> the strings that merely CONTAIN "qmgr" and that no person
/// reads — <c>SetApplicationName("QMgr")</c> (the Data Protection key-derivation purpose), the JWT issuer
/// and audience, the Android <c>applicationId</c>, the schema, the <c>qmgr-*</c> localStorage keys.
/// Each is an identity or a stored format, and changing it signs everybody out, stops protected payloads
/// decrypting or turns the app into a different app.</para>
/// </summary>
public static class ProductBrand
{
    /// <summary>The company, and the protectable half of our product name.</summary>
    public const string Company = "SACC";

    /// <summary>The descriptive half. Never added to a school's own name.</summary>
    public const string Descriptor = "Dashboard";

    /// <summary>Our product: "SACC Dashboard", spaced (decision B1).</summary>
    public const string Name = Company + " " + Descriptor;

    /// <summary>The contracting party — Terms, Privacy, invoices. Not the product.</summary>
    public const string LegalEntity = "SACC Software Limited";

    /// <summary>What is inside, in a line. Replaces the retired "Front Office" suffix (decision B10).</summary>
    public const string Tagline = "Staff, welfare and front office";

    /// <summary>The longer sentence for a page description or a store listing.</summary>
    public const string Description =
        "Staff, student welfare, visitors, queues and signage for schools and organisations, in one system.";

    public const string Website = "https://getsacc.com";
    public const string SupportEmail = "support@getsacc.com";

    /// <summary>The prefix an SMS or an email subject carries when no school's own name applies.</summary>
    public const string MessagePrefix = Name + ": ";

    /// <summary>The iCalendar PRODID (RFC 5545 §3.7.3). Identifies the product that wrote the feed.</summary>
    public const string CalendarProductId = "-//" + Company + "//" + Name + "//EN";

    /// <summary>Longest name a school may give its app. Long enough for "Maryhill High School Dashboard".</summary>
    public const int MaxBrandNameLength = 40;

    public const int MinBrandNameLength = 2;

    /// <summary>What fits a phone's home-screen label, the mobile bar or a narrow sidebar.</summary>
    public const int ShortNameLength = 12;

    /// <summary>
    /// The mark, in one folder. Nothing else in the app references a logo file by path.
    /// </summary>
    public static class Assets
    {
        /// <summary>The tile: an S made of dashboard panels on the wine ground (decision B6).</summary>
        public const string Mark = "/brand/mark.svg";

        /// <summary>The same S on a light tile, for a light theme's loading screen.</summary>
        public const string MarkLight = "/brand/mark-light.svg";

        /// <summary>Single colour, for a mono printer.</summary>
        public const string MarkMono = "/brand/mark-mono.svg";

        /// <summary>The installable app icon, full bleed.</summary>
        public const string AppIcon = "/brand/app-icon.svg";

        /// <summary>Maskable: the S kept inside the inner 80% safe zone.</summary>
        public const string AppIconMaskable = "/brand/app-icon-maskable.svg";

        /// <summary>The simplified S, drawn for 16–32px.</summary>
        public const string Favicon = "/brand/favicon.svg";
    }

    /// <summary>
    /// The app's name here. The API calls this once per response and sends the result; no page, job
    /// or email works it out for itself.
    ///
    /// <para>A school's name is used EXACTLY AS TYPED — we never add a word to it — but only while
    /// white-labelling is both entitled and switched on (decision B2), and only when it passes
    /// <see cref="ValidateBrandName"/>. A stored value that fails the rule (one written before the rule
    /// existed, or edited by hand) falls back to ours rather than being shown or rewritten.</para>
    /// </summary>
    public static string NameFor(string? brandName, bool whiteLabelActive)
        // Empty is VALID to the rule ("use ours"), so it must be tested before the value is trimmed —
        // the first cut went straight to brandName!.Trim() and 500ed every save of an empty name.
        => whiteLabelActive && !string.IsNullOrWhiteSpace(brandName) && ValidateBrandName(brandName) is null
            ? brandName.Trim()
            : Name;

    /// <summary>
    /// A name short enough for a home-screen label: the whole name when it fits, otherwise its first
    /// word, otherwise the first <see cref="ShortNameLength"/> characters. Ours shortens to "SACC".
    /// </summary>
    public static string ShortNameFor(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return Company;
        if (trimmed == Name) return Company;
        if (trimmed.Length <= ShortNameLength) return trimmed;

        var first = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return first.Length <= ShortNameLength ? first : first[..ShortNameLength];
    }

    /// <summary>
    /// The one rule for a school's app name, run by the API, the registration form and the Appearance
    /// page, so the sentence a person reads is the same on all three (decision B3). Null means valid.
    /// Empty is valid too — it means "use ours".
    /// </summary>
    public static string? ValidateBrandName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var trimmed = name.Trim();
        if (trimmed.Length < MinBrandNameLength)
            return $"An app name needs at least {MinBrandNameLength} characters.";
        if (trimmed.Length > MaxBrandNameLength)
            return $"An app name can be at most {MaxBrandNameLength} characters.";
        if (trimmed.Any(char.IsControl))
            return "An app name must be on one line.";

        // A school's app that calls itself ours reads as our endorsement, and in a store listing it
        // would be mistaken for our product. Compared on letters only, so "S.A.C.C." is caught too.
        var letters = new string(trimmed.Where(char.IsLetter).ToArray());
        if (letters.Contains(Company, StringComparison.OrdinalIgnoreCase))
            return $"An app name cannot include \"{Company}\". That name is ours; leave the field empty to use it.";

        return null;
    }

    /// <summary>
    /// What we OFFER a school, never what we impose: its organisation's first word in capitals, followed
    /// by our descriptor — "Maryhill High School" → "MARYHILL Dashboard". A generic first word ("St",
    /// "The", "Saint") gives way to the initials. The school may accept it, change it or clear it.
    /// </summary>
    public static string SuggestFor(string? organizationName)
    {
        var words = (organizationName ?? string.Empty)
            .Split(new[] { ' ', '\t', '-', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();
        if (words.Count == 0) return string.Empty;

        string word;
        if (Generic.Contains(words[0]) && words.Count > 1)
            word = new string(words.Where(w => !Generic.Contains(w)).Select(w => char.ToUpperInvariant(w[0])).ToArray());
        else
            word = words[0].ToUpperInvariant();

        if (word.Length < MinBrandNameLength) word = words[0].ToUpperInvariant();
        var suggestion = $"{word} {Descriptor}";
        return ValidateBrandName(suggestion) is null ? suggestion : string.Empty;
    }

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "St", "Saint", "The", "Our", "Holy", "Mt", "Mount"
    };
}
