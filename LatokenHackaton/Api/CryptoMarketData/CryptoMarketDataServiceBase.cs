using System;
using System.Collections.Concurrent;
using LatokenHackaton.Common;

namespace LatokenHackaton.Api.CryptoMarketData
{
    internal abstract class CryptoMarketDataServiceBase : ICryptoMarketDataService
    {
        private static readonly PersistentDictionary cache = new(nameof(CryptoMarketDataServiceBase));

        public abstract IAsyncEnumerable<PriceHistoryEntry> GetPriceHistoryAsync(
            string ticker,
            DateTime fromDate,
            TimeFrame timeFrame,
            DateTime? toDate = null
        );

        protected async IAsyncEnumerable<PriceHistoryEntry> GetPriceHistoryWithCachingAsync(
            string ticker,
            DateTime fromDate,
            DateTime toDate,
            TimeFrame timeFrame,
            Func<IAsyncEnumerable<PriceHistoryEntry>> fetchFromServer
        )
        {
            var cacheKey = ticker + "|" + timeFrame;
            var updatedJson = await cache.AddOrUpdateAsync(
                cacheKey,
                async _ =>
                {
                    var fetchedData = fetchFromServer();
                    var intervals = new List<CachedInterval>
                    {
                        new CachedInterval
                        {
                            Start = fromDate,
                            End = toDate,
                            Data = await fetchedData.ToListAsync()
                        }
                    };
                    return JsonUtils.SerializeDefault(intervals);
                },
                async (_, existingJson) =>
                {
                    var intervals = JsonUtils.DeserializeDefault<List<CachedInterval>>(existingJson) ?? new List<CachedInterval>();
                    var (fullCoverage, missingRanges) = this.GetCoverage(intervals, fromDate, toDate);
                    if (fullCoverage)
                    {
                        return existingJson;
                    }

                    foreach (var (start, end) in missingRanges)
                    {
                        var partialData = fetchFromServer();
                        intervals.Add(new CachedInterval
                        {
                            Start = start,
                            End = end,
                            Data = await partialData.ToListAsync()
                        });
                    }
                    intervals = this.MergeIntervals(intervals);
                    var newJson = JsonUtils.SerializeDefault(intervals);
                    return newJson;
                }
            );

            var finalIntervals = JsonUtils.DeserializeDefault<List<CachedInterval>>(updatedJson) ?? new List<CachedInterval>();
            var resultData = finalIntervals
                .SelectMany(i => i.Data.Where(d => d.DateTime >= fromDate && d.DateTime <= toDate))
                .OrderBy(d => d.DateTime);

            foreach(var value in resultData)
            {
                yield return value;
            }
        }

        private (bool FullCoverage, List<(DateTime start, DateTime end)> MissingRanges) GetCoverage(
            List<CachedInterval> intervals,
            DateTime fromDate,
            DateTime toDate
        )
        {
            var sorted = intervals.OrderBy(i => i.Start).ToList();
            var current = fromDate;
            var missing = new List<(DateTime start, DateTime end)>();

            foreach (var interval in sorted)
            {
                if (interval.End < fromDate || interval.Start > toDate)
                {
                    continue;
                }
                if (interval.Start > current)
                {
                    var gapStart = current;
                    var gapEnd = interval.Start;
                    if (gapEnd > toDate) gapEnd = toDate;
                    missing.Add((gapStart, gapEnd));
                }
                if (interval.End > current)
                {
                    current = interval.End;
                }
                if (current >= toDate)
                {
                    break;
                }
            }

            if (current < toDate)
            {
                missing.Add((current, toDate));
            }

            var fullCoverage = missing.Count == 0 || missing.All(x => x.start >= x.end);
            var actualMissing = missing.Where(x => x.end > x.start).ToList();
            return (fullCoverage, actualMissing);
        }

        private List<CachedInterval> MergeIntervals(List<CachedInterval> intervals)
        {
            if (intervals == null || intervals.Count == 0)
            {
                return new List<CachedInterval>();
            }

            intervals = intervals
                .OrderBy(i => i.Start)
                .ToList();

            var merged = new List<CachedInterval>
            {
                new CachedInterval
                {
                    Start = intervals[0].Start,
                    End = intervals[0].End,
                    Data = intervals[0].Data
                        .OrderBy(d => d.DateTime)
                        .Distinct()
                        .ToList()
                }
            };

            for (var i = 1; i < intervals.Count; i++)
            {
                var current = intervals[i];
                var last = merged[^1];

                if (current.Start <= last.End)
                {
                    if (current.End > last.End)
                    {
                        last.End = current.End;
                    }
                    last.Data.AddRange(current.Data);
                    last.Data = last.Data
                        .OrderBy(d => d.DateTime)
                        .Distinct()
                        .ToList();
                }
                else
                {
                    merged.Add(new CachedInterval
                    {
                        Start = current.Start,
                        End = current.End,
                        Data = current.Data
                            .OrderBy(d => d.DateTime)
                            .Distinct()
                            .ToList()
                    });
                }
            }

            for (var j = 0; j < merged.Count; j++)
            {
                var item = merged[j];
                item.Data = item.Data
                    .Where(d => d.DateTime >= item.Start && d.DateTime <= item.End)
                    .OrderBy(d => d.DateTime)
                    .Distinct()
                    .ToList();
            }

            return merged;
        }

        private sealed class CachedInterval
        {
            public DateTime Start { get; set; }
            public DateTime End { get; set; }
            public List<PriceHistoryEntry> Data { get; set; } = new List<PriceHistoryEntry>();
        }
    }
}

