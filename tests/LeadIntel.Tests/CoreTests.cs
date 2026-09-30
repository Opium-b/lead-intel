using LeadIntel.Collectors;
using LeadIntel.Data;
using LeadIntel.Pipeline;
using LeadIntel.Scoring;
using LeadIntel.Services;
using LeadIntel.Signals;

namespace LeadIntel.Tests;

/// <summary>Collectors -> signals -> scoring -> pitch, all without a database or network.</summary>
public class CoreTests
{
    public static readonly DateOnly Today = new(2026, 9, 27);

    public static string Ago(int days, string fmt = "yyyyMMdd") => Today.AddDays(-days).ToString(fmt).ToUpperInvariant();
    public static Row R(params (string, string?)[] kv) => new Row().With(kv);

    static Row Insp(int i, int daysAgo, int viol, int oos) => R(("inspection_id", i.ToString()), ("dot_number", "123"), ("insp_date", Ago(daysAgo)),
        ("report_state", "TX"), ("viol_total", viol.ToString()), ("oos_total", oos.ToString()), ("driver_oos_total", "0"), ("vehicle_oos_total", oos.ToString()));

    static Row Viol(string uid, int daysAgo, string basic, string code, bool oos = false) => R(("unique_id", uid), ("dot_number", "123"),
        ("insp_date", Ago(daysAgo, "dd-MMM-yy")), ("basic_desc", basic), ("viol_code", code), ("section_desc", $"desc {code}"),
        ("oos_indicator", oos ? "true" : "false"), ("viol_unit", "D"));

    public static RawFmcsa Raw() => new()
    {
        Census =
        [
            R(("dot_number", "123"), ("legal_name", "ACME FREIGHT LLC"), ("phy_state", "TX"), ("phy_city", "DALLAS"), ("power_units", "12"),
              ("total_drivers", "14"), ("status_code", "A"), ("add_date", Ago(60)), ("mcs150_date", Ago(900)), ("phone", "(214) 555-0100"),
              ("email_address", "Ops@AcmeFreight.com"), ("hm_ind", "N"), ("company_officer_1", "JOHN Q DOE"), ("cell_phone", "(214) 555-0111"),
              ("fax", "214-555-0122"), ("carrier_mailing_street", "PO BOX 9"), ("carrier_mailing_city", "DALLAS"),
              ("carrier_mailing_state", "TX"), ("carrier_mailing_zip", "75201")),
            R(("dot_number", "abc"), ("legal_name", "BAD ROW")),
        ],
        Inspections = [Insp(1, 5, 2, 1), Insp(2, 20, 1, 1), Insp(3, 40, 3, 1), Insp(4, 50, 1, 0), Insp(5, 70, 0, 0),
                       Insp(6, 400, 5, 2),  // outside 12-month window
                       Insp(7, 3, 1, 0).With(("dot_number", "999"))],  // unknown company -> dropped
        Crashes = [R(("crash_id", "c1"), ("dot_number", "123"), ("report_date", Ago(10)), ("report_state", "OK"), ("fatalities", "1"),
                     ("injuries", "0"), ("tow_away", "Y"))],
        Violations = [Viol("u1", 5, "Hours-of-Service Compliance", "3958A"), Viol("u2", 20, "Hours-of-Service Compliance", "3958A"),
                      Viol("u1", 5, "Vehicle Maintenance", "39347"),  // 1 < min 3 -> no maintenance signal
                      Viol("u3", 40, "Controlled Substances/&#8203;Alcohol", "3924", oos: true)],
        Authority = [R(("docket_number", "MC999"), ("dot_number", "00000123"), ("sub_number", "0"), ("mod_col_1", "MOTOR PROPERTY COMMON CARRIER"),
                       ("original_action_desc", "GRANTED"), ("orig_served_date", Ago(40, "MM/dd/yyyy")))],
        Insurance =
        [
            R(("docket_number", "MC999"), ("dot_number", "00000123"), ("mod_col_1", "BIPD/Primary"), ("name_company", "ACME INS"),
              ("policy_no", "P1"), ("ins_form_code", "91X"), ("effective_date", Ago(345, "MM/dd/yyyy"))),
            R(("docket_number", "MC999"), ("dot_number", "00000123"), ("mod_col_1", "BIPD/Primary"), ("name_company", "OLD INS"),
              ("policy_no", "P2"), ("ins_form_code", "91X"), ("effective_date", Ago(300, "MM/dd/yyyy")), ("cancl_effective_date", Ago(-10, "MM/dd/yyyy"))),
        ],
    };

    public static List<Fact> Facts(NormalizedBatch b) =>
        b.Events.Select(e => new Fact(null, e.Source, e.RecordType, e.ExternalId, e.ObservedAt, e.SourceUrl, e.Payload)).ToList();

    [Fact]
    public void ParsesAllFmcsaDateFormats()
    {
        var d = new DateOnly(2026, 9, 27);
        Assert.Equal(d, FmcsaCollector.ParseDate("20260927 2140"));
        Assert.Equal(d, FmcsaCollector.ParseDate("09/27/2026"));
        Assert.Equal(d, FmcsaCollector.ParseDate("27-SEP-26"));
        Assert.Null(FmcsaCollector.ParseDate(""));
        Assert.Null(FmcsaCollector.ParseDate("garbage"));
    }

    [Fact]
    public void NormalizesCompaniesContactsAndFacts()
    {
        var b = FmcsaCollector.Normalize(Raw());
        var c = Assert.Single(b.Companies);
        Assert.Equal(("123", "TX", 12, "A", "MC999"), (c.DotNumber, c.State, c.FleetSize, c.OperatingStatus, c.McNumber));
        Assert.Equal(new HashSet<(string, string, string?)>
        {
            ("phone", "2145550100", "Registered phone"), ("email", "ops@acmefreight.com", "Registered email"),
            ("person", "John Q Doe", "Company officer"), ("phone", "2145550111", "Registered cell phone"),
            ("fax", "2145550122", "Registered fax"), ("address", "PO BOX 9, DALLAS, TX 75201", "Mailing address"),
        }, c.Contacts.Select(x => (x.Type, x.Value, x.Label)).ToHashSet());
        Assert.Equal(6, b.Events.Count(e => e.RecordType == "inspection"));
        Assert.Equal(4, b.Events.Count(e => e.RecordType == "violation"));
        Assert.All(b.Events, e => Assert.StartsWith("https://data.transportation.gov/", e.SourceUrl));
    }

    [Fact]
    public void RulesTurnFactsIntoEvidenceBackedSignals()
    {
        var sigs = SignalEngine.Detect(Facts(FmcsaCollector.Normalize(Raw())), Today);
        string[] expected = ["OUT_OF_SERVICE", "OUT_OF_SERVICE", "OUT_OF_SERVICE", "INSPECTION_VIOLATION", "REPEATED_VIOLATIONS", "HIGH_OOS_RATE",
            "CRASH", "NEW_CARRIER", "STALE_MCS150", "HOS_VIOLATIONS", "DRUG_ALCOHOL", "NEW_AUTHORITY", "INSURANCE_CANCELLATION", "INSURANCE_RENEWAL"];
        Assert.Equal(expected.Order(), sigs.Select(s => s.Type).Order());
        var by = sigs.GroupBy(s => s.Type).ToDictionary(g => g.Key, g => g.First());
        Assert.Equal("critical", by["CRASH"].Severity);
        Assert.Contains("crash_id=c1", by["CRASH"].SourceUrl);
        Assert.Equal("3", Json.Text(by["HIGH_OOS_RATE"].Evidence["oos_inspections"]));
        Assert.Equal("5", Json.Text(by["HIGH_OOS_RATE"].Evidence["total_inspections"]));
        Assert.Equal("high", by["DRUG_ALCOHOL"].Severity);  // had an OOS violation
        Assert.Contains("OLD INS", by["INSURANCE_CANCELLATION"].Description);
        Assert.Equal(Today.AddDays(-900), by["STALE_MCS150"].ObservedAt);  // dated by the fact, not the fetch
        Assert.Equal(sigs.Count, sigs.Select(s => s.DedupeKey).Distinct().Count());
    }

    [Fact]
    public void PitchMapsSignalsToServiceLines()
    {
        var sigs = SignalEngine.Detect(Facts(FmcsaCollector.Normalize(Raw())), Today);
        var ranked = ServicesCatalog.Rank(sigs.Select(s => (s.Type, s.Severity, (IReadOnlyDictionary<string, object?>)s.Evidence)));
        Assert.Superset(new HashSet<string> { "insurance", "compliance", "eld_hos", "driver_files", "new_carrier" }, ranked.Select(p => p.Key).ToHashSet());
        var insurance = ranked.First(p => p.Key == "insurance");
        Assert.Contains(insurance.Reasons, r => r.Type == "INSURANCE_CANCELLATION");
        Assert.Equal(["compliance", "driver_files", "eld_hos"],
            ServicesCatalog.LinesFor("OUT_OF_SERVICE", new Dictionary<string, object?> { ["driver_oos_total"] = "1", ["vehicle_oos_total"] = "0" }));
    }

    static List<ScoringRule> Rules(string? key = null, int weight = 0) =>
        ScoringEngine.DefaultRuleRows().Select(r => { if (r.Key == key) r.Weight = weight; return r; }).ToList();

    [Fact]
    public void ScoreIsExplainedAndCapped()
    {
        var fatal = new SignalView("CRASH", Today.AddDays(-3), "critical");
        var view = new CompanyView("A", 12, [fatal], new HashSet<string> { "phone" }, ["insurance", "safety_tech", "compliance"]);
        var (points, breakdown) = ScoringEngine.Score(view, Rules(), Today);
        Assert.Equal(new HashSet<string> { "sig_crash", "sig_crash_injury", "sig_crash_fatal", "hot_now", "multi_service" }, breakdown.Select(b => b.Key).ToHashSet());
        Assert.Equal(50, points);
        Assert.Equal(breakdown.Sum(b => b.Points), points);
        Assert.Equal(100, ScoringEngine.Score(view, Rules("sig_crash", 500), Today).Points);
        var inactive = new CompanyView("I", 12, [new SignalView("INACTIVE_STATUS", null)], new HashSet<string>(), []);
        Assert.Equal(0, ScoringEngine.Score(inactive, Rules(), Today).Points);
    }

    [Fact]
    public void LongExternalIdsFitAndStayDistinct()
    {
        string a = "motus:" + new string('X', 200) + ":1", b = "motus:" + new string('X', 200) + ":2";
        Assert.True(LeadPipeline.FitExternalId(a).Length <= LeadPipeline.ExternalIdMax);
        Assert.NotEqual(LeadPipeline.FitExternalId(a), LeadPipeline.FitExternalId(b));
        Assert.Equal("short", LeadPipeline.FitExternalId("short"));
    }

    [Fact]
    public void DailyReportSplitsDecisionsIntoTheEightCategories()
    {
        const string prop = "Motor Carrier of Property (Except Household Goods)", broker = "Broker of Property (Except Household Goods)";
        Row A(string reason, string type) => R(("reason", reason), ("op_auth_type", type));
        List<Row> auth = [A("Granted", prop), A("Granted", broker), A("Granted", "Mexico Domiciled Motor Carrier of Property (Except Household Goods)"),
            A("Granted", "Motor Carrier of Passengers"), A("Published to FMCSA Register", prop), A("Reinstated", prop), A("Initial Status", prop),
            A("GRANTED", prop), A("Involuntary Suspension - insurance cancellation effective", prop)];
        List<Row> orders = [R(("order1_type_desc", "Operating Authority Involuntary Suspension Notice"), ("op_auth_type", broker))];
        List<Row> ins = [R(("filing_status_reason", "CANCEL"), ("ins_type_desc", "SURETY")),
            R(("filing_status_reason", "NAMECHG"), ("cancl_effective_date", "20260920")),
            R(("filing_status_reason", "NAMECHG"), ("cancl_effective_date", "20261020"))];  // after the period
        var n = DailyReport.Categorize(auth, orders, ins, "20260915", "20260929").ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        Assert.Equal(new Dictionary<string, int>
        {
            ["1 Daily Register"] = 3, ["2 Certificates of Authority"] = 3, ["3 OP-2 MX Commercial Zone"] = 1, ["4 Regular Routes (passenger)"] = 1,
            ["5 Name Changes"] = 1, ["6 Reinstatements"] = 1, ["7 Transfers"] = 0, ["8 Broker-FF Financial Security"] = 2,
        }, n);
    }
}
