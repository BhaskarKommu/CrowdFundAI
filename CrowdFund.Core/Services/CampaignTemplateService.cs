namespace CrowdFund.Core.Services;

public record TemplateDocument(string Key, string Label, bool Required, string Description);

public record CampaignTemplate(
    string Key,
    string Name,
    string Category,
    string StoryPlaceholder,
    IReadOnlyList<TemplateDocument> Documents);

public class CampaignTemplateService
{
    private static readonly IReadOnlyList<CampaignTemplate> Templates = new List<CampaignTemplate>
    {
        new("medical", "Medical treatment", "Medical",
            "Describe the patient, diagnosis (at a high level), hospital, and how funds will be used. Do not post sensitive medical records publicly.",
            new[]
            {
                new TemplateDocument("id_proof", "Patient/guardian ID", true, "Government ID for the beneficiary or guardian."),
                new TemplateDocument("medical_estimate", "Hospital estimate / prescription", true, "Cost estimate or treatment plan summary."),
                new TemplateDocument("doctor_letter", "Doctor letter (optional)", false, "Supporting clinical letter if available."),
            }),
        new("education", "Education", "Education",
            "Describe the student, course or school, fees breakdown, and timeline.",
            new[]
            {
                new TemplateDocument("admission_letter", "Admission / enrollment letter", true, "Proof of admission or enrollment."),
                new TemplateDocument("fee_structure", "Fee structure", true, "Official fee schedule."),
                new TemplateDocument("id_proof", "Student ID", true, "Student government or school ID."),
            }),
        new("disaster", "Disaster relief", "Disaster Relief",
            "Describe the event, affected people, location, and intended use of funds.",
            new[]
            {
                new TemplateDocument("situation_summary", "Situation summary", true, "What happened and who is affected."),
                new TemplateDocument("local_verification", "Local verification", true, "Letter or attestation from a local body/NGO if available."),
                new TemplateDocument("budget", "Relief budget", true, "Line items for aid distribution."),
            }),
        new("general", "General community", "Other",
            "Describe the community need and how donations will help.",
            new[]
            {
                new TemplateDocument("plan", "Project plan", true, "Goals, timeline, and budget."),
                new TemplateDocument("identity", "Organizer identity", true, "ID for the primary organizer."),
            }),
    };

    public IReadOnlyList<CampaignTemplate> All() => Templates;

    public CampaignTemplate? Get(string? key) =>
        Templates.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public (bool ok, IReadOnlyList<string> missing) ValidateDocuments(string? templateKey, IEnumerable<string> uploadedDocTypes)
    {
        var t = Get(templateKey);
        if (t == null) return (true, Array.Empty<string>());
        var set = new HashSet<string>(uploadedDocTypes.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        var missing = t.Documents.Where(d => d.Required && !set.Contains(d.Key)).Select(d => d.Label).ToList();
        return (missing.Count == 0, missing);
    }
}
