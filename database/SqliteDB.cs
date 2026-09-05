using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Biegove.controllers;

namespace Biegove.database
{
    public class SqliteDB
    {
        private readonly string DBUrl = "Data Source=biegove.db";

        private static string DetectAndDecode(byte[] data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return Encoding.UTF8.GetString(data);

            var utf8 = new UTF8Encoding(false, true);
            try
            {
                return utf8.GetString(data);
            }
            catch (DecoderFallbackException) { }

            return Encoding.GetEncoding(1250).GetString(data);
        }

        public void Init()
        {
            try
            {
                using var connection = new SqliteConnection(DBUrl);
                connection.Open();

                using var command = connection.CreateCommand();

                command.CommandText =
                    @"CREATE TABLE IF NOT EXISTS Run
                    (id INTEGER PRIMARY KEY AUTOINCREMENT,
                     name TEXT NOT NULL,
                     created_at INTEGER NOT NULL DEFAULT 0);";
                command.ExecuteNonQuery();

                command.CommandText =
                    @"CREATE TABLE IF NOT EXISTS Person
                    (id INTEGER PRIMARY KEY AUTOINCREMENT,
                     numer INTEGER NOT NULL,
                     czas INTEGER NOT NULL,
                     run_id INTEGER NOT NULL DEFAULT 0);";
                command.ExecuteNonQuery();

                command.CommandText =
                    @"CREATE TABLE IF NOT EXISTS Runner
                    (id INTEGER PRIMARY KEY AUTOINCREMENT,
                     numer INTEGER NOT NULL,
                     imie TEXT NOT NULL DEFAULT '',
                     nazwisko TEXT NOT NULL DEFAULT '',
                     kategoria TEXT NOT NULL DEFAULT '',
                     klasyfikacja TEXT NOT NULL DEFAULT '',
                     run_id INTEGER NOT NULL DEFAULT 0);";
                command.ExecuteNonQuery();

                try
                {
                    command.CommandText = "ALTER TABLE Person ADD COLUMN run_id INTEGER NOT NULL DEFAULT 0;";
                    command.ExecuteNonQuery();
                }
                catch { }

                try
                {
                    command.CommandText = "ALTER TABLE Person ADD COLUMN note TEXT NOT NULL DEFAULT '';";
                    command.ExecuteNonQuery();
                }
                catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize DB: {ex}");
            }
        }

        public void ImportCsv(int runId, string filePath)
        {
            try
            {
                using var connection = new SqliteConnection(DBUrl);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM Runner WHERE run_id = $runId;";
                    cmd.Parameters.AddWithValue("$runId", runId);
                    cmd.ExecuteNonQuery();
                }

                var rawBytes = File.ReadAllBytes(filePath);
                var text = DetectAndDecode(rawBytes);
                var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                if (lines.Length < 2) { transaction.Commit(); return; }

                var header = lines[0].TrimStart('\uFEFF').Split(';');
                int iNumer = -1, iImie = -1, iNazwisko = -1, iKategoria = -1, iKlasyfikacja = -1;
                for (int i = 0; i < header.Length; i++)
                {
                    var h = header[i].Trim().ToLower();
                    if (h.Contains("nr zawodnika")) iNumer = i;
                    else if (h.Contains("imi")) iImie = i;
                    else if (h.Contains("nazwisko")) iNazwisko = i;
                    else if (h.Contains("kategoria")) iKategoria = i;
                    else if (h.Contains("klasyfikacja")) iKlasyfikacja = i;
                }

                if (iNumer < 0) { transaction.Commit(); return; }

                for (int li = 1; li < lines.Length; li++)
                {
                    var cols = lines[li].Split(';');
                    if (cols.Length <= iNumer) continue;

                    if (!int.TryParse(cols[iNumer].Trim(), out var numer)) continue;

                    var imie = iImie >= 0 && iImie < cols.Length ? cols[iImie].Trim() : "";
                    var nazwisko = iNazwisko >= 0 && iNazwisko < cols.Length ? cols[iNazwisko].Trim() : "";
                    var kategoria = iKategoria >= 0 && iKategoria < cols.Length ? cols[iKategoria].Trim() : "";
                    var klasyfikacja = iKlasyfikacja >= 0 && iKlasyfikacja < cols.Length ? cols[iKlasyfikacja].Trim() : "";

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "INSERT INTO Runner (numer, imie, nazwisko, kategoria, klasyfikacja, run_id) VALUES ($n, $i, $na, $k, $kl, $r);";
                    cmd.Parameters.AddWithValue("$n", numer);
                    cmd.Parameters.AddWithValue("$i", imie);
                    cmd.Parameters.AddWithValue("$na", nazwisko);
                    cmd.Parameters.AddWithValue("$k", kategoria);
                    cmd.Parameters.AddWithValue("$kl", klasyfikacja);
                    cmd.Parameters.AddWithValue("$r", runId);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to import CSV: {ex}");
            }
        }

        public RunnerInfo? GetRunnerInfo(int numer, int runId)
        {
            try
            {
                using var connection = new SqliteConnection(DBUrl);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT imie, nazwisko, kategoria, klasyfikacja FROM Runner WHERE numer = $n AND run_id = $r LIMIT 1;";
                cmd.Parameters.AddWithValue("$n", numer);
                cmd.Parameters.AddWithValue("$r", runId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                    return new RunnerInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            }
            catch { }
            return null;
        }

        public void SyncFromMobile(long startTime, List<(int numer, int elapsed, string? note)> entries, string raceName = "")
        {
            if (startTime == 0 && raceName == "0")
            {
                try
                {
                    using var connection = new SqliteConnection(DBUrl);
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "DELETE FROM Person; DELETE FROM Run;";
                    cmd.ExecuteNonQuery();
                }
                catch { }
                return;
            }

            if (startTime == 0) return;

            if (string.IsNullOrEmpty(raceName) && entries.Count == 0)
            {
                try
                {
                    using var connection = new SqliteConnection(DBUrl);
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "DELETE FROM Person WHERE run_id IN (SELECT id FROM Run WHERE created_at = $st); DELETE FROM Runner WHERE run_id IN (SELECT id FROM Run WHERE created_at = $st); DELETE FROM Run WHERE created_at = $st;";
                    cmd.Parameters.AddWithValue("$st", startTime);
                    cmd.ExecuteNonQuery();
                }
                catch { }
                return;
            }

            try
            {
                using var connection = new SqliteConnection(DBUrl);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                int runId = -1;
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT id FROM Run WHERE created_at = $startTime LIMIT 1;";
                    cmd.Parameters.AddWithValue("$startTime", startTime);
                    var result = cmd.ExecuteScalar();
                    if (result != null)
                        runId = Convert.ToInt32(result);
                }

                if (runId == -1)
                {
                    var name = string.IsNullOrEmpty(raceName)
                        ? $"Bieg {DateTimeOffset.FromUnixTimeMilliseconds(startTime).LocalDateTime:dd.MM.yyyy HH:mm}"
                        : raceName;
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "INSERT INTO Run (name, created_at) VALUES ($name, $startTime);";
                    cmd.Parameters.AddWithValue("$name", name);
                    cmd.Parameters.AddWithValue("$startTime", startTime);
                    cmd.ExecuteNonQuery();

                    using var idCmd = connection.CreateCommand();
                    idCmd.CommandText = "SELECT last_insert_rowid();";
                    runId = Convert.ToInt32(idCmd.ExecuteScalar());
                }
                else if (!string.IsNullOrEmpty(raceName))
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "UPDATE Run SET name = $name WHERE id = $id;";
                    cmd.Parameters.AddWithValue("$name", raceName);
                    cmd.Parameters.AddWithValue("$id", runId);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM Person WHERE run_id = $runId;";
                    cmd.Parameters.AddWithValue("$runId", runId);
                    cmd.ExecuteNonQuery();
                }

                foreach (var (numer, elapsed, note) in entries)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "INSERT INTO Person (numer, czas, run_id, note) VALUES ($numer, $czas, $runId, $note);";
                    cmd.Parameters.AddWithValue("$numer", numer);
                    cmd.Parameters.AddWithValue("$czas", elapsed);
                    cmd.Parameters.AddWithValue("$runId", runId);
                    cmd.Parameters.AddWithValue("$note", note ?? "");
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to sync from mobile: {ex}");
            }
        }

        public void ReplaceAllFull(
            List<(int id, string name, long createdAt)> runs,
            List<(int numer, long czas, int runId)> entries)
        {
            try
            {
                using var connection = new SqliteConnection(DBUrl);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM Person; DELETE FROM Run;";
                    cmd.ExecuteNonQuery();
                }

                foreach (var (id, name, createdAt) in runs)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "INSERT INTO Run (id, name, created_at) VALUES ($id, $name, $createdAt);";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.Parameters.AddWithValue("$name", name);
                    cmd.Parameters.AddWithValue("$createdAt", createdAt);
                    cmd.ExecuteNonQuery();
                }

                foreach (var (numer, czas, runId) in entries)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "INSERT INTO Person (numer, czas, run_id) VALUES ($numer, $czas, $runId);";
                    cmd.Parameters.AddWithValue("$numer", numer);
                    cmd.Parameters.AddWithValue("$czas", czas);
                    cmd.Parameters.AddWithValue("$runId", runId);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to replace data: {ex}");
            }
        }

        public List<RunHolder> GetAllRuns()
        {
            var result = new List<RunHolder>();
            try
            {
                using var connection = new SqliteConnection(DBUrl);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, name, created_at FROM Run ORDER BY created_at DESC;";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                    result.Add(new RunHolder(reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read runs: {ex}");
            }
            return result;
        }

        public List<PersonHolder> GetAll()
        {
            var result = new List<PersonHolder>();
            try
            {
                using var connection = new SqliteConnection(DBUrl);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, numer, czas, run_id, COALESCE(note,'') FROM Person ORDER BY czas ASC;";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                    result.Add(new PersonHolder(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt32(3)) { Note = reader.GetString(4) });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read from db: {ex}");
            }
            return result;
        }

        public List<PersonHolder> GetByRunId(int runId)
        {
            var result = new List<PersonHolder>();
            try
            {
                using var connection = new SqliteConnection(DBUrl);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, numer, czas, run_id, COALESCE(note,'') FROM Person WHERE run_id = $runId ORDER BY czas ASC;";
                command.Parameters.AddWithValue("$runId", runId);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                    result.Add(new PersonHolder(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt32(3)) { Note = reader.GetString(4) });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read by run: {ex}");
            }
            return result;
        }

        public bool HasRunnerData(int runId)
        {
            try
            {
                using var connection = new SqliteConnection(DBUrl);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Runner WHERE run_id = $r;";
                cmd.Parameters.AddWithValue("$r", runId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            catch { return false; }
        }
    }

    public class RunnerInfo
    {
        public string Imie { get; }
        public string Nazwisko { get; }
        public string Kategoria { get; }
        public string Klasyfikacja { get; }
        public RunnerInfo(string imie, string nazwisko, string kategoria, string klasyfikacja)
        {
            Imie = imie; Nazwisko = nazwisko; Kategoria = kategoria; Klasyfikacja = klasyfikacja;
        }
    }
}
