using System;
using System.Windows;

namespace AIWeather.Views
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public partial class DatasetLabelReviewWindow : Window
    {
        private readonly DatasetLabelReviewViewModel _viewModel;

        public DatasetLabelReviewWindow(string datasetRoot)
        {
            InitializeComponent();
            _viewModel = new DatasetLabelReviewViewModel(datasetRoot, ConfirmPermanentDeletion);
            DataContext = _viewModel;
            Loaded += OnLoaded;
        }

        private bool ConfirmPermanentDeletion(string title, string message)
        {
            return MessageBox.Show(
                       this,
                       message,
                       title,
                       MessageBoxButton.YesNo,
                       MessageBoxImage.Warning,
                       MessageBoxResult.No)
                   == MessageBoxResult.Yes;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception)
            {
                // The view model converts expected dataset errors into a visible status.
                // This final guard prevents a review-tool failure from affecting N.I.N.A.
            }
        }
    }
}
