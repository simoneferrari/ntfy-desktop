using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NtfyDesktop.Features.Rules.Ai;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Rules;

public static class RulesFeature
{
    extension(IServiceCollection services)
    {
        public void AddRules()
        {
            services.AddSingleton<IIncidentStore>(sp => new IncidentStore(
                Path.Combine(App.DataPath, "rules.db"),
                sp.GetRequiredService<AppSettings>().GetOrCreateHistoryKey()));

            services.AddSingleton<PackStore>(_ => new PackStore(
                Path.Combine(App.DataPath, "rules")));

            services.AddSingleton<RuleEngine>();

            services.AddSingleton<RulePackHistoryService>();

            services.AddSingleton<ExpectationStore>(sp => new ExpectationStore(
                Path.Combine(App.DataPath, "rules.db"),
                sp.GetRequiredService<AppSettings>().GetOrCreateHistoryKey()));

            services.AddHostedService<ExpectationMonitor>();

            // ===== AI-assisted authoring (Phase 1c) =====

            services.AddSingleton<ProviderPresets>(_ =>
            {
                // Built-in list (ships + updates with the app) merged with an optional
                // user providers.json in the data folder for overrides/additions.
                var presets = new ProviderPresets(Path.Combine(App.DataPath, "providers.json"));
                var bundled = Path.Combine(AppContext.BaseDirectory, "assets", "providers.json");
                presets.Load(File.Exists(bundled) ? File.ReadAllText(bundled) : "[]");
                return presets;
            });

            services.AddSingleton<ModelCatalog>();

            services.AddSingleton<IChatClient>(sp =>
            {
                var settings = sp.GetRequiredService<AppSettings>();
                return new OpenAiChatClient(() => (settings.AiBaseUrl, settings.AiModel, settings.GetAiApiKey()));
            });

            services.AddSingleton<PackDraftService>();
            services.AddTransient<DraftRulesViewModel>();

            // ===== Rule-pack manager (Phase 2) =====

            services.AddTransient<Editor.RulePackManagerViewModel>(sp =>
            {
                var settings = sp.GetRequiredService<AppSettings>();
                return new Editor.RulePackManagerViewModel(
                    sp.GetRequiredService<PackStore>(),
                    sp.GetRequiredService<RulePackHistoryService>(),
                    () => settings.Topics.Select(t => (t.Id, t.EffectiveDisplayName)).ToList());
            });
        }
    }
}
