// ম্যাট্রিক্স — the field catalog: the RMG vocabulary (docs/MATRIX_PROMPT.md §3.3, §3.4).
//
// Every canonical field once: its key (which is also its column name in a
// dataset table — so column names come from here, never from a header), role,
// type, sensitivity, Bengali and English labels, and the header spellings that
// mean it. Headers that name personal data the app must not hold (NID, bank,
// phone, address, family, date of birth, religion…) are recognised by pattern
// and dropped at import.

using System.Text.RegularExpressions;

namespace RabbitIC.Api.Matrix;

public static class MxSensitivity
{
    public const string Public = "public";
    public const string Personal = "personal";
    public const string Salary = "salary";
    public const string Sensitive = "sensitive";

    public static readonly string[] All = { Public, Personal, Salary, Sensitive };
}

public static class MxRole
{
    public const string Id = "id";
    public const string Dimension = "dimension";
    public const string Measure = "measure";
    public const string Date = "date";
    public const string Time = "time";
    public const string Text = "text";
}

public static class MxType
{
    public const string Text = "text";
    public const string Integer = "integer";
    public const string Real = "real";
    public const string Date = "date";
    public const string Time = "time";
    public const string Bool = "bool";
}

public static class MxReportKind
{
    public const string Manpower = "manpower";
    public const string Master = "master";
    public const string AttendanceDaily = "attendance-daily";
    public const string DesignationLookup = "designation-lookup";
    public const string SeparationList = "separation-list";
    public const string Custom = "custom";

    public static readonly string[] All = { Manpower, Master, AttendanceDaily, DesignationLookup, SeparationList, Custom };
}

/// <summary>One canonical field. <paramref name="Derived"/>: made at import, never mapped from a header.</summary>
public sealed record MxFieldDef(
    string Key, string Group, string Role, string Type, string Sensitivity, string En, string Bn,
    string[] Synonyms, string[]? OnlyForKinds = null, bool Derived = false);

public sealed record MxFieldMatch(MxFieldDef Field, string Source);

public static class FieldCatalog
{
    private static MxFieldDef F(string key, string group, string role, string type, string sens, string en, string bn, params string[] syn) =>
        new(key, group, role, type, sens, en, bn, syn);

    private const string P = MxSensitivity.Public, Pe = MxSensitivity.Personal, S = MxSensitivity.Salary;

    public static readonly IReadOnlyList<MxFieldDef> All = new[]
    {
        // ── Identity ──
        F("emp_id", "identity", MxRole.Id, MxType.Text, P, "Employee ID", "কর্মী আইডি", "EmpID", "Emp ID", "Employee ID", "EmployeeID", "EmpCode", "Emp Code", "Employee Code", "Emp No", "Employee No", "ID No"),
        // PunchNo maps to emp_id only through the normaliser (punch_emp_id, derived), never directly.
        F("punch_no", "identity", MxRole.Id, MxType.Text, P, "Punch number", "পাঞ্চ নম্বর", "PunchNo", "Punch No", "Punch ID", "PunchCardNo", "Punch Card No"),
        F("apparel_emp_id", "identity", MxRole.Id, MxType.Text, P, "Apparel employee ID", "অ্যাপারেল আইডি", "ApparelEmpId", "Apparel Emp ID", "ApparelEmpID"),
        F("track_id", "identity", MxRole.Id, MxType.Text, P, "Track ID", "ট্র্যাক আইডি", "TrackId", "Track ID"),
        F("prev_emp_id", "identity", MxRole.Id, MxType.Text, P, "Previous employee ID", "আগের কর্মী আইডি", "PrevEmpID", "Prev Emp ID", "Previous Emp ID"),
        F("prev_punch_no", "identity", MxRole.Id, MxType.Text, P, "Previous punch number", "আগের পাঞ্চ নম্বর", "PrevPunchNo", "Prev Punch No"),
        F("day_id", "identity", MxRole.Text, MxType.Text, P, "Day ID (system)", "দিন আইডি (সিস্টেম)", "dayid", "Day ID"),
        F("serial", "identity", MxRole.Text, MxType.Integer, P, "Serial", "ক্রমিক", "SL", "SL.", "Serial", "SL No", "Sl No", "SN"),

        // ── Organisation ──
        F("company", "organisation", MxRole.Dimension, MxType.Text, P, "Company", "কোম্পানি", "CompanyName", "Company Name", "Company"),
        F("company_bn", "organisation", MxRole.Dimension, MxType.Text, P, "Company (Bengali)", "কোম্পানি (বাংলা)", "CompanyNameBan", "Company Name Ban"),
        F("unit", "organisation", MxRole.Dimension, MxType.Text, P, "Unit", "ইউনিট", "UnitName", "Unit Name", "Unit"),
        F("unit_bn", "organisation", MxRole.Dimension, MxType.Text, P, "Unit (Bengali)", "ইউনিট (বাংলা)", "UnitNameBan"),
        F("unit_id", "organisation", MxRole.Id, MxType.Text, P, "Unit ID", "ইউনিট আইডি", "UnitId", "Unit ID", "UnitID"),
        F("unit_type", "organisation", MxRole.Dimension, MxType.Text, P, "Unit type", "ইউনিটের ধরন", "UnitTypeName", "Unit Type", "UnitType"),
        F("department", "organisation", MxRole.Dimension, MxType.Text, P, "Department", "বিভাগ", "DepartmentName", "Department Name", "Department", "Dept", "DeptName"),
        F("department_bn", "organisation", MxRole.Dimension, MxType.Text, P, "Department (Bengali)", "বিভাগ (বাংলা)", "DepartmentNameBan"),
        F("section", "organisation", MxRole.Dimension, MxType.Text, P, "Section", "সেকশন", "SectionName", "Section Name", "Section"),
        F("section_bn", "organisation", MxRole.Dimension, MxType.Text, P, "Section (Bengali)", "সেকশন (বাংলা)", "SectionNameBan"),
        F("wing", "organisation", MxRole.Dimension, MxType.Text, P, "Wing", "উইং", "WingName", "Wing Name", "Wing"),
        F("wing_bn", "organisation", MxRole.Dimension, MxType.Text, P, "Wing (Bengali)", "উইং (বাংলা)", "WingNameBan"),
        F("team", "organisation", MxRole.Dimension, MxType.Text, P, "Team", "টিম", "TeamName", "Team Name", "Team"),
        F("team_bn", "organisation", MxRole.Dimension, MxType.Text, P, "Team (Bengali)", "টিম (বাংলা)", "TeamNameBan"),
        F("job_location", "organisation", MxRole.Dimension, MxType.Text, P, "Job location", "কর্মস্থল", "JobLocationName", "Job Location", "JobLocation"),
        F("salary_company", "organisation", MxRole.Dimension, MxType.Text, P, "Salary paid by", "বেতন প্রদানকারী কোম্পানি", "SalaryCompany", "SALARY PAID COMPANY", "Salary Paid Company", "Salary Company"),

        // ── Line ──
        F("line_name", "line", MxRole.Dimension, MxType.Text, P, "Line", "লাইন", "Line", "Line Name", "LineName", "Line No", "LineNo"),
        new MxFieldDef("line_no", "line", MxRole.Dimension, MxType.Integer, P, "Line number", "লাইন নম্বর", Array.Empty<string>(), Derived: true),
        new MxFieldDef("line_type", "line", MxRole.Dimension, MxType.Text, P, "Line type", "লাইনের ধরন", Array.Empty<string>(), Derived: true),

        // ── Job ──
        F("designation", "job", MxRole.Dimension, MxType.Text, P, "Designation", "পদবি", "DesignationName", "Designation Name", "Designation", "Desig"),
        F("designation_bn", "job", MxRole.Dimension, MxType.Text, P, "Designation (Bengali)", "পদবি (বাংলা)", "DesignationNameBan"),
        F("designation_spec", "job", MxRole.Dimension, MxType.Text, P, "Designation specification", "পদবির বিবরণ", "DesignationSpecification", "Designation Specification"),
        F("last_designation", "job", MxRole.Dimension, MxType.Text, P, "Previous designation", "আগের পদবি", "LastDesignationName", "Last Designation"),
        F("grade_id", "job", MxRole.Dimension, MxType.Text, P, "Grade", "গ্রেড", "GradeId", "Grade ID", "Grade", "GradeName"),
        F("position", "job", MxRole.Dimension, MxType.Text, P, "Position", "পজিশন", "PositionName", "Position"),
        F("emp_type", "job", MxRole.Dimension, MxType.Text, P, "Employee type", "কর্মীর ধরন", "EmpTypeName", "Emp Type", "Employee Type", "EmpType"),
        F("emp_category", "job", MxRole.Dimension, MxType.Text, P, "Employee category", "কর্মীর শ্রেণি", "EmpCategoryName", "Emp Category", "Category"),
        F("equivalency", "job", MxRole.Dimension, MxType.Text, P, "Equivalency", "সমতুল্য পদ", "EquivalencyName", "Equivalency Name", "Equivalency"),
        F("equivalency_grade", "job", MxRole.Dimension, MxType.Text, P, "Equivalency grade", "সমতুল্য গ্রেড", "EquivalencyGradeName", "Equivalency Grade", "EquivalencyGrade"),
        F("fixed_shift", "job", MxRole.Dimension, MxType.Text, P, "Fixed shift", "নির্ধারিত শিফট", "CurrentFixedShift", "Fixed Shift"),
        F("week_off", "job", MxRole.Dimension, MxType.Text, P, "Weekly off day", "সাপ্তাহিক ছুটি", "WeekOffDay", "Week Off", "Weekly Off"),

        // ── Status and dates ──
        new MxFieldDef("status", "status", MxRole.Dimension, MxType.Text, P, "Status", "অবস্থা",
            new[] { "EmpStatusName", "Emp Status", "Employee Status", "Status" }, OnlyForKinds: null),
        F("joining_date", "status", MxRole.Date, MxType.Date, P, "Joining date", "যোগদানের তারিখ", "JoiningDate", "Joining Date", "DOJ", "Date of Joining", "JoinDate", "Join Date"),
        F("effect_date", "status", MxRole.Date, MxType.Date, P, "Effect date", "কার্যকর তারিখ", "EffectDate", "Effect Date", "Effective Date"),
        F("separation_date", "status", MxRole.Date, MxType.Date, P, "Separation date", "বিচ্ছেদের তারিখ", "SeparationEffectDate", "SeparationDate", "Separation Date", "Resign Date", "Left Date"),
        F("probation_date", "status", MxRole.Date, MxType.Date, P, "Probation end", "শিক্ষানবিশ শেষ", "ProbationDate", "Probation Date"),
        F("confirm_status", "status", MxRole.Dimension, MxType.Text, P, "Confirmation status", "স্থায়ীকরণ", "ConfirmStatus", "Confirm Status", "Confirmation Status"),
        F("is_approved", "status", MxRole.Dimension, MxType.Text, P, "Approved", "অনুমোদিত", "IsApproved", "Is Approved"),
        F("submission_date", "status", MxRole.Date, MxType.Date, P, "Submission date", "জমার তারিখ", "SubmissionDate", "Submission Date"),
        F("accept_date", "status", MxRole.Date, MxType.Date, P, "Acceptance date", "গ্রহণের তারিখ", "AcceptDate", "Accept Date"),
        F("probation_months", "status", MxRole.Measure, MxType.Integer, P, "Probation (months)", "শিক্ষানবিশ (মাস)", "MonthProbation", "Probation Months"),
        F("need_correction", "status", MxRole.Dimension, MxType.Text, P, "Needs correction", "সংশোধন দরকার", "NeedCorrection", "Need Correction"),
        F("work_duration_years", "status", MxRole.Measure, MxType.Real, P, "Service years (file)", "চাকরির বছর (ফাইল)", "WorkDurationYears", "Work Duration Years"),
        F("work_duration_text", "status", MxRole.Text, MxType.Text, P, "Service length (file)", "চাকরির মেয়াদ (ফাইল)", "WorkDurationDetails", "Work Duration Details", "Work Duration"),

        // ── Person (personal) ──
        F("name", "person", MxRole.Text, MxType.Text, Pe, "Name", "নাম", "NAME", "Name", "EmpName", "Emp Name", "Employee Name", "Full Name"),
        F("name_bn", "person", MxRole.Text, MxType.Text, Pe, "Name (Bengali)", "নাম (বাংলা)", "NameBan", "EmpNameBan", "Name Ban", "Name Bangla"),
        F("gender", "person", MxRole.Dimension, MxType.Text, P, "Gender", "লিঙ্গ", "Gender", "GenderName", "Sex"),
        F("title", "person", MxRole.Text, MxType.Text, Pe, "Title", "সম্বোধন", "Title", "Salutation"),
        F("age_years", "person", MxRole.Measure, MxType.Integer, Pe, "Age (file)", "বয়স (ফাইল)", "AgeYears", "Age Years", "Age"),
        F("age_text", "person", MxRole.Text, MxType.Text, Pe, "Age details (file)", "বয়সের বিবরণ (ফাইল)", "AgeDetails", "Age Details"),
        F("degree", "person", MxRole.Text, MxType.Text, Pe, "Degree", "শিক্ষাগত যোগ্যতা", "DegreeName", "Degree", "Education"),
        F("institute", "person", MxRole.Text, MxType.Text, Pe, "Institute", "শিক্ষা প্রতিষ্ঠান", "Institute", "InstituteName"),

        // ── Derived bands ──
        new MxFieldDef("tenure_years", "derived", MxRole.Measure, MxType.Real, P, "Tenure (years)", "চাকরিকাল (বছর)", Array.Empty<string>(), Derived: true),
        new MxFieldDef("tenure_band", "derived", MxRole.Dimension, MxType.Text, P, "Tenure band", "চাকরিকালের পরিসর", Array.Empty<string>(), Derived: true),
        new MxFieldDef("age_band", "derived", MxRole.Dimension, MxType.Text, P, "Age band", "বয়সের পরিসর", Array.Empty<string>(), Derived: true),
        new MxFieldDef("punch_emp_id", "derived", MxRole.Id, MxType.Text, P, "Employee ID from punch no.", "পাঞ্চ থেকে কর্মী আইডি", Array.Empty<string>(), Derived: true),

        // ── Attendance ──
        new MxFieldDef("att_date", "attendance", MxRole.Date, MxType.Date, P, "Attendance date", "হাজিরার তারিখ",
            new[] { "Date", "Att Date", "Attendance Date", "AttDate" }, OnlyForKinds: new[] { MxReportKind.AttendanceDaily }),
        F("shift", "attendance", MxRole.Dimension, MxType.Text, P, "Shift", "শিফট", "Shift", "Shift Name", "ShiftName", "Shift Code"),
        F("in_time", "attendance", MxRole.Time, MxType.Time, P, "In time", "প্রবেশ", "In Time", "InTime", "Punch In", "Entry Time"),
        F("out_time", "attendance", MxRole.Time, MxType.Time, P, "Out time", "প্রস্থান", "Out Time", "OutTime", "Punch Out", "Exit Time"),
        F("ot_hours", "attendance", MxRole.Measure, MxType.Real, P, "OT hours", "ওভারটাইম ঘণ্টা", "OT Hours", "OT", "OTHour", "OT Hrs", "OTHours", "Overtime"),
        F("ot_deduction", "attendance", MxRole.Measure, MxType.Real, P, "OT deduction", "ওভারটাইম কর্তন", "Deduction OT", "OT Deduction"),
        new MxFieldDef("att_status", "attendance", MxRole.Dimension, MxType.Text, P, "Attendance status", "হাজিরার অবস্থা",
            new[] { "Status", "Att Status", "Attendance Status" }, OnlyForKinds: new[] { MxReportKind.AttendanceDaily }),
        F("present_days", "attendance", MxRole.Measure, MxType.Real, P, "Present days", "উপস্থিত দিন", "Present Days", "PresentDays"),
        F("absent_days", "attendance", MxRole.Measure, MxType.Real, P, "Absent days", "অনুপস্থিত দিন", "Absent Days", "AbsentDays"),
        F("leave_days", "attendance", MxRole.Measure, MxType.Real, P, "Leave days", "ছুটির দিন", "Leave Days", "LeaveDays"),
        F("holidays", "attendance", MxRole.Measure, MxType.Real, P, "Holidays", "সরকারি ছুটি", "Holidays", "Holiday"),
        F("weekend_days", "attendance", MxRole.Measure, MxType.Real, P, "Weekend days", "সাপ্তাহিক ছুটির দিন", "Weekend", "Weekend Days"),
        F("payable_days", "attendance", MxRole.Measure, MxType.Real, P, "Payable days", "বেতনযোগ্য দিন", "Payable Days", "PayableDays"),
        F("month", "attendance", MxRole.Dimension, MxType.Text, P, "Month", "মাস", "Month", "Salary Month"),

        // ── Salary (permission) ──
        F("gross", "salary", MxRole.Measure, MxType.Real, S, "Gross salary", "মোট বেতন", "GrossSalary", "Gross Salary", "Gross"),
        F("basic", "salary", MxRole.Measure, MxType.Real, S, "Basic", "মূল বেতন", "Basic", "Basic Salary", "BasicSalary"),
        F("house_rent", "salary", MxRole.Measure, MxType.Real, S, "House rent", "বাড়ি ভাড়া", "HouseRent", "House Rent"),
        F("medical", "salary", MxRole.Measure, MxType.Real, S, "Medical", "চিকিৎসা ভাতা", "Medical", "Medical Allowance"),
        F("conveyance", "salary", MxRole.Measure, MxType.Real, S, "Conveyance", "যাতায়াত ভাতা", "Conveyance", "Conveyance Allowance"),
        F("food", "salary", MxRole.Measure, MxType.Real, S, "Food", "খাদ্য ভাতা", "Food", "Food Allowance"),
        F("other_allow", "salary", MxRole.Measure, MxType.Real, S, "Other allowance", "অন্যান্য ভাতা", "Other Allowance", "OtherAllow", "Others", "Other"),
        F("current_salary", "salary", MxRole.Measure, MxType.Real, S, "Current salary", "বর্তমান বেতন", "CurrentSalary", "Current Salary"),
        F("increment_amount", "salary", MxRole.Measure, MxType.Real, S, "Increment", "ইনক্রিমেন্ট", "Increment", "IncrementAmount", "Increment Amount", "Last Increment"),
        F("increment_date", "salary", MxRole.Date, MxType.Date, S, "Increment date", "ইনক্রিমেন্টের তারিখ", "LastIncEffectDate_A", "Increment Date", "LastIncrementDate", "Last Increment Date"),
        F("special_increment_date", "salary", MxRole.Date, MxType.Date, S, "Special increment date", "বিশেষ ইনক্রিমেন্টের তারিখ", "LastSplAllIncEffectDate_A"),
        F("is_fixed_salary", "salary", MxRole.Dimension, MxType.Text, S, "Fixed salary", "নির্দিষ্ট বেতন", "IsFixed_A", "IsFixed"),
        F("ot_rate", "salary", MxRole.Measure, MxType.Real, S, "OT rate", "ওভারটাইম হার", "OT Rate", "OTRate"),
        F("ot_amount", "salary", MxRole.Measure, MxType.Real, S, "OT amount", "ওভারটাইম অর্থ", "OT Amount", "OTAmount"),
        F("attendance_bonus", "salary", MxRole.Measure, MxType.Real, S, "Attendance bonus", "হাজিরা বোনাস", "Attendance Bonus", "AttendanceBonus", "AttBonus"),
        F("stamp", "salary", MxRole.Measure, MxType.Real, S, "Stamp", "স্ট্যাম্প", "Stamp"),
        F("advance", "salary", MxRole.Measure, MxType.Real, S, "Advance", "অগ্রিম", "Advance"),
        F("net_pay", "salary", MxRole.Measure, MxType.Real, S, "Net pay", "নিট বেতন", "Net Pay", "NetPay", "Net Salary"),
        F("payment_mode", "salary", MxRole.Dimension, MxType.Text, S, "Payment mode", "পরিশোধের মাধ্যম", "PaymentMode", "PaymentModeName", "Payment Mode", "Pay Mode"),
        F("new_current_salary", "salary", MxRole.Measure, MxType.Real, S, "New current salary", "নতুন বর্তমান বেতন", "NewCurrentSalary", "New Current Salary"),
        F("tiffin_allow", "salary", MxRole.Measure, MxType.Real, S, "Tiffin allowance", "টিফিন ভাতা", "TiffinAllow", "Tiffin Allowance"),
        F("iftar_allow", "salary", MxRole.Measure, MxType.Real, S, "Iftar allowance", "ইফতার ভাতা", "IftarAllow", "Iftar Allowance"),
        F("night_allow", "salary", MxRole.Measure, MxType.Real, S, "Night allowance", "রাত্রিকালীন ভাতা", "NightAllow", "Night Allowance"),
        F("night_incentive", "salary", MxRole.Measure, MxType.Real, S, "Night incentive", "রাত্রিকালীন প্রণোদনা", "NightInsentive", "NightIncentive", "Night Incentive"),
        F("holiday_allow", "salary", MxRole.Measure, MxType.Real, S, "Holiday allowance", "ছুটির দিনের ভাতা", "HolidayAllow", "Holiday Allowance"),
        F("fixed_ot", "salary", MxRole.Measure, MxType.Real, S, "Fixed OT", "নির্দিষ্ট ওভারটাইম", "FixedOT", "Fixed OT"),
        F("salary_type", "salary", MxRole.Dimension, MxType.Text, S, "Salary type", "বেতনের ধরন", "SalaryType", "Salary Type"),
        F("salary_formula_id", "salary", MxRole.Text, MxType.Text, S, "Salary formula (system)", "বেতনের সূত্র (সিস্টেম)", "CalculationFormulaId", "Calculation Formula Id"),
        F("salary_mismatch", "salary", MxRole.Dimension, MxType.Text, S, "Salary mismatch flag", "বেতন অমিল", "SalaryMissSMactch", "SalaryMismatch", "Salary Mismatch"),
        F("currency_id", "salary", MxRole.Dimension, MxType.Text, P, "Currency", "মুদ্রা", "CurrencyId", "Currency"),
    };

    private static readonly Dictionary<string, MxFieldDef> ByKey = All.ToDictionary(f => f.Key);

    public static MxFieldDef? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var f) ? f : null;

    /// <summary>Lowercase letters and digits only, any script: "Deduction OT " → "deductionot".</summary>
    public static string Normalise(string? header) =>
        Regex.Replace((header ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]", "");

    private static readonly Dictionary<string, List<MxFieldDef>> BySynonym = All
        .SelectMany(f => f.Synonyms.Select(s => (Key: Normalise(s), Field: f)))
        .GroupBy(x => x.Key)
        .ToDictionary(g => g.Key, g => g.Select(x => x.Field).ToList());

    /// <summary>
    /// The field a header means, by synonym. A spelling shared by two fields
    /// ("Status", "Date") goes to the one whose report kind the table is.
    /// </summary>
    public static MxFieldDef? MatchSynonym(string header, string kind)
    {
        if (!BySynonym.TryGetValue(Normalise(header), out var fields)) return null;
        return fields.FirstOrDefault(f => f.OnlyForKinds?.Contains(kind) == true)
            ?? fields.FirstOrDefault(f => f.OnlyForKinds is null);
    }

    // ---- sensitive headers (§3.4): dropped at import ----

    private static readonly (string Category, Regex Pattern)[] SensitivePatterns =
    {
        ("date of birth", new Regex(@"dateofbirth|^dob$|birthdate|^birthday")),
        ("NID", new Regex(@"^nid|nationalid|nidno|smartcard")),
        ("birth certificate", new Regex(@"birthcert|birthreg")),
        ("BGMEA ID", new Regex(@"bgmea")),
        ("bank", new Regex(@"bank|branch|accountno|accno|acno|^account|routing|bkash|nagad|walletno")),
        ("emergency contact", new Regex(@"emerg")),
        ("phone", new Regex(@"mobile|phone|contact|pabx|cellno|telephone")),
        ("e-mail", new Regex(@"email|^mail")),
        ("address", new Regex(@"address|village|road|postoffice|postcode|thana|upazila|upozila|district|division|policestation|union")),
        ("family", new Regex(@"father|mother|spouse|husband|wife|guardian|nominee")),
        ("religion", new Regex(@"religion")),
        ("blood group", new Regex(@"blood")),
        ("passport", new Regex(@"passport|tin$|etin")),
    };

    /// <summary>The sensitive category a header names, or null.</summary>
    public static string? SensitiveCategory(string header)
    {
        var n = Normalise(header);
        if (n.Length == 0) return null;
        foreach (var (category, pattern) in SensitivePatterns)
            if (pattern.IsMatch(n)) return category;
        return null;
    }

    /// <summary>Date of birth is read once, for age_band, and then dropped like the rest.</summary>
    public static bool IsDateOfBirth(string header) => SensitiveCategory(header) == "date of birth";

    // ---- report kinds ----

    /// <summary>The report kind of a table from the fields its headers map to.</summary>
    public static string DetectKind(IReadOnlyCollection<string> headers)
    {
        var norm = headers.Select(Normalise).ToHashSet();
        bool Has(string key) => Find(key)!.Synonyms.Any(s => norm.Contains(Normalise(s)));

        if (Has("in_time") || Has("out_time") || norm.Contains("attstatus")) return MxReportKind.AttendanceDaily;
        if ((Has("equivalency") || Has("equivalency_grade")) && Has("designation") && headers.Count <= 6) return MxReportKind.DesignationLookup;
        if (Has("punch_no") || headers.Count >= 90) return MxReportKind.Master;
        if (Has("emp_id") && Has("designation") && (Has("unit") || Has("unit_type")) && headers.Count >= 20) return MxReportKind.Manpower;
        if (Has("status") && (Has("effect_date") || Has("separation_date"))) return MxReportKind.SeparationList;
        return MxReportKind.Custom;
    }

    // ---- derived values ----

    private static readonly Regex LinePattern = new(@"^\s*line\s*[-–:#]?\s*(\d+)", RegexOptions.IgnoreCase);

    /// <summary>"LINE - 143" → 143 (Q4's default: a sewing line is named LINE - &lt;n&gt;).</summary>
    public static int? LineNumber(string? wingOrLine) =>
        wingOrLine is not null && LinePattern.Match(wingOrLine) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;

    /// <summary>Sewing, Cutting, Finishing, Washing, Packing, Button Stitch or Other, from the section.</summary>
    public static string? LineType(string? section)
    {
        if (string.IsNullOrWhiteSpace(section)) return null;
        var s = section.ToLowerInvariant();
        if (s.Contains("sew")) return "Sewing";
        if (s.Contains("cut")) return "Cutting";
        if (s.Contains("finish")) return "Finishing";
        if (s.Contains("wash")) return "Washing";
        if (s.Contains("pack")) return "Packing";
        if (s.Contains("button")) return "Button Stitch";
        return "Other";
    }

    public static string TenureBand(double years) => years switch
    {
        < 1 => "<1",
        < 3 => "1–3",
        < 5 => "3–5",
        < 10 => "5–10",
        _ => "10+",
    };

    public static string AgeBand(int years) => years switch
    {
        < 18 => "<18",
        <= 25 => "18–25",
        <= 35 => "26–35",
        <= 45 => "36–45",
        <= 55 => "46–55",
        _ => "55+",
    };
}

/// <summary>The normalisers relations and quality checks use, by name (§3.2 MxRelation.Normaliser).</summary>
public static class MatrixNormalisers
{
    public const string EmpId6 = "emp-id-6";

    /// <summary>Text compared trimmed and lower-cased — designations against the equivalency lookup.</summary>
    public const string TrimLower = "trim-lower";

    /// <summary>"512" → "000512"; text that is not all digits is returned trimmed.</summary>
    public static string? PadEmpId(string? id)
    {
        var t = id?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.All(char.IsAsciiDigit) && t.Length < 6 ? t.PadLeft(6, '0') : t;
    }
}
