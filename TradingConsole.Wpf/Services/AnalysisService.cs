// In TradingConsole.Wpf/Services/AnalysisService.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TradingConsole.Core.Models;
using TradingConsole.DhanApi;
using TradingConsole.DhanApi.Models;
using TradingConsole.Wpf.ViewModels;

namespace TradingConsole.Wpf.Services
{
    public enum IntradayContext
    {
        Trending,
        RangeBound,
        Breakout,
        Volatile,
        Indeterminate
    }

    public class AnalysisService : INotifyPropertyChanged
    {
        #region Parameters and State
        private readonly SettingsViewModel _settingsViewModel;
        private readonly DhanApiClient _apiClient;
        private readonly ScripMasterService _scripMasterService;
        private readonly HistoricalIvService _historicalIvService;
        private readonly MarketProfileService _marketProfileService;
        private readonly IndicatorStateService _indicatorStateService;
        private readonly SignalLoggerService _signalLoggerService;
        private readonly NotificationService _notificationService;
        private readonly DashboardViewModel _dashboardViewModel;
        private readonly Dictionary<string, List<MarketProfileData>> _historicalMarketProfiles = new Dictionary<string, List<MarketProfileData>>();

        private readonly Dictionary<string, IntradayIvState.CustomLevelState> _customLevelStates = new();
        private readonly HashSet<string> _backfilledInstruments = new HashSet<string>();
        private readonly Dictionary<string, AnalysisResult> _analysisResults = new();
        private readonly Dictionary<string, MarketProfile> _marketProfiles = new Dictionary<string, MarketProfile>();
        private readonly Dictionary<string, (bool isBreakout, bool isBreakdown)> _initialBalanceState = new();

        private readonly Dictionary<string, DashboardInstrument> _instrumentCache = new();
        private readonly Dictionary<string, RelativeStrengthState> _relativeStrengthStates = new();
        private readonly Dictionary<string, IvSkewState> _ivSkewStates = new(); // ADDED


        public int ShortEmaLength { get; set; }
        public int LongEmaLength { get; set; }
        public int AtrPeriod { get; set; }
        public int AtrSmaPeriod { get; set; }
        public int RsiPeriod { get; set; }
        public int RsiDivergenceLookback { get; set; }
        public int VolumeHistoryLength { get; set; }
        public double VolumeBurstMultiplier { get; set; }
        public int IvHistoryLength { get; set; }
        public decimal IvSpikeThreshold { get; set; }
        public int ObvMovingAveragePeriod { get; set; }
        public decimal VwapUpperBandMultiplier { get; set; }
        public decimal VwapLowerBandMultiplier { get; set; }


        private const int MinIvHistoryForSignal = 2;
        private readonly List<TimeSpan> _timeframes = new()
        {
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15)
        };
        private readonly Dictionary<string, (decimal cumulativePriceVolume, long cumulativeVolume, List<decimal> ivHistory)> _tickAnalysisState = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, List<Candle>>> _multiTimeframeCandles = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, EmaState>> _multiTimeframePriceEmaState = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, EmaState>> _multiTimeframeVwapEmaState = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, RsiState>> _multiTimeframeRsiState = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, AtrState>> _multiTimeframeAtrState = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, ObvState>> _multiTimeframeObvState = new();
        private readonly Dictionary<string, IntradayIvState> _intradayIvStates = new Dictionary<string, IntradayIvState>();

        public event Action<AnalysisResult>? OnAnalysisUpdated;

        public event Action<string, Candle, TimeSpan>? CandleUpdated;
        #endregion

        public AnalysisService(SettingsViewModel settingsViewModel, DhanApiClient apiClient, ScripMasterService scripMasterService, HistoricalIvService historicalIvService, MarketProfileService marketProfileService, IndicatorStateService indicatorStateService, SignalLoggerService signalLoggerService, NotificationService notificationService, DashboardViewModel dashboardViewModel)
        {
            _settingsViewModel = settingsViewModel;
            _apiClient = apiClient;
            _scripMasterService = scripMasterService;
            _historicalIvService = historicalIvService;
            _marketProfileService = marketProfileService;
            _indicatorStateService = indicatorStateService;
            _signalLoggerService = signalLoggerService;
            _notificationService = notificationService;
            _dashboardViewModel = dashboardViewModel;

            UpdateParametersFromSettings();
        }

        public void SaveIndicatorStates()
        {
            foreach (var securityId in _multiTimeframePriceEmaState.Keys)
            {
                foreach (var timeframe in _timeframes)
                {
                    var key = $"{securityId}_{timeframe.TotalMinutes}";

                    var priceEmaState = _multiTimeframePriceEmaState[securityId][timeframe];
                    var vwapEmaState = _multiTimeframeVwapEmaState[securityId][timeframe];
                    var rsiState = _multiTimeframeRsiState[securityId][timeframe];
                    var atrState = _multiTimeframeAtrState[securityId][timeframe];
                    var obvState = _multiTimeframeObvState[securityId][timeframe];

                    var stateToSave = new IndicatorState
                    {
                        LastShortEma = priceEmaState.CurrentShortEma,
                        LastLongEma = priceEmaState.CurrentLongEma,
                        LastVwapShortEma = vwapEmaState.CurrentShortEma,
                        LastVwapLongEma = vwapEmaState.CurrentLongEma,
                        LastRsiAvgGain = rsiState.AvgGain,
                        LastRsiAvgLoss = rsiState.AvgLoss,
                        LastAtr = atrState.CurrentAtr,
                        LastObv = obvState.CurrentObv,
                        LastObvMovingAverage = obvState.CurrentMovingAverage
                    };

                    _indicatorStateService.UpdateState(key, stateToSave);
                }
            }
            _indicatorStateService.SaveDatabase();
        }


        public void SaveMarketProfileDatabase()
        {
            _marketProfileService.SaveDatabase();
        }

        public void UpdateParametersFromSettings()
        {
            ShortEmaLength = _settingsViewModel.ShortEmaLength;
            LongEmaLength = _settingsViewModel.LongEmaLength;
            AtrPeriod = _settingsViewModel.AtrPeriod;
            AtrSmaPeriod = _settingsViewModel.AtrSmaPeriod;
            RsiPeriod = _settingsViewModel.RsiPeriod;
            RsiDivergenceLookback = _settingsViewModel.RsiDivergenceLookback;
            VolumeHistoryLength = _settingsViewModel.VolumeHistoryLength;
            VolumeBurstMultiplier = _settingsViewModel.VolumeBurstMultiplier;
            IvHistoryLength = _settingsViewModel.IvHistoryLength;
            IvSpikeThreshold = _settingsViewModel.IvSpikeThreshold;
            ObvMovingAveragePeriod = _settingsViewModel.ObvMovingAveragePeriod;
            VwapUpperBandMultiplier = _settingsViewModel.VwapUpperBandMultiplier;
            VwapLowerBandMultiplier = _settingsViewModel.VwapLowerBandMultiplier;

        }

        public List<Candle>? GetCandles(string securityId, TimeSpan timeframe)
        {
            if (_multiTimeframeCandles.TryGetValue(securityId, out var timeframes) &&
                timeframes.TryGetValue(timeframe, out var candles))
            {
                return candles;
            }
            return null;
        }

        public void OnInstrumentDataReceived(DashboardInstrument instrument, decimal underlyingPrice)
        {
            if (string.IsNullOrEmpty(instrument.SecurityId)) return;

            _instrumentCache[instrument.SecurityId] = instrument;

            if (instrument.InstrumentType.StartsWith("OPT") && instrument.ImpliedVolatility > 0)
            {
                var ivKey = GetHistoricalIvKey(instrument, underlyingPrice);
                if (!string.IsNullOrEmpty(ivKey))
                {
                    if (!_intradayIvStates.ContainsKey(ivKey))
                    {
                        _intradayIvStates[ivKey] = new IntradayIvState();
                    }
                    var ivState = _intradayIvStates[ivKey];

                    ivState.DayHighIv = Math.Max(ivState.DayHighIv, instrument.ImpliedVolatility);
                    ivState.DayLowIv = Math.Min(ivState.DayLowIv, instrument.ImpliedVolatility);

                    _historicalIvService.RecordDailyIv(ivKey, ivState.DayHighIv, ivState.DayLowIv);

                    var (ivRank, ivPercentile) = CalculateIvRankAndPercentile(instrument.ImpliedVolatility, ivKey, ivState);
                    var ivTrendSignal = GetIvTrendSignal(ivPercentile, ivRank, ivState);

                    if (_analysisResults.TryGetValue(instrument.SecurityId, out var existingResult))
                    {
                        existingResult.IvRank = ivRank;
                        existingResult.IvPercentile = ivPercentile;
                        existingResult.IvTrendSignal = ivTrendSignal;
                    }
                }
            }

            bool isNewInstrument = !_backfilledInstruments.Contains(instrument.SecurityId);
            if (isNewInstrument)
            {
                _backfilledInstruments.Add(instrument.SecurityId);

                _tickAnalysisState[instrument.SecurityId] = (0, 0, new List<decimal>());
                _multiTimeframeCandles[instrument.SecurityId] = new Dictionary<TimeSpan, List<Candle>>();
                _multiTimeframePriceEmaState[instrument.SecurityId] = new Dictionary<TimeSpan, EmaState>();
                _multiTimeframeVwapEmaState[instrument.SecurityId] = new Dictionary<TimeSpan, EmaState>();
                _multiTimeframeRsiState[instrument.SecurityId] = new Dictionary<TimeSpan, RsiState>();
                _multiTimeframeAtrState[instrument.SecurityId] = new Dictionary<TimeSpan, AtrState>();
                _multiTimeframeObvState[instrument.SecurityId] = new Dictionary<TimeSpan, ObvState>();

                if (instrument.InstrumentType == "INDEX")
                {
                    _relativeStrengthStates[instrument.SecurityId] = new RelativeStrengthState();
                    _ivSkewStates[instrument.SecurityId] = new IvSkewState(); // ADDED
                }

                _historicalMarketProfiles[instrument.SecurityId] = _marketProfileService.GetHistoricalProfiles(instrument.SecurityId);

                if (!_marketProfiles.ContainsKey(instrument.SecurityId))
                {
                    decimal tickSize = GetTickSize(instrument);
                    var startTime = DateTime.Today.Add(new TimeSpan(9, 15, 0));
                    _marketProfiles[instrument.SecurityId] = new MarketProfile(tickSize, startTime);
                }

                foreach (var tf in _timeframes)
                {
                    var key = $"{instrument.SecurityId}_{tf.TotalMinutes}";
                    var savedState = _indicatorStateService.GetState(key);

                    var priceEmaState = new EmaState();
                    var vwapEmaState = new EmaState();
                    var rsiState = new RsiState();
                    var atrState = new AtrState();
                    var obvState = new ObvState();

                    if (savedState != null)
                    {
                        priceEmaState.CurrentShortEma = savedState.LastShortEma;
                        priceEmaState.CurrentLongEma = savedState.LastLongEma;
                        vwapEmaState.CurrentShortEma = savedState.LastVwapShortEma;
                        vwapEmaState.CurrentLongEma = savedState.LastVwapLongEma;
                        rsiState.AvgGain = savedState.LastRsiAvgGain;
                        rsiState.AvgLoss = savedState.LastRsiAvgLoss;
                        atrState.CurrentAtr = savedState.LastAtr;
                        obvState.CurrentObv = savedState.LastObv;
                        obvState.CurrentMovingAverage = savedState.LastObvMovingAverage;
                        Debug.WriteLine($"[IndicatorWarmup] Loaded saved state for {key}");
                    }

                    _multiTimeframeCandles[instrument.SecurityId][tf] = new List<Candle>();
                    _multiTimeframePriceEmaState[instrument.SecurityId][tf] = priceEmaState;
                    _multiTimeframeVwapEmaState[instrument.SecurityId][tf] = vwapEmaState;
                    _multiTimeframeRsiState[instrument.SecurityId][tf] = rsiState;
                    _multiTimeframeAtrState[instrument.SecurityId][tf] = atrState;
                    _multiTimeframeObvState[instrument.SecurityId][tf] = obvState;
                }

                if (instrument.SegmentId == 0)
                {
                    _customLevelStates[instrument.Symbol] = new IntradayIvState.CustomLevelState();
                }

                Task.Run(() => BackfillAndSavePreviousDayProfileAsync(instrument));
                Task.Run(() => BackfillCurrentDayCandlesAsync(instrument));
                RunDailyBiasAnalysis(instrument);
            }

            foreach (var timeframe in _timeframes)
            {
                AggregateIntoCandle(instrument, timeframe);
            }
        }

        private string CalculateTrend(List<decimal> history, int period)
        {
            if (history.Count < period) return "Neutral";

            var recentHistory = history.TakeLast(period).ToList();
            var firstHalfAvg = recentHistory.Take(period / 2).DefaultIfEmpty(0).Average();
            var secondHalfAvg = recentHistory.Skip(period / 2).DefaultIfEmpty(0).Average();

            if (secondHalfAvg > firstHalfAvg * 1.0005m) return "Trending Up";
            if (secondHalfAvg < firstHalfAvg * 0.9995m) return "Trending Down";
            return "Neutral";
        }

        private string RunTier1InstitutionalIntentAnalysis(DashboardInstrument spotIndex)
        {
            if (!_relativeStrengthStates.ContainsKey(spotIndex.SecurityId)) return "Analyzing...";

            var state = _relativeStrengthStates[spotIndex.SecurityId];

            var future = _instrumentCache.Values.FirstOrDefault(i => i.IsFuture && i.UnderlyingSymbol == spotIndex.Symbol);
            if (future == null || future.Open == 0 || spotIndex.Open == 0) return "Futures Not Found";

            decimal spotChange = (spotIndex.LTP - spotIndex.Open) / spotIndex.Open;
            decimal futureChange = (future.LTP - future.Open) / future.Open;
            decimal basisDelta = futureChange - spotChange;

            state.BasisDeltaHistory.Add(basisDelta);
            if (state.BasisDeltaHistory.Count > 30)
            {
                state.BasisDeltaHistory.RemoveAt(0);
            }

            string basisTrend = CalculateTrend(state.BasisDeltaHistory, 30);

            string confirmation = "";
            var futureCandles = GetCandles(future.SecurityId, TimeSpan.FromMinutes(1));
            if (futureCandles != null && futureCandles.Count > 1)
            {
                var (volSignal, _, _) = CalculateVolumeSignalForTimeframe(futureCandles);
                var oiSignal = CalculateOiSignal(futureCandles);

                if (basisTrend == "Trending Up")
                {
                    if (volSignal == "Volume Burst" && oiSignal == "Long Buildup")
                        confirmation = " (Confirmed by Vol & OI)";
                    else if (volSignal == "Volume Burst")
                        confirmation = " (Confirmed by Vol)";
                }
                else if (basisTrend == "Trending Down")
                {
                    if (volSignal == "Volume Burst" && oiSignal == "Short Buildup")
                        confirmation = " (Confirmed by Vol & OI)";
                    else if (volSignal == "Volume Burst")
                        confirmation = " (Confirmed by Vol)";
                }
            }

            if (basisTrend == "Trending Up") return $"Bullish (Futures Strengthening){confirmation}";
            if (basisTrend == "Trending Down") return $"Bearish (Futures Weakening){confirmation}";

            return "Neutral";
        }

        private DateTime GetPreviousTradingDay(DateTime currentDate)
        {
            var date = currentDate.Date.AddDays(-1);
            while (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday || _settingsViewModel.MarketHolidays.Contains(date))
            {
                date = date.AddDays(-1);
            }
            return date;
        }

        private async Task BackfillAndSavePreviousDayProfileAsync(DashboardInstrument instrument)
        {
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istZone);

            if (istNow.TimeOfDay >= new TimeSpan(9, 15, 0))
            {
                Debug.WriteLine($"[BackfillPrevDay] Skipping, market is open. Current time: {istNow.TimeOfDay}");
                return;
            }

            DateTime dateToFetch = GetPreviousTradingDay(istNow);

            if (_historicalMarketProfiles.GetValueOrDefault(instrument.SecurityId)?.Any(p => p.Date.Date == dateToFetch.Date) == true)
            {
                Debug.WriteLine($"[BackfillPrevDay] Profile for {instrument.DisplayName} on {dateToFetch:yyyy-MM-dd} already exists. Skipping fetch.");
                return;
            }

            Debug.WriteLine($"[BackfillPrevDay] Starting backfill process for {instrument.DisplayName} for date: {dateToFetch:yyyy-MM-dd}.");

            try
            {
                var scripInfo = _scripMasterService.FindBySecurityIdAndType(instrument.SecurityId, instrument.InstrumentType);
                if (scripInfo == null)
                {
                    Debug.WriteLine($"[BackfillPrevDay] FAILED: Could not find scrip info for {instrument.SecurityId}.");
                    return;
                }

                var historicalData = await _apiClient.GetIntradayHistoricalDataAsync(scripInfo, "1", dateToFetch);

                if (historicalData?.Open == null || !historicalData.Open.Any())
                {
                    Debug.WriteLine($"[BackfillPrevDay] No historical data points returned from API for {instrument.DisplayName} for date {dateToFetch:yyyy-MM-dd}.");
                    return;
                }

                Debug.WriteLine($"[BackfillPrevDay] SUCCESS: Received {historicalData.Open.Count} historical data points for {instrument.DisplayName}.");

                decimal tickSize = GetTickSize(instrument);
                var sessionStartTime = dateToFetch.Date.Add(new TimeSpan(9, 15, 0));
                var historicalProfile = new MarketProfile(tickSize, sessionStartTime);

                for (int i = 0; i < historicalData.Open.Count; i++)
                {
                    var candle = new Candle
                    {
                        Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)historicalData.StartTime[i]).UtcDateTime,
                        Open = historicalData.Open[i],
                        High = historicalData.High[i],
                        Low = historicalData.Low[i],
                        Close = historicalData.Close[i],
                        Volume = (long)historicalData.Volume[i],
                        OpenInterest = historicalData.OpenInterest.Count > i ? (long)historicalData.OpenInterest[i] : 0,
                    };
                    UpdateMarketProfile(historicalProfile, candle);
                }

                CalculateDevelopingProfileLevels(historicalProfile);
                var profileDataToSave = historicalProfile.ToMarketProfileData();
                _marketProfileService.UpdateProfile(instrument.SecurityId, profileDataToSave);

                Debug.WriteLine($"[BackfillPrevDay] Successfully built and saved historical market profile for {instrument.DisplayName} for {dateToFetch:yyyy-MM-dd}.");

                if (!_historicalMarketProfiles.ContainsKey(instrument.SecurityId))
                {
                    _historicalMarketProfiles[instrument.SecurityId] = new List<MarketProfileData>();
                }
                _historicalMarketProfiles[instrument.SecurityId].RemoveAll(p => p.Date.Date == dateToFetch.Date);
                _historicalMarketProfiles[instrument.SecurityId].Add(profileDataToSave);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackfillPrevDay] UNEXPECTED ERROR during backfill for {instrument.DisplayName}: {ex.Message}");
            }
        }

        private async Task BackfillCurrentDayCandlesAsync(DashboardInstrument instrument)
        {
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istZone);

            if (istNow.TimeOfDay < new TimeSpan(9, 15, 0)) return;

            Debug.WriteLine($"[BackfillCurrentDay] App started mid-day. Backfilling today's candles for {instrument.DisplayName}.");

            try
            {
                var scripInfo = _scripMasterService.FindBySecurityIdAndType(instrument.SecurityId, instrument.InstrumentType);
                if (scripInfo == null) return;

                var historicalData = await _apiClient.GetIntradayHistoricalDataAsync(scripInfo, "1", istNow.Date);

                if (historicalData?.Open != null && historicalData.Open.Any())
                {
                    var candles = new List<Candle>();
                    for (int i = 0; i < historicalData.Open.Count; i++)
                    {
                        var candle = new Candle
                        {
                            Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)historicalData.StartTime[i]).UtcDateTime,
                            Open = historicalData.Open[i],
                            High = historicalData.High[i],
                            Low = historicalData.Low[i],
                            Close = historicalData.Close[i],
                            Volume = (long)historicalData.Volume[i],
                            OpenInterest = historicalData.OpenInterest.Count > i ? (long)historicalData.OpenInterest[i] : 0,
                        };
                        candles.Add(candle);

                        if (_marketProfiles.TryGetValue(instrument.SecurityId, out var liveProfile))
                        {
                            UpdateMarketProfile(liveProfile, candle);
                        }
                    }

                    foreach (var timeframe in _timeframes)
                    {
                        var aggregatedCandles = AggregateHistoricalCandles(candles, timeframe);
                        _multiTimeframeCandles[instrument.SecurityId][timeframe] = aggregatedCandles;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackfillCurrentDay] ERROR: {ex.Message}");
            }
        }

        #region Market Profile (TPO) and Volume Profile Calculation
        private void UpdateMarketProfile(MarketProfile profile, Candle candle)
        {
            profile.UpdateInitialBalance(candle);
            var tpoPeriod = profile.GetTpoPeriod(candle.Timestamp);

            // This loop is for TPOs only
            for (decimal price = candle.Low; price <= candle.High; price += profile.TickSize)
            {
                var quantizedPrice = profile.QuantizePrice(price);
                if (!profile.TpoLevels.ContainsKey(quantizedPrice))
                {
                    profile.TpoLevels[quantizedPrice] = new List<char>();
                }
                if (!profile.TpoLevels[quantizedPrice].Contains(tpoPeriod))
                {
                    profile.TpoLevels[quantizedPrice].Add(tpoPeriod);
                }
            }

            var typicalPrice = (candle.High + candle.Low + candle.Close) / 3;
            var typicalPriceQuantized = profile.QuantizePrice(typicalPrice);
            if (!profile.VolumeLevels.ContainsKey(typicalPriceQuantized))
            {
                profile.VolumeLevels[typicalPriceQuantized] = 0;
            }
            profile.VolumeLevels[typicalPriceQuantized] += candle.Volume;
        }

        private void UpdateMarketProfile(string securityId, Candle candle)
        {
            if (!_marketProfiles.TryGetValue(securityId, out var profile))
            {
                return;
            }
            UpdateMarketProfile(profile, candle);
            CalculateDevelopingProfileLevels(profile);
        }

        private void CalculateDevelopingProfileLevels(MarketProfile profile)
        {
            if (profile.TpoLevels.Count == 0) return;

            var pocLevel = profile.TpoLevels
                .OrderByDescending(kvp => kvp.Value.Count)
                .ThenBy(kvp => Math.Abs(kvp.Key - profile.DevelopingTpoLevels.PointOfControl))
                .FirstOrDefault();

            if (pocLevel.Key == 0) return;

            profile.DevelopingTpoLevels.PointOfControl = pocLevel.Key;

            long totalTpos = profile.TpoLevels.Sum(kvp => kvp.Value.Count);
            long tposInVaTarget = (long)(totalTpos * 0.70);

            var valueAreaLevels = new List<KeyValuePair<decimal, List<char>>> { pocLevel };
            long tposInVaCurrent = pocLevel.Value.Count;

            var levelsAbovePoc = profile.TpoLevels.Where(kvp => kvp.Key > pocLevel.Key).OrderBy(kvp => kvp.Key).ToList();
            var levelsBelowPoc = profile.TpoLevels.Where(kvp => kvp.Key < pocLevel.Key).OrderByDescending(kvp => kvp.Key).ToList();

            int aboveIndex = 0;
            int belowIndex = 0;

            while (tposInVaCurrent < tposInVaTarget && (aboveIndex < levelsAbovePoc.Count || belowIndex < levelsBelowPoc.Count))
            {
                long tpoCountAbove = (aboveIndex < levelsAbovePoc.Count) ? levelsAbovePoc[aboveIndex].Value.Count : 0;
                long tpoCountBelow = (belowIndex < levelsBelowPoc.Count) ? levelsBelowPoc[belowIndex].Value.Count : 0;

                if (tpoCountAbove > tpoCountBelow)
                {
                    valueAreaLevels.Add(levelsAbovePoc[aboveIndex]);
                    tposInVaCurrent += tpoCountAbove;
                    aboveIndex++;
                }
                else if (tpoCountBelow > 0)
                {
                    valueAreaLevels.Add(levelsBelowPoc[belowIndex]);
                    tposInVaCurrent += tpoCountBelow;
                    belowIndex++;
                }
                else
                {
                    break;
                }
            }

            if (valueAreaLevels.Any())
            {
                profile.DevelopingTpoLevels.ValueAreaHigh = valueAreaLevels.Max(kvp => kvp.Key);
                profile.DevelopingTpoLevels.ValueAreaLow = valueAreaLevels.Min(kvp => kvp.Key);
            }

            if (profile.VolumeLevels.Count == 0) return;

            var vpocLevel = profile.VolumeLevels
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key)
                .FirstOrDefault();

            if (vpocLevel.Key != 0)
            {
                profile.DevelopingVolumeProfile.VolumePoc = vpocLevel.Key;
            }
        }

        public void RunDailyBiasAnalysis(DashboardInstrument instrument)
        {
            if (!_historicalMarketProfiles.TryGetValue(instrument.SecurityId, out var profiles) || profiles.Count < 2)
            {
                if (_analysisResults.TryGetValue(instrument.SecurityId, out var result))
                {
                    result.DailyBias = "Insufficient History";
                }
                return;
            }

            var recentProfiles = profiles.OrderByDescending(p => p.Date).Take(8).ToList();
            var previousDay = recentProfiles.FirstOrDefault(p => p.Date.Date < DateTime.Today);
            if (previousDay == null) return;

            string structure = AnalyzeMarketStructure(recentProfiles);
            string openingBias = AnalyzeOpeningCondition(instrument.Open, previousDay);
            string finalBias = SynthesizeBias(structure, openingBias);

            if (_analysisResults.TryGetValue(instrument.SecurityId, out var analysisResult))
            {
                analysisResult.MarketStructure = structure;
                analysisResult.DailyBias = finalBias;
                OnAnalysisUpdated?.Invoke(analysisResult);
            }
        }

        private string AnalyzeMarketStructure(List<MarketProfileData> profiles)
        {
            if (profiles.Count < 3) return "Building";

            var lastThreeDays = profiles.Take(3).ToList();
            var day1 = lastThreeDays[0];
            var day2 = lastThreeDays[1];
            var day3 = lastThreeDays[2];

            bool isTrendingUp = day1.TpoLevelsInfo.ValueAreaLow > day2.TpoLevelsInfo.ValueAreaLow &&
                                day2.TpoLevelsInfo.ValueAreaLow > day3.TpoLevelsInfo.ValueAreaLow;

            bool isTrendingDown = day1.TpoLevelsInfo.ValueAreaHigh < day2.TpoLevelsInfo.ValueAreaHigh &&
                                  day2.TpoLevelsInfo.ValueAreaHigh < day3.TpoLevelsInfo.ValueAreaHigh;

            if (isTrendingUp) return "Trending Up";
            if (isTrendingDown) return "Trending Down";

            bool isOverlapping = (day1.TpoLevelsInfo.ValueAreaHigh >= day2.TpoLevelsInfo.ValueAreaLow) &&
                                 (day1.TpoLevelsInfo.ValueAreaLow <= day2.TpoLevelsInfo.ValueAreaHigh);

            if (isOverlapping) return "Balancing";

            return "Transitioning";
        }

        private string AnalyzeOpeningCondition(decimal openPrice, MarketProfileData previousDay)
        {
            if (openPrice == 0) return "Awaiting Open";

            var prevVAH = previousDay.TpoLevelsInfo.ValueAreaHigh;
            var prevVAL = previousDay.TpoLevelsInfo.ValueAreaLow;
            var prevPOC = previousDay.TpoLevelsInfo.PointOfControl;

            if (openPrice > prevVAH) return "Opening Above Value";
            if (openPrice < prevVAL) return "Opening Below Value";
            if (openPrice > prevPOC) return "Opening Inside Value (High)";
            if (openPrice < prevPOC) return "Opening Inside Value (Low)";

            return "Opening at POC";
        }

        private string SynthesizeBias(string structure, string opening)
        {
            if (opening == "Awaiting Open") return "Awaiting Open";

            if (structure == "Trending Up" && opening == "Opening Above Value") return "Strong Bullish";
            if (structure == "Trending Down" && opening == "Opening Below Value") return "Strong Bearish";
            if (structure == "Trending Up" && opening.Contains("Inside Value")) return "Bullish Rotational";
            if (structure == "Trending Down" && opening.Contains("Inside Value")) return "Bearish Rotational";
            if (structure == "Balancing" && opening == "Opening Above Value") return "Bullish Breakout Watch";
            if (structure == "Balancing" && opening == "Opening Below Value") return "Bearish Breakout Watch";
            if (structure == "Balancing" && opening.Contains("Inside Value")) return "Pure Rotational";

            return "Neutral";
        }

        private string AnalyzeOpenType(DashboardInstrument instrument, List<Candle> oneMinCandles)
        {
            if (oneMinCandles.Count < 3) return "Analyzing Open...";

            var firstCandle = oneMinCandles[0];
            bool isFirstCandleStrong = Math.Abs(firstCandle.Close - firstCandle.Open) > (firstCandle.High - firstCandle.Low) * 0.7m;

            if (isFirstCandleStrong && firstCandle.Close > firstCandle.Open && oneMinCandles[1].Low > firstCandle.Low && oneMinCandles[2].Low > firstCandle.Low)
            {
                return "Open-Drive (Bullish)";
            }
            if (isFirstCandleStrong && firstCandle.Close < firstCandle.Open && oneMinCandles[1].High < firstCandle.High && oneMinCandles[2].High < firstCandle.High)
            {
                return "Open-Drive (Bearish)";
            }

            if (firstCandle.Low < instrument.Open && firstCandle.Close > instrument.Open && isFirstCandleStrong)
            {
                return "Open-Test-Drive (Bullish)";
            }
            if (firstCandle.High > instrument.Open && firstCandle.Close < instrument.Open && isFirstCandleStrong)
            {
                return "Open-Test-Drive (Bearish)";
            }

            var thirdCandle = oneMinCandles[2];
            if (firstCandle.Close > firstCandle.Open && thirdCandle.Close < firstCandle.Open)
            {
                return "Open-Rejection-Reverse (Bearish)";
            }
            if (firstCandle.Close < firstCandle.Open && thirdCandle.Close > firstCandle.Open)
            {
                return "Open-Rejection-Reverse (Bullish)";
            }

            return "Open-Auction (Rotational)";
        }

        private string AnalyzePriceRelativeToYesterdayProfile(decimal ltp, MarketProfileData? previousDay)
        {
            if (previousDay == null || ltp == 0) return "N/A";

            var prevVAH = previousDay.TpoLevelsInfo.ValueAreaHigh;
            var prevVAL = previousDay.TpoLevelsInfo.ValueAreaLow;
            var prevPOC = previousDay.TpoLevelsInfo.PointOfControl;

            if (ltp > prevVAH) return "Trading Above Y-VAH";
            if (ltp < prevVAL) return "Trading Below Y-VAL";
            if (ltp > prevPOC) return "Trading in Upper Y-Value";
            if (ltp < prevPOC) return "Trading in Lower Y-Value";

            return "Trading at Y-POC";
        }


        private void RunMarketProfileAnalysis(DashboardInstrument instrument, MarketProfile currentProfile, AnalysisResult result)
        {
            var previousDayProfile = _historicalMarketProfiles.GetValueOrDefault(instrument.SecurityId)?.FirstOrDefault(p => p.Date.Date < DateTime.Today.Date);
            if (previousDayProfile == null)
            {
                result.MarketProfileSignal = "Awaiting Previous Day Data";
                return;
            }

            var ltp = instrument.LTP;
            var prevVAH = previousDayProfile.TpoLevelsInfo.ValueAreaHigh;
            var prevVAL = previousDayProfile.TpoLevelsInfo.ValueAreaLow;
            var currentVAH = currentProfile.DevelopingTpoLevels.ValueAreaHigh;
            var currentVAL = currentProfile.DevelopingTpoLevels.ValueAreaLow;

            // True Acceptance (Highest Conviction)
            if (currentVAL > prevVAH) { result.MarketProfileSignal = "True Acceptance Above Y-VAH"; return; }
            if (currentVAH < prevVAL) { result.MarketProfileSignal = "True Acceptance Below Y-VAL"; return; }

            // Look Above/Below and Fail (Strong Reversal)
            var oneMinCandles = GetCandles(instrument.SecurityId, TimeSpan.FromMinutes(1));
            if (oneMinCandles != null && oneMinCandles.Count > 2)
            {
                var lastCandle = oneMinCandles.Last();
                var secondLastCandle = oneMinCandles[^2];

                if (secondLastCandle.High > prevVAH && lastCandle.Close < prevVAH) { result.MarketProfileSignal = "Look Above and Fail at Y-VAH"; return; }
                if (secondLastCandle.Low < prevVAL && lastCandle.Close > prevVAL) { result.MarketProfileSignal = "Look Below and Fail at Y-VAL"; return; }
            }

            // Initiative Buying/Selling (Medium Conviction)
            if (ltp > prevVAH) { result.MarketProfileSignal = "Initiative Buying Above Y-VAH"; return; }
            if (ltp < prevVAL) { result.MarketProfileSignal = "Initiative Selling Below Y-VAL"; return; }

            result.MarketProfileSignal = "Trading Inside Y-Value";
        }

        private string GetInitialBalanceSignal(decimal ltp, MarketProfile profile, string securityId)
        {
            if (!profile.IsInitialBalanceSet)
            {
                return "IB Forming";
            }

            if (!_initialBalanceState.ContainsKey(securityId))
            {
                _initialBalanceState[securityId] = (false, false);
            }

            var (isBreakout, isBreakdown) = _initialBalanceState[securityId];

            if (ltp > profile.InitialBalanceHigh && !isBreakout)
            {
                _initialBalanceState[securityId] = (true, false);
                return "IB Breakout";
            }

            if (ltp < profile.InitialBalanceLow && !isBreakdown)
            {
                _initialBalanceState[securityId] = (false, true);
                return "IB Breakdown";
            }

            if (ltp > profile.InitialBalanceHigh && isBreakout) return "IB Extension Up";
            if (ltp < profile.InitialBalanceLow && isBreakdown) return "IB Extension Down";

            if (ltp <= profile.InitialBalanceHigh && isBreakout)
            {
                _initialBalanceState[securityId] = (false, false);
                return "IB Failed Breakout";
            }

            if (ltp >= profile.InitialBalanceLow && isBreakdown)
            {
                _initialBalanceState[securityId] = (false, false);
                return "IB Failed Breakdown";
            }

            return "Inside IB";
        }


        private string GetBaseMarketSignal(decimal ltp, MarketProfile profile)
        {
            var tpoInfo = profile.DevelopingTpoLevels;
            var volumeInfo = profile.DevelopingVolumeProfile;
            decimal tolerance = ltp * 0.0002m;

            var vahUpperBand = tpoInfo.ValueAreaHigh + tolerance;
            var vahLowerBand = tpoInfo.ValueAreaHigh - tolerance;
            var valUpperBand = tpoInfo.ValueAreaLow + tolerance;
            var valLowerBand = tpoInfo.ValueAreaLow - tolerance;
            var pocUpperBand = tpoInfo.PointOfControl + tolerance;
            var pocLowerBand = tpoInfo.PointOfControl - tolerance;
            var vpocUpperBand = volumeInfo.VolumePoc + tolerance;
            var vpocLowerBand = volumeInfo.VolumePoc - tolerance;

            if (ltp > vahUpperBand) return "Breakout above value";
            if (ltp < valLowerBand) return "Breakdown below value";
            if (ltp >= vahLowerBand && ltp <= vahUpperBand) return "At dVAH Band";
            if (ltp >= valLowerBand && ltp <= valUpperBand) return "At dVAL Band";

            bool inPocBand = ltp >= pocLowerBand && ltp <= pocUpperBand;
            bool inVpocBand = volumeInfo.VolumePoc > 0 && (ltp >= vpocLowerBand && ltp <= vpocUpperBand);

            if (inPocBand && inVpocBand) return "At dPOC & dVPOC - High conviction";
            if (inPocBand) return "At dPOC Band";
            if (inVpocBand) return "At dVPOC Band";

            return "Inside Value Area";
        }

        private decimal GetTickSize(DashboardInstrument? instrument)
        {
            if (instrument?.InstrumentType == "INDEX")
            {
                return 1.0m;
            }
            return 0.05m;
        }

        #endregion

        private string GetHistoricalIvKey(DashboardInstrument instrument, decimal underlyingPrice)
        {
            if (string.IsNullOrEmpty(instrument.UnderlyingSymbol)) return string.Empty;

            var scripInfo = _scripMasterService.FindBySecurityId(instrument.SecurityId);
            if (scripInfo == null || scripInfo.StrikePrice <= 0) return string.Empty;

            int strikeDistance = (int)Math.Round((scripInfo.StrikePrice - underlyingPrice) / 50);
            string moneyness;
            if (strikeDistance == 0) moneyness = "ATM";
            else if (strikeDistance > 0) moneyness = $"ATM+{strikeDistance}";
            else moneyness = $"ATM{strikeDistance}";

            return $"{instrument.UnderlyingSymbol}_{moneyness}_{scripInfo.OptionType}";
        }

        private (decimal ivRank, decimal ivPercentile) CalculateIvRankAndPercentile(decimal currentIv, string key, IntradayIvState ivState)
        {
            decimal dayRange = ivState.DayHighIv - ivState.DayLowIv;
            decimal ivPercentile = (dayRange > 0) ? (currentIv - ivState.DayLowIv) / dayRange * 100 : 0;

            var (histHigh, histLow) = _historicalIvService.Get90DayIvRange(key);
            decimal histRange = histHigh - histLow;
            decimal ivRank = (histRange > 0) ? (currentIv - histLow) / histRange * 100 : 0;

            return (Math.Round(ivRank, 2), Math.Round(ivPercentile, 2));
        }

        private string GetIvTrendSignal(decimal ivp, decimal ivr, IntradayIvState state)
        {
            state.IvPercentileHistory.Add(ivp);
            if (state.IvPercentileHistory.Count > 10)
            {
                state.IvPercentileHistory.RemoveAt(0);
            }

            if (state.IvPercentileHistory.Count < 5)
            {
                return "Building History...";
            }

            var recentIVP = state.IvPercentileHistory.Last();
            var previousIVP = state.IvPercentileHistory[^2];
            var fivePeriodAvgIVP = state.IvPercentileHistory.TakeLast(5).Average();
            var tenPeriodAvgIVP = state.IvPercentileHistory.Average();

            if (recentIVP > previousIVP + 15 && recentIVP > 60) return "IV Spike Up";
            if (recentIVP < previousIVP - 15 && recentIVP < 40) return "IV Contraction";
            if (ivr > 85 && recentIVP < fivePeriodAvgIVP && recentIVP < tenPeriodAvgIVP) return "IV Crush Warning";
            if (ivr < 60 && recentIVP > fivePeriodAvgIVP && previousIVP < tenPeriodAvgIVP) return "IV Rising (Momentum)";
            if (ivr < 20 && ivp < 20) return "IV Low & Stable";

            return "Neutral";
        }

        private List<Candle> AggregateHistoricalCandles(List<Candle> minuteCandles, TimeSpan timeframe)
        {
            return minuteCandles
                .GroupBy(c => new DateTime(c.Timestamp.Ticks - (c.Timestamp.Ticks % timeframe.Ticks), DateTimeKind.Utc))
                .Select(g => new Candle
                {
                    Timestamp = g.Key,
                    Open = g.First().Open,
                    High = g.Max(c => c.High),
                    Low = g.Min(c => c.Low),
                    Close = g.Last().Close,
                    Volume = g.Sum(c => c.Volume),
                    OpenInterest = g.Last().OpenInterest,
                    Vwap = g.Sum(c => c.Close * c.Volume) / (g.Sum(c => c.Volume) == 0 ? 1 : g.Sum(c => c.Volume))
                })
                .ToList();
        }

        private void AggregateIntoCandle(DashboardInstrument instrument, TimeSpan timeframe)
        {
            if (!_multiTimeframeCandles.ContainsKey(instrument.SecurityId) || !_multiTimeframeCandles[instrument.SecurityId].ContainsKey(timeframe))
            {
                return;
            }

            var candles = _multiTimeframeCandles[instrument.SecurityId][timeframe];
            var now = DateTime.UtcNow;
            var candleTimestamp = new DateTime(now.Ticks - (now.Ticks % timeframe.Ticks), now.Kind);

            var currentCandle = candles.LastOrDefault();

            if (currentCandle == null || currentCandle.Timestamp != candleTimestamp)
            {
                var lastClosedCandle = currentCandle;

                var newCandle = new Candle
                {
                    Timestamp = candleTimestamp,
                    Open = instrument.LTP,
                    High = instrument.LTP,
                    Low = instrument.LTP,
                    Close = instrument.LTP,
                    Volume = instrument.LastTradedQuantity,
                    OpenInterest = instrument.OpenInterest,
                    CumulativePriceVolume = instrument.AvgTradePrice * instrument.LastTradedQuantity,
                    CumulativeVolume = instrument.LastTradedQuantity,
                    Vwap = instrument.AvgTradePrice
                };
                candles.Add(newCandle);

                if (lastClosedCandle != null)
                {
                    if (timeframe.TotalMinutes == 1)
                    {
                        UpdateMarketProfile(instrument.SecurityId, lastClosedCandle);
                    }
                    RunComplexAnalysis(instrument);
                }

                CandleUpdated?.Invoke(instrument.SecurityId, newCandle, timeframe);
            }
            else
            {
                currentCandle.High = Math.Max(currentCandle.High, instrument.LTP);
                currentCandle.Low = Math.Min(currentCandle.Low, instrument.LTP);
                currentCandle.Close = instrument.LTP;
                currentCandle.Volume += instrument.LastTradedQuantity;
                currentCandle.OpenInterest = instrument.OpenInterest;
                currentCandle.CumulativePriceVolume += instrument.AvgTradePrice * instrument.LastTradedQuantity;
                currentCandle.CumulativeVolume += instrument.LastTradedQuantity;
                currentCandle.Vwap = (currentCandle.CumulativeVolume > 0)
                    ? currentCandle.CumulativePriceVolume / currentCandle.CumulativeVolume
                    : currentCandle.Close;

                CandleUpdated?.Invoke(instrument.SecurityId, currentCandle, timeframe);
            }
        }

        private void RunComplexAnalysis(DashboardInstrument instrument)
        {
            if (!_analysisResults.TryGetValue(instrument.SecurityId, out var result))
            {
                result = new AnalysisResult { SecurityId = instrument.SecurityId };
                _analysisResults[instrument.SecurityId] = result;
            }

            // --- MODIFIED: Use future for volume-based analysis of an index ---
            DashboardInstrument instrumentForVolume = instrument;
            if (instrument.InstrumentType == "INDEX")
            {
                var future = _instrumentCache.Values.FirstOrDefault(i => i.IsFuture && i.UnderlyingSymbol == instrument.Symbol);
                if (future != null)
                {
                    instrumentForVolume = future;
                }
            }

            var tickState = _tickAnalysisState[instrumentForVolume.SecurityId];
            tickState.cumulativePriceVolume += instrumentForVolume.AvgTradePrice * instrumentForVolume.LastTradedQuantity;
            tickState.cumulativeVolume += instrumentForVolume.LastTradedQuantity;
            decimal dayVwap = (tickState.cumulativeVolume > 0) ? tickState.cumulativePriceVolume / tickState.cumulativeVolume : 0;

            if (instrument.ImpliedVolatility > 0) tickState.ivHistory.Add(instrument.ImpliedVolatility);
            if (tickState.ivHistory.Count > this.IvHistoryLength) tickState.ivHistory.RemoveAt(0);
            var (avgIv, ivSignal) = CalculateIvSignal(instrument.ImpliedVolatility, tickState.ivHistory);

            _tickAnalysisState[instrumentForVolume.SecurityId] = tickState;

            var oneMinCandles = _multiTimeframeCandles[instrumentForVolume.SecurityId].GetValueOrDefault(TimeSpan.FromMinutes(1));
            var fiveMinCandles = _multiTimeframeCandles[instrumentForVolume.SecurityId].GetValueOrDefault(TimeSpan.FromMinutes(5));

            if (oneMinCandles != null)
            {
                result.RsiValue1Min = CalculateRsi(oneMinCandles, _multiTimeframeRsiState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(1)], this.RsiPeriod);
                result.RsiSignal1Min = DetectRsiDivergence(oneMinCandles, _multiTimeframeRsiState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(1)], this.RsiDivergenceLookback);
                result.Atr1Min = CalculateAtr(oneMinCandles, _multiTimeframeAtrState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(1)], this.AtrPeriod);
                result.AtrSignal1Min = GetAtrSignal(result.Atr1Min, _multiTimeframeAtrState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(1)], this.AtrSmaPeriod);
                result.ObvValue1Min = CalculateObv(oneMinCandles, _multiTimeframeObvState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(1)]);
                result.ObvSignal1Min = CalculateObvSignal(oneMinCandles, _multiTimeframeObvState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(1)], this.ObvMovingAveragePeriod);
                result.ObvDivergenceSignal1Min = DetectObvDivergence(oneMinCandles, _multiTimeframeObvState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(1)], this.RsiDivergenceLookback);
            }
            if (fiveMinCandles != null)
            {
                result.RsiValue5Min = CalculateRsi(fiveMinCandles, _multiTimeframeRsiState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(5)], this.RsiPeriod);
                result.RsiSignal5Min = DetectRsiDivergence(fiveMinCandles, _multiTimeframeRsiState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(5)], this.RsiDivergenceLookback);
                result.Atr5Min = CalculateAtr(fiveMinCandles, _multiTimeframeAtrState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(5)], this.AtrPeriod);
                result.AtrSignal5Min = GetAtrSignal(result.Atr5Min, _multiTimeframeAtrState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(5)], this.AtrSmaPeriod);
                result.ObvValue5Min = CalculateObv(fiveMinCandles, _multiTimeframeObvState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(5)]);
                result.ObvSignal5Min = CalculateObvSignal(fiveMinCandles, _multiTimeframeObvState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(5)], this.ObvMovingAveragePeriod);
                result.ObvDivergenceSignal5Min = DetectObvDivergence(fiveMinCandles, _multiTimeframeObvState[instrumentForVolume.SecurityId][TimeSpan.FromMinutes(5)], this.RsiDivergenceLookback);
            }

            var priceEmaSignals = new Dictionary<TimeSpan, string>();
            var vwapEmaSignals = new Dictionary<TimeSpan, string>();
            foreach (var timeframe in _timeframes)
            {
                var candles = _multiTimeframeCandles[instrumentForVolume.SecurityId].GetValueOrDefault(timeframe);
                if (candles == null || !candles.Any()) continue;
                priceEmaSignals[timeframe] = CalculateEmaSignal(instrumentForVolume.SecurityId, candles, _multiTimeframePriceEmaState, useVwap: false);
                vwapEmaSignals[timeframe] = CalculateEmaSignal(instrumentForVolume.SecurityId, candles, _multiTimeframeVwapEmaState, useVwap: true);
            }

            var (volumeSignal, currentCandleVolume, avgCandleVolume) = ("Neutral", 0L, 0L);
            if (oneMinCandles != null && oneMinCandles.Any())
            {
                (volumeSignal, currentCandleVolume, avgCandleVolume) = CalculateVolumeSignalForTimeframe(oneMinCandles);
            }

            string oiSignal = "N/A";
            if (oneMinCandles != null && oneMinCandles.Any())
            {
                oiSignal = CalculateOiSignal(oneMinCandles);
            }

            // --- ADDED: Run new IV Skew analysis for indices ---
            if (instrument.InstrumentType == "INDEX")
            {
                RunIvSkewAnalysis(instrument);
            }

            var paSignals = CalculatePriceActionSignals(instrument, dayVwap);
            string customLevelSignal = CalculateCustomLevelSignal(instrument);

            string candleSignal1Min = "N/A";
            if (oneMinCandles != null) candleSignal1Min = RecognizeCandlestickPattern(oneMinCandles, result);
            string candleSignal5Min = "N/A";
            if (fiveMinCandles != null) candleSignal5Min = RecognizeCandlestickPattern(fiveMinCandles, result);

            if (_marketProfiles.TryGetValue(instrumentForVolume.SecurityId, out var profile))
            {
                result.InitialBalanceSignal = GetInitialBalanceSignal(instrumentForVolume.LTP, profile, instrumentForVolume.SecurityId);

                if (profile.IsInitialBalanceSet)
                {
                    result.DevelopingPoc = profile.DevelopingTpoLevels.PointOfControl;
                    result.DevelopingVah = profile.DevelopingTpoLevels.ValueAreaHigh;
                    result.DevelopingVal = profile.DevelopingTpoLevels.ValueAreaLow;
                    result.DevelopingVpoc = profile.DevelopingVolumeProfile.VolumePoc;
                }
                else
                {
                    result.DevelopingPoc = 0;
                    result.DevelopingVah = 0;
                    result.DevelopingVal = 0;
                    result.DevelopingVpoc = 0;
                }

                result.InitialBalanceHigh = profile.InitialBalanceHigh;
                result.InitialBalanceLow = profile.InitialBalanceLow;

                var historicalProfiles = _historicalMarketProfiles.GetValueOrDefault(instrumentForVolume.SecurityId);
                RunMarketProfileAnalysis(instrumentForVolume, profile, result);
                _marketProfileService.UpdateProfile(instrumentForVolume.SecurityId, profile.ToMarketProfileData());
            }

            result.Symbol = instrument.DisplayName;
            result.Vwap = dayVwap;
            result.CurrentIv = instrument.ImpliedVolatility;
            result.AvgIv = avgIv;
            result.IvSignal = ivSignal;
            result.CurrentVolume = currentCandleVolume;
            result.AvgVolume = avgCandleVolume;
            result.VolumeSignal = volumeSignal;
            result.OiSignal = oiSignal;
            result.CustomLevelSignal = customLevelSignal;
            result.CandleSignal1Min = candleSignal1Min;
            result.CandleSignal5Min = candleSignal5Min;
            result.EmaSignal1Min = priceEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(1), "N/A");
            result.EmaSignal5Min = priceEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(5), "N/A");
            result.EmaSignal15Min = priceEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(15), "N/A");
            result.VwapEmaSignal1Min = vwapEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(1), "N/A");
            result.VwapEmaSignal5Min = vwapEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(5), "N/A");
            result.VwapEmaSignal15Min = vwapEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(15), "N/A");
            result.InstrumentGroup = GetInstrumentGroup(instrument);
            result.UnderlyingGroup = instrument.UnderlyingSymbol;
            result.PriceVsVwapSignal = paSignals.priceVsVwap;
            result.PriceVsCloseSignal = paSignals.priceVsClose;
            result.DayRangeSignal = paSignals.dayRange;

            if (oneMinCandles != null && oneMinCandles.Count > 0)
            {
                result.OpenTypeSignal = AnalyzeOpenType(instrument, oneMinCandles);
                var (vwapBandSignal, upperBand, lowerBand) = CalculateVwapBandSignal(instrument.LTP, oneMinCandles);
                result.VwapBandSignal = vwapBandSignal;
                result.VwapUpperBand = upperBand;
                result.VwapLowerBand = lowerBand;
                result.AnchoredVwap = CalculateAnchoredVwap(oneMinCandles);
            }
            var yesterdayProfile = _historicalMarketProfiles.GetValueOrDefault(instrumentForVolume.SecurityId)?.FirstOrDefault(p => p.Date.Date < DateTime.Today);
            result.YesterdayProfileSignal = AnalyzePriceRelativeToYesterdayProfile(instrument.LTP, yesterdayProfile);


            if (instrument.InstrumentType == "INDEX")
            {
                result.InstitutionalIntent = RunTier1InstitutionalIntentAnalysis(instrument);
            }
            else
            {
                result.InstitutionalIntent = "N/A";
            }

            SynthesizeTradeSignal(result);

            if (result.InstrumentGroup == "Indices")
            {
                UpdateComprehensiveIndexSignal(result);
            }

            OnAnalysisUpdated?.Invoke(result);
        }

        // --- ADDED: New method for intelligent IV Skew analysis ---
        private void RunIvSkewAnalysis(DashboardInstrument indexInstrument)
        {
            if (!_ivSkewStates.ContainsKey(indexInstrument.SecurityId)) return;

            var state = _ivSkewStates[indexInstrument.SecurityId];
            var result = _analysisResults[indexInstrument.SecurityId];

            int strikeStep = GetStrikePriceStep(indexInstrument.Symbol);
            decimal atmStrike = Math.Round(indexInstrument.LTP / strikeStep) * strikeStep;

            var atmCall = _instrumentCache.Values.FirstOrDefault(i =>
                i.UnderlyingSymbol == indexInstrument.Symbol && i.InstrumentType == "OPTIDX" &&
                i.DisplayName.Contains("CALL") && i.DisplayName.Contains(atmStrike.ToString("F0")));

            var atmPut = _instrumentCache.Values.FirstOrDefault(i =>
                i.UnderlyingSymbol == indexInstrument.Symbol && i.InstrumentType == "OPTIDX" &&
                i.DisplayName.Contains("PUT") && i.DisplayName.Contains(atmStrike.ToString("F0")));

            if (atmCall == null || atmPut == null || atmCall.ImpliedVolatility == 0 || atmPut.ImpliedVolatility == 0)
            {
                result.IvSkewSignal = "ATM Options Not Found";
                return;
            }

            state.CallIvHistory.Add(atmCall.ImpliedVolatility);
            if (state.CallIvHistory.Count > 10) state.CallIvHistory.RemoveAt(0);

            state.PutIvHistory.Add(atmPut.ImpliedVolatility);
            if (state.PutIvHistory.Count > 10) state.PutIvHistory.RemoveAt(0);

            decimal skew = atmPut.ImpliedVolatility - atmCall.ImpliedVolatility;
            state.SkewHistory.Add(skew);
            if (state.SkewHistory.Count > 10) state.SkewHistory.RemoveAt(0);

            if (state.CallIvHistory.Count < 3)
            {
                result.IvSkewSignal = "Building IV History...";
                return;
            }

            var lastCallIv = state.CallIvHistory.Last();
            var prevCallIv = state.CallIvHistory[^1];
            var lastPutIv = state.PutIvHistory.Last();
            var prevPutIv = state.PutIvHistory[^1];
            var oneMinCandles = GetCandles(indexInstrument.SecurityId, TimeSpan.FromMinutes(1));
            var anchoredVwap = oneMinCandles?.LastOrDefault()?.AnchoredVwap ?? 0;

            var lastFiveMinCandles = GetCandles(indexInstrument.SecurityId, TimeSpan.FromMinutes(5))?.TakeLast(2).ToList();
            bool isNewLow = lastFiveMinCandles?.Count == 2 && lastFiveMinCandles[1].Low < lastFiveMinCandles[0].Low;
            bool isNewHigh = lastFiveMinCandles?.Count == 2 && lastFiveMinCandles[1].High > lastFiveMinCandles[0].High;

            bool isBullishSkew = state.SkewHistory.Last() < state.SkewHistory[^2];
            bool isBearishSkew = state.SkewHistory.Last() > state.SkewHistory[^2];


            if (isNewLow && isBullishSkew) { result.IvSkewSignal = "Bullish Skew Divergence (Full)"; return; }
            if (isBullishSkew) { result.IvSkewSignal = "Bullish Skew Divergence (Partial)"; return; }
            if (isNewHigh && isBearishSkew) { result.IvSkewSignal = "Bearish Skew Divergence (Full)"; return; }
            if (isBearishSkew) { result.IvSkewSignal = "Bearish Skew Divergence (Partial)"; return; }

            if (lastCallIv > prevCallIv * 1.02m && lastCallIv > lastPutIv && indexInstrument.LTP > anchoredVwap)
            {
                result.IvSkewSignal = "Bullish IV Momentum";
                return;
            }

            if (lastPutIv > prevPutIv * 1.02m && lastPutIv > lastCallIv && indexInstrument.LTP < anchoredVwap)
            {
                result.IvSkewSignal = "Bearish IV Momentum";
                return;
            }

            if (lastCallIv < prevCallIv && lastPutIv < prevPutIv)
            {
                result.IvSkewSignal = "Range Contraction";
                return;
            }

            result.IvSkewSignal = "Neutral";
        }


        private IntradayContext DetermineIntradayContext(AnalysisResult result)
        {
            bool isTrendingOnEma = result.EmaSignal15Min.Contains("Cross") && result.EmaSignal5Min == result.EmaSignal15Min;
            bool isBreakoutOnIb = result.InitialBalanceSignal.Contains("Extension");
            bool isVolatileOnAtr = result.AtrSignal5Min == "Vol Expanding" || result.AtrSignal1Min == "Vol Expanding";
            bool isInsideIb = result.InitialBalanceSignal == "Inside IB";

            if (isBreakoutOnIb)
            {
                return isVolatileOnAtr ? IntradayContext.Breakout : IntradayContext.Trending;
            }

            if (isVolatileOnAtr)
            {
                return IntradayContext.Volatile;
            }

            if (isTrendingOnEma && !isInsideIb)
            {
                return IntradayContext.Trending;
            }

            if (isInsideIb && !isTrendingOnEma && !isVolatileOnAtr)
            {
                return IntradayContext.RangeBound;
            }

            return IntradayContext.Indeterminate;
        }

        private void SynthesizeTradeSignal(AnalysisResult result)
        {
            var context = DetermineIntradayContext(result);
            result.IntradayContext = context;

            var (bullDrivers, bearDrivers, conviction) = CalculateConvictionScore(result, context);
            result.BullishDrivers = bullDrivers;
            result.BearishDrivers = bearDrivers;
            result.ConvictionScore = conviction;

            string playbook;
            if (conviction >= 7) playbook = "Strong Bullish Conviction";
            else if (conviction >= 3) playbook = "Moderate Bullish Conviction";
            else if (conviction <= -7) playbook = "Strong Bearish Conviction";
            else if (conviction <= -3) playbook = "Moderate Bearish Conviction";
            else playbook = "Neutral / Observe";


            string oldSignal = result.FinalTradeSignal;
            result.FinalTradeSignal = playbook;
            result.MarketNarrative = GenerateMarketNarrative(result);

            if (result.FinalTradeSignal != oldSignal)
            {
                _signalLoggerService.LogSignal(result);
                Task.Run(() => _notificationService.SendTelegramSignalAsync(result));
            }
        }

        private (List<string> BullishDrivers, List<string> BearishDrivers, int Score) CalculateConvictionScore(AnalysisResult r, IntradayContext context)
        {
            var bullDrivers = new List<SignalDriver>();
            var bearDrivers = new List<SignalDriver>();

            switch (context)
            {
                case IntradayContext.Trending:
                case IntradayContext.Breakout:
                    bullDrivers = _settingsViewModel.Strategy.TrendingBullDrivers.Where(d => d.IsEnabled).ToList();
                    bearDrivers = _settingsViewModel.Strategy.TrendingBearDrivers.Where(d => d.IsEnabled).ToList();
                    break;
                case IntradayContext.RangeBound:
                    bullDrivers = _settingsViewModel.Strategy.RangeBoundBullishDrivers.Where(d => d.IsEnabled).ToList();
                    bearDrivers = _settingsViewModel.Strategy.RangeBoundBearishDrivers.Where(d => d.IsEnabled).ToList();
                    break;
                case IntradayContext.Volatile:
                    bullDrivers = _settingsViewModel.Strategy.VolatileBullishDrivers.Where(d => d.IsEnabled).ToList();
                    bearDrivers = _settingsViewModel.Strategy.VolatileBearishDrivers.Where(d => d.IsEnabled).ToList();
                    break;
            }

            int score = 0;
            var triggeredBullDrivers = new List<string>();
            var triggeredBearDrivers = new List<string>();

            const int confluenceBonus = 2;

            foreach (var driver in bullDrivers)
            {
                if (CheckDriverCondition(r, driver.Name))
                {
                    int currentWeight = driver.Weight;
                    string driverText = $"{driver.Name} (+{currentWeight})";

                    bool isAtSupport = r.MarketProfileSignal.Contains("dVAL") || r.MarketProfileSignal.Contains("Y-VAL") || r.VwapBandSignal == "At Lower Band";
                    if (isAtSupport && (driver.Name.Contains("Div") || driver.Name.Contains("Exhaustion") || driver.Name.Contains("Pattern")))
                    {
                        currentWeight += confluenceBonus;
                        driverText = $"{driver.Name} (+{driver.Weight} +{confluenceBonus} Conf.)";
                    }

                    score += currentWeight;
                    triggeredBullDrivers.Add(driverText);
                }
            }

            foreach (var driver in bearDrivers)
            {
                if (CheckDriverCondition(r, driver.Name))
                {
                    int currentWeight = driver.Weight;
                    string driverText = $"{driver.Name} (-{currentWeight})";

                    bool isAtResistance = r.MarketProfileSignal.Contains("dVAH") || r.MarketProfileSignal.Contains("Y-VAH") || r.VwapBandSignal == "At Upper Band";
                    if (isAtResistance && (driver.Name.Contains("Div") || driver.Name.Contains("Exhaustion") || driver.Name.Contains("Pattern")))
                    {
                        currentWeight += confluenceBonus;
                        driverText = $"{driver.Name} (-{driver.Weight} +{confluenceBonus} Conf.)";
                    }

                    score -= currentWeight;
                    triggeredBearDrivers.Add(driverText);
                }
            }

            return (triggeredBullDrivers, triggeredBearDrivers, score);
        }

        private bool CheckDriverCondition(AnalysisResult r, string driverName)
        {
            bool isBullishPattern = r.CandleSignal5Min.Contains("Bullish");
            bool isBearishPattern = r.CandleSignal5Min.Contains("Bearish");
            bool atSupport = r.DayRangeSignal == "Near Low" || r.VwapBandSignal == "At Lower Band" || r.MarketProfileSignal.Contains("VAL");
            bool atResistance = r.DayRangeSignal == "Near High" || r.VwapBandSignal == "At Upper Band" || r.MarketProfileSignal.Contains("VAH");
            bool volumeConfirmed = r.VolumeSignal == "Volume Burst";
            switch (driverName)
            {
                // --- NEW: Intelligent IV Skew Drivers ---
                case "Bullish IV Momentum": return r.IvSkewSignal == "Bullish IV Momentum";
                case "Bearish IV Momentum": return r.IvSkewSignal == "Bearish IV Momentum";
                case "Range Contraction": return r.IvSkewSignal == "Range Contraction";
                case "Bullish Skew Divergence (Full)": return r.IvSkewSignal == "Bullish Skew Divergence (Full)";
                case "Bullish Skew Divergence (Partial)": return r.IvSkewSignal == "Bullish Skew Divergence (Partial)";
                case "Bearish Skew Divergence (Full)": return r.IvSkewSignal == "Bearish Skew Divergence (Full)";
                case "Bearish Skew Divergence (Partial)": return r.IvSkewSignal == "Bearish Skew Divergence (Partial)";

                // Contextual Candlestick Drivers
                case "Bullish Pattern at Key Support": return isBullishPattern && atSupport;
                case "Bearish Pattern at Key Resistance": return isBearishPattern && atResistance;
                case "Bullish Pattern with Volume Confirmation": return isBullishPattern && volumeConfirmed;
                case "Bearish Pattern with Volume Confirmation": return isBearishPattern && volumeConfirmed;
                case "Bullish Pattern (Unconfirmed)": return isBullishPattern && !atSupport && !volumeConfirmed;
                case "Bearish Pattern (Unconfirmed)": return isBearishPattern && !atResistance && !volumeConfirmed;
                // Market Structure Drivers
                case "True Acceptance Above Y-VAH": return r.MarketProfileSignal == "True Acceptance Above Y-VAH";
                case "Look Below and Fail at Y-VAL": return r.MarketProfileSignal == "Look Below and Fail at Y-VAL";
                case "Initiative Buying Above Y-VAH": return r.MarketProfileSignal == "Initiative Buying Above Y-VAH";
                case "True Acceptance Below Y-VAL": return r.MarketProfileSignal == "True Acceptance Below Y-VAL";
                case "Look Above and Fail at Y-VAH": return r.MarketProfileSignal == "Look Above and Fail at Y-VAH";
                case "Initiative Selling Below Y-VAL": return r.MarketProfileSignal == "Initiative Selling Below Y-VAL";

                // Trending Bull Drivers
                case "Institutional Intent is Bullish": return r.InstitutionalIntent.Contains("Bullish");
                case "Price above VWAP": return r.PriceVsVwapSignal == "Above VWAP";
                case "5m VWAP EMA confirms bullish trend": return r.VwapEmaSignal5Min == "Bullish Cross";
                case "OI confirms new longs": return r.OiSignal == "Long Buildup";
                case "IB breakout is extending": return r.InitialBalanceSignal == "IB Extension Up";
                case "Bullish OBV Div at Profile Support": return r.ObvDivergenceSignal5Min.Contains("Bullish") && (r.MarketProfileSignal.Contains("dVAL") || r.MarketProfileSignal.Contains("Y-VAL"));
                case "Bullish RSI Div at Profile Support": return r.RsiSignal5Min.Contains("Bullish") && (r.MarketProfileSignal.Contains("dVAL") || r.MarketProfileSignal.Contains("Y-VAL"));

                // Trending Bear Drivers
                case "Institutional Intent is Bearish": return r.InstitutionalIntent.Contains("Bearish");
                case "Price below VWAP": return r.PriceVsVwapSignal == "Below VWAP";
                case "5m VWAP EMA confirms bearish trend": return r.VwapEmaSignal5Min == "Bearish Cross";
                case "OI confirms new shorts": return r.OiSignal == "Short Buildup";
                case "IB breakdown is extending": return r.InitialBalanceSignal == "IB Extension Down";
                case "Bearish OBV Div at Profile Resistance": return r.ObvDivergenceSignal5Min.Contains("Bearish") && (r.MarketProfileSignal.Contains("dVAH") || r.MarketProfileSignal.Contains("Y-VAH"));
                case "Bearish RSI Div at Profile Resistance": return r.RsiSignal5Min.Contains("Bearish") && (r.MarketProfileSignal.Contains("dVAH") || r.MarketProfileSignal.Contains("Y-VAH"));

                // Range Bound Bullish Drivers
                case "Bullish OBV Div at range low": return r.ObvDivergenceSignal5Min.Contains("Bullish") && r.VwapBandSignal == "At Lower Band";
                case "Bullish RSI Div at range low": return r.RsiSignal5Min.Contains("Bullish") && r.VwapBandSignal == "At Lower Band";
                case "Low volume suggests exhaustion (Bullish)": return r.VolumeSignal != "Volume Burst" && r.AtrSignal5Min == "Vol Contracting" && r.DayRangeSignal == "Near Low";
                case "Possible range breakout with volume": return r.DayRangeSignal == "Near High" && r.VolumeSignal == "Volume Burst";

                // Range Bound Bearish Drivers
                case "Bearish OBV Div at range high": return r.ObvDivergenceSignal5Min.Contains("Bearish") && r.VwapBandSignal == "At Upper Band";
                case "Bearish RSI Div at range high": return r.RsiSignal5Min.Contains("Bearish") && r.VwapBandSignal == "At Upper Band";
                case "Low volume suggests exhaustion (Bearish)": return r.VolumeSignal != "Volume Burst" && r.AtrSignal5Min == "Vol Contracting" && r.DayRangeSignal == "Near High";
                case "Possible range breakdown with volume": return r.DayRangeSignal == "Near Low" && r.VolumeSignal == "Volume Burst";

                // Volatile Bullish Drivers
                case "Strong bullish confluence with Inst. backing": return r.InstitutionalIntent.Contains("Bullish") && r.EmaSignal5Min == "Bullish Cross" && r.VolumeSignal == "Volume Burst";

                // Volatile Bearish Drivers
                case "Strong bearish confluence with Inst. backing": return r.InstitutionalIntent.Contains("Bearish") && r.EmaSignal5Min == "Bearish Cross" && r.VolumeSignal == "Volume Burst";

                default: return false;
            }
        }


        private string GenerateMarketNarrative(AnalysisResult r)
        {
            var narrative = new List<string>();
            narrative.Add($"Context: {r.IntradayContext}.");
            narrative.Add($"Open: {r.OpenTypeSignal}.");
            narrative.Add($"vs Y-Value: {r.YesterdayProfileSignal}.");
            narrative.Add($"vs VWAP: {r.PriceVsVwapSignal}.");

            if (r.IntradayContext == IntradayContext.Trending)
            {
                narrative.Add(r.EmaSignal15Min == "Bullish Cross" ? "Long-term trend is Up." : "Long-term trend is Down.");
            }
            if (r.IntradayContext == IntradayContext.RangeBound)
            {
                narrative.Add($"Price is between VWAP bands ({r.VwapLowerBand:N2} - {r.VwapUpperBand:N2}).");
            }
            if (r.IntradayContext == IntradayContext.Breakout)
            {
                narrative.Add($"IB Signal: {r.InitialBalanceSignal} with {r.VolumeSignal}.");
            }

            return string.Join(" ", narrative);
        }

        private decimal CalculateDashboardPcr(string underlyingSymbol)
        {
            var relevantOptions = _dashboardViewModel.MonitoredInstruments
                .Where(i => i.UnderlyingSymbol == underlyingSymbol && i.InstrumentType == "OPTIDX")
                .ToList();

            if (!relevantOptions.Any()) return 0;

            long totalCallOi = relevantOptions.Where(o => o.DisplayName.Contains("CALL")).Sum(o => o.OpenInterest);
            long totalPutOi = relevantOptions.Where(o => o.DisplayName.Contains("PUT")).Sum(o => o.OpenInterest);

            if (totalCallOi == 0) return 0;

            return (decimal)totalPutOi / totalCallOi;
        }

        private void UpdateComprehensiveIndexSignal(AnalysisResult result)
        {
            var signal = new IndexSignal();

            int score = result.ConvictionScore;

            if (result.DailyBias.Contains("Bullish"))
            {
                signal.Bias = "Bullish";
            }
            else if (result.DailyBias.Contains("Bearish"))
            {
                signal.Bias = "Bearish";
            }
            else
            {
                signal.Bias = "Neutral / Rotational";
            }

            if (result.EmaSignal5Min == "Bullish Cross" && result.EmaSignal15Min == "Bullish Cross")
            {
                signal.TrendDirection = "Uptrend";
            }
            else if (result.EmaSignal5Min == "Bearish Cross" && result.EmaSignal15Min == "Bearish Cross")
            {
                signal.TrendDirection = "Downtrend";
            }
            else
            {
                signal.TrendDirection = "Sideways / Choppy";
            }

            signal.TrendConviction = result.InstitutionalIntent;

            if (result.RsiSignal5Min.Contains("Bullish Divergence") || result.ObvDivergenceSignal5Min.Contains("Bullish Divergence"))
            {
                signal.Momentum = "Fading (Bullish Reversal Signal)";
            }
            else if (result.RsiValue5Min > 60 && result.RsiValue5Min > result.RsiValue1Min)
            {
                signal.Momentum = "Increasing (Bullish)";
            }
            else if (result.RsiValue5Min < 40 && result.RsiValue5Min < result.RsiValue1Min)
            {
                signal.Momentum = "Increasing (Bearish)";
            }
            else
            {
                signal.Momentum = "Neutral";
            }

            if (result.AtrSignal5Min == "Vol Expanding")
            {
                signal.Volatility = "Expanding";
            }
            else if (result.AtrSignal5Min == "Vol Contracting")
            {
                signal.Volatility = "Contracting";
            }
            else
            {
                signal.Volatility = "Stable";
            }

            if (score >= 7) signal.OverallSignal = "Strong Buy Opportunity";
            else if (score >= 3) signal.OverallSignal = "Cautious Buy";
            else if (score <= -7) signal.OverallSignal = "Strong Sell Opportunity";
            else if (score <= -3) signal.OverallSignal = "Cautious Sell";
            else signal.OverallSignal = "Observe / No Clear Edge";

            signal.SupportingFactors = result.BullishDrivers;
            signal.ContradictingFactors = result.BearishDrivers;

            result.IndexSignal = signal;
        }


        private string CalculateEmaSignal(string securityId, List<Candle> candles, Dictionary<string, Dictionary<TimeSpan, EmaState>> stateDictionary, bool useVwap)
        {
            if (candles.Count < LongEmaLength) return "Building History...";

            var timeframe = candles.Count > 1 ? (candles[1].Timestamp - candles[0].Timestamp) : TimeSpan.FromMinutes(1);
            var state = stateDictionary[securityId][timeframe];

            Func<Candle, decimal> sourceSelector = useVwap ? (c => c.Vwap) : (c => c.Close);

            var prices = candles.Select(sourceSelector).ToList();
            if (prices.Count == 0) return "Building History...";

            if (state.CurrentShortEma == 0 || state.CurrentLongEma == 0)
            {
                state.CurrentShortEma = prices.Skip(prices.Count - ShortEmaLength).Average();
                state.CurrentLongEma = prices.Average();
            }
            else
            {
                decimal shortMultiplier = 2.0m / (ShortEmaLength + 1);
                state.CurrentShortEma = ((prices.Last() - state.CurrentShortEma) * shortMultiplier) + state.CurrentShortEma;

                decimal longMultiplier = 2.0m / (LongEmaLength + 1);
                state.CurrentLongEma = ((prices.Last() - state.CurrentLongEma) * longMultiplier) + state.CurrentLongEma;
            }

            if (state.CurrentShortEma > state.CurrentLongEma) return "Bullish Cross";
            if (state.CurrentShortEma < state.CurrentLongEma) return "Bearish Cross";
            return "Neutral";
        }


        #region Helper Calculation Methods
        // --- ADDED: Helper method to get strike step for an index ---
        private int GetStrikePriceStep(string underlyingSymbol)
        {
            string upperSymbol = underlyingSymbol.ToUpperInvariant();
            if (upperSymbol.Contains("SENSEX") || (upperSymbol.Contains("NIFTY") && upperSymbol.Contains("BANK")))
            {
                return 100;
            }
            return 50;
        }

        private (string signal, decimal upperBand, decimal lowerBand) CalculateVwapBandSignal(decimal ltp, List<Candle> candles)
        {
            if (candles.Count < 2) return ("N/A", 0, 0);

            var vwap = candles.Last().Vwap;
            if (vwap == 0) return ("N/A", 0, 0);

            decimal sumOfSquares = 0;
            foreach (var candle in candles)
            {
                sumOfSquares += (decimal)Math.Pow((double)(candle.Close - vwap), 2);
            }
            decimal stdDev = (decimal)Math.Sqrt((double)(sumOfSquares / candles.Count));

            var upperBand = vwap + (stdDev * VwapUpperBandMultiplier);
            var lowerBand = vwap - (stdDev * VwapLowerBandMultiplier);

            string signal = "Inside Bands";
            if (ltp > upperBand) signal = "Above Upper Band";
            else if (ltp < lowerBand) signal = "Below Lower Band";
            else if (Math.Abs(ltp - upperBand) < (upperBand * 0.001m)) signal = "At Upper Band";
            else if (Math.Abs(ltp - lowerBand) < (lowerBand * 0.001m)) signal = "At Lower Band";

            return (signal, upperBand, lowerBand);
        }

        private decimal CalculateAnchoredVwap(List<Candle> candles)
        {
            if (candles == null || !candles.Any()) return 0;

            decimal cumulativePriceVolume = 0;
            long cumulativeVolume = 0;

            foreach (var candle in candles)
            {
                cumulativePriceVolume += candle.Close * candle.Volume;
                cumulativeVolume += candle.Volume;
            }

            return (cumulativeVolume > 0) ? cumulativePriceVolume / cumulativeVolume : 0;
        }

        private (decimal avgIv, string ivSignal) CalculateIvSignal(decimal currentIv, List<decimal> ivHistory)
        {
            string signal = "Neutral";
            decimal avgIv = 0;
            var validIvHistory = ivHistory.Where(iv => iv > 0).ToList();

            if (validIvHistory.Any() && validIvHistory.Count >= MinIvHistoryForSignal)
            {
                avgIv = validIvHistory.Average();
                if (currentIv > (avgIv + this.IvSpikeThreshold)) signal = "IV Spike Up";
                else if (currentIv < (avgIv - this.IvSpikeThreshold)) signal = "IV Drop Down";
            }
            else if (currentIv > 0)
            {
                signal = "Building History...";
            }
            return (avgIv, signal);
        }

        private (string signal, long currentVolume, long averageVolume) CalculateVolumeSignalForTimeframe(List<Candle> candles)
        {
            if (!candles.Any()) return ("N/A", 0, 0);

            long currentCandleVolume = candles.Last().Volume;
            if (candles.Count < 2) return ("Building History...", currentCandleVolume, 0);

            var historyCandles = candles.Take(candles.Count - 1).ToList();
            if (historyCandles.Count > this.VolumeHistoryLength)
            {
                historyCandles = historyCandles.Skip(historyCandles.Count - this.VolumeHistoryLength).ToList();
            }

            if (!historyCandles.Any()) return ("Building History...", currentCandleVolume, 0);

            double averageVolume = historyCandles.Average(c => (double)c.Volume);
            if (averageVolume > 0 && currentCandleVolume > (averageVolume * this.VolumeBurstMultiplier))
            {
                return ("Volume Burst", currentCandleVolume, (long)averageVolume);
            }
            return ("Neutral", currentCandleVolume, (long)averageVolume);
        }

        private string CalculateOiSignal(List<Candle> candles)
        {
            if (candles.Count < 2) return "Building History...";

            var currentCandle = candles.Last();
            var previousCandle = candles[candles.Count - 2];

            if (previousCandle.OpenInterest == 0 || currentCandle.OpenInterest == 0)
            {
                return "Building History...";
            }

            bool isPriceUp = currentCandle.Close > previousCandle.Close;
            bool isPriceDown = currentCandle.Close < previousCandle.Close;
            bool isOiUp = currentCandle.OpenInterest > previousCandle.OpenInterest;
            bool isOiDown = currentCandle.OpenInterest < previousCandle.OpenInterest;

            if (isPriceUp && isOiUp) return "Long Buildup";
            if (isPriceUp && isOiDown) return "Short Covering";
            if (isPriceDown && isOiUp) return "Short Buildup";
            if (isPriceDown && isOiDown) return "Long Unwinding";

            return "Neutral";
        }

        private decimal CalculateRsi(List<Candle> candles, RsiState state, int period)
        {
            if (candles.Count <= period) return 0m;

            var lastCandle = candles.Last();
            var secondLastCandle = candles[candles.Count - 2];
            var change = lastCandle.Close - secondLastCandle.Close;
            var gain = Math.Max(0, change);
            var loss = Math.Max(0, -change);

            if (state.AvgGain == 0)
            {
                var initialChanges = candles.Skip(1).Select((c, i) => c.Close - candles[i].Close).ToList();
                state.AvgGain = initialChanges.Take(period).Where(ch => ch > 0).DefaultIfEmpty(0).Average();
                state.AvgLoss = initialChanges.Take(period).Where(ch => ch < 0).Select(ch => -ch).DefaultIfEmpty(0).Average();
            }
            else
            {
                state.AvgGain = ((state.AvgGain * (period - 1)) + gain) / period;
                state.AvgLoss = ((state.AvgLoss * (period - 1)) + loss) / period;
            }

            if (state.AvgLoss == 0) return 100m;

            var rs = state.AvgGain / state.AvgLoss;
            var rsi = 100 - (100 / (1 + rs));

            state.RsiValues.Add(rsi);
            if (state.RsiValues.Count > 50) state.RsiValues.RemoveAt(0);

            return Math.Round(rsi, 2);
        }

        private string DetectRsiDivergence(List<Candle> candles, RsiState state, int lookback)
        {
            if (candles.Count < lookback || state.RsiValues.Count < lookback) return "N/A";

            var relevantCandles = candles.TakeLast(lookback).ToList();
            var relevantRsi = state.RsiValues.TakeLast(lookback).ToList();
            int swingWindow = 3;

            var swingHighs = FindSwingPoints(relevantCandles, relevantRsi, isHigh: true, swingWindow);
            if (swingHighs.Count >= 2)
            {
                var high1 = swingHighs[0];
                var high2 = swingHighs[1];
                if (high1.price > high2.price && high1.indicator < high2.indicator)
                {
                    return "Bearish Divergence";
                }
            }

            var swingLows = FindSwingPoints(relevantCandles, relevantRsi, isHigh: false, swingWindow);
            if (swingLows.Count >= 2)
            {
                var low1 = swingLows[0];
                var low2 = swingLows[1];
                if (low1.price < low2.price && low1.indicator > low2.indicator)
                {
                    return "Bullish Divergence";
                }
            }

            return "Neutral";
        }

        private decimal CalculateObv(List<Candle> candles, ObvState state)
        {
            if (candles.Count < 2) return 0m;

            var lastCandle = candles.Last();
            var secondLastCandle = candles[candles.Count - 2];

            if (lastCandle.Close > secondLastCandle.Close)
            {
                state.CurrentObv += lastCandle.Volume;
            }
            else if (lastCandle.Close < secondLastCandle.Close)
            {
                state.CurrentObv -= lastCandle.Volume;
            }

            state.ObvValues.Add(state.CurrentObv);
            if (state.ObvValues.Count > 50) state.ObvValues.RemoveAt(0);

            return state.CurrentObv;
        }

        private string CalculateObvSignal(List<Candle> candles, ObvState state, int period)
        {
            if (state.ObvValues.Count < period) return "Building History...";

            var currentObv = state.CurrentObv;
            var previousObv = state.ObvValues.Count > 1 ? state.ObvValues[^2] : 0;

            var sma = state.ObvValues.TakeLast(period).Average();
            var previousSma = state.ObvValues.SkipLast(1).TakeLast(period).Average();
            state.CurrentMovingAverage = sma;

            bool wasBelow = previousObv < previousSma;
            bool isAbove = currentObv > sma;
            if (isAbove && wasBelow) return "Bullish Cross";

            bool wasAbove = previousObv > previousSma;
            bool isBelow = currentObv < sma;
            if (isBelow && wasAbove) return "Bearish Cross";

            if (isAbove) return "Trending Up";
            if (isBelow) return "Trending Down";

            return "Neutral";
        }

        private string DetectObvDivergence(List<Candle> candles, ObvState state, int lookback)
        {
            if (candles.Count < lookback || state.ObvValues.Count < lookback) return "N/A";

            var relevantCandles = candles.TakeLast(lookback).ToList();
            var relevantObv = state.ObvValues.TakeLast(lookback).ToList();
            int swingWindow = 3;

            var swingHighs = FindSwingPoints(relevantCandles, relevantObv, isHigh: true, swingWindow);
            if (swingHighs.Count >= 2)
            {
                var high1 = swingHighs[0];
                var high2 = swingHighs[1];
                if (high1.price > high2.price && high1.indicator < high2.indicator)
                {
                    return "Bearish Divergence";
                }
            }

            var swingLows = FindSwingPoints(relevantCandles, relevantObv, isHigh: false, swingWindow);
            if (swingLows.Count >= 2)
            {
                var low1 = swingLows[0];
                var low2 = swingLows[1];
                if (low1.price < low2.price && low1.indicator > low2.indicator)
                {
                    return "Bullish Divergence";
                }
            }

            return "Neutral";
        }


        private List<(decimal price, decimal indicator)> FindSwingPoints(List<Candle> candles, List<decimal> indicatorValues, bool isHigh, int window)
        {
            var swingPoints = new List<(decimal price, decimal indicator)>();
            for (int i = window; i < candles.Count - window; i++)
            {
                var currentPrice = isHigh ? candles[i].High : candles[i].Low;
                bool isSwing = true;
                for (int j = 1; j <= window; j++)
                {
                    var prevPrice = isHigh ? candles[i - j].High : candles[i - j].Low;
                    var nextPrice = isHigh ? candles[i + j].High : candles[i + j].Low;
                    if ((isHigh && (currentPrice < prevPrice || currentPrice < nextPrice)) ||
                        (!isHigh && (currentPrice > prevPrice || currentPrice > nextPrice)))
                    {
                        isSwing = false;
                        break;
                    }
                }
                if (isSwing)
                {
                    swingPoints.Add((currentPrice, indicatorValues[i]));
                }
            }
            return swingPoints.TakeLast(2).ToList();
        }

        private decimal CalculateAtr(List<Candle> candles, AtrState state, int period)
        {
            if (candles.Count < period) return 0m;

            var trueRanges = new List<decimal>();
            for (int i = 1; i < candles.Count; i++)
            {
                var high = candles[i].High;
                var low = candles[i].Low;
                var prevClose = candles[i - 1].Close;

                var tr = Math.Max(high - low, Math.Abs(high - prevClose));
                tr = Math.Max(tr, Math.Abs(low - prevClose));
                trueRanges.Add(tr);
            }

            if (!trueRanges.Any()) return 0m;

            if (state.CurrentAtr == 0)
            {
                state.CurrentAtr = trueRanges.Take(period).Average();
            }
            else
            {
                var lastTr = trueRanges.Last();
                state.CurrentAtr = ((state.CurrentAtr * (period - 1)) + lastTr) / period;
            }

            state.AtrValues.Add(state.CurrentAtr);
            if (state.AtrValues.Count > 20) state.AtrValues.RemoveAt(0);

            return Math.Round(state.CurrentAtr, 2);
        }

        private string GetAtrSignal(decimal currentAtr, AtrState state, int smaPeriod)
        {
            if (state.AtrValues.Count < smaPeriod) return "N/A";

            var smaOfAtr = state.AtrValues.TakeLast(smaPeriod).Average();
            var previousAtr = state.AtrValues.Count > 1 ? state.AtrValues[^2] : 0;
            var previousSmaOfAtr = state.AtrValues.Count > smaPeriod ? state.AtrValues.SkipLast(1).TakeLast(smaPeriod).Average() : 0;

            bool wasBelow = previousAtr < previousSmaOfAtr;
            bool isAbove = currentAtr > smaOfAtr;

            if (isAbove && wasBelow)
            {
                return "Vol Expanding";
            }

            bool wasAbove = previousAtr > previousSmaOfAtr;
            bool isBelow = currentAtr < smaOfAtr;

            if (isBelow && wasAbove)
            {
                return "Vol Contracting";
            }

            return isAbove ? "High Vol" : "Low Vol";
        }


        private (string priceVsVwap, string priceVsClose, string dayRange) CalculatePriceActionSignals(DashboardInstrument instrument, decimal vwap)
        {
            string priceVsVwap = "Neutral";
            if (vwap > 0)
            {
                decimal tolerance = vwap * 0.0005m; // 0.05% tolerance
                if (instrument.LTP > vwap + tolerance) priceVsVwap = "Above VWAP";
                else if (instrument.LTP < vwap - tolerance) priceVsVwap = "Below VWAP";
                else priceVsVwap = "At VWAP";
            }

            string priceVsClose = "Neutral";
            if (instrument.Close > 0)
            {
                if (instrument.LTP > instrument.Close) priceVsClose = "Above Close";
                else if (instrument.LTP < instrument.Close) priceVsClose = "Below Close";
            }

            string dayRange = "Neutral";
            decimal range = instrument.High - instrument.Low;
            if (range > 0)
            {
                decimal positionInDayRange = (instrument.LTP - instrument.Low) / range;
                if (positionInDayRange > 0.8m) dayRange = "Near High";
                else if (positionInDayRange < 0.2m) dayRange = "Near Low";
                else dayRange = "Mid-Range";
            }

            return (priceVsVwap, priceVsClose, dayRange);
        }

        private string CalculateCustomLevelSignal(DashboardInstrument instrument)
        {
            if (instrument.SegmentId != 0) return "N/A";

            var levels = _settingsViewModel.GetLevelsForIndex(instrument.Symbol);
            if (levels == null) return "No Levels Set";

            if (!_customLevelStates.ContainsKey(instrument.Symbol))
            {
                _customLevelStates[instrument.Symbol] = new IntradayIvState.CustomLevelState();
            }
            var state = _customLevelStates[instrument.Symbol];

            decimal ltp = instrument.LTP;
            IntradayIvState.PriceZone currentZone;

            if (ltp > levels.NoTradeUpperBand) currentZone = IntradayIvState.PriceZone.Above;
            else if (ltp < levels.NoTradeLowerBand) currentZone = IntradayIvState.PriceZone.Below;
            else currentZone = IntradayIvState.PriceZone.Inside;

            if (currentZone != state.LastZone)
            {
                if (state.LastZone == IntradayIvState.PriceZone.Inside && currentZone == IntradayIvState.PriceZone.Above) state.BreakoutCount++;
                else if (state.LastZone == IntradayIvState.PriceZone.Inside && currentZone == IntradayIvState.PriceZone.Below) state.BreakdownCount++;
                state.LastZone = currentZone;
            }

            switch (currentZone)
            {
                case IntradayIvState.PriceZone.Inside: return "No trade zone";
                case IntradayIvState.PriceZone.Above: return $"{GetOrdinal(state.BreakoutCount)} Breakout";
                case IntradayIvState.PriceZone.Below: return $"{GetOrdinal(state.BreakdownCount)} Breakdown";
                default: return "N/A";
            }
        }

        private string RecognizeCandlestickPattern(List<Candle> candles, AnalysisResult analysisResult)
        {
            if (candles.Count < 3) return "N/A";

            var c1 = candles.Last();
            var c2 = candles[candles.Count - 2];
            var c3 = candles[candles.Count - 3];

            string volInfo = GetVolumeConfirmation(c1, c2);
            string pattern = "N/A";

            decimal body1 = Math.Abs(c1.Open - c1.Close);
            decimal range1 = c1.High - c1.Low;
            decimal upperShadow1 = c1.High - Math.Max(c1.Open, c1.Close);
            decimal lowerShadow1 = Math.Min(c1.Open, c1.Close) - c1.Low;

            if (range1 > 0)
            {
                if (body1 / range1 < 0.15m) pattern = "Neutral Doji";
                if (lowerShadow1 > body1 * 1.8m && upperShadow1 < body1 * 0.8m) pattern = c1.Close > c1.Open ? "Bullish Hammer" : "Bearish Hanging Man";
                if (upperShadow1 > body1 * 1.8m && lowerShadow1 < body1 * 0.8m) pattern = c1.Close > c1.Open ? "Bullish Inv Hammer" : "Bearish Shooting Star";
                if (body1 / range1 > 0.9m) pattern = c1.Close > c1.Open ? "Bullish Marubozu" : "Bearish Marubozu";
            }

            if (c1.Close > c2.Open && c1.Open < c2.Close && c1.Close > c1.Open && c2.Close < c2.Open) pattern = "Bullish Engulfing";
            if (c1.Open > c2.Close && c1.Close < c2.Open && c1.Close < c1.Open && c2.Close > c2.Open) pattern = "Bearish Engulfing";

            bool isMorningStar = c3.Close < c3.Open && Math.Max(c2.Open, c2.Close) < c3.Close && c1.Close > c1.Open && c1.Close > (c3.Open + c3.Close) / 2;
            if (isMorningStar) pattern = "Bullish Morning Star";

            bool isEveningStar = c3.Close > c3.Open && Math.Min(c2.Open, c2.Close) > c3.Close && c1.Close < c1.Open && c1.Close < (c3.Open + c3.Close) / 2;
            if (isEveningStar) pattern = "Bearish Evening Star";

            if (pattern == "N/A") return "N/A";

            string context = "";
            if (pattern.Contains("Bullish"))
            {
                if (analysisResult.DayRangeSignal == "Near Low") context = " at Day's Low";
                else if (analysisResult.VwapBandSignal == "At Lower Band") context = " at VWAP Band";
                else if (analysisResult.MarketProfileSignal.Contains("VAL")) context = " at Value Area Low";
            }
            else if (pattern.Contains("Bearish"))
            {
                if (analysisResult.DayRangeSignal == "Near High") context = " at Day's High";
                else if (analysisResult.VwapBandSignal == "At Upper Band") context = " at VWAP Band";
                else if (analysisResult.MarketProfileSignal.Contains("VAH")) context = " at Value Area High";
            }

            return $"{pattern}{context}{volInfo}";
        }


        private string GetVolumeConfirmation(Candle current, Candle previous)
        {
            if (previous.Volume > 0)
            {
                decimal volChange = ((decimal)current.Volume - previous.Volume) / previous.Volume;
                if (volChange > 0.2m)
                {
                    return $" (+{volChange:P0} Vol)";
                }
            }
            return "";
        }

        private string GetOrdinal(int num)
        {
            if (num <= 0) return num.ToString();
            switch (num % 100)
            {
                case 11: case 12: case 13: return num + "th";
            }
            switch (num % 10)
            {
                case 1: return num + "st";
                case 2: return num + "nd";
                case 3: return num + "rd";
                default: return num + "th";
            }
        }

        private string GetInstrumentGroup(DashboardInstrument instrument)
        {
            if (instrument.SegmentId == 0) return "Indices";
            if (instrument.IsFuture) return "Futures";
            if (instrument.DisplayName.ToUpper().Contains("CALL") || instrument.DisplayName.ToUpper().Contains("PUT")) return "Options";
            return "Stocks";
        }

        #endregion

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion
    }
}
