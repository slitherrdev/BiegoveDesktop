using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Biegove.controllers;
using Biegove.database;

namespace Biegove
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public new event PropertyChangedEventHandler? PropertyChanged;

        private readonly SqliteDB database = new();
        private readonly PersonController controller;
        private readonly HTTPServer httpserver = new();
        private readonly mDNSServer mdnsserver = new();
        private bool serverRunning;

        public ObservableCollection<PersonHolder> People => controller.People;
        public ObservableCollection<RunHolder> Runs => controller.Runs;

        private int _sortMode = 0;

        private RunHolder? _selectedRun;
        public RunHolder? SelectedRun
        {
            get => _selectedRun;
            set
            {
                _selectedRun = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRun)));
                ApplyCurrentView();
                UpdateImportButton();
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            controller = new PersonController(database);
            database.Init();

            httpserver.OnSync = (runs, entries) =>
                Dispatcher.UIThread.Post(() =>
                {
                    controller.Sync(runs, entries);
                    RefreshAfterSync();
                });

            httpserver.OnSyncMobile = (startTime, entries, raceName) =>
                Dispatcher.UIThread.Post(() =>
                {
                    controller.SyncMobile(startTime, entries, raceName);
                    RefreshAfterSync();
                });

            httpserver.OnConnectionChanged += () =>
                Dispatcher.UIThread.Post(UpdateConnectionStatus);

            try { httpserver.init(); serverRunning = true; } catch { }
            try { mdnsserver.init(httpserver.port); } catch { }

            controller.LoadRuns();
            controller.Load();
            DataContext = this;

            if (Runs.Count > 0)
            {
                _selectedRun = Runs[0];
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRun)));
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) => UpdateConnectionStatus();
            timer.Start();

            UpdateConnectionStatus();
            UpdateRecordCount();
            UpdateImportButton();
        }

        private async void OnImportCsv(object? sender, RoutedEventArgs e)
        {
            var runId = _selectedRun?.Id ?? -1;
            if (runId == -1) return;

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Wybierz plik CSV",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } }
            });

            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            controller.ImportCsv(runId, path);
            RefreshAfterSync();
        }

        private async void OnExportClick(object? sender, RoutedEventArgs e)
        {
            var runId = _selectedRun?.Id ?? -1;
            var groups = controller.GetExportGroups(runId);
            groups.RemoveAll(g => g.Rows.Count == 0);
            if (groups.Count == 0) return;

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Wybierz folder do zapisu wyników",
                AllowMultiple = false
            });

            if (folders.Count == 0) return;
            var folderPath = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(folderPath)) return;

            foreach (var (klasyfikacja, rows) in groups)
            {
                var fileName = SanitizeFileName(klasyfikacja) + "-wyniki.csv";
                var fullPath = System.IO.Path.Combine(folderPath, fileName);
                WriteExportCsv(fullPath, rows);
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "bez_klasyfikacji" : result;
        }

        private static void WriteExportCsv(string path, System.Collections.Generic.List<PersonHolder> rows)
        {
            try
            {
                using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
                writer.WriteLine("Miejsce;Nr;Zawodnik;Czas");
                int place = 1;
                foreach (var row in rows)
                {
                    writer.WriteLine($"{place};{row.numer};{EscapeCsvField(row.ExportName)};{row.TimeDisplay}");
                    place++;
                }
            }
            catch { }
        }

        private static string EscapeCsvField(string value)
        {
            if (value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private void OnSortClick(object? sender, RoutedEventArgs e)
        {
            _sortMode = (_sortMode + 1) % 4;
            var labels = new[] { "Czas \u2191", "Czas \u2193", "Nr \u2191", "Nr \u2193" };
            var btn = this.FindControl<Button>("sortBtn");
            if (btn != null) btn.Content = labels[_sortMode];
            controller.SortMode = _sortMode;
            ApplyCurrentView();
        }

        private void ApplyCurrentView()
        {
            if (_selectedRun == null || _selectedRun.Id == -1)
                controller.Load();
            else
                controller.FilterByRun(_selectedRun.Id);
            UpdateRecordCount();
        }

        private void UpdateImportButton()
        {
            var btn = this.FindControl<Button>("importCsvBtn");
            if (btn != null)
                btn.IsVisible = _selectedRun != null && _selectedRun.Id != -1;
        }

        private void RefreshAfterSync()
        {
            var currentId = _selectedRun?.Id ?? -1;

            var dg = this.FindControl<DataGrid>("MainDataGrid");
            var sv = dg?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var scrollOffset = sv?.Offset ?? default;

            _selectedRun = null;
            controller.LoadRuns();
            RunHolder? match = Runs.Count > 0 ? Runs[0] : null;
            foreach (var r in Runs)
                if (r.Id == currentId) { match = r; break; }
            SelectedRun = match;
            UpdateRecordCount();

            if (sv != null)
                sv.Offset = scrollOffset;
        }

        private void UpdateConnectionStatus()
        {
            var dot = this.FindControl<Ellipse>("statusDot")!;
            var txt = this.FindControl<TextBlock>("statusText")!;
            var dcBtn = this.FindControl<Button>("disconnectBtn");

            if (httpserver.IsPhoneConnected)
            {
                dot.Fill = new SolidColorBrush(Color.Parse("#333"));
                txt.Text = "Połączono";
                if (dcBtn != null) dcBtn.IsVisible = true;
            }
            else if (serverRunning)
            {
                dot.Fill = new SolidColorBrush(Color.Parse("#ccc"));
                txt.Text = "Oczekiwanie...";
                if (dcBtn != null) dcBtn.IsVisible = false;
            }
            else
            {
                dot.Fill = new SolidColorBrush(Color.Parse("#ccc"));
                txt.Text = "Offline";
                if (dcBtn != null) dcBtn.IsVisible = false;
            }
        }

        private void UpdateRecordCount()
        {
            var lbl = this.FindControl<TextBlock>("recordCount");
            if (lbl != null)
                lbl.Text = $"{People.Count} wyników";
        }

        private void OnSearchChanged(object? sender, TextChangedEventArgs e)
        {
            var box = this.FindControl<TextBox>("searchBox");
            controller.SearchQuery = box?.Text ?? "";
            ApplyCurrentView();
        }

        private void OnDisconnect(object? sender, RoutedEventArgs e)
        {
            httpserver.Disconnect();
            UpdateConnectionStatus();
        }
    }
}