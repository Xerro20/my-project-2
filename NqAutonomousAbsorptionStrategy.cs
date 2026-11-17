using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Indicators.Technical;
using ATAS.Strategies;
using ATAS.Strategies.Chart;
using OF.Core.Data;
using Utils.Common.Logging;

namespace OF.Core.Data
{
    /// <summary>
    /// Structures de données Order Flow & Market-By-Order (MBO) internes
    /// garantissant une compilation autonome dans ATAS X sans dépendance externe manquante.
    /// </summary>
    public enum LevelType
    {
        None,
        CurrentVPOC,
        CurrentVAH,
        CurrentVAL,
        PreviousVPOC,
        PreviousVAH,
        PreviousVAL
    }

    public enum LevelPolarity
    {
        None,
        Support,      // ex: VAL -> recherche d'absorption vendeuse pour Long
        Resistance,   // ex: VAH -> recherche d'absorption acheteuse pour Short
        Bidirectional // ex: VPOC -> agit comme Support ou Résistance selon l'absorption
    }

    public readonly struct VolumeProfileSnapshot
    {
        public decimal CurrentVPOC { get; init; }
        public decimal CurrentVAH { get; init; }
        public decimal CurrentVAL { get; init; }
        public decimal PreviousVPOC { get; init; }
        public decimal PreviousVAH { get; init; }
        public decimal PreviousVAL { get; init; }
        public bool IsValid => CurrentVPOC > 0m && CurrentVAH > 0m && CurrentVAL > 0m;
    }

    public readonly struct ActiveZoneContext
    {
        public LevelType Level { get; init; }
        public LevelPolarity Polarity { get; init; }
        public decimal LevelPrice { get; init; }
        public decimal DistanceInTicks { get; init; }
    }
}

namespace ATAS.Strategies
{
    /// <summary>
    /// Classe de base abstraite Strategy héritant de ChartStrategy (ATAS X).
    /// Permet de respecter strictement l'héritage `: Strategy` et expose les méthodes
    /// virtuelles OnOrderRejected et OnOrderCanceled branchées sur le moteur d'événements ATAS.
    /// </summary>
    public abstract class Strategy : ChartStrategy
    {
        /// <summary>
        /// Déclenché lorsqu'un ordre est rejeté par le broker, la Prop Firm ou Rithmic/CQG.
        /// </summary>
        protected virtual void OnOrderRejected(Order order, string message)
        {
        }

        /// <summary>
        /// Déclenché lorsqu'un ordre actif est annulé.
        /// </summary>
        protected virtual void OnOrderCanceled(Order order)
        {
        }

        protected override void OnOrderRegisterFailed(Order order, string message)
        {
            base.OnOrderRegisterFailed(order, message);
            OnOrderRejected(order, message);
        }

        protected override void OnOrderChanged(Order order)
        {
            base.OnOrderChanged(order);

            if (order == null)
                return;

            if (order.State == OrderStates.Failed)
            {
                OnOrderRejected(order, "Order state transitioned to Failed.");
            }
            else if (order.State == OrderStates.Done && order.Status() == OrderStatus.Canceled)
            {
                OnOrderCanceled(order);
            }
        }
    }

    /// <summary>
    /// Indicateur interne de Volume Profile (Session en cours N et Session précédente N-1).
    /// Calcule en temps réel le VPOC, le VAH et le VAL à partir des données Footprint (GetAllPriceLevels)
    /// selon l'algorithme institutionnel CME Value Area (70% du volume autour du POC).
    /// </summary>
    [Browsable(false)]
    public class InternalSessionVolumeProfile : Indicator
    {
        private readonly Dictionary<decimal, decimal> _currentSessionPrices = new Dictionary<decimal, decimal>();
        private readonly Dictionary<decimal, decimal> _lastBarContribution = new Dictionary<decimal, decimal>();
        private int _lastUpdatedBar = -1;
        private int _valueAreaPercent = 70;

        public decimal CurrentVPOC { get; private set; }
        public decimal CurrentVAH { get; private set; }
        public decimal CurrentVAL { get; private set; }

        public decimal PreviousVPOC { get; private set; }
        public decimal PreviousVAH { get; private set; }
        public decimal PreviousVAL { get; private set; }

        public int ValueAreaPercent
        {
            get => _valueAreaPercent;
            set => _valueAreaPercent = Math.Clamp(value, 10, 95);
        }

        public InternalSessionVolumeProfile() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = false;
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar < 0)
                return;

            // Réinitialisation complète en début d'historique
            if (bar == 0)
            {
                _currentSessionPrices.Clear();
                _lastBarContribution.Clear();
                _lastUpdatedBar = -1;
                CurrentVPOC = CurrentVAH = CurrentVAL = 0m;
                PreviousVPOC = PreviousVAH = PreviousVAL = 0m;
            }

            // Détection d'une nouvelle session de trading via l'API native ATAS
            if (bar > 0 && IsNewSession(bar) && _lastUpdatedBar != bar)
            {
                if (CurrentVPOC > 0m)
                {
                    PreviousVPOC = CurrentVPOC;
                    PreviousVAH = CurrentVAH;
                    PreviousVAL = CurrentVAL;
                }

                _currentSessionPrices.Clear();
                _lastBarContribution.Clear();
            }

            // Si on recalcule la même bougie en temps réel (tick-by-tick), on retire d'abord
            // son ancienne contribution pour éviter tout double comptage de volume.
            if (_lastUpdatedBar == bar && _lastBarContribution.Count > 0)
            {
                foreach (var kvp in _lastBarContribution)
                {
                    if (_currentSessionPrices.TryGetValue(kvp.Key, out decimal existingVol))
                    {
                        decimal updated = existingVol - kvp.Value;
                        if (updated <= 0m)
                            _currentSessionPrices.Remove(kvp.Key);
                        else
                            _currentSessionPrices[kvp.Key] = updated;
                    }
                }
                _lastBarContribution.Clear();
            }
            else if (bar != _lastUpdatedBar)
            {
                _lastBarContribution.Clear();
                _lastUpdatedBar = bar;
            }

            var candle = GetCandle(bar);
            if (candle == null)
                return;

            // Extraction des volumes par niveau de prix depuis le Footprint de la bougie
            var priceLevels = candle.GetAllPriceLevels();
            if (priceLevels != null)
            {
                foreach (var level in priceLevels)
                {
                    if (level == null || level.Volume <= 0m)
                        continue;

                    decimal price = level.Price;
                    decimal vol = level.Volume;

                    if (_currentSessionPrices.TryGetValue(price, out decimal curVol))
                        _currentSessionPrices[price] = curVol + vol;
                    else
                        _currentSessionPrices[price] = vol;

                    if (_lastBarContribution.TryGetValue(price, out decimal barVol))
                        _lastBarContribution[price] = barVol + vol;
                    else
                        _lastBarContribution[price] = vol;
                }
            }

            RecalculateProfileNodes();
        }

        /// <summary>
        /// Calcule dynamiquement le VPOC, VAH et VAL de la session en cours.
        /// Algorithme standard Market/Volume Profile : expansion bilatérale autour du VPOC
        /// jusqu'à englober ValueAreaPercent (ex: 70%) du volume total de la session.
        /// </summary>
        private void RecalculateProfileNodes()
        {
            if (_currentSessionPrices.Count == 0)
                return;

            var sortedLevels = _currentSessionPrices.OrderBy(k => k.Key).ToList();
            decimal totalVolume = 0m;
            decimal maxVolume = -1m;
            int pocIndex = 0;

            for (int i = 0; i < sortedLevels.Count; i++)
            {
                decimal vol = sortedLevels[i].Value;
                totalVolume += vol;
                if (vol > maxVolume)
                {
                    maxVolume = vol;
                    pocIndex = i;
                }
            }

            if (totalVolume <= 0m)
                return;

            CurrentVPOC = sortedLevels[pocIndex].Key;
            decimal targetVolume = totalVolume * (_valueAreaPercent / 100m);
            decimal accumulatedVolume = maxVolume;

            int upperIdx = pocIndex;
            int lowerIdx = pocIndex;

            while (accumulatedVolume < targetVolume && (upperIdx < sortedLevels.Count - 1 || lowerIdx > 0))
            {
                decimal upVolume = 0m;
                int nextUpIdx = upperIdx;
                if (upperIdx < sortedLevels.Count - 1)
                {
                    nextUpIdx = Math.Min(upperIdx + 2, sortedLevels.Count - 1);
                    for (int i = upperIdx + 1; i <= nextUpIdx; i++)
                        upVolume += sortedLevels[i].Value;
                }

                decimal downVolume = 0m;
                int nextDownIdx = lowerIdx;
                if (lowerIdx > 0)
                {
                    nextDownIdx = Math.Max(lowerIdx - 2, 0);
                    for (int i = lowerIdx - 1; i >= nextDownIdx; i--)
                        downVolume += sortedLevels[i].Value;
                }

                if (upVolume >= downVolume && upperIdx < sortedLevels.Count - 1)
                {
                    accumulatedVolume += upVolume;
                    upperIdx = nextUpIdx;
                }
                else if (lowerIdx > 0)
                {
                    accumulatedVolume += downVolume;
                    lowerIdx = nextDownIdx;
                }
                else if (upperIdx < sortedLevels.Count - 1)
                {
                    accumulatedVolume += upVolume;
                    upperIdx = nextUpIdx;
                }
                else
                {
                    break;
                }
            }

            CurrentVAH = sortedLevels[upperIdx].Key;
            CurrentVAL = sortedLevels[lowerIdx].Key;
        }

        public VolumeProfileSnapshot GetSnapshot()
        {
            return new VolumeProfileSnapshot
            {
                CurrentVPOC = CurrentVPOC,
                CurrentVAH = CurrentVAH,
                CurrentVAL = CurrentVAL,
                PreviousVPOC = PreviousVPOC,
                PreviousVAH = PreviousVAH,
                PreviousVAL = PreviousVAL
            };
        }
    }

    /// <summary>
    /// Stratégie Quantitative 100% Autonome d'Order Flow pour Nasdaq Futures (NQ / MNQ).
    /// Basée exclusivement sur la cartographie dynamique du Volume Profile (VPOC, VAH, VAL)
    /// et la détection d'Absorption / Divergence de Delta au Footprint avec gestion OCO et Prop Firm.
    /// </summary>
    [DisplayName("NQ Autonomous Absorption & Delta Divergence (Prop Firm Ready)")]
    [Category("Order Flow Quantitative Strategies")]
    public class NqAutonomousAbsorptionStrategy : Strategy
    {
        #region Machine à États (State Machine)

        private enum BotExecutionState
        {
            Idle = 0,               // En observation, hors zone d'intervention
            Armed = 1,              // Prix dans la zone de tolérance d'un niveau clé (VPOC / VAH / VAL)
            EnteringMarket = 2,     // Ordre Market d'entrée envoyé, en attente de remplissage
            PlacingOcoBrackets = 3, // Position ouverte, envoi conjoint du Stop Loss et Take Profit OCO
            InPositionOcoActive = 4,// Position protégée par le groupe OCO actif
            EmergencyFlatten = 5,   // Clôture d'urgence en cours (Fin de session / Daily Loss Limit)
            HaltedForSession = 6    // Coupe-circuit Prop Firm activé jusqu'à la prochaine session
        }

        private BotExecutionState _state = BotExecutionState.Idle;
        private ActiveZoneContext _armedZone;

        #endregion

        #region Champs Internes & Références d'Ordres

        private InternalSessionVolumeProfile _volumeProfile;
        private readonly object _stateLock = new object();

        // Suivi des ordres pour la machine à états et le groupe OCO
        private Order _entryOrder;
        private Order _stopLossOrder;
        private Order _takeProfitOrder;
        private string _activeOcoGroupId;

        // Contexte de la bougie d'absorption ayant déclenché l'entrée
        private decimal _pendingStopLossPrice;
        private decimal _pendingTakeProfitPrice;
        private OrderDirections _pendingEntryDirection;
        private int _lastSignalBarIndex = -1;
        private int _lastSeenBarIndex = -1;

        // Suivi Prop Firm (PnL journalier & Compteur de trades)
        private int _tradesToday;
        private decimal _sessionRealizedPnLUsd;
        private decimal _entryAvgPrice;
        private decimal _trackedPositionVolume;

        // Séries visuelles optionnelles pour afficher les niveaux dynamiques sur le graphique ATAS
        private readonly ValueDataSeries _vpocSeries;
        private readonly ValueDataSeries _vahSeries;
        private readonly ValueDataSeries _valSeries;

        #endregion

        #region Paramètres Exposés UI (ATAS X)

        // --- 1. PARAMÈTRES GÉNÉRAUX & EXÉCUTION ---
        [Display(Name = "Quantité (Contrats NQ/MNQ)", GroupName = "1. Exécution & OCO", Order = 10)]
        [Range(1, 100)]
        public decimal OrderVolume { get; set; } = 1m;

        [Display(Name = "Take Profit Fixe (Ticks)", GroupName = "1. Exécution & OCO", Order = 20)]
        [Range(1, 2000)]
        public int TakeProfitTicks { get; set; } = 40;

        [Display(Name = "Marge Stop Loss au-delà Mèche (Ticks)", GroupName = "1. Exécution & OCO", Order = 30)]
        [Range(1, 50)]
        public int StopLossWickBufferTicks { get; set; } = 2;

        // --- 2. CARTOGRAPHIE DU VOLUME PROFILE ---
        [Display(Name = "Tolérance de Zone (Ticks)", GroupName = "2. Cartographie Volume Profile", Order = 10)]
        [Range(1, 50)]
        public int ZoneToleranceTicks { get; set; } = 5;

        [Display(Name = "Value Area (%)", GroupName = "2. Cartographie Volume Profile", Order = 20)]
        [Range(50, 90)]
        public int ValueAreaPercent { get; set; } = 70;

        [Display(Name = "Inclure Niveaux Session Précédente (N-1)", GroupName = "2. Cartographie Volume Profile", Order = 30)]
        public bool UsePreviousSessionLevels { get; set; } = true;

        // --- 3. DÉCLENCHEUR ORDER FLOW & ABSORPTION ---
        [Display(Name = "Delta Minimum d'Absorption (|Delta|)", GroupName = "3. Déclencheur Footprint & Absorption", Order = 10)]
        [Range(1, 50000)]
        public decimal MinAbsorptionDelta { get; set; } = 200m;

        [Display(Name = "Exiger Imbalance Footprint dans la Mèche", GroupName = "3. Déclencheur Footprint & Absorption", Order = 20)]
        public bool RequireWickImbalance { get; set; } = false;

        [Display(Name = "Ratio d'Imbalance Diagonal (ex: 3.0 = 300%)", GroupName = "3. Déclencheur Footprint & Absorption", Order = 30)]
        [Range(1.5, 20.0)]
        public decimal ImbalanceRatio { get; set; } = 3.0m;

        // --- 4. GESTION DU RISQUE PROP FIRM (INACTIFS PAR DÉFAUT) ---
        [Display(Name = "Activer Daily Loss Limit", GroupName = "4. Sécurités Prop Firm", Order = 10)]
        public bool EnableDailyLossLimit { get; set; } = false;

        [Display(Name = "Daily Loss Limit (USD)", GroupName = "4. Sécurités Prop Firm", Order = 20)]
        [Range(50, 100000)]
        public decimal DailyLossLimit { get; set; } = 1000m;

        [Display(Name = "Activer Max Trades Par Jour", GroupName = "4. Sécurités Prop Firm", Order = 30)]
        public bool EnableMaxTradesPerDay { get; set; } = false;

        [Display(Name = "Max Trades Par Jour", GroupName = "4. Sécurités Prop Firm", Order = 40)]
        [Range(1, 100)]
        public int MaxTradesPerDay { get; set; } = 5;

        [Display(Name = "Activer Filtre Horaire de Session", GroupName = "4. Sécurités Prop Firm", Order = 50)]
        public bool EnableSessionFilter { get; set; } = false;

        [Display(Name = "Heure Début Session (HH:mm:ss)", GroupName = "4. Sécurités Prop Firm", Order = 60)]
        public TimeSpan SessionStart { get; set; } = new TimeSpan(9, 30, 0);

        [Display(Name = "Heure Fin Session / Close All (HH:mm:ss)", GroupName = "4. Sécurités Prop Firm", Order = 70)]
        public TimeSpan SessionEnd { get; set; } = new TimeSpan(16, 0, 0);

        #endregion

        #region Constructeur & Initialisation (OnInitialize)

        public NqAutonomousAbsorptionStrategy()
        {
            EnableCustomDrawing = true;

            _vpocSeries = (ValueDataSeries)DataSeries[0];
            _vpocSeries.Name = "Session VPOC";
            _vpocSeries.Color = Colors.Gold;
            _vpocSeries.Width = 2;
            _vpocSeries.VisualType = VisualMode.Hash;

            _vahSeries = new ValueDataSeries("Session VAH")
            {
                Color = Colors.Crimson,
                Width = 1,
                VisualType = VisualMode.Hash
            };
            DataSeries.Add(_vahSeries);

            _valSeries = new ValueDataSeries("Session VAL")
            {
                Color = Colors.DodgerBlue,
                Width = 1,
                VisualType = VisualMode.Hash
            };
            DataSeries.Add(_valSeries);
        }

        /// <summary>
        /// Initialisation de l'indicateur interne de Volume Profile et réinitialisation de la Machine à États.
        /// </summary>
        protected override void OnInitialize()
        {
            base.OnInitialize();

            _volumeProfile = new InternalSessionVolumeProfile
            {
                ValueAreaPercent = ValueAreaPercent
            };

            // Enregistrement de l'indicateur interne dans le pipeline de calcul ATAS
            Add(_volumeProfile);

            ResetStateToIdle("Initialisation de la stratégie");
            _lastSignalBarIndex = -1;
            _lastSeenBarIndex = -1;
            _tradesToday = 0;
            _sessionRealizedPnLUsd = 0m;
            _entryAvgPrice = 0m;
            _trackedPositionVolume = 0m;

            this.LogInfo("[NQ Absorption Bot] Initialisé avec succès. En attente de données Order Flow temps réel.");
        }

        #endregion

        #region Boucle Principale Temps Réel (OnCalculate)

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar < 2 || _volumeProfile == null)
                return;

            // Synchronisation du paramètre ValueAreaPercent avec l'indicateur interne
            _volumeProfile.ValueAreaPercent = ValueAreaPercent;

            // Extraction temps réel des niveaux du Volume Profile (Session N et Session N-1)
            VolumeProfileSnapshot profile = _volumeProfile.GetSnapshot();

            // Traçage visuel sur le graphique des niveaux de la session en cours
            if (profile.IsValid)
            {
                _vpocSeries[bar] = profile.CurrentVPOC;
                _vahSeries[bar] = profile.CurrentVAH;
                _valSeries[bar] = profile.CurrentVAL;
            }

            // Réinitialisation des compteurs journaliers Prop Firm à chaque nouvelle session
            if (IsNewSession(bar) && _lastSeenBarIndex != bar)
            {
                _tradesToday = 0;
                _sessionRealizedPnLUsd = 0m;
                if (_state == BotExecutionState.HaltedForSession)
                {
                    ResetStateToIdle("Nouvelle session détectée : réactivation du bot.");
                }
            }

            bool isNewBar = bar > _lastSeenBarIndex;
            _lastSeenBarIndex = bar;

            // Sécurité cruciale ATAS : n'exécuter la logique d'envoi d'ordres que sur le flux temps réel
            if (!CanProcess(bar))
                return;

            var currentCandle = GetCandle(bar);
            if (currentCandle == null)
                return;

            // 1. VÉRIFICATION DES COUPE-CIRCUITS PROP FIRM (DailyLossLimit, SessionEnd, MaxTradesPerDay)
            if (CheckAndEnforcePropFirmRules(currentCandle))
                return;

            // 2. SYNCHRONISATION STRICTE DE LA MACHINE À ÉTATS AVEC LA POSITION RÉELLE
            SynchronizeStateWithPosition();

            // Si le bot est déjà en cours d'exécution d'ordres ou en position, on ne cherche pas de nouveau signal
            if (_state != BotExecutionState.Idle && _state != BotExecutionState.Armed)
                return;

            if (!profile.IsValid)
                return;

            decimal tickSize = GetTickSize();

            // 3. CARTOGRAPHIE & LOGIQUE DE PROXIMITÉ (Passage en état ARMED)
            // On évalue la proximité du prix actuel (et de la bougie venant de clôturer) vis-à-vis des niveaux clés
            ActiveZoneContext zoneNow = FindClosestKeyLevel(currentCandle.Close, profile, tickSize);
            if (zoneNow.Level != LevelType.None)
            {
                if (_state == BotExecutionState.Idle)
                {
                    _state = BotExecutionState.Armed;
                    _armedZone = zoneNow;
                    this.LogInfo($"[STATE -> ARMED] Niveau détecté : {zoneNow.Level} @ {zoneNow.LevelPrice} (Distance: {zoneNow.DistanceInTicks:F1} ticks).");
                }
                else
                {
                    // Mise à jour continue du niveau le plus proche tant qu'on est ARMED
                    _armedZone = zoneNow;
                }
            }
            else if (!isNewBar && _state == BotExecutionState.Armed)
            {
                // Si le prix s'éloigne nettement au-delà de 2x la tolérance sans clôturer sur signal, retour à Idle
                decimal distFromArmed = Math.Abs(currentCandle.Close - _armedZone.LevelPrice) / tickSize;
                if (distFromArmed > ZoneToleranceTicks * 2)
                {
                    ResetStateToIdle("Prix sorti de la zone de tolérance.");
                }
            }

            // 4. DÉCLENCHEUR D'ABSORPTION FOOTPRINT À LA CLÔTURE DE LA BOUGIE
            // Dès qu'une nouvelle bougie démarre (isNewBar), la bougie précédente (bar - 1) est 100% clôturée et confirmée.
            int closedBarIndex = bar - 1;
            if (isNewBar && closedBarIndex > _lastSignalBarIndex)
            {
                EvaluateClosedBarForAbsorption(closedBarIndex, profile, tickSize);
            }
        }

        #endregion

        #region Cartographie des Niveaux & Détection de l'Absorption sur Footprint

        /// <summary>
        /// Interroge les niveaux dynamiques (VPOC, VAH, VAL de la session en cours et précédente)
        /// et vérifie si le prix se situe dans la zone de tolérance (+/- ZoneToleranceTicks).
        /// </summary>
        private ActiveZoneContext FindClosestKeyLevel(decimal price, VolumeProfileSnapshot profile, decimal tickSize)
        {
            var candidates = new List<(LevelType Type, LevelPolarity Polarity, decimal Price)>
            {
                (LevelType.CurrentVAH, LevelPolarity.Resistance, profile.CurrentVAH),
                (LevelType.CurrentVAL, LevelPolarity.Support, profile.CurrentVAL),
                (LevelType.CurrentVPOC, LevelPolarity.Bidirectional, profile.CurrentVPOC)
            };

            if (UsePreviousSessionLevels)
            {
                if (profile.PreviousVAH > 0m)
                    candidates.Add((LevelType.PreviousVAH, LevelPolarity.Resistance, profile.PreviousVAH));
                if (profile.PreviousVAL > 0m)
                    candidates.Add((LevelType.PreviousVAL, LevelPolarity.Support, profile.PreviousVAL));
                if (profile.PreviousVPOC > 0m)
                    candidates.Add((LevelType.PreviousVPOC, LevelPolarity.Bidirectional, profile.PreviousVPOC));
            }

            ActiveZoneContext bestMatch = default;
            decimal minDistanceTicks = decimal.MaxValue;

            foreach (var candidate in candidates)
            {
                if (candidate.Price <= 0m)
                    continue;

                decimal distanceTicks = Math.Abs(price - candidate.Price) / tickSize;
                if (distanceTicks <= ZoneToleranceTicks && distanceTicks < minDistanceTicks)
                {
                    minDistanceTicks = distanceTicks;
                    bestMatch = new ActiveZoneContext
                    {
                        Level = candidate.Type,
                        Polarity = candidate.Polarity,
                        LevelPrice = candidate.Price,
                        DistanceInTicks = distanceTicks
                    };
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Analyse la bougie tout juste clôturée pour détecter une Absorption sur Footprint :
        /// - Short sur Résistance (ex: VAH / VPOC) : Delta >= +MinAbsorptionDelta MAIS Close < POC de la bougie.
        /// - Long sur Support (ex: VAL / VPOC) : Delta <= -MinAbsorptionDelta MAIS Close > POC de la bougie.
        /// </summary>
        private void EvaluateClosedBarForAbsorption(int closedBar, VolumeProfileSnapshot profile, decimal tickSize)
        {
            var candle = GetCandle(closedBar);
            if (candle == null || candle.MaxVolumePriceInfo == null)
                return;

            // Vérifier si la bougie clôturée a interagi avec la zone armée ou un niveau clé dans la tolérance
            ActiveZoneContext zoneAtClose = FindClosestKeyLevel(candle.Close, profile, tickSize);
            if (zoneAtClose.Level == LevelType.None)
            {
                // On vérifie également si le High ou le Low de la bougie a testé le niveau armé
                ActiveZoneContext zoneAtHigh = FindClosestKeyLevel(candle.High, profile, tickSize);
                ActiveZoneContext zoneAtLow = FindClosestKeyLevel(candle.Low, profile, tickSize);

                if (zoneAtHigh.Level != LevelType.None && zoneAtHigh.Polarity != LevelPolarity.Support)
                    zoneAtClose = zoneAtHigh;
                else if (zoneAtLow.Level != LevelType.None && zoneAtLow.Polarity != LevelPolarity.Resistance)
                    zoneAtClose = zoneAtLow;
                else if (_state == BotExecutionState.Armed && _armedZone.Level != LevelType.None)
                {
                    // Vérifier si la mèche de la bougie a touché le niveau armé à +/- ZoneToleranceTicks
                    decimal highDist = Math.Abs(candle.High - _armedZone.LevelPrice) / tickSize;
                    decimal lowDist = Math.Abs(candle.Low - _armedZone.LevelPrice) / tickSize;
                    if (highDist <= ZoneToleranceTicks || lowDist <= ZoneToleranceTicks)
                        zoneAtClose = _armedZone;
                }
            }

            if (zoneAtClose.Level == LevelType.None)
                return;

            // Passage / maintien en état ARMED sur ce niveau
            _state = BotExecutionState.Armed;
            _armedZone = zoneAtClose;

            // Extraction des métriques Order Flow de la bougie (Delta global & POC de la bougie)
            decimal candleDelta = candle.Delta;
            decimal candlePocPrice = candle.MaxVolumePriceInfo.Price;

            // Condition 1 : VENTE (SHORT) SUR RÉSISTANCE (ex: VAH ou VPOC)
            // Agressivité acheteuse massive (Delta >= +MinAbsorptionDelta) absorbée par des ordres limites vendeurs (Close < POC bougie)
            bool canShortLevel = zoneAtClose.Polarity == LevelPolarity.Resistance || zoneAtClose.Polarity == LevelPolarity.Bidirectional;
            bool isBullishDeltaAbsorbed = candleDelta >= Math.Abs(MinAbsorptionDelta) && candle.Close < candlePocPrice;

            if (canShortLevel && isBullishDeltaAbsorbed)
            {
                if (!RequireWickImbalance || HasUpperWickAskImbalance(candle, candlePocPrice))
                {
                    _lastSignalBarIndex = closedBar;

                    // Calcul du Hard Stop ultra-serré : exactement à 2 ticks (StopLossWickBufferTicks) au-dessus du High de la bougie d'absorption
                    decimal hardStopPrice = AlignToTick(candle.High + (StopLossWickBufferTicks * tickSize), tickSize);
                    decimal takeProfitPrice = AlignToTick(candle.Close - (TakeProfitTicks * tickSize), tickSize);

                    this.LogInfo($"[SIGNAL SHORT ABSORPTION] Niveau: {zoneAtClose.Level} ({zoneAtClose.LevelPrice}) | " +
                                 $"Delta Bougie: +{candleDelta} | Close ({candle.Close}) < CandlePOC ({candlePocPrice}) | " +
                                 $"SL: {hardStopPrice} | TP: {takeProfitPrice}");

                    ExecuteMarketEntryWithOco(OrderDirections.Sell, hardStopPrice, takeProfitPrice);
                    return;
                }
            }

            // Condition 2 : ACHAT (LONG) SUR SUPPORT (ex: VAL ou VPOC)
            // Agressivité vendeuse massive (Delta <= -MinAbsorptionDelta) absorbée par des ordres limites acheteurs (Close > POC bougie)
            bool canLongLevel = zoneAtClose.Polarity == LevelPolarity.Support || zoneAtClose.Polarity == LevelPolarity.Bidirectional;
            bool isBearishDeltaAbsorbed = candleDelta <= -Math.Abs(MinAbsorptionDelta) && candle.Close > candlePocPrice;

            if (canLongLevel && isBearishDeltaAbsorbed)
            {
                if (!RequireWickImbalance || HasLowerWickBidImbalance(candle, candlePocPrice))
                {
                    _lastSignalBarIndex = closedBar;

                    // Calcul du Hard Stop ultra-serré : exactement à 2 ticks (StopLossWickBufferTicks) en dessous du Low de la bougie d'absorption
                    decimal hardStopPrice = AlignToTick(candle.Low - (StopLossWickBufferTicks * tickSize), tickSize);
                    decimal takeProfitPrice = AlignToTick(candle.Close + (TakeProfitTicks * tickSize), tickSize);

                    this.LogInfo($"[SIGNAL LONG ABSORPTION] Niveau: {zoneAtClose.Level} ({zoneAtClose.LevelPrice}) | " +
                                 $"Delta Bougie: {candleDelta} | Close ({candle.Close}) > CandlePOC ({candlePocPrice}) | " +
                                 $"SL: {hardStopPrice} | TP: {takeProfitPrice}");

                    ExecuteMarketEntryWithOco(OrderDirections.Buy, hardStopPrice, takeProfitPrice);
                }
            }
        }

        /// <summary>
        /// Filtre Footprint optionnel : vérifie la présence d'une Imbalance diagonale acheteuse piégée
        /// dans la partie haute de la bougie (au-dessus du POC de la bougie).
        /// </summary>
        private bool HasUpperWickAskImbalance(IndicatorCandle candle, decimal candlePocPrice)
        {
            var levels = candle.GetAllPriceLevels()?.OrderBy(l => l.Price).ToList();
            if (levels == null || levels.Count < 2)
                return false;

            for (int i = 1; i < levels.Count; i++)
            {
                if (levels[i].Price < candlePocPrice)
                    continue;

                decimal askUpper = levels[i].Ask;
                decimal bidLower = levels[i - 1].Bid;

                if (bidLower == 0m && askUpper > 0m)
                    return true;
                if (bidLower > 0m && (askUpper / bidLower) >= ImbalanceRatio)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Filtre Footprint optionnel : vérifie la présence d'une Imbalance diagonale vendeuse piégée
        /// dans la partie basse de la bougie (en dessous du POC de la bougie).
        /// </summary>
        private bool HasLowerWickBidImbalance(IndicatorCandle candle, decimal candlePocPrice)
        {
            var levels = candle.GetAllPriceLevels()?.OrderBy(l => l.Price).ToList();
            if (levels == null || levels.Count < 2)
                return false;

            for (int i = 0; i < levels.Count - 1; i++)
            {
                if (levels[i].Price > candlePocPrice)
                    continue;

                decimal bidLower = levels[i].Bid;
                decimal askUpper = levels[i + 1].Ask;

                if (askUpper == 0m && bidLower > 0m)
                    return true;
                if (askUpper > 0m && (bidLower / askUpper) >= ImbalanceRatio)
                    return true;
            }

            return false;
        }

        #endregion

        #region Machine à États & Exécution OCO Asynchrone

        /// <summary>
        /// Vérifie strictement si la position est égale à 0 via TradingManager.Position et CurrentPosition.
        /// </summary>
        private bool IsStrictlyFlat()
        {
            // Vérification stricte de TradingManager.Position == 0 (CurrentVolume == 0) et CurrentPosition == 0
            decimal managerPositionVolume = TradingManager?.Position?.CurrentVolume ?? 0m;
            return CurrentPosition == 0m && managerPositionVolume == 0m;
        }

        /// <summary>
        /// Lance l'exécution d'un ordre Market d'entrée après vérification stricte que Position == 0.
        /// </summary>
        private void ExecuteMarketEntryWithOco(OrderDirections direction, decimal stopLossPrice, decimal takeProfitPrice)
        {
            lock (_stateLock)
            {
                // Vérification stricte : TradingManager.Position == 0 avant tout envoi d'ordre
                if (!IsStrictlyFlat())
                {
                    this.LogWarn("[EXÉCUTION BLOQUÉE] TradingManager.Position != 0. Annulation de la tentative d'entrée.");
                    return;
                }

                if (_state != BotExecutionState.Armed && _state != BotExecutionState.Idle)
                    return;

                if (Portfolio == null || Security == null)
                {
                    this.LogError("[ERREUR] Portfolio ou Security non configuré dans ATAS ChartStrategy.");
                    return;
                }

                _state = BotExecutionState.EnteringMarket;
                _pendingEntryDirection = direction;
                _pendingStopLossPrice = stopLossPrice;
                _pendingTakeProfitPrice = takeProfitPrice;

                _entryOrder = new Order
                {
                    Portfolio = Portfolio,
                    Security = Security,
                    Direction = direction,
                    Type = OrderTypes.Market,
                    QuantityToFill = OrderVolume,
                    Comment = $"NQ_ABS_ENTRY_{direction}"
                };

                _ = SubmitEntryOrderAsync(_entryOrder);
            }
        }

        private async Task SubmitEntryOrderAsync(Order entryOrder)
        {
            try
            {
                await OpenOrderAsync(entryOrder);
                this.LogInfo($"[ORDRE MARKET ENVOYÉ] Direction: {entryOrder.Direction} | Qté: {entryOrder.QuantityToFill}");
            }
            catch (Exception ex)
            {
                this.LogError($"[ERREUR ENVOI ORDRE D'ENTRÉE] {ex.Message}", ex);
                ResetStateToIdle("Exception lors de l'envoi de l'ordre Market d'entrée");
            }
        }

        /// <summary>
        /// Déploie conjointement le Stop Loss et le Take Profit liés par un identifiant OCOGroup unique
        /// dès que la position est ouverte.
        /// </summary>
        private async Task DeployOcoBracketsAsync(OrderDirections entryDirection, decimal filledVolume, decimal actualEntryPrice)
        {
            try
            {
                decimal tickSize = GetTickSize();
                OrderDirections exitDirection = entryDirection == OrderDirections.Buy
                    ? OrderDirections.Sell
                    : OrderDirections.Buy;

                // Ajustement du Take Profit sur le prix d'exécution réel si disponible, tout en conservant le Hard Stop sur la mèche d'absorption
                decimal tpPrice = _pendingTakeProfitPrice;
                if (actualEntryPrice > 0m)
                {
                    tpPrice = entryDirection == OrderDirections.Buy
                        ? AlignToTick(actualEntryPrice + (TakeProfitTicks * tickSize), tickSize)
                        : AlignToTick(actualEntryPrice - (TakeProfitTicks * tickSize), tickSize);
                }

                decimal slPrice = _pendingStopLossPrice;

                // Sécurité : vérifier que le Stop Loss est du bon côté du prix actuel
                if (entryDirection == OrderDirections.Buy && slPrice >= actualEntryPrice && actualEntryPrice > 0m)
                    slPrice = AlignToTick(actualEntryPrice - (StopLossWickBufferTicks * tickSize), tickSize);
                else if (entryDirection == OrderDirections.Sell && slPrice <= actualEntryPrice && actualEntryPrice > 0m)
                    slPrice = AlignToTick(actualEntryPrice + (StopLossWickBufferTicks * tickSize), tickSize);

                _activeOcoGroupId = "OCO_NQ_ABS_" + Guid.NewGuid().ToString("N").Substring(0, 12);

                _stopLossOrder = new Order
                {
                    Portfolio = Portfolio,
                    Security = Security,
                    Direction = exitDirection,
                    Type = OrderTypes.Stop,
                    TriggerPrice = slPrice,
                    QuantityToFill = Math.Abs(filledVolume),
                    OCOGroup = _activeOcoGroupId,
                    Comment = "NQ_ABS_SL_OCO"
                };

                _takeProfitOrder = new Order
                {
                    Portfolio = Portfolio,
                    Security = Security,
                    Direction = exitDirection,
                    Type = OrderTypes.Limit,
                    Price = tpPrice,
                    QuantityToFill = Math.Abs(filledVolume),
                    OCOGroup = _activeOcoGroupId,
                    Comment = "NQ_ABS_TP_OCO"
                };

                await OpenOrderAsync(_stopLossOrder);
                await OpenOrderAsync(_takeProfitOrder);

                lock (_stateLock)
                {
                    if (_state == BotExecutionState.PlacingOcoBrackets)
                        _state = BotExecutionState.InPositionOcoActive;
                }

                this.LogInfo($"[OCO DÉPLOYÉ] Groupe: {_activeOcoGroupId} | SL (Hard Stop 2 ticks mèche): {slPrice} | TP: {tpPrice}");
            }
            catch (Exception ex)
            {
                this.LogError($"[ERREUR CRITIQUE DÉPLOIEMENT OCO] {ex.Message} -> Fermeture d'urgence de la position !", ex);
                await FlattenAllAsync("Échec du déploiement OCO");
            }
        }

        /// <summary>
        /// Appelé automatiquement par ATAS lorsque la position de la stratégie évolue.
        /// Gère la transition vers le déploiement OCO à l'ouverture et le nettoyage à la clôture.
        /// </summary>
        protected override void OnPositionChanged(Position position)
        {
            base.OnPositionChanged(position);

            decimal currentVol = CurrentPosition;
            decimal tickSize = GetTickSize();
            decimal tickCost = GetTickCost();

            // Détection de l'ouverture de position -> déclenchement immédiat des ordres OCO
            if (_trackedPositionVolume == 0m && currentVol != 0m)
            {
                _trackedPositionVolume = currentVol;
                _entryAvgPrice = position?.AveragePrice ?? GetCandle(CurrentBar - 1)?.Close ?? 0m;
                _tradesToday++;

                bool shouldDeployOco = false;
                lock (_stateLock)
                {
                    if (_state == BotExecutionState.EnteringMarket || _state == BotExecutionState.Armed || _state == BotExecutionState.Idle)
                    {
                        _state = BotExecutionState.PlacingOcoBrackets;
                        shouldDeployOco = true;
                    }
                }

                if (shouldDeployOco)
                {
                    OrderDirections dir = currentVol > 0m ? OrderDirections.Buy : OrderDirections.Sell;
                    _ = DeployOcoBracketsAsync(dir, Math.Abs(currentVol), _entryAvgPrice);
                }
            }
            // Détection du retour à zéro (Position == 0 suite à TP, SL ou Flatten)
            else if (_trackedPositionVolume != 0m && currentVol == 0m)
            {
                // Estimation prudente du PnL réalisé sur le trade clôturé si les callbacks MyTrade ne l'ont pas déjà comptabilisé
                decimal lastClose = GetCandle(CurrentBar - 1)?.Close ?? _entryAvgPrice;
                if (_entryAvgPrice > 0m && lastClose > 0m && tickSize > 0m)
                {
                    decimal priceDiff = _trackedPositionVolume > 0m
                        ? (lastClose - _entryAvgPrice)
                        : (_entryAvgPrice - lastClose);
                    decimal tradePnlUsd = (priceDiff / tickSize) * tickCost * Math.Abs(_trackedPositionVolume);
                    _sessionRealizedPnLUsd += tradePnlUsd;

                    this.LogInfo($"[POSITION CLÔTURÉE] PnL Trade estimé: {tradePnlUsd:F2} USD | PnL Cumulé Session: {_sessionRealizedPnLUsd:F2} USD");
                }

                _trackedPositionVolume = 0m;
                _entryAvgPrice = 0m;

                // Annulation préventive de tout reliquat d'ordre OCO encore actif
                _ = CancelRemainingBracketOrdersAsync();

                lock (_stateLock)
                {
                    if (_state != BotExecutionState.HaltedForSession)
                    {
                        ResetStateToIdle("Position revenue à 0 (Flat).");
                    }
                }
            }
        }

        /// <summary>
        /// Surcharge de OnOrderRejected (exigée par l'architecture) :
        /// Réinitialise l'état interne si l'ordre d'entrée est rejeté, ou ferme d'urgence la position
        /// si un ordre de protection OCO (Stop Loss / Take Profit) est rejeté par le broker.
        /// </summary>
        protected override void OnOrderRejected(Order order, string message)
        {
            if (order == null)
                return;

            if (order == _entryOrder)
            {
                this.LogError($"[ORDRE D'ENTRÉE REJETÉ] Raison: {message}. Réinitialisation de la Machine à États.");
                ResetStateToIdle("Ordre d'entrée rejeté");
            }
            else if (order == _stopLossOrder || order == _takeProfitOrder)
            {
                this.LogError($"[ORDRE OCO REJETÉ] ({order.Comment}) Raison: {message}. Fermeture immédiate de sécurité !");
                _ = FlattenAllAsync("Ordre de protection OCO rejeté");
            }
        }

        /// <summary>
        /// Surcharge de OnOrderCanceled (exigée par l'architecture) :
        /// Réinitialise l'état interne si un ordre d'entrée est annulé avant exécution.
        /// </summary>
        protected override void OnOrderCanceled(Order order)
        {
            if (order == null)
                return;

            if (order == _entryOrder && IsStrictlyFlat())
            {
                this.LogWarn("[ORDRE D'ENTRÉE ANNULÉ] Retour à l'état Idle.");
                ResetStateToIdle("Ordre d'entrée annulé");
            }
        }

        private void SynchronizeStateWithPosition()
        {
            if (IsStrictlyFlat() && _state == BotExecutionState.InPositionOcoActive)
            {
                ResetStateToIdle("Synchronisation automatique : Position = 0.");
            }
        }

        private void ResetStateToIdle(string reason)
        {
            lock (_stateLock)
            {
                if (_state == BotExecutionState.HaltedForSession)
                    return;

                _state = BotExecutionState.Idle;
                _armedZone = default;
                _entryOrder = null;
                _stopLossOrder = null;
                _takeProfitOrder = null;
                _activeOcoGroupId = null;
            }

            this.LogInfo($"[STATE -> IDLE] {reason}");
        }

        #endregion

        #region Sécurités Prop Firm (DailyLossLimit, MaxTradesPerDay, SessionStart/End)

        /// <summary>
        /// Vérifie les règles de gestion du risque Prop Firm à chaque tick/clôture.
        /// Retourne true si la prise de position est bloquée ou si un coupe-circuit est actif.
        /// </summary>
        private bool CheckAndEnforcePropFirmRules(IndicatorCandle currentCandle)
        {
            if (_state == BotExecutionState.HaltedForSession || _state == BotExecutionState.EmergencyFlatten)
                return true;

            // 1. Vérification du DailyLossLimit (en USD) : PnL réalisé + PnL latent de la position ouverte
            if (EnableDailyLossLimit && DailyLossLimit > 0m)
            {
                decimal openUnrealizedPnL = CalculateUnrealizedPnLUsd(currentCandle.Close);
                decimal totalDailyPnL = _sessionRealizedPnLUsd + openUnrealizedPnL;

                if (totalDailyPnL <= -Math.Abs(DailyLossLimit))
                {
                    this.LogError($"[COUPE-CIRCUIT PROP FIRM] DailyLossLimit atteint ({totalDailyPnL:F2} USD <= -{ Math.Abs(DailyLossLimit):F2} USD). Close All & Désactivation jusqu'à la prochaine session !");
                    _state = BotExecutionState.HaltedForSession;
                    _ = FlattenAllAsync("DailyLossLimit atteint");
                    return true;
                }
            }

            // 2. Vérification de la fenêtre horaire de trading (SessionStart & SessionEnd)
            if (EnableSessionFilter)
            {
                TimeSpan candleTime = currentCandle.Time.TimeOfDay;
                bool isInsideSession = IsTimeWithinSession(candleTime, SessionStart, SessionEnd);

                if (!isInsideSession)
                {
                    // Si nous sommes hors session et qu'une position ou des ordres sont ouverts -> Force Close All
                    if (!IsStrictlyFlat() || _stopLossOrder != null || _takeProfitOrder != null)
                    {
                        this.LogWarn($"[FIN DE SESSION ({candleTime})] Force Close All & Annulation des ordres en attente.");
                        _ = FlattenAllAsync("Heure de fin de session atteinte");
                    }
                    return true;
                }
            }

            // 3. Vérification du nombre maximal de trades par jour (MaxTradesPerDay)
            if (EnableMaxTradesPerDay && _tradesToday >= MaxTradesPerDay && IsStrictlyFlat())
            {
                this.LogWarn($"[MAX TRADES ATTEINT] {_tradesToday}/{MaxTradesPerDay} trades exécutés aujourd'hui. Bot mis en pause pour la session.");
                _state = BotExecutionState.HaltedForSession;
                return true;
            }

            return false;
        }

        private bool IsTimeWithinSession(TimeSpan current, TimeSpan start, TimeSpan end)
        {
            if (start <= end)
                return current >= start && current < end;

            // Gestion des sessions chevauchant minuit
            return current >= start || current < end;
        }

        private decimal CalculateUnrealizedPnLUsd(decimal currentPrice)
        {
            if (IsStrictlyFlat() || _entryAvgPrice <= 0m || currentPrice <= 0m)
                return 0m;

            decimal tickSize = GetTickSize();
            decimal tickCost = GetTickCost();
            if (tickSize <= 0m)
                return 0m;

            decimal priceDiff = CurrentPosition > 0m
                ? (currentPrice - _entryAvgPrice)
                : (_entryAvgPrice - currentPrice);

            return (priceDiff / tickSize) * tickCost * Math.Abs(CurrentPosition);
        }

        /// <summary>
        /// Clôture immédiatement toute position ouverte au Marché et annule tous les ordres en attente.
        /// </summary>
        private async Task FlattenAllAsync(string reason)
        {
            bool preserveHalt = _state == BotExecutionState.HaltedForSession;
            _state = preserveHalt ? BotExecutionState.HaltedForSession : BotExecutionState.EmergencyFlatten;

            try
            {
                await CancelRemainingBracketOrdersAsync();

                decimal posVol = CurrentPosition;
                if (posVol != 0m && Portfolio != null && Security != null)
                {
                    var closeOrder = new Order
                    {
                        Portfolio = Portfolio,
                        Security = Security,
                        Direction = posVol > 0m ? OrderDirections.Sell : OrderDirections.Buy,
                        Type = OrderTypes.Market,
                        QuantityToFill = Math.Abs(posVol),
                        Comment = $"NQ_ABS_FLATTEN_{reason}"
                    };

                    await OpenOrderAsync(closeOrder);
                    this.LogWarn($"[CLOSE ALL ENVOYÉ] Raison: {reason} | Fermeture de {Math.Abs(posVol)} contrat(s).");
                }
            }
            catch (Exception ex)
            {
                this.LogError($"[ERREUR FLATTEN ALL] {ex.Message}", ex);
            }
            finally
            {
                if (!preserveHalt && IsStrictlyFlat())
                {
                    ResetStateToIdle($"Flatten terminé ({reason})");
                }
            }
        }

        private async Task CancelRemainingBracketOrdersAsync()
        {
            try
            {
                if (_stopLossOrder != null && _stopLossOrder.State == OrderStates.Active)
                    await CancelOrderAsync(_stopLossOrder);
            }
            catch (Exception ex)
            {
                this.LogWarn($"[ANNULATION SL] {ex.Message}");
            }

            try
            {
                if (_takeProfitOrder != null && _takeProfitOrder.State == OrderStates.Active)
                    await CancelOrderAsync(_takeProfitOrder);
            }
            catch (Exception ex)
            {
                this.LogWarn($"[ANNULATION TP] {ex.Message}");
            }
        }

        #endregion

        #region Utilitaires Instrument (NQ / MNQ TickSize & TickCost)

        private decimal GetTickSize()
        {
            if (InstrumentInfo != null && InstrumentInfo.TickSize > 0m)
                return InstrumentInfo.TickSize;
            if (Security != null && Security.TickSize > 0m)
                return Security.TickSize;

            return 0.25m; // Défaut Nasdaq Futures (NQ / MNQ = 0.25 pt)
        }

        private decimal GetTickCost()
        {
            if (Security != null && Security.TickCost > 0m)
                return Security.TickCost;

            return 5.0m; // Défaut NQ (5.00 USD / tick ; MNQ = 0.50 USD / tick via Security.TickCost)
        }

        private static decimal AlignToTick(decimal price, decimal tickSize)
        {
            if (tickSize <= 0m)
                return price;

            return Math.Round(price / tickSize, 0, MidpointRounding.AwayFromZero) * tickSize;
        }

        #endregion
    }
}
