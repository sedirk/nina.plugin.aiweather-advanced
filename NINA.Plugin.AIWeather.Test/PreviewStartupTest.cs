using System.Diagnostics;
using System.Drawing;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test;

[TestFixture]
public class PreviewStartupTest
{
    private const string Url = "rtsp://camera.invalid:554/test";

    [Test]
    public void StopInvalidatesBothCurrentAndQueuedStarts()
    {
        var state = new PreviewStartupState();
        var first = state.Start();
        var queued = state.Start();
        state.Stop();
        state.IsCurrent(first).Should().BeFalse();
        state.IsCurrent(queued).Should().BeFalse();
        var restarted = state.Start();
        state.IsCurrent(restarted).Should().BeTrue();
    }


    [Test]
    public async Task PendingReservationNeverInvokesSnapshotOrOpensSecondDecoder()
    {
        var provider = new SharedRtspPreviewFrameProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.SourceReserved += (_, ct) => release.Task.WaitAsync(ct);
        using var decoder = new FakeDecoder();
        using var capture = new UnifiedCaptureService(null, provider, decoder);
        capture.ConfigureRTSP(Url);
        var snapshots = 0;
        var registrationTask = provider.RegisterAsync(Url, _ =>
        {
            snapshots++;
            return Task.FromResult<Bitmap?>(new Bitmap(8, 8));
        });
        try
        {
            using var frame = await capture.CaptureImageAsync().WaitAsync(TimeSpan.FromSeconds(1));
            frame.Should().BeNull();
            snapshots.Should().Be(0);
            decoder.OpenCalls.Should().Be(0);
        }
        finally
        {
            release.TrySetResult();
            (await registrationTask).Dispose();
        }
    }

    [Test]
    public async Task WaitingForPlayingDoesNotFallBackToAnotherDecoder()
    {
        var provider = new SharedRtspPreviewFrameProvider();
        using var decoder = new FakeDecoder();
        using var capture = new UnifiedCaptureService(null, provider, decoder);
        capture.ConfigureRTSP(Url);
        bool ready = false;
        using var registration = await provider.RegisterAsync(Url,
            _ => Task.FromResult<Bitmap?>(new Bitmap(8, 8)), ready: () => ready);
        using var pending = await capture.CaptureImageAsync();
        pending.Should().BeNull();
        decoder.OpenCalls.Should().Be(0);
        ready = true;
        using var first = await capture.CaptureImageAsync();
        first.Should().NotBeNull();
        decoder.OpenCalls.Should().Be(0);
    }

    [Test]
    public async Task CancelWhileReservingReleasesTheSourceImmediately()
    {
        var provider = new SharedRtspPreviewFrameProvider();
        var uncooperative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.SourceReserved += (_, _) => uncooperative.Task;
        using var stop = new CancellationTokenSource();
        var start = provider.RegisterAsync(Url, _ => Task.FromResult<Bitmap?>(null), stop.Token);
        provider.HasActiveSource(Url).Should().BeTrue();
        stop.Cancel();
        Func<Task> action = async () => await start.WaitAsync(TimeSpan.FromSeconds(1));
        await action.Should().ThrowAsync<OperationCanceledException>();
        provider.HasActiveSource(Url).Should().BeFalse();
        uncooperative.TrySetResult();
    }

    [Test]
    public async Task PreviewReservationCancelsAnInFlightBackgroundOpen()
    {
        var provider = new SharedRtspPreviewFrameProvider();
        using var decoder = new FakeDecoder { Stall = true };
        using var capture = new UnifiedCaptureService(null, provider, decoder);
        capture.ConfigureRTSP(Url);
        var background = capture.CaptureImageAsync();
        await decoder.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var registration = await provider.RegisterAsync(Url, _ =>
            Task.FromResult<Bitmap?>(new Bitmap(8, 8))).WaitAsync(TimeSpan.FromSeconds(1));
        using var abandoned = await background;
        abandoned.Should().BeNull();
        decoder.OpenCancelled.Should().BeTrue();
        using var preview = await capture.CaptureImageAsync();
        preview.Should().NotBeNull();
        decoder.OpenCalls.Should().Be(1);
    }

    private sealed class FakeDecoder : IRtspFrameCaptureService
    {
        public int OpenCalls;
        public bool Stall;
        public bool OpenCancelled;
        public TaskCompletionSource OpenStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsInitializedFor(string url) => false;
        public async Task<bool> InitializeAsync(string url, CancellationToken ct = default)
        {
            OpenCalls++;
            OpenStarted.TrySetResult();
            if (Stall)
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { OpenCancelled = true; throw; }
            }
            return true;
        }
        public Task<Bitmap?> CaptureFrameAsync(CancellationToken ct = default) =>
            Task.FromResult<Bitmap?>(null);
        public Task<bool> SaveFrameAsync(Bitmap frame, string file, CancellationToken ct = default) =>
            Task.FromResult(true);
        public void Reset() { }
        public void Dispose() { }
    }
}
