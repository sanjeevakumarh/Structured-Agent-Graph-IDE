using SAGIDE.Core.Models;
using SAGIDE.Service.Prompts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests that Scriban templates in skill YAML files render correctly with the variable
/// context they receive at execution time.  These are pure in-process rendering tests
/// (no LLM calls, no I/O beyond YAML loading) so they run in milliseconds and catch
/// template bugs deterministically — filling the gap between structural integration tests
/// and full end-to-end runs.
///
/// Rendering path exercised:
///   SubtaskCoordinator.ExpandSkillRefs → CloneStepRendered (SectionAnalysisPrompt
///   passed through unchanged) → per-section sectionVars built with ["parameters"] +
///   ["section_name"] → PromptTemplate.RenderRaw(template, sectionVars)
/// </summary>
public class SkillRenderingTests
{
    private readonly string _repoRoot;
    private readonly IDeserializer _yaml;

    public SkillRenderingTests()
    {
        _repoRoot = FindRepoRoot();
        _yaml = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    // ── study-guide-generator ────────────────────────────────────────────────
    // These tests verify the five-section if-else chain in section_analysis_prompt
    // renders correctly when the section name and parameters are in scope — which
    // requires the SectionAnalysisPrompt pass-through fix in CloneStepRendered.

    [SkippableTheory]
    [InlineData("Orientation (101)",     "### 101 Orientation")]
    [InlineData("Core Concepts (201)",   "### Core Concepts (201)")]
    [InlineData("Advanced Topics (301)", "### Advanced Topics (301)")]
    [InlineData("Practice & Projects",  "### Practice & Projects")]
    [InlineData("Assessment & Timeline","### Assessment & Timeline")]
    public void StudyGuideGenerator_KnownSection_RendersCorrectHeading(
        string sectionName, string expectedHeading)
    {
        var template = LoadSectionAnalysisPrompt("shared", "study-guide-generator");
        var rendered = RenderStudyGuideSection(template, sectionName);

        Assert.Contains(expectedHeading, rendered);
    }

    [SkippableTheory]
    [InlineData("Orientation (101)")]
    [InlineData("Core Concepts (201)")]
    [InlineData("Advanced Topics (301)")]
    [InlineData("Practice & Projects")]
    [InlineData("Assessment & Timeline")]
    public void StudyGuideGenerator_KnownSection_ContainsGateSignalBlock(string sectionName)
    {
        var template = LoadSectionAnalysisPrompt("shared", "study-guide-generator");
        var rendered = RenderStudyGuideSection(template, sectionName);

        Assert.Contains("### GATE_SIGNAL", rendered);
        Assert.Contains("SIGNAL:",         rendered);
        Assert.Contains("RATIONALE:",      rendered);
    }

    [SkippableFact]
    public void StudyGuideGenerator_UnknownSection_NoSectionBodyOrGateSignal()
    {
        var template = LoadSectionAnalysisPrompt("shared", "study-guide-generator");
        // All {{ if/else if }} branches are false for an unknown section name so no
        // section-specific instructions or GATE_SIGNAL block are rendered.
        // The parameter header lines (Subject, Learner profile …) precede the branch
        // and always render, but no section body or signal is produced.
        var rendered = RenderStudyGuideSection(template, "Nonexistent Section");

        Assert.DoesNotContain("### GATE_SIGNAL", rendered);
        Assert.DoesNotContain("### 101 Orientation", rendered);
        Assert.DoesNotContain("⚠ OUTPUT RULE", rendered);
    }

    [SkippableFact]
    public void StudyGuideGenerator_SubjectAndLearnerBackground_AppearInRenderedOutput()
    {
        var template = LoadSectionAnalysisPrompt("shared", "study-guide-generator");
        var rendered = RenderStudyGuideSection(template, "Orientation (101)",
            subject: "Rust programming",
            learnerBackground: "C++ expert with 10 years systems experience");

        Assert.Contains("Rust programming", rendered);
        Assert.Contains("C++ expert with 10 years systems experience", rendered);
    }

    [SkippableFact]
    public void StudyGuideGenerator_EmptyParameters_StringDefaultFallbacksApply()
    {
        var template = LoadSectionAnalysisPrompt("shared", "study-guide-generator");
        // Pass empty parameters dict — string.default filter must supply fallback values.
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["section_name"] = "Orientation (101)",
            ["parameters"]   = new Dictionary<string, object>(),
        };
        var rendered = PromptTemplate.RenderRaw(template, vars);

        Assert.Contains("assume novice", rendered);           // string.default for learner_background
        Assert.Contains("reach working proficiency", rendered); // string.default for goals
    }

    [SkippableFact]
    public void StudyGuideGenerator_AssessmentTimeline_AssessmentStyleParamAppearsInOutput()
    {
        var template = LoadSectionAnalysisPrompt("shared", "study-guide-generator");
        const string customStyle = "monthly peer code reviews and a capstone demo";
        var rendered = RenderStudyGuideSection(template, "Assessment & Timeline",
            assessmentStyle: customStyle);

        Assert.Contains(customStyle, rendered);
    }

    [SkippableFact]
    public void StudyGuideGenerator_SectionAnalysisPrompt_RawTemplatePreservesControlFlow()
    {
        // Verifies the SectionAnalysisPrompt is NOT pre-rendered at expansion time
        // (i.e. CloneStepRendered still treats it as a pass-through).
        // If someone re-adds Render(src.SectionAnalysisPrompt) in CloneStepRendered,
        // the if-else chain would disappear from the stored template and this test fails.
        var template = LoadSectionAnalysisPrompt("shared", "study-guide-generator");

        Assert.Contains("{{ if section_name ==", template);
        Assert.Contains("{{ end }}",              template);
    }

    // ── evidence-normalizer ──────────────────────────────────────────────────
    // Verify the two prompt_template strings pass search-result placeholders through
    // (they are pass-through templates rendered at execution time with vars in scope).

    [SkippableFact]
    public void EvidenceNormalizer_EntityInventory_PromptContainsAllFourResultVars()
    {
        var template = LoadPromptTemplate("research", "evidence-normalizer", "entity_inventory");

        Assert.Contains("{{market_results}}",  template);
        Assert.Contains("{{tech_results}}",    template);
        Assert.Contains("{{finance_results}}", template);
        Assert.Contains("{{risk_results}}",    template);
    }

    [SkippableFact]
    public void EvidenceNormalizer_NormalizeEvidence_PromptContainsAllFourResultVars()
    {
        var template = LoadPromptTemplate("research", "evidence-normalizer", "normalize_evidence");

        Assert.Contains("{{market_results}}",  template);
        Assert.Contains("{{tech_results}}",    template);
        Assert.Contains("{{finance_results}}", template);
        Assert.Contains("{{risk_results}}",    template);
    }

    [SkippableFact]
    public void EvidenceNormalizer_EntityInventory_RendersWithSearchResultsFromVars()
    {
        var template = LoadPromptTemplate("research", "evidence-normalizer", "entity_inventory");
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["market_results"]  = "Synology NAS, QNAP market share data",
            ["tech_results"]    = "ZFS, Btrfs benchmarks",
            ["finance_results"] = "ARR $2M seed round",
            ["risk_results"]    = "GDPR data residency rules",
        };
        var rendered = PromptTemplate.RenderRaw(template, vars);

        Assert.Contains("Synology NAS, QNAP market share data", rendered);
        Assert.Contains("ZFS, Btrfs benchmarks",                rendered);
    }

    // ── stock-data-track search disambiguation ────────────────────────────────
    // These tests verify that the planning_prompt correctly includes asset_type
    // in the qualified form (exchange set) and bare ticker form (exchange empty),
    // preventing search ambiguity like "BATS: PICK" matching British American Tobacco.

    [SkippableFact]
    public void StockDataTrack_PlanningPrompt_WithExchange_ContainsAssetTypeInQualifiedForm()
    {
        var skill = LoadSkill("finance", "stock-data-track");
        var impl = skill.Implementation.First(s => s.Name == "search");
        Assert.False(string.IsNullOrEmpty(impl.PlanningPrompt));

        var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["ticker"]     = "PICK",
            ["exchange"]   = "BATS",
            ["asset_type"] = "etf",
            ["context"]    = "General investor",
            ["max_results"] = "6",
            ["output_var"] = "all_search_results",
        };
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["parameters"] = parameters,
            ["date"]       = "2026-03-04",
        };
        var rendered = PromptTemplate.RenderRaw(impl.PlanningPrompt!, vars);

        // The qualified form should include both exchange:ticker AND asset_type
        Assert.Contains("BATS: PICK etf", rendered);
        // The bare-ticker else branch should NOT appear
        Assert.DoesNotContain("PICK stock\" for stocks", rendered);
    }

    [SkippableFact]
    public void StockDataTrack_PlanningPrompt_WithoutExchange_ContainsBareTickerWithType()
    {
        var skill = LoadSkill("finance", "stock-data-track");
        var impl = skill.Implementation.First(s => s.Name == "search");
        Assert.False(string.IsNullOrEmpty(impl.PlanningPrompt));

        var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["ticker"]     = "AAPL",
            ["exchange"]   = "",
            ["asset_type"] = "stock",
            ["context"]    = "Growth investor",
            ["max_results"] = "5",
            ["output_var"] = "all_search_results",
        };
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["parameters"] = parameters,
            ["date"]       = "2026-03-04",
        };
        var rendered = PromptTemplate.RenderRaw(impl.PlanningPrompt!, vars);

        // The bare ticker branch should render (else branch)
        Assert.Contains("AAPL stock", rendered);
        Assert.Contains("AAPL ETF", rendered);
        // The exchange-qualified form should NOT appear
        Assert.DoesNotContain("Exchange:", rendered);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private string LoadSectionAnalysisPrompt(string domain, string skillName)
    {
        var skill = LoadSkill(domain, skillName);
        var impl  = skill.Implementation.FirstOrDefault(s => !string.IsNullOrEmpty(s.SectionAnalysisPrompt));
        Assert.NotNull(impl);
        return impl.SectionAnalysisPrompt!;
    }

    private string LoadPromptTemplate(string domain, string skillName, string stepName)
    {
        var skill = LoadSkill(domain, skillName);
        var impl  = skill.Implementation.FirstOrDefault(s => s.Name == stepName);
        Assert.NotNull(impl);
        Assert.False(string.IsNullOrEmpty(impl.PromptTemplate),
            $"Step '{stepName}' in skill '{skillName}' has no prompt_template");
        return impl.PromptTemplate!;
    }

    private SkillDefinition LoadSkill(string domain, string skillName)
    {
        var path = Path.Combine(_repoRoot, "skills", $"{domain}.{skillName}.yaml");
        if (!File.Exists(path))
            path = Path.Combine(_repoRoot, "skills", domain, $"{skillName}.yaml");
        Skip.If(!File.Exists(path), $"Skill YAML not present: skills/{domain}.{skillName}.yaml");
        return _yaml.Deserialize<SkillDefinition>(File.ReadAllText(path));
    }

    private static string RenderStudyGuideSection(
        string template,
        string sectionName,
        string subject            = "distributed systems",
        string learnerBackground  = "Python developer, new to theory",
        string goals              = "job-ready proficiency",
        string timeframe          = "12 weeks",
        string hoursPerWeek       = "8",
        string preferredResources = "textbooks",
        string assessmentStyle    = "weekly quizzes")
    {
        var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["subject"]             = subject,
            ["learner_background"]  = learnerBackground,
            ["goals"]               = goals,
            ["timeframe"]           = timeframe,
            ["hours_per_week"]      = hoursPerWeek,
            ["preferred_resources"] = preferredResources,
            ["assessment_style"]    = assessmentStyle,
        };
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["section_name"] = sectionName,
            ["parameters"]   = parameters,
        };
        return PromptTemplate.RenderRaw(template, vars);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (.git not found).");
    }
}
