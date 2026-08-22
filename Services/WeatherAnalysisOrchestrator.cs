using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// Runs the configured analysis and the local student on the same captured frame, then
    /// explicitly chooses the result that enters the existing safety logic.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public sealed class WeatherAnalysisOrchestrator
    {
        private readonly LocalWeatherAnalysisService _student = new LocalWeatherAnalysisService();

        public async Task<WeatherAnalysisBundle> AnalyzeAsync(
            IWeatherAnalysisService configuredService,
            Bitmap image,
            AstroContext? astroContext,
            CancellationToken cancellationToken)
        {
            if (configuredService is LocalWeatherAnalysisService)
            {
                var local = await configuredService.AnalyzeImageAsync(image, astroContext, cancellationToken);
                EnsureLocalProvenance(local);
                return new WeatherAnalysisBundle
                {
                    EffectiveResult = local,
                    Student = local,
                    UsedFallback = false
                };
            }

            // The first student is intentionally sequential for now. GDI+ does not promise
            // that Bitmap.Save (teacher upload) and LockBits (student features) are safe on
            // the same Bitmap concurrently. The heuristic normally takes only milliseconds.
            var student = await _student.AnalyzeImageAsync(image, astroContext, cancellationToken);
            EnsureLocalProvenance(student);

            OnlineAnalysisAttempt teacherAttempt;
            if (configuredService is IOnlineWeatherAnalysisService onlineTeacher)
            {
                teacherAttempt = await onlineTeacher.TryAnalyzeOnlineOnlyAsync(
                    image,
                    astroContext,
                    cancellationToken);
            }
            else
            {
                // Legacy providers are still normalized here. They now attach provenance;
                // a local-origin result can never be mistaken for an online teacher label.
                var candidate = await configuredService.AnalyzeImageAsync(
                    image,
                    astroContext,
                    cancellationToken);

                teacherAttempt = candidate.Provenance.OnlineSucceeded
                    && !candidate.Provenance.IsFallback
                    ? OnlineAnalysisAttempt.Succeeded(candidate)
                    : OnlineAnalysisAttempt.Failed(
                        BuildLegacyTeacherFailure(configuredService, candidate),
                        candidate.Description);
            }

            if (teacherAttempt.Success && teacherAttempt.Result != null)
            {
                return new WeatherAnalysisBundle
                {
                    EffectiveResult = teacherAttempt.Result,
                    Teacher = teacherAttempt,
                    Student = student,
                    UsedFallback = false
                };
            }

            var fallback = student.Clone();
            fallback.Provenance.IsFallback = true;
            fallback.Provenance.FailureCategory = teacherAttempt.Provenance.FailureCategory;
            fallback.Description = BuildFallbackDescription(teacherAttempt, fallback.Description);

            Logger.Warning(
                $"Teacher analysis failed ({teacherAttempt.Provenance.Provider}/" +
                $"{teacherAttempt.Provenance.Model}, {teacherAttempt.Provenance.FailureCategory}); " +
                "using explicit local heuristic fallback");

            return new WeatherAnalysisBundle
            {
                EffectiveResult = fallback,
                Teacher = teacherAttempt,
                Student = student,
                UsedFallback = true
            };
        }

        private static void EnsureLocalProvenance(WeatherAnalysisResult result)
        {
            if (result.Provenance.Origin == AnalysisOrigin.Unknown)
            {
                result.Provenance = AnalysisMetadata.Local(0);
            }
        }

        private static string BuildFallbackDescription(
            OnlineAnalysisAttempt attempt,
            string localDescription)
        {
            var source = string.IsNullOrWhiteSpace(attempt.Provenance.Provider)
                ? "online teacher"
                : attempt.Provenance.Provider;
            return $"[Fallback: Local] {source} failed ({attempt.Provenance.FailureCategory}). {localDescription}";
        }

        private static AnalysisProvenance BuildLegacyTeacherFailure(
            IWeatherAnalysisService service,
            WeatherAnalysisResult candidate)
        {
            var identity = service switch
            {
                OpenAIAnalysisService => (AnalysisOrigin.OpenAI, "OpenAI"),
                AnthropicAnalysisService => (AnalysisOrigin.Anthropic, "Anthropic"),
                OllamaAnalysisService => (AnalysisOrigin.Ollama, "Ollama"),
                GitHubModelsAnalysisService => (AnalysisOrigin.GitHubModels, "GitHubModels"),
                _ => (AnalysisOrigin.Unknown, service.GetType().Name)
            };
            var category = candidate.Provenance.FailureCategory == AnalysisFailureCategory.None
                ? AnalysisFailureCategory.Unknown
                : candidate.Provenance.FailureCategory;
            return AnalysisMetadata.FailedOnline(
                identity.Item1,
                identity.Item2,
                Properties.Settings.Default.SelectedModel ?? "unknown",
                category,
                candidate.Provenance.Attempts,
                candidate.Provenance.LatencyMilliseconds,
                candidate.Provenance.HttpStatus);
        }
    }
}
