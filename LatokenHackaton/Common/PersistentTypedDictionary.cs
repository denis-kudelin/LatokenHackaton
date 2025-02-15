using System.Collections.Concurrent;
using System.Text.Json;
using Nito.AsyncEx;

namespace LatokenHackaton.Common
{
    internal class PersistentTypedDictionary<TKey, TValue>
    {
        private readonly PersistentDictionary baseDictionary;
        private readonly ConcurrentDictionary<TKey, AsyncLazy<TValue>> ephemeralStore = new();

        public PersistentTypedDictionary(string filePath)
        {
            baseDictionary = new PersistentDictionary(filePath, false, true);
        }

        public bool TryAdd(TKey key, TValue value)
        {
            var skey = GetStringKey(key);
            var lazyValue = ephemeralStore.AddOrUpdate(
                key,
                _ => new AsyncLazy<TValue>(async () =>
                {
                    if (await baseDictionary.ContainsKeyAsync(skey))
                    {
                        var raw = baseDictionary[skey];
                        return raw == null ? default! : Deserialize(raw);
                    }
                    var inserted = await baseDictionary.TryAddAsync(skey, Serialize(value));
                    if (!inserted)
                    {
                        var finalRaw = baseDictionary[skey];
                        return finalRaw == null ? default! : Deserialize(finalRaw);
                    }
                    return value;
                }),
                (_, existingLazy) => existingLazy
            );
            var finalValue = lazyValue.GetAwaiter().GetResult();
            return EqualityComparer<TValue>.Default.Equals(finalValue, value);
        }

        public bool TryGetValue(TKey key, out TValue? value)
        {
            var skey = GetStringKey(key);
            var lazyValue = ephemeralStore.GetOrAdd(
                key,
                _ => new AsyncLazy<TValue>(async () =>
                {
                    if (!baseDictionary.TryGetValue(skey, out var raw)) return default!;
                    return Deserialize(raw);
                })
            );
            var finalValue = lazyValue.GetAwaiter().GetResult();
            if (EqualityComparer<TValue>.Default.Equals(finalValue, default!))
            {
                value = default;
                return false;
            }
            value = finalValue;
            return true;
        }

        public bool Remove(TKey key)
        {
            var skey = GetStringKey(key);
            var lazyValue = ephemeralStore.AddOrUpdate(
                key,
                _ => new AsyncLazy<TValue>(async () =>
                {
                    var removed = baseDictionary.Remove(skey);
                    if (removed) return default!;
                    var raw = baseDictionary[skey];
                    return raw == null ? default! : Deserialize(raw);
                }),
                (_, __) => new AsyncLazy<TValue>(async () =>
                {
                    var removed = baseDictionary.Remove(skey);
                    if (removed) return default!;
                    var raw = baseDictionary[skey];
                    return raw == null ? default! : Deserialize(raw);
                })
            );
            var finalValue = lazyValue.GetAwaiter().GetResult();
            return EqualityComparer<TValue>.Default.Equals(finalValue, default!);
        }

        public async Task<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> valueFactory)
        {
            var skey = GetStringKey(key);
            var lazyValue = ephemeralStore.GetOrAdd(
                key,
                _ => new AsyncLazy<TValue>(async () =>
                {
                    if (baseDictionary.TryGetValue(skey, out var raw)) return Deserialize(raw);
                    var created = await valueFactory(key);
                    await baseDictionary.TryAddAsync(skey, Serialize(created));
                    return created;
                })
            );
            return await lazyValue;
        }

        public async Task<TValue> AddOrUpdateAsync(
            TKey key,
            Func<TKey, Task<TValue>> addFactory,
            Func<TKey, TValue, Task<TValue>> updateFactory
        )
        {
            var skey = GetStringKey(key);
            var lazyValue = ephemeralStore.AddOrUpdate(
                key,
                _ => new AsyncLazy<TValue>(async () =>
                {
                    if (!baseDictionary.TryGetValue(skey, out var raw))
                    {
                        var added = await addFactory(key);
                        baseDictionary[skey] = Serialize(added);
                        return added;
                    }
                    var oldVal = Deserialize(raw);
                    var newVal = await updateFactory(key, oldVal);
                    baseDictionary[skey] = Serialize(newVal);
                    return newVal;
                }),
                (_, __) => new AsyncLazy<TValue>(async () =>
                {
                    if (!baseDictionary.TryGetValue(skey, out var raw))
                    {
                        var added = await addFactory(key);
                        baseDictionary[skey] = Serialize(added);
                        return added;
                    }
                    var oldVal = Deserialize(raw);
                    var newVal = await updateFactory(key, oldVal);
                    baseDictionary[skey] = Serialize(newVal);
                    return newVal;
                })
            );
            return await lazyValue;
        }

        public TValue? this[TKey key]
        {
            get
            {
                if (TryGetValue(key, out var val)) return val;
                throw new KeyNotFoundException(nameof(PersistentTypedDictionary<TKey, TValue>));
            }
            set
            {
                _ = AddOrUpdateAsync(
                    key,
                    _ => Task.FromResult(value!),
                    (_, __) => Task.FromResult(value!)
                ).Result;
            }
        }

        public async Task UpdateValueAsync(TKey key, TValue value)
        {
            await AddOrUpdateAsync(
                key,
                _ => throw new KeyNotFoundException($"Key not found: {key}"),
                (_, __) => Task.FromResult(value)
            );
        }

        public bool ContainsKey(TKey key) => baseDictionary.ContainsKey(GetStringKey(key));
        public Task<bool> ContainsKeyAsync(TKey key) => baseDictionary.ContainsKeyAsync(GetStringKey(key));
        public int Count => baseDictionary.Count;
        public Task<int> CountAsync() => baseDictionary.CountAsync();

        public IAsyncEnumerator<TValue> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new TypedStoreAsyncEnumerator(baseDictionary.GetAsyncEnumerator(cancellationToken));
        }

        static string GetStringKey(TKey key)
        {
            if (key is null) throw new ArgumentNullException(nameof(key));
            return key.ToString()!;
        }

        static string Serialize(TValue value) => JsonSerializer.Serialize(value);
        static TValue Deserialize(string raw) => JsonSerializer.Deserialize<TValue>(raw)!;

        sealed class TypedStoreAsyncEnumerator : IAsyncEnumerator<TValue>
        {
            readonly IAsyncEnumerator<string> baseEnumerator;
            public TValue Current { get; private set; } = default!;
            public TypedStoreAsyncEnumerator(IAsyncEnumerator<string> baseEnumerator) => this.baseEnumerator = baseEnumerator;
            public async ValueTask<bool> MoveNextAsync()
            {
                var hasNext = await baseEnumerator.MoveNextAsync();
                if (!hasNext) return false;
                Current = Deserialize(baseEnumerator.Current);
                return true;
            }
            public ValueTask DisposeAsync() => baseEnumerator.DisposeAsync();
        }
    }
}