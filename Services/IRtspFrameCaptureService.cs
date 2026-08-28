using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// Narrow lifecycle boundary used by the unified capture coordinator. Keeping the
    /// decoder behind this interface lets the active preview remain the preferred source
    /// while retaining a testable, bounded independent health fallback.
    /// </summary>
    internal interface IRtspFrameCaptureService : IDisposable
    {
        bool IsInitializedFor(string rtspUrl);

        Task<bool> InitializeAsync(
            string rtspUrl,
            CancellationToken cancellationToken = default);

        Task<Bitmap?> CaptureFrameAsync(
            CancellationToken cancellationToken = default);

        Task<bool> SaveFrameAsync(
            Bitmap frame,
            string filePath,
            CancellationToken cancellationToken = default);

        void Reset();
    }
}
