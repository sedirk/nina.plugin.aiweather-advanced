using AIWeather.Models;
using AIWeather.Services;
using AIWeather.Localization;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace AIWeather
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public sealed class DatasetLabelReviewViewModel : INotifyPropertyChanged
    {
        public const string AllStatusFilter = "All";

        private readonly DatasetReviewService _service;
        private readonly Func<string, string, bool> _confirmDeletion;
        private readonly ObservableCollection<DatasetReviewItemViewModel> _samples = new();
        private DatasetReviewItemViewModel? _selectedItem;
        private BitmapImage? _previewImage;
        private string _searchText = string.Empty;
        private string _selectedStatusFilter = AllStatusFilter;
        private string _summaryText = UiLocalization.Text("Review.Loading");
        private string _statusMessage = string.Empty;
        private bool _isBusy;
        private WeatherCondition _humanCondition = WeatherCondition.Unknown;
        private string _humanCloudCoverageText = "0";
        private bool _humanRainDetected;
        private bool _humanFogDetected;
        private string _reviewNotes = string.Empty;
        private int _previewGeneration;

        public DatasetLabelReviewViewModel(
            string datasetRoot,
            Func<string, string, bool>? confirmDeletion = null)
        {
            _service = new DatasetReviewService(datasetRoot);
            _confirmDeletion = confirmDeletion ?? ((_, _) => false);
            SamplesView = CollectionViewSource.GetDefaultView(_samples);
            SamplesView.Filter = FilterSample;

            RefreshCommand = new AsyncRelayCommand(RefreshAsync);
            PreviousCommand = new RelayCommand(SelectPrevious);
            NextCommand = new RelayCommand(SelectNext);
            AcceptTeacherCommand = new AsyncRelayCommand(AcceptTeacherAsync);
            SaveCorrectionCommand = new AsyncRelayCommand(SaveCorrectionAsync);
            RejectCommand = new AsyncRelayCommand(RejectAsync);
            ResetReviewCommand = new AsyncRelayCommand(ResetReviewAsync);
            DeleteSampleCommand = new AsyncRelayCommand(DeleteSampleAsync);
            OpenDatasetFolderCommand = new RelayCommand(OpenDatasetFolder);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ICollectionView SamplesView { get; }
        public IReadOnlyList<string> StatusFilters { get; } = new[]
        {
            AllStatusFilter,
            DatasetReviewStatuses.Unreviewed,
            DatasetReviewStatuses.Accepted,
            DatasetReviewStatuses.Corrected,
            DatasetReviewStatuses.Rejected
        };
        public IReadOnlyList<WeatherCondition> Conditions { get; } = Enum
            .GetValues<WeatherCondition>()
            .Where(condition => condition != WeatherCondition.Unknown)
            .ToArray();

        public ICommand RefreshCommand { get; }
        public ICommand PreviousCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand AcceptTeacherCommand { get; }
        public ICommand SaveCorrectionCommand { get; }
        public ICommand RejectCommand { get; }
        public ICommand ResetReviewCommand { get; }
        public ICommand DeleteSampleCommand { get; }
        public ICommand OpenDatasetFolderCommand { get; }

        public string DatasetRoot => _service.RootDirectory;

        public DatasetReviewItemViewModel? SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (ReferenceEquals(_selectedItem, value))
                {
                    return;
                }

                _selectedItem = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelection));
                PopulateReviewEditor();
                _ = LoadPreviewAsync(value);
            }
        }

        public bool HasSelection => SelectedItem?.Entry.Record != null;

        public BitmapImage? PreviewImage
        {
            get => _previewImage;
            private set
            {
                _previewImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPreviewImage));
            }
        }

        public bool HasPreviewImage => PreviewImage != null;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetField(ref _searchText, value ?? string.Empty))
                {
                    SamplesView.Refresh();
                    UpdateSummary();
                }
            }
        }

        public string SelectedStatusFilter
        {
            get => _selectedStatusFilter;
            set
            {
                if (SetField(ref _selectedStatusFilter, value ?? AllStatusFilter))
                {
                    SamplesView.Refresh();
                    UpdateSummary();
                }
            }
        }

        public string SummaryText
        {
            get => _summaryText;
            private set => SetField(ref _summaryText, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetField(ref _statusMessage, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set => SetField(ref _isBusy, value);
        }

        public WeatherCondition HumanCondition
        {
            get => _humanCondition;
            set => SetField(ref _humanCondition, value);
        }

        public string HumanCloudCoverageText
        {
            get => _humanCloudCoverageText;
            set => SetField(ref _humanCloudCoverageText, value ?? string.Empty);
        }

        public bool HumanRainDetected
        {
            get => _humanRainDetected;
            set => SetField(ref _humanRainDetected, value);
        }

        public bool HumanFogDetected
        {
            get => _humanFogDetected;
            set => SetField(ref _humanFogDetected, value);
        }

        public string ReviewNotes
        {
            get => _reviewNotes;
            set => SetField(ref _reviewNotes, value ?? string.Empty);
        }

        public async Task InitializeAsync()
        {
            if (_samples.Count == 0)
            {
                await RefreshAsync();
            }
        }

        private async Task RefreshAsync()
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            StatusMessage = UiLocalization.Text("Review.Scanning");
            var selectedId = SelectedItem?.SampleId;
            try
            {
                var entries = await _service.LoadAsync();
                _samples.Clear();
                foreach (var entry in entries)
                {
                    _samples.Add(new DatasetReviewItemViewModel(entry));
                }

                SamplesView.Refresh();
                SelectedItem = _samples.FirstOrDefault(item => item.SampleId == selectedId)
                               ?? SamplesView.Cast<DatasetReviewItemViewModel>().FirstOrDefault();
                UpdateSummary();
                StatusMessage = _samples.Count == 0
                    ? UiLocalization.Text("Review.NoLabels")
                    : UiLocalization.Text("Review.Loaded", _samples.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = UiLocalization.Text(
                    "Review.LoadFailed",
                    LogRedactor.RedactSensitiveText(ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool FilterSample(object candidate)
        {
            if (candidate is not DatasetReviewItemViewModel item)
            {
                return false;
            }

            if (!string.Equals(SelectedStatusFilter, AllStatusFilter, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.ReviewStatus, SelectedStatusFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var query = SearchText.Trim();
            return query.Length == 0 || item.SearchableText.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private async Task LoadPreviewAsync(DatasetReviewItemViewModel? item)
        {
            var generation = Interlocked.Increment(ref _previewGeneration);
            PreviewImage = null;
            if (item?.Entry.ImageFilePath == null || !File.Exists(item.Entry.ImageFilePath))
            {
                return;
            }

            try
            {
                var bytes = await File.ReadAllBytesAsync(item.Entry.ImageFilePath);
                if (generation != _previewGeneration)
                {
                    return;
                }

                using var stream = new MemoryStream(bytes, writable: false);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 1600;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                PreviewImage = image;
            }
            catch (Exception ex)
            {
                StatusMessage = UiLocalization.Text(
                    "Review.PreviewFailed",
                    LogRedactor.RedactSensitiveText(ex.Message));
            }
        }

        private void PopulateReviewEditor()
        {
            var record = SelectedItem?.Entry.Record;
            var overlay = SelectedItem?.Entry.Review;
            var source = overlay?.HumanLabel;
            var teacher = record?.Teacher.Result;

            HumanCondition = source?.Condition ?? teacher?.Condition ?? WeatherCondition.Unknown;
            HumanCloudCoverageText = (source?.CloudCoverage ?? teacher?.CloudCoverage ?? 0)
                .ToString("F0", CultureInfo.CurrentCulture);
            HumanRainDetected = source?.RainDetected ?? teacher?.RainDetected ?? false;
            HumanFogDetected = source?.FogDetected ?? teacher?.FogDetected ?? false;
            ReviewNotes = overlay?.Notes ?? string.Empty;
            StatusMessage = SelectedItem?.Entry.LoadError == null
                ? string.Empty
                : UiLocalization.Text("Review.LabelError", SelectedItem.Entry.LoadError);
        }

        private Task AcceptTeacherAsync()
        {
            return SaveReviewAsync(DatasetReviewStatuses.Accepted, null);
        }

        private async Task SaveCorrectionAsync()
        {
            if (!TryParseCloudCoverage(out var cloudCoverage))
            {
                StatusMessage = UiLocalization.Text("Review.CloudRange");
                return;
            }

            var humanLabel = new DatasetHumanLabel
            {
                Condition = HumanCondition,
                CloudCoverage = cloudCoverage,
                RainDetected = HumanRainDetected,
                FogDetected = HumanFogDetected
            };
            await SaveReviewAsync(DatasetReviewStatuses.Corrected, humanLabel);
        }

        private Task RejectAsync()
        {
            return SaveReviewAsync(DatasetReviewStatuses.Rejected, null);
        }

        private Task ResetReviewAsync()
        {
            return SaveReviewAsync(DatasetReviewStatuses.Unreviewed, null);
        }

        private async Task DeleteSampleAsync()
        {
            var item = SelectedItem;
            if (item?.Entry.Record == null || IsBusy)
            {
                return;
            }

            var confirmed = _confirmDeletion(
                UiLocalization.Text("Review.DeleteConfirmTitle"),
                UiLocalization.Text(
                    "Review.DeleteConfirm",
                    item.SampleId,
                    item.CapturedLocal));
            if (!confirmed)
            {
                return;
            }

            var visibleBefore = SamplesView.Cast<DatasetReviewItemViewModel>().ToList();
            var selectedIndex = Math.Max(0, visibleBefore.IndexOf(item));
            IsBusy = true;
            StatusMessage = UiLocalization.Text("Review.Deleting", item.SampleId);
            try
            {
                var result = await _service.DeleteSampleAsync(item.Entry);
                _samples.Remove(item);
                SamplesView.Refresh();

                var visibleAfter = SamplesView.Cast<DatasetReviewItemViewModel>().ToList();
                SelectedItem = visibleAfter.Count == 0
                    ? null
                    : visibleAfter[Math.Min(selectedIndex, visibleAfter.Count - 1)];
                UpdateSummary();

                var releasedMiB = result.ReleasedBytes / 1024d / 1024d;
                StatusMessage = result.RetainedSharedImage
                    ? UiLocalization.Text(
                        "Review.DeletedSharedImage",
                        result.SampleId,
                        result.DeletedFileCount,
                        releasedMiB)
                    : UiLocalization.Text(
                        "Review.Deleted",
                        result.SampleId,
                        result.DeletedFileCount,
                        releasedMiB);
            }
            catch (Exception ex)
            {
                StatusMessage = UiLocalization.Text(
                    "Review.DeleteFailed",
                    LogRedactor.RedactSensitiveText(ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task SaveReviewAsync(string status, DatasetHumanLabel? humanLabel)
        {
            var item = SelectedItem;
            if (item?.Entry.Record == null || IsBusy)
            {
                return;
            }

            IsBusy = true;
            StatusMessage = UiLocalization.Text("Review.Saving");
            try
            {
                var overlay = await _service.SaveReviewAsync(
                    item.Entry,
                    status,
                    humanLabel,
                    ReviewNotes);
                item.ApplyReview(overlay);
                SamplesView.Refresh();
                UpdateSummary();
                StatusMessage = UiLocalization.Text(
                    "Review.Saved",
                    UiLocalization.ReviewStatus(overlay.Status),
                    overlay.Revision);
            }
            catch (Exception ex)
            {
                StatusMessage = UiLocalization.Text(
                    "Review.SaveFailed",
                    LogRedactor.RedactSensitiveText(ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool TryParseCloudCoverage(out double value)
        {
            if (!double.TryParse(
                    HumanCloudCoverageText,
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out value)
                && !double.TryParse(
                    HumanCloudCoverageText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                return false;
            }
            return double.IsFinite(value) && value >= 0 && value <= 100;
        }

        private void SelectPrevious()
        {
            MoveSelection(-1);
        }

        private void SelectNext()
        {
            MoveSelection(1);
        }

        private void MoveSelection(int delta)
        {
            var visible = SamplesView.Cast<DatasetReviewItemViewModel>().ToList();
            if (visible.Count == 0)
            {
                return;
            }

            var index = SelectedItem == null ? -1 : visible.IndexOf(SelectedItem);
            var next = Math.Clamp(index + delta, 0, visible.Count - 1);
            SelectedItem = visible[next];
        }

        private void OpenDatasetFolder()
        {
            try
            {
                Directory.CreateDirectory(DatasetRoot);
                Process.Start(new ProcessStartInfo
                {
                    FileName = DatasetRoot,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusMessage = UiLocalization.Text(
                    "Review.OpenFailed",
                    LogRedactor.RedactSensitiveText(ex.Message));
            }
        }

        private void UpdateSummary()
        {
            var visible = SamplesView.Cast<DatasetReviewItemViewModel>().Count();
            var unreviewed = _samples.Count(item => item.ReviewStatus == DatasetReviewStatuses.Unreviewed);
            var accepted = _samples.Count(item => item.ReviewStatus == DatasetReviewStatuses.Accepted);
            var corrected = _samples.Count(item => item.ReviewStatus == DatasetReviewStatuses.Corrected);
            var rejected = _samples.Count(item => item.ReviewStatus == DatasetReviewStatuses.Rejected);
            var damaged = _samples.Count(item => item.Entry.Record == null);
            SummaryText = UiLocalization.Text(
                "Review.Summary",
                visible,
                _samples.Count,
                unreviewed,
                accepted,
                corrected,
                rejected,
                damaged);
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class DatasetReviewItemViewModel : INotifyPropertyChanged
    {
        public DatasetReviewItemViewModel(DatasetReviewEntry entry)
        {
            Entry = entry;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public DatasetReviewEntry Entry { get; }
        public string SampleId => Entry.SampleId;
        public string CapturedLocal => Entry.Record == null
            ? UiLocalization.Text("Common.Invalid")
            : Entry.Record.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        public string ReviewStatus => Entry.EffectiveReviewStatus;
        public string ReviewStatusDisplay => UiLocalization.ReviewStatus(ReviewStatus);
        public string TeacherCondition => Entry.Record?.Teacher.Result == null
            ? UiLocalization.Text("Common.Invalid")
            : UiLocalization.Condition(Entry.Record.Teacher.Result.Condition);
        public string TeacherCloud => Entry.Record?.Teacher.Result == null
            ? "—"
            : Entry.Record.Teacher.Result.CloudCoverage.ToString("F0", CultureInfo.CurrentCulture) + "%";
        public string StudentCloud => Entry.Record?.Student.Result == null
            ? "—"
            : Entry.Record.Student.Result.CloudCoverage.ToString("F0", CultureInfo.CurrentCulture) + "%";
        public string Difference
        {
            get
            {
                var teacher = Entry.Record?.Teacher.Result;
                var student = Entry.Record?.Student.Result;
                return teacher == null || student == null
                    ? "—"
                    : Math.Abs(teacher.CloudCoverage - student.CloudCoverage)
                        .ToString("F0", CultureInfo.CurrentCulture) + "%";
            }
        }
        public string Reasons => Entry.Record == null
            ? Entry.LoadError ?? UiLocalization.Text("Common.Invalid")
            : string.Join(", ", Entry.Record.Selection.Reason.Select(UiLocalization.SelectionReason));
        public string SelectionSummary => UiLocalization.Text("Review.Selection", Reasons);
        public string ProvenanceSummary
        {
            get
            {
                var record = Entry.Record;
                if (record == null)
                {
                    return Entry.LoadError ?? UiLocalization.Text("Common.Invalid");
                }

                var teacher = record.Teacher.Provenance;
                return UiLocalization.Text(
                    "Review.Provenance",
                    teacher.Provider,
                    teacher.Model,
                    teacher.PromptVersion,
                    teacher.LatencyMilliseconds,
                    UiLocalization.Boolean(teacher.OnlineSucceeded),
                    UiLocalization.Boolean(teacher.IsFallback));
            }
        }
        public string TeacherSummary
        {
            get
            {
                var result = Entry.Record?.Teacher.Result;
                return result == null
                    ? UiLocalization.Text("Review.NoTeacher")
                    : UiLocalization.Text(
                        "Review.TeacherSummary",
                        UiLocalization.Condition(result.Condition),
                        result.CloudCoverage,
                        result.Confidence,
                        UiLocalization.Boolean(result.RainDetected),
                        UiLocalization.Boolean(result.FogDetected),
                        result.Description);
            }
        }
        public string StudentSummary
        {
            get
            {
                var result = Entry.Record?.Student.Result;
                var model = Entry.Record?.Student.Provenance.Model ?? UiLocalization.Text("Common.Unknown");
                return result == null
                    ? UiLocalization.Text("Review.NoStudent")
                    : UiLocalization.Text(
                        "Review.StudentSummary",
                        model,
                        UiLocalization.Condition(result.Condition),
                        result.CloudCoverage,
                        result.Confidence);
            }
        }
        public string AstroSummary
        {
            get
            {
                var astro = Entry.Record?.Astro;
                return astro == null
                    ? UiLocalization.Text("Review.NoAstro")
                    : UiLocalization.Text(
                        "Review.AstroSummary",
                        astro.SunAltitude,
                        UiLocalization.AstroTerm(astro.SunState),
                        astro.MoonIllumination,
                        UiLocalization.AstroTerm(astro.MoonPhase),
                        astro.MoonAltitude);
            }
        }
        public string ImageSummary
        {
            get
            {
                var image = Entry.Record?.Image;
                return image == null
                    ? UiLocalization.Text("Review.NoImage")
                    : UiLocalization.Text(
                        "Review.ImageSummary",
                        image.Width,
                        image.Height,
                        image.SourceWidth,
                        image.SourceHeight,
                        Shorten(image.Sha256, 16),
                        image.PerceptualHash,
                        UiLocalization.Boolean(Entry.Record!.Selection.NearDuplicate));
            }
        }
        public string ReviewSummary
        {
            get
            {
                var review = Entry.Review;
                if (review == null)
                {
                    return UiLocalization.Text("Review.UnreviewedHelp");
                }

                var human = review.HumanLabel == null
                    ? string.Empty
                    : UiLocalization.Text(
                        "Review.HumanLabel",
                        UiLocalization.Condition(review.HumanLabel.Condition),
                        review.HumanLabel.CloudCoverage);
                return UiLocalization.Text(
                    "Review.ReviewSummary",
                    UiLocalization.ReviewStatus(review.Status),
                    review.Revision,
                    review.ReviewedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                    human);
            }
        }
        public string SearchableText => string.Join(" ", new[]
        {
            SampleId,
            CapturedLocal,
            ReviewStatus,
            TeacherCondition,
            TeacherCloud,
            StudentCloud,
            Reasons,
            Entry.Record?.Teacher.Provenance.Model ?? string.Empty
        });

        public void ApplyReview(DatasetReviewOverlay overlay)
        {
            Entry.Review = overlay;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewStatus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewStatusDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchableText)));
        }

        private static string Shorten(string value, int length)
        {
            return string.IsNullOrEmpty(value) || value.Length <= length
                ? value
                : value.Substring(0, length);
        }
    }
}
