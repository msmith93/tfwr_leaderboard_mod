using System;
using System.Collections.Generic;
using System.Threading;
using Steamworks;

namespace TFWRRemoteAPI
{
    /// <summary>
    /// Read-only look at a Steam leaderboard through the game's own Steam session: how many
    /// entries it has, the times at the top and at a few percentiles, the player's own entry,
    /// and where a given time would rank. Nothing is uploaded. Scores are run times in
    /// milliseconds, lower is better.
    ///
    /// Steam answers through callbacks that the game pumps on the main thread, so a query is
    /// a small chain of steps; the HTTP thread waits on <see cref="Done"/>.
    /// </summary>
    public sealed class SteamBoardQuery
    {
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public string Error;
        public int EntryCount;
        public readonly List<KeyValuePair<int, int>> Top = new List<KeyValuePair<int, int>>();         // rank, ms
        public readonly List<KeyValuePair<double, KeyValuePair<int, int>>> Percentiles
            = new List<KeyValuePair<double, KeyValuePair<int, int>>>();                               // percent -> (rank, ms)
        public bool HasPlayer;
        public int PlayerRank, PlayerMs;
        /// <summary>Rank each queried time would get, in the order the times were given.</summary>
        public readonly List<int> TimeRanks = new List<int>();

        private readonly string _steamName;
        private readonly int _topCount;
        private readonly List<int> _queryMs;   // times to place, in milliseconds
        private int _queryIndex;
        // Up to this many entries the whole board is fetched in one call and times are placed
        // locally; above it each time is placed by binary search over single-entry downloads.
        private const int FullDownloadLimit = 5000;
        private SteamLeaderboard_t _board;

        // Kept alive for the life of the query: Steam drops the callback if these are collected.
        private readonly CallResult<LeaderboardFindResult_t> _find = new CallResult<LeaderboardFindResult_t>();
        private readonly List<CallResult<LeaderboardScoresDownloaded_t>> _downloads = new List<CallResult<LeaderboardScoresDownloaded_t>>();

        private static readonly double[] PercentSteps = { 1, 5, 10, 25, 50, 75 };
        private int _percentIndex;
        private int _lo, _hi;            // binary search bounds (ranks) for the time query

        public SteamBoardQuery(string steamName, int topCount, List<int> queryMs)
        {
            _steamName = steamName;
            _topCount = Math.Max(1, Math.Min(100, topCount));
            _queryMs = queryMs ?? new List<int>();
        }

        /// <summary>Call on the main thread.</summary>
        public void Start()
        {
            try
            {
                if (!SteamManager.Initialized) { Fail("Steam is not initialised in the game"); return; }
                _find.Set(SteamUserStats.FindLeaderboard(_steamName), OnFound);
            }
            catch (Exception ex)
            {
                Fail("Steam call failed: " + ex.Message);
            }
        }

        private void Fail(string message)
        {
            Error = message;
            Done.Set();
        }

        private void Download(ELeaderboardDataRequest kind, int from, int to, Action<List<KeyValuePair<int, int>>> then)
        {
            var call = new CallResult<LeaderboardScoresDownloaded_t>();
            _downloads.Add(call);
            call.Set(SteamUserStats.DownloadLeaderboardEntries(_board, kind, from, to), (result, failure) =>
            {
                try
                {
                    if (failure) { Fail("Steam could not download leaderboard entries"); return; }
                    var entries = new List<KeyValuePair<int, int>>();
                    for (int i = 0; i < result.m_cEntryCount; i++)
                    {
                        LeaderboardEntry_t entry;
                        if (SteamUserStats.GetDownloadedLeaderboardEntry(result.m_hSteamLeaderboardEntries, i, out entry, null, 0))
                            entries.Add(new KeyValuePair<int, int>(entry.m_nGlobalRank, entry.m_nScore));
                    }
                    then(entries);
                }
                catch (Exception ex)
                {
                    Fail("Leaderboard query failed: " + ex.Message);
                }
            });
        }

        private void OnFound(LeaderboardFindResult_t result, bool failure)
        {
            try
            {
                if (failure || result.m_bLeaderboardFound == 0) { Fail($"Steam has no leaderboard named '{_steamName}'"); return; }
                _board = result.m_hSteamLeaderboard;
                EntryCount = SteamUserStats.GetLeaderboardEntryCount(_board);
                if (EntryCount <= 0) { Done.Set(); return; }

                Download(ELeaderboardDataRequest.k_ELeaderboardDataRequestGlobal, 1, Math.Min(_topCount, EntryCount), top =>
                {
                    Top.AddRange(top);
                    // The player's own entry (empty if they have never completed this leaderboard).
                    Download(ELeaderboardDataRequest.k_ELeaderboardDataRequestGlobalAroundUser, 0, 0, mine =>
                    {
                        if (mine.Count > 0)
                        {
                            HasPlayer = true;
                            PlayerRank = mine[0].Key;
                            PlayerMs = mine[0].Value;
                        }
                        NextPercentile();
                    });
                });
            }
            catch (Exception ex)
            {
                Fail("Leaderboard query failed: " + ex.Message);
            }
        }

        // The time at a few fixed percentiles, so a caller can see what times are achievable.
        private void NextPercentile()
        {
            if (_percentIndex >= PercentSteps.Length) { StartTimeSearch(); return; }
            double percent = PercentSteps[_percentIndex++];
            int rank = Math.Max(1, (int)Math.Ceiling(EntryCount * percent / 100.0));
            Download(ELeaderboardDataRequest.k_ELeaderboardDataRequestGlobal, rank, rank, entries =>
            {
                if (entries.Count > 0)
                    Percentiles.Add(new KeyValuePair<double, KeyValuePair<int, int>>(percent, entries[0]));
                NextPercentile();
            });
        }

        // Where each given time would rank: the number of faster entries, plus one.
        private void StartTimeSearch()
        {
            if (_queryMs.Count == 0) { Done.Set(); return; }

            if (EntryCount <= FullDownloadLimit)
            {
                Download(ELeaderboardDataRequest.k_ELeaderboardDataRequestGlobal, 1, EntryCount, entries =>
                {
                    var scores = new List<int>(entries.Count);
                    foreach (var e in entries) scores.Add(e.Value);
                    scores.Sort();
                    foreach (int ms in _queryMs)
                    {
                        int lo = 0, hi = scores.Count;           // first index whose score is >= ms
                        while (lo < hi)
                        {
                            int mid = lo + (hi - lo) / 2;
                            if (scores[mid] < ms) lo = mid + 1; else hi = mid;
                        }
                        TimeRanks.Add(lo + 1);
                    }
                    Done.Set();
                });
                return;
            }

            _queryIndex = 0;
            BeginSearch();
        }

        private void BeginSearch()
        {
            if (_queryIndex >= _queryMs.Count) { Done.Set(); return; }
            _lo = 1;
            _hi = EntryCount + 1;
            SearchStep();
        }

        // Binary search for the first rank whose time is not better than the queried one:
        // about log2(entries) single-entry downloads per time.
        private void SearchStep()
        {
            if (_lo >= _hi)
            {
                TimeRanks.Add(_lo);
                _queryIndex++;
                BeginSearch();
                return;
            }
            int target = _queryMs[_queryIndex];
            int mid = _lo + (_hi - _lo) / 2;
            Download(ELeaderboardDataRequest.k_ELeaderboardDataRequestGlobal, mid, mid, entries =>
            {
                if (entries.Count == 0) { _hi = mid; }                   // past the end
                else if (entries[0].Value < target) { _lo = mid + 1; }   // that entry is faster
                else { _hi = mid; }
                SearchStep();
            });
        }
    }
}
