using LeadIntel.Data;

namespace LeadIntel.Scoring;

public record ServiceLine(string Label, string[] Services);
public record PitchReason(string Type, int Count);
public record Pitch(string Key, string Label, string[] Services, int Strength, List<PitchReason> Reasons);

/// <summary>What we sell, and which detected signals indicate a need for it. Edit here to change what gets pitched.</summary>
public static class ServicesCatalog
{
    public static readonly Dictionary<string, ServiceLine> Lines = new()
    {
        ["compliance"] = new("Compliance & audits", [
            "DOT / FMCSA compliance", "DOT audit preparation", "New Entrant Safety Audit support", "CSA / SMS monitoring",
            "CSA score improvement", "DataQs dispute assistance", "Safety program development", "Safety manuals and policies",
            "Compliance document management", "Risk assessment"]),
        ["driver_files"] = new("Driver files & drug testing", [
            "Driver Qualification Files (DQF)", "MVR / driver record checks", "CDL verification", "Medical certificate tracking",
            "Clearinghouse compliance", "Drug & alcohol testing programs", "Random drug testing", "SAP coordination"]),
        ["eld_hos"] = new("ELD & hours of service", ["ELD setup / installation", "HOS compliance", "Log auditing", "ELD troubleshooting"]),
        ["safety_tech"] = new("Safety tech & training", [
            "Driver safety training", "AI dashcams / video telematics", "Driver behavior monitoring", "GPS / fleet tracking",
            "Telematics", "Fleet analytics", "Accident / incident management"]),
        ["maintenance"] = new("Maintenance & repair", [
            "Preventive maintenance", "Truck / trailer repair coordination", "Tire management", "Vehicle diagnostics",
            "Roadside / breakdown assistance"]),
        ["insurance"] = new("Insurance & claims", [
            "Trucking insurance", "Commercial auto liability", "Motor truck cargo insurance", "Physical damage insurance",
            "General liability", "Bobtail / non-trucking liability", "Trailer interchange", "Workers' compensation",
            "Occupational accident", "Excess / umbrella liability", "Reefer / specialized cargo coverage",
            "Certificate of Insurance (COI) management", "Insurance filings", "Claims assistance",
            "Accident / cargo claims support", "Risk management"]),
        ["registration"] = new("Registration, permits & taxes", [
            "IRP registration & renewal", "IFTA registration & filing", "UCR registration", "2290 / Heavy Vehicle Use Tax",
            "State highway-use taxes", "Oversize / overweight permits", "Trip permits", "State-specific permits"]),
        ["new_carrier"] = new("New carrier setup", [
            "New trucking company / authority setup", "USDOT registration", "MC authority", "BOC-3",
            "Business formation / LLC setup", "Compliance setup for new carriers", "New Entrant Safety Audit support"]),
        ["back_office"] = new("Back office & finance", [
            "Bookkeeping", "Trucking accounting", "Tax preparation", "Payroll", "Driver settlements", "Invoice preparation",
            "Accounts receivable (AR)", "Broker payment follow-up", "Collections", "Fuel cards", "Fuel discounts",
            "TMS setup / implementation", "Workflow automation", "Cost-per-mile analysis", "Fleet profitability analysis",
            "Fleet growth consulting"]),
    };

    static readonly Dictionary<string, string[]> SignalLines = new()
    {
        ["OUT_OF_SERVICE"] = ["compliance"],  // + driver/vehicle specific lines, see LinesFor()
        ["HIGH_OOS_RATE"] = ["compliance", "maintenance", "safety_tech"],
        ["REPEATED_VIOLATIONS"] = ["compliance", "safety_tech"],
        ["INSPECTION_VIOLATION"] = ["compliance"],
        ["CRASH"] = ["insurance", "safety_tech", "compliance"],
        ["NEW_CARRIER"] = ["new_carrier", "insurance", "eld_hos", "registration", "back_office"],
        ["NEW_AUTHORITY"] = ["new_carrier", "insurance", "registration", "back_office"],
        ["STALE_MCS150"] = ["registration", "compliance"],
        ["INACTIVE_STATUS"] = ["compliance", "registration"],
        ["HOS_VIOLATIONS"] = ["eld_hos", "compliance"],
        ["MAINTENANCE_VIOLATIONS"] = ["maintenance"],
        ["UNSAFE_DRIVING"] = ["safety_tech"],
        ["DRIVER_FITNESS"] = ["driver_files"],
        ["DRUG_ALCOHOL"] = ["driver_files", "compliance"],
        ["HAZMAT_VIOLATIONS"] = ["compliance", "safety_tech"],
        ["HAZMAT_CARRIER"] = ["insurance", "compliance"],
        ["AUTHORITY_REVOKED"] = ["insurance", "new_carrier", "compliance"],
        ["INSURANCE_CANCELLATION"] = ["insurance"],
        ["INSURANCE_SUSPENDED"] = ["insurance", "compliance"],
        ["SUSPENSION_NOTICE"] = ["insurance", "compliance"],
        ["INSURANCE_NEEDED"] = ["insurance", "new_carrier"],
        ["INSURANCE_RENEWAL"] = ["insurance"],
        ["FLEET_GROWTH"] = ["insurance", "driver_files", "safety_tech", "back_office"],
        ["FLEET_SHRINK"] = ["back_office", "insurance"],
        ["DRIVER_GROWTH"] = ["driver_files", "eld_hos", "safety_tech"],
        ["HIRING_DRIVERS"] = ["driver_files", "safety_tech", "insurance"],
        ["REACTIVATED"] = ["registration", "insurance", "compliance", "new_carrier"],
        ["REINSTATED_AUTHORITY"] = ["insurance", "compliance", "registration"],
        ["REGISTER_PUBLISHED"] = ["new_carrier", "insurance", "registration", "eld_hos"],
        ["VOLUNTARY_SUSPENSION"] = ["insurance", "back_office"],
        ["NAME_CHANGE"] = ["registration", "insurance"],
    };

    static readonly Dictionary<string, int> SeverityWeight = new() { ["critical"] = 4, ["high"] = 3, ["medium"] = 2, ["low"] = 1 };

    public static List<string> LinesFor(string type, IReadOnlyDictionary<string, object?> evidence)
    {
        var lines = SignalLines.GetValueOrDefault(type, []).ToList();
        if (type == "OUT_OF_SERVICE")
        {
            if (int.TryParse(Json.Text(evidence.GetValueOrDefault("driver_oos_total")), out var d) && d != 0) lines.AddRange(["driver_files", "eld_hos"]);
            if (int.TryParse(Json.Text(evidence.GetValueOrDefault("vehicle_oos_total")), out var v) && v != 0) lines.Add("maintenance");
        }
        return lines;
    }

    /// <summary>Service lines ranked by how strongly the evidence points at them, with the reasons.</summary>
    public static List<Pitch> Rank(IEnumerable<(string Type, string Severity, IReadOnlyDictionary<string, object?> Evidence)> signals)
    {
        var acc = new Dictionary<string, (int Strength, Dictionary<string, int> Reasons)>();
        foreach (var (type, severity, evidence) in signals)
            foreach (var line in LinesFor(type, evidence))
            {
                var (strength, reasons) = acc.TryGetValue(line, out var cur) ? cur : (0, new Dictionary<string, int>());
                reasons[type] = reasons.GetValueOrDefault(type) + 1;
                acc[line] = (strength + SeverityWeight.GetValueOrDefault(severity, 1), reasons);
            }
        // OrderBy is stable, like Python's sorted(): ties keep first-seen order
        return acc.OrderByDescending(kv => kv.Value.Strength)
            .Select(kv => new Pitch(kv.Key, Lines[kv.Key].Label, Lines[kv.Key].Services, kv.Value.Strength,
                kv.Value.Reasons.OrderByDescending(r => r.Value).Select(r => new PitchReason(r.Key, r.Value)).ToList()))
            .ToList();
    }
}
