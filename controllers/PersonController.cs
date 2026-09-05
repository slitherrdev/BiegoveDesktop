using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Biegove.database;

namespace Biegove.controllers
{
    public class RunHolder
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public long CreatedAt { get; set; }
        public RunHolder(int id, string name, long createdAt = 0) { Id = id; Name = name; CreatedAt = createdAt; }
        public override string ToString() => Name;
    }

    public class PersonController
    {
        private readonly SqliteDB database;
        public ObservableCollection<PersonHolder> People { get; } = new();
        public ObservableCollection<RunHolder> Runs { get; } = new();
        public int SortMode { get; set; } = 0;
        public string SearchQuery { get; set; } = "";

        public PersonController(SqliteDB database_arg)
        {
            database = database_arg;
        }

        public void Load()
        {
            People.Clear();
            var rows = database.GetAll();
            ApplySort(rows);
            ApplySearch(rows);
            int i = 1;
            foreach (var row in rows)
            {
                row.Index = i++;
                row.RunName = GetRunName(row.RunId ?? 0);
                row.RaceStartTime = GetRunStartTime(row.RunId ?? 0);
                EnrichWithRunnerData(row);
                People.Add(row);
            }
        }

        public void FilterByRun(int runId)
        {
            People.Clear();
            var rows = runId == -1 ? database.GetAll() : database.GetByRunId(runId);
            ApplySort(rows);
            ApplySearch(rows);
            int i = 1;
            foreach (var row in rows)
            {
                row.Index = i++;
                row.RunName = GetRunName(row.RunId ?? 0);
                row.RaceStartTime = GetRunStartTime(row.RunId ?? 0);
                EnrichWithRunnerData(row);
                People.Add(row);
            }
        }

        private void ApplySort(List<PersonHolder> rows)
        {
            switch (SortMode)
            {
                case 0: rows.Sort((a, b) => (a.czas ?? 0).CompareTo(b.czas ?? 0)); break;
                case 1: rows.Sort((a, b) => (b.czas ?? 0).CompareTo(a.czas ?? 0)); break;
                case 2: rows.Sort((a, b) => (a.numer ?? 0).CompareTo(b.numer ?? 0)); break;
                case 3: rows.Sort((a, b) => (b.numer ?? 0).CompareTo(a.numer ?? 0)); break;
            }
        }

        private void EnrichWithRunnerData(PersonHolder row)
        {
            var info = database.GetRunnerInfo(row.numer ?? 0, row.RunId ?? 0);
            if (info != null)
            {
                row.Imie = info.Imie;
                row.Nazwisko = info.Nazwisko;
                row.Kategoria = info.Kategoria;
                row.Klasyfikacja = info.Klasyfikacja;
            }
        }

        private void ApplySearch(List<PersonHolder> rows)
        {
            if (string.IsNullOrWhiteSpace(SearchQuery)) return;
            var q = SearchQuery.Trim().ToLower();
            rows.RemoveAll(r =>
            {
                if (r.numer.ToString()?.Contains(q) == true) return false;
                var info = database.GetRunnerInfo(r.numer ?? 0, r.RunId ?? 0);
                if (info != null)
                {
                    if (info.Nazwisko.ToLower().Contains(q)) return false;
                    if (info.Imie.ToLower().Contains(q)) return false;
                }
                return true;
            });
        }

        private string GetRunName(int runId)
        {
            foreach (var r in Runs)
                if (r.Id == runId) return r.Name;
            return "—";
        }

        private long GetRunStartTime(int runId)
        {
            foreach (var r in Runs)
                if (r.Id == runId) return r.CreatedAt;
            return 0;
        }

        public void LoadRuns()
        {
            Runs.Clear();
            Runs.Add(new RunHolder(-1, "Wszystkie biegi"));
            foreach (var run in database.GetAllRuns())
                Runs.Add(run);
        }

        public void ImportCsv(int runId, string filePath)
        {
            database.ImportCsv(runId, filePath);
        }

        public void Sync(List<(int id, string name, long createdAt)> runs, List<(int numer, long czas, int runId)> entries)
        {
            database.ReplaceAllFull(runs, entries);
        }

        public void SyncMobile(long startTime, List<(int numer, int elapsed, string? note)> entries, string raceName = "")
        {
            database.SyncFromMobile(startTime, entries, raceName);
        }

        public List<(string Klasyfikacja, List<PersonHolder> Rows)> GetExportGroups(int runId)
        {
            var rows = runId == -1 ? database.GetAll() : database.GetByRunId(runId);
            foreach (var row in rows)
            {
                row.RunName = GetRunName(row.RunId ?? 0);
                row.RaceStartTime = GetRunStartTime(row.RunId ?? 0);
                EnrichWithRunnerData(row);
            }

            var groups = new Dictionary<string, List<PersonHolder>>();
            var order = new List<string>();
            foreach (var row in rows)
            {
                var key = string.IsNullOrWhiteSpace(row.Klasyfikacja) ? "Bez klasyfikacji" : row.Klasyfikacja.Trim();
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<PersonHolder>();
                    groups[key] = list;
                    order.Add(key);
                }
                list.Add(row);
            }

            var result = new List<(string, List<PersonHolder>)>();
            foreach (var key in order)
            {
                var list = groups[key];
                list.Sort((a, b) => (a.czas ?? 0).CompareTo(b.czas ?? 0));
                result.Add((key, list));
            }
            return result;
        }
    }

    public class PersonHolder
    {
        public int Index { get; set; }
        public int? id { get; set; }
        public int? numer { get; set; }
        public long? czas { get; set; }
        public int? RunId { get; set; }
        public long RaceStartTime { get; set; }
        public string RunName { get; set; } = "";
        public string Imie { get; set; } = "";
        public string Nazwisko { get; set; } = "";
        public string Kategoria { get; set; } = "";
        public string Klasyfikacja { get; set; } = "";
        public string Note { get; set; } = "";
        public string? NoteTooltip => string.IsNullOrEmpty(Note) ? null : Note;
        public bool HasNote => !string.IsNullOrEmpty(Note);

        public string DisplayName => string.IsNullOrEmpty(Imie) && string.IsNullOrEmpty(Nazwisko)
            ? $"Nr {numer}" : $"{Imie} {Nazwisko}";

        public string DisplayKategoria => string.IsNullOrEmpty(Kategoria) ? "" : Kategoria;

        public string DisplayKlasyfikacja => string.IsNullOrEmpty(Klasyfikacja) ? "" : Klasyfikacja;

        public string ExportName => string.IsNullOrEmpty(Imie) && string.IsNullOrEmpty(Nazwisko)
            ? "anonimowy" : $"{Imie} {Nazwisko}".Trim();

        public string TimeDisplay
        {
            get
            {
                if (czas is not long seconds) return "—";
                var ts = TimeSpan.FromSeconds(seconds);
                return ts.TotalHours >= 1
                    ? ts.ToString(@"hh\:mm\:ss")
                    : ts.ToString(@"mm\:ss");
            }
        }

        public string ClockTime
        {
            get
            {
                if (czas is not long seconds || RaceStartTime == 0) return "—";
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(RaceStartTime + seconds * 1000).LocalDateTime;
                return dt.ToString("HH:mm:ss");
            }
        }

        public PersonHolder(int id, int numer, long czas, int runId = 0)
        {
            this.id = id;
            this.numer = numer;
            this.czas = czas;
            this.RunId = runId;
        }
    }
}
