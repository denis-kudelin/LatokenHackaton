using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Nito.AsyncEx;
using SQLitePCL;

namespace LatokenHackaton.Common
{
    internal sealed class PersistentDictionary : IAsyncEnumerable<string>
    {
        private const string CreateTableSql = "CREATE TABLE IF NOT EXISTS KeyValueStore (Key TEXT PRIMARY KEY, Value TEXT)";
        private const string SelectAllSql = "SELECT Key, Value FROM KeyValueStore";
        private const string SelectOneSql = "SELECT Value FROM KeyValueStore WHERE Key=@key LIMIT 1";
        private const string InsertOrIgnoreSql = "INSERT OR IGNORE INTO KeyValueStore (Key, Value) VALUES (@key, @value)";
        private const string InsertOrReplaceSql = "INSERT OR REPLACE INTO KeyValueStore (Key, Value) VALUES (@key, @value)";
        private const string DeleteSql = "DELETE FROM KeyValueStore WHERE Key=@key";
        private const string ExistsSql = "SELECT 1 FROM KeyValueStore WHERE Key=@key LIMIT 1";
        private const string CountSql = "SELECT COUNT(*) FROM KeyValueStore";
        private const string DataSourcePrefix = "Data Source=";

        static PersistentDictionary() { Batteries.Init(); }

        private readonly bool storeInMemory;
        private readonly bool hashedKeys;
        private readonly ConcurrentDictionary<string, string> inMemoryStorage = new();
        private readonly string connectionString;

        public PersistentDictionary(string filePath, bool hashedKeys = false, bool storeInMemory = false)
        {
            this.hashedKeys = hashedKeys;
            this.storeInMemory = storeInMemory;
#if DEBUG
            connectionString = DataSourcePrefix + filePath + ";";
#else
            filePath = Path.IsPathRooted(filePath)
                ? filePath
                : Path.Combine("C:\\", nameof(PersistentDictionary), Assembly.GetEntryAssembly().GetName().Name, filePath);
            var d = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            connectionString = DataSourcePrefix + filePath + ";";
#endif
            using var c = new SqliteConnection(connectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = CreateTableSql;
            cmd.ExecuteNonQuery();
        }

        public int Count => CountAsync().Result;

        public async Task<int> CountAsync()
        {
            var o = await ExecScalarAsync(CountSql);
            return Convert.ToInt32(o);
        }

        public async Task<bool> ContainsKeyAsync(string key)
        {
            var k = ProcessKey(key);
            if (storeInMemory && inMemoryStorage.ContainsKey(k)) return true;
            var o = await ExecScalarAsync(ExistsSql, ("@key", k));
            if (o == null) return false;
            if (storeInMemory && !inMemoryStorage.ContainsKey(k))
            {
                var v = await ReadValueAsync(k);
                if (v != null) inMemoryStorage[k] = v;
            }
            return true;
        }

        public bool ContainsKey(string key) => ContainsKeyAsync(key).Result;

        public async Task<string> GetOrAddAsync(string key, Func<string, Task<string>> valueFactory)
        {
            var k = ProcessKey(key);
            if (storeInMemory && inMemoryStorage.TryGetValue(k, out var mem)) return mem;
            var dbVal = await ReadValueAsync(k);
            if (dbVal != null)
            {
                if (storeInMemory) inMemoryStorage[k] = dbVal;
                return dbVal;
            }
            var created = await valueFactory(k);
            await ExecNonQueryAsync(InsertOrReplaceSql, ("@key", k), ("@value", created));
            if (storeInMemory) inMemoryStorage[k] = created;
            return created;
        }

        public async Task<string> AddOrUpdateAsync(string key, Func<string, Task<string>> addFactory, Func<string, string, Task<string>> updateFactory)
        {
            var k = ProcessKey(key);
            string? oldVal = null;
            if (storeInMemory && inMemoryStorage.TryGetValue(k, out var mem)) oldVal = mem;
            if (oldVal == null)
            {
                var dbVal = await ReadValueAsync(k);
                if (dbVal != null)
                {
                    oldVal = dbVal;
                    if (storeInMemory) inMemoryStorage[k] = dbVal;
                }
            }
            if (oldVal == null)
            {
                var a = await addFactory(k);
                await ExecNonQueryAsync(InsertOrReplaceSql, ("@key", k), ("@value", a));
                if (storeInMemory) inMemoryStorage[k] = a;
                return a;
            }
            else
            {
                var u = await updateFactory(k, oldVal);
                await ExecNonQueryAsync(InsertOrReplaceSql, ("@key", k), ("@value", u));
                if (storeInMemory) inMemoryStorage[k] = u;
                return u;
            }
        }

        public async Task<bool> TryAddAsync(string key, string value)
        {
            var k = ProcessKey(key);
            if (storeInMemory && inMemoryStorage.ContainsKey(k)) return false;
            var dbVal = await ReadValueAsync(k);
            if (dbVal != null) return false;
            var rows = await ExecNonQueryAsync(InsertOrIgnoreSql, ("@key", k), ("@value", value));
            if (rows > 0 && storeInMemory) inMemoryStorage[k] = value;
            return rows > 0;
        }

        public async Task<(bool found, string? value)> TryGetValueAsync(string key)
        {
            var k = ProcessKey(key);
            if (storeInMemory && inMemoryStorage.TryGetValue(k, out var mem)) return (true, mem);
            var dbVal = await ReadValueAsync(k);
            if (dbVal != null && storeInMemory) inMemoryStorage[k] = dbVal;
            return (dbVal != null, dbVal);
        }

        public bool TryGetValue(string key, out string? value)
        {
            var t = TryGetValueAsync(key).Result;
            value = t.value;
            return t.found;
        }

        public async Task<bool> RemoveAsync(string key)
        {
            var k = ProcessKey(key);
            var rows = await ExecNonQueryAsync(DeleteSql, ("@key", k));
            if (rows > 0 && storeInMemory) inMemoryStorage.TryRemove(k, out _);
            return rows > 0;
        }

        public bool Remove(string key) => RemoveAsync(key).Result;

        public string? this[string key]
        {
            get
            {
                if (TryGetValue(key, out var v)) return v;
                throw new KeyNotFoundException(nameof(PersistentDictionary));
            }
            set => AddOrUpdateAsync(key, async _ => value!, async (_, __) => value!).Wait();
        }

        public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (!storeInMemory) return new PersistentDictionaryAsyncEnumerator(connectionString);
            var t = PopulateMemoryFromDb();
            t.Wait(cancellationToken);
            return new InMemoryEnumerator(inMemoryStorage);
        }

        private async Task PopulateMemoryFromDb()
        {
            using var c = new SqliteConnection(connectionString);
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = SelectAllSql;
            using var r = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
            while (await r.ReadAsync())
            {
                var k = r.GetString(0);
                var v = r.GetString(1);
                inMemoryStorage[k] = v;
            }
        }

        private async Task<string?> ReadValueAsync(string k)
        {
            using var c = new SqliteConnection(connectionString);
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = SelectOneSql;
            var p = cmd.CreateParameter();
            p.ParameterName = "@key";
            p.Value = k;
            cmd.Parameters.Add(p);
            var o = await cmd.ExecuteScalarAsync();
            return o?.ToString();
        }

        private async Task<object?> ExecScalarAsync(string sql, params (string n, object v)[] p)
        {
            using var c = new SqliteConnection(connectionString);
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (nn, vv) in p)
            {
                var prm = cmd.CreateParameter();
                prm.ParameterName = nn;
                prm.Value = vv;
                cmd.Parameters.Add(prm);
            }
            return await cmd.ExecuteScalarAsync();
        }

        private async Task<int> ExecNonQueryAsync(string sql, params (string n, object v)[] p)
        {
            using var c = new SqliteConnection(connectionString);
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (nn, vv) in p)
            {
                var prm = cmd.CreateParameter();
                prm.ParameterName = nn;
                prm.Value = vv;
                cmd.Parameters.Add(prm);
            }
            return await cmd.ExecuteNonQueryAsync();
        }

        private string ProcessKey(string key)
        {
            if (!hashedKeys) return key;
            using var md5 = MD5.Create();
            using var sha256 = SHA256.Create();
            var sb = new StringBuilder();
            foreach (var b in md5.ComputeHash(Encoding.UTF8.GetBytes(key))) sb.Append(b.ToString("x2"));
            foreach (var b in sha256.ComputeHash(Encoding.UTF8.GetBytes(key))) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private sealed class PersistentDictionaryAsyncEnumerator : IAsyncEnumerator<string>
        {
            private readonly string connStr;
            private SqliteConnection? c;
            private SqliteDataReader? r;
            public string Current { get; private set; } = "";
            public PersistentDictionaryAsyncEnumerator(string connStr) => this.connStr = connStr;
            public async ValueTask<bool> MoveNextAsync()
            {
                if (c == null)
                {
                    c = new SqliteConnection(connStr);
                    await c.OpenAsync();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = SelectAllSql;
                    r = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
                }
                if (r == null) return false;
                if (await r.ReadAsync())
                {
                    Current = r.GetString(1);
                    return true;
                }
                return false;
            }
            public ValueTask DisposeAsync()
            {
                r?.Dispose();
                c?.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class InMemoryEnumerator : IAsyncEnumerator<string>
        {
            private readonly IEnumerator<string> e;
            public string Current { get; private set; } = "";
            public InMemoryEnumerator(ConcurrentDictionary<string, string> s) => e = s.Values.GetEnumerator();
            public ValueTask<bool> MoveNextAsync()
            {
                if (e.MoveNext())
                {
                    Current = e.Current;
                    return new ValueTask<bool>(true);
                }
                return new ValueTask<bool>(false);
            }
            public ValueTask DisposeAsync()
            {
                e.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    internal static class ConcurrentDictionaryAsyncExtensions
    {
        public static async Task<TValue> GetOrAddAsync<TKey, TValue>(
            this ConcurrentDictionary<TKey, TValue> d,
            TKey k,
            Func<TKey, Task<TValue>> f
        ) where TKey : notnull
        {
            if (d.TryGetValue(k, out var e)) return e;
            var c = await f(k);
            return d.GetOrAdd(k, c);
        }

        public static async Task<TValue> AddOrUpdateAsync<TKey, TValue>(
            this ConcurrentDictionary<TKey, TValue> d,
            TKey k,
            Func<TKey, Task<TValue>> addF,
            Func<TKey, TValue, Task<TValue>> updateF
        ) where TKey : notnull
        {
            while (true)
            {
                if (d.TryGetValue(k, out var oldV))
                {
                    var newV = await updateF(k, oldV);
                    if (d.TryUpdate(k, newV, oldV)) return newV;
                }
                else
                {
                    var newVal = await addF(k);
                    if (d.TryAdd(k, newVal)) return newVal;
                }
            }
        }
    }
}