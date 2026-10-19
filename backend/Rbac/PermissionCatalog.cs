// Access — the permission catalog. THE list of what can be allowed.
//
// Every tab, section and action is declared here once, with its labels, its
// group in the module menu and its parent. Nothing else may list them: the role
// presets are seeded from this list (RolePresetSeeds), the policy works out a
// user's access over it (AccessPolicy), and the frontend draws the Access screen
// and the module menu from it (GET /api/access/catalog, GET /api/me/access).
//
// Keys:   tab:<key>                 the shell's own tab id, e.g. tab:chat, tab:rbac
//         section:<tab>.<section>   e.g. section:hr.rulebook
//         action:<area>.<verb>      e.g. action:hr.rulebook.confirm
//
// Parents: a section needs its tab, an action its section (or its tab when it has
// no section). A child allowed without its parent has no effect.

namespace RabbitIC.Api.Rbac;

/// <summary>One permission. <paramref name="Label"/> is how the shell names a tab today; it defaults to <paramref name="En"/>.</summary>
public sealed record PermissionDef(
    string Key, string Kind, string En, string Bn, string Group, string? Parent, string? Label = null)
{
    public string DisplayLabel => Label ?? En;
}

public static class PermissionKeys
{
    public const string TabSettings = "tab:settings";
    public const string TabRbac = "tab:rbac";
    public const string TabOddity = "tab:oddity";

    public const string HrAsk = "section:hr.ask";
    public const string HrLibrary = "section:hr.library";
    public const string HrRulebook = "section:hr.rulebook";
    public const string HrProcedures = "section:hr.procedures";

    public const string HrLibraryEdit = "action:hr.library.edit";
    public const string HrLibraryStats = "action:hr.library.stats";
    public const string HrRulebookEdit = "action:hr.rulebook.edit";
    public const string HrRulebookConfirm = "action:hr.rulebook.confirm";
    public const string HrProceduresUpload = "action:hr.procedures.upload";
    public const string HrProceduresAnswer = "action:hr.procedures.answer";
    public const string HrLookupsEdit = "action:hr.lookups.edit";
    public const string PoProfilesEdit = "action:po.profiles.edit";
    public const string LogsAudit = "action:logs.audit";
    public const string AccessManage = "action:access.manage";

    public static string Tab(string tabKey) => "tab:" + tabKey;
}

public static class PermissionCatalog
{
    public const string KindTab = "tab";
    public const string KindSection = "section";
    public const string KindAction = "action";

    /// <summary>The module menu's groups, in its order. Favourites is the user's own, not a group.</summary>
    public static readonly string[] Groups = { "Work", "Tools", "System" };

    private static PermissionDef Tab(string key, string en, string bn, string group, string? label = null) =>
        new(PermissionKeys.Tab(key), KindTab, en, bn, group, null, label);

    private static PermissionDef Section(string key, string en, string bn, string group, string parent) =>
        new(key, KindSection, en, bn, group, parent);

    private static PermissionDef Action(string key, string en, string bn, string group, string parent) =>
        new(key, KindAction, en, bn, group, parent);

    /// <summary>
    /// Every permission, in display order: the tabs in the order the module menu
    /// lists them, each tab followed by its sections and actions.
    /// </summary>
    public static readonly IReadOnlyList<PermissionDef> All = new[]
    {
        // ── Work ──
        Tab("dashboard", "Dashboard", "ড্যাশবোর্ড", "Work"),
        Tab("chat", "Chat", "চ্যাট", "Work"),
        Tab("workspace", "Workspace Studio", "ওয়ার্কস্পেস স্টুডিও", "Work"),
        // Documents has no module of its own (Workspace Studio shows it), but it
        // gates /api/documents, /api/buyer-profiles and /api/po-studio.
        Tab("docs", "Documents", "ডকুমেন্টস", "Work"),
        Action(PermissionKeys.PoProfilesEdit, "Edit PO Studio buyer profiles", "বায়ার প্রোফাইল সম্পাদনা", "Work", "tab:docs"),
        Tab("hr", "HR", "মানব সম্পদ", "Work", label: "মানব সম্পদ"),
        Section(PermissionKeys.HrAsk, "Ask", "প্রশ্ন", "Work", "tab:hr"),
        Section(PermissionKeys.HrLibrary, "Library", "লাইব্রেরি", "Work", "tab:hr"),
        Action(PermissionKeys.HrLibraryEdit, "Edit the library", "লাইব্রেরি সম্পাদনা", "Work", PermissionKeys.HrLibrary),
        Action(PermissionKeys.HrLibraryStats, "See the index statistics", "ইনডেক্সের পরিসংখ্যান", "Work", PermissionKeys.HrLibrary),
        Section(PermissionKeys.HrRulebook, "Rulebook", "নিয়মাবলি", "Work", "tab:hr"),
        Action(PermissionKeys.HrRulebookEdit, "Write and edit Rulebook pages", "নিয়মাবলি লেখা ও সম্পাদনা", "Work", PermissionKeys.HrRulebook),
        Action(PermissionKeys.HrRulebookConfirm, "Confirm a Rulebook page", "নিয়মাবলি পাতা নিশ্চিত করা", "Work", PermissionKeys.HrRulebook),
        Section(PermissionKeys.HrProcedures, "Procedures", "প্রসিডিউর", "Work", "tab:hr"),
        Action(PermissionKeys.HrProceduresUpload, "Upload a procedure", "প্রসিডিউর আপলোড", "Work", PermissionKeys.HrProcedures),
        Action(PermissionKeys.HrProceduresAnswer, "Answer a procedure question", "প্রশ্নের উত্তর দেওয়া", "Work", PermissionKeys.HrProcedures),
        Action(PermissionKeys.HrLookupsEdit, "Edit the code registry", "কোড রেজিস্ট্রি সম্পাদনা", "Work", PermissionKeys.HrProcedures),
        Tab("todo", "To-Do", "করণীয়", "Work"),
        Tab("search", "Search", "অনুসন্ধান", "Work"),
        Tab("notes", "Notes", "নোট", "Work"),

        // ── Tools ──
        Tab("jsonto", "JSON to ...", "JSON রূপান্তর", "Tools"),
        Tab("5lakhs", "5Lakhs", "৫ লাখ", "Tools"),
        Tab("ielts", "IELTS", "আইইএলটিএস", "Tools"),
        Tab("component-splitter", "Splitter", "কম্পোনেন্ট বিভাজক", "Tools"),

        // ── System ──
        Tab("sources", "Sources", "উৎস", "System"),
        Tab("settings", "Settings", "সেটিংস", "System"),
        Tab("rbac", "Access", "অ্যাক্সেস", "System"),
        Action(PermissionKeys.AccessManage, "Manage access", "অ্যাক্সেস পরিচালনা", "System", PermissionKeys.TabRbac),
        Tab("logs", "Logs", "লগ", "System"),
        Action(PermissionKeys.LogsAudit, "Read the audit log", "অডিট লগ পড়া", "System", "tab:logs"),
        Tab("oddity", "Oddity", "অডিটি", "System"),
    };

    private static readonly Dictionary<string, PermissionDef> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static PermissionDef? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key.Trim(), out var def) ? def : null;

    public static bool IsKnown(string? key) => Find(key) is not null;

    /// <summary>The tab keys without their prefix, in display order — e.g. "chat".</summary>
    public static IEnumerable<string> TabKeys =>
        All.Where(p => p.Kind == KindTab).Select(p => p.Key["tab:".Length..]);
}
