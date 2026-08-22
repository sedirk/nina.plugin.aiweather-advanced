using AIWeather.Models;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// Interface for weather analysis services
    /// </summary>
    public interface IWeatherAnalysisService
    {
        Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, AstroContext? astroContext = null, CancellationToken cancellationToken = default);
        Task<bool> InitializeAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// An online teacher that never performs an internal local fallback. The monitor owns
    /// fallback selection so provenance remains unambiguous.
    /// </summary>
    public interface IOnlineWeatherAnalysisService : IWeatherAnalysisService
    {
        Task<OnlineAnalysisAttempt> TryAnalyzeOnlineOnlyAsync(
            Bitmap image,
            AstroContext? astroContext = null,
            CancellationToken cancellationToken = default);
    }
}
