namespace QMgr.Domain.Constants;

/// <summary>
/// Country and first-level-administrative-area reference data, held in code.
///
/// In code and not in a table, deliberately, and for three reasons. The project's standing rule is
/// to widen an existing table before adding a new one, and two reference tables that no tenant can
/// edit and every tenant shares would be the least useful kind of new table. Nothing here is
/// tenant data, so there is nothing to migrate, back up, or scope by organization. And a lookup
/// that lives in the deployed binary cannot be half-seeded on one server and complete on another,
/// which is exactly the failure mode a seeded reference table invites.
///
/// COVERAGE IS DELIBERATELY PARTIAL. Uganda is complete because that is where this product's
/// schools are; its East African neighbours are included because their nationals are a real part
/// of a Ugandan school's intake. Every other country is selectable and simply has no district
/// list, which is why <see cref="HasDistricts"/> exists: the form falls back to a free-text box
/// rather than presenting an empty dropdown that cannot be satisfied.
///
/// These lists change — districts are created and split by statute. Treat them as a convenience
/// for the common case, never as an authority, and note that <c>Student.HomeDistrict</c> stays a
/// plain string precisely so a value this file has not caught up with is still recordable.
/// </summary>
public static class Geography
{
    public const string Uganda = "Uganda";

    /// <summary>
    /// Countries offered in the picker. Uganda and its neighbours first because that is the
    /// overwhelmingly common answer and a school should not scroll to find it; the rest follow
    /// alphabetically. Ordering a picker by real-world frequency rather than by alphabet is the
    /// whole difference between one tap and thirty seconds of scrolling.
    /// </summary>
    public static readonly IReadOnlyList<string> Countries = new[]
    {
        Uganda,
        "Kenya",
        "Tanzania",
        "Rwanda",
        "Burundi",
        "South Sudan",
        "Democratic Republic of the Congo",
        "Somalia",
        "Ethiopia",
        "Eritrea",
        "Sudan",
        "Nigeria",
        "Ghana",
        "South Africa",
        "Zambia",
        "Zimbabwe",
        "Malawi",
        "Mozambique",
        "Botswana",
        "Egypt",
        "India",
        "Pakistan",
        "China",
        "United Kingdom",
        "Ireland",
        "United States",
        "Canada",
        "Australia",
        "Other"
    };

    /// <summary>Uganda's districts. The unit a Ugandan school actually thinks in when asked where a child is from.</summary>
    private static readonly string[] UgandaDistricts =
    {
        "Abim", "Adjumani", "Agago", "Alebtong", "Amolatar", "Amudat", "Amuria", "Amuru", "Apac",
        "Arua", "Budaka", "Bududa", "Bugiri", "Bugweri", "Buhweju", "Buikwe", "Bukedea", "Bukomansimbi",
        "Bukwo", "Bulambuli", "Buliisa", "Bundibugyo", "Bunyangabu", "Bushenyi", "Busia", "Butaleja",
        "Butambala", "Butebo", "Buvuma", "Buyende", "Dokolo", "Gomba", "Gulu", "Hoima", "Ibanda",
        "Iganga", "Isingiro", "Jinja", "Kaabong", "Kabale", "Kabarole", "Kaberamaido", "Kagadi",
        "Kakumiro", "Kalaki", "Kalangala", "Kaliro", "Kalungu", "Kampala", "Kamuli", "Kamwenge",
        "Kanungu", "Kapchorwa", "Kapelebyong", "Karenga", "Kasanda", "Kasese", "Katakwi", "Kayunga",
        "Kazo", "Kibaale", "Kiboga", "Kibuku", "Kikuube", "Kiruhura", "Kiryandongo", "Kisoro",
        "Kitagwenda", "Kitgum", "Koboko", "Kole", "Kotido", "Kumi", "Kwania", "Kween", "Kyankwanzi",
        "Kyegegwa", "Kyenjojo", "Kyotera", "Lamwo", "Lira", "Luuka", "Luwero", "Lwengo", "Lyantonde",
        "Madi-Okollo", "Manafwa", "Maracha", "Masaka", "Masindi", "Mayuge", "Mbale", "Mbarara",
        "Mitooma", "Mityana", "Moroto", "Moyo", "Mpigi", "Mubende", "Mukono", "Nabilatuk", "Nakapiripirit",
        "Nakaseke", "Nakasongola", "Namayingo", "Namisindwa", "Namutumba", "Napak", "Nebbi", "Ngora",
        "Ntoroko", "Ntungamo", "Nwoya", "Obongi", "Omoro", "Otuke", "Oyam", "Pader", "Pakwach",
        "Pallisa", "Rakai", "Rubanda", "Rubirizi", "Rukiga", "Rukungiri", "Rwampara", "Sembabule",
        "Serere", "Sheema", "Sironko", "Soroti", "Terego", "Tororo", "Wakiso", "Yumbe", "Zombo"
    };

    /// <summary>Kenya's counties — the equivalent first-level unit.</summary>
    private static readonly string[] KenyaCounties =
    {
        "Baringo", "Bomet", "Bungoma", "Busia", "Elgeyo-Marakwet", "Embu", "Garissa", "Homa Bay",
        "Isiolo", "Kajiado", "Kakamega", "Kericho", "Kiambu", "Kilifi", "Kirinyaga", "Kisii",
        "Kisumu", "Kitui", "Kwale", "Laikipia", "Lamu", "Machakos", "Makueni", "Mandera", "Marsabit",
        "Meru", "Migori", "Mombasa", "Murang'a", "Nairobi", "Nakuru", "Nandi", "Narok", "Nyamira",
        "Nyandarua", "Nyeri", "Samburu", "Siaya", "Taita-Taveta", "Tana River", "Tharaka-Nithi",
        "Trans Nzoia", "Turkana", "Uasin Gishu", "Vihiga", "Wajir", "West Pokot"
    };

    /// <summary>Rwanda's districts.</summary>
    private static readonly string[] RwandaDistricts =
    {
        "Bugesera", "Burera", "Gakenke", "Gasabo", "Gatsibo", "Gicumbi", "Gisagara", "Huye",
        "Kamonyi", "Karongi", "Kayonza", "Kicukiro", "Kirehe", "Muhanga", "Musanze", "Ngoma",
        "Ngororero", "Nyabihu", "Nyagatare", "Nyamagabe", "Nyamasheke", "Nyanza", "Nyarugenge",
        "Nyaruguru", "Rubavu", "Ruhango", "Rulindo", "Rusizi", "Rutsiro", "Rwamagana"
    };

    /// <summary>Tanzania's regions — its first-level unit, coarser than a district but the level people name.</summary>
    private static readonly string[] TanzaniaRegions =
    {
        "Arusha", "Dar es Salaam", "Dodoma", "Geita", "Iringa", "Kagera", "Katavi", "Kigoma",
        "Kilimanjaro", "Lindi", "Manyara", "Mara", "Mbeya", "Mjini Magharibi", "Morogoro", "Mtwara",
        "Mwanza", "Njombe", "Kaskazini Pemba", "Kusini Pemba", "Pwani", "Rukwa", "Ruvuma", "Shinyanga",
        "Simiyu", "Singida", "Songwe", "Tabora", "Tanga", "Kaskazini Unguja", "Kusini Unguja"
    };

    /// <summary>Burundi's provinces.</summary>
    private static readonly string[] BurundiProvinces =
    {
        "Bubanza", "Bujumbura Mairie", "Bujumbura Rural", "Bururi", "Cankuzo", "Cibitoke", "Gitega",
        "Karuzi", "Kayanza", "Kirundo", "Makamba", "Muramvya", "Muyinga", "Mwaro", "Ngozi", "Rumonge",
        "Rutana", "Ruyigi"
    };

    /// <summary>South Sudan's states and administrative areas.</summary>
    private static readonly string[] SouthSudanStates =
    {
        "Central Equatoria", "Eastern Equatoria", "Jonglei", "Lakes", "Northern Bahr el Ghazal",
        "Unity", "Upper Nile", "Warrap", "Western Bahr el Ghazal", "Western Equatoria",
        "Abyei", "Pibor", "Ruweng"
    };

    private static readonly Dictionary<string, string[]> DistrictsByCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        [Uganda] = UgandaDistricts,
        ["Kenya"] = KenyaCounties,
        ["Rwanda"] = RwandaDistricts,
        ["Tanzania"] = TanzaniaRegions,
        ["Burundi"] = BurundiProvinces,
        ["South Sudan"] = SouthSudanStates
    };

    /// <summary>Whether a district picker can be offered for this country, or the form must fall back to free text.</summary>
    public static bool HasDistricts(string? country) =>
        !string.IsNullOrWhiteSpace(country) && DistrictsByCountry.ContainsKey(country);

    /// <summary>The districts for a country, or an empty list when none are known.</summary>
    public static IReadOnlyList<string> DistrictsFor(string? country) =>
        !string.IsNullOrWhiteSpace(country) && DistrictsByCountry.TryGetValue(country, out var list)
            ? list
            : Array.Empty<string>();

    /// <summary>
    /// What the district field is called in that country, so the form's own label is honest — a
    /// Kenyan school asked for a "district" would look for something that does not exist there.
    /// </summary>
    public static string DistrictLabelFor(string? country) => country switch
    {
        "Kenya" => "Home county",
        "Tanzania" => "Home region",
        "Burundi" => "Home province",
        "South Sudan" => "Home state",
        _ => "Home district"
    };

    public static bool IsKnownCountry(string? country) =>
        !string.IsNullOrWhiteSpace(country) && Countries.Contains(country, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Server-side check that a district belongs to its country. Only enforced for countries whose
    /// list is complete here — for anything else the field is free text and any value is valid,
    /// which is the whole reason coverage being partial is safe.
    /// </summary>
    public static bool IsValidDistrict(string? country, string? district)
    {
        if (string.IsNullOrWhiteSpace(district)) return true;
        if (!HasDistricts(country)) return true;
        return DistrictsFor(country).Contains(district.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
